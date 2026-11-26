using Fetchy.Core.Engine;
using Fetchy.Core.Management;
using Fetchy.Core.Storage;
using Xunit;

namespace Fetchy.Core.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _tempDir;

    public DownloadManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fetchy-mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("setup.exe", DownloadCategory.Programs)]
    [InlineData("installer.msi", DownloadCategory.Programs)]
    [InlineData("archive.tar.gz", DownloadCategory.Compressed)]
    [InlineData("bundle.7z", DownloadCategory.Compressed)]
    [InlineData("movie.mkv", DownloadCategory.Videos)]
    [InlineData("clip.mp4", DownloadCategory.Videos)]
    [InlineData("manual.pdf", DownloadCategory.Documents)]
    [InlineData("notes.md", DownloadCategory.Documents)]
    [InlineData("track.flac", DownloadCategory.Music)]
    [InlineData("song.mp3", DownloadCategory.Music)]
    [InlineData("payload.bin", DownloadCategory.Other)]
    [InlineData("noextension", DownloadCategory.Other)]
    public void CategoryClassifier_ClassifiesExtensionsAccurately(string fileName, DownloadCategory expected)
    {
        Assert.Equal(expected, CategoryClassifier.Classify(fileName));
    }

    [Fact]
    public async Task DownloadManager_RespectsMaxConcurrentDownloadsAndPersistsReloadableCatalog()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(512 * 1024, seed: 1001);
        await using var server = new TestHttpServer(payload)
        {
            PerRequestBytesPerSecond = (_, _, _) => 900_000
        };

        string catalogPath = Path.Combine(_tempDir, "downloads.json");
        int peakDownloading = 0;

        await using (var manager = new DownloadManager(catalogPath, _tempDir, maxConcurrentDownloads: 2))
        {
            manager.EntryUpdated += _ =>
            {
                int currentActive = manager.GetAll().Count(e => e.Status == DownloadStatus.Downloading);
                int prev;
                do
                {
                    prev = Volatile.Read(ref peakDownloading);
                    if (currentActive <= prev) break;
                } while (Interlocked.CompareExchange(ref peakDownloading, currentActive, prev) != prev);
            };

            var ids = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                var entry = await manager.EnqueueAsync(
                    server.GetUri($"item-{i}.zip"),
                    Path.Combine(_tempDir, $"item-{i}.zip"),
                    new DownloadOptions
                    {
                        MaxConnections = 2,
                        MinSplitBytes = 64 * 1024,
                        ExpectedSha256 = server.PayloadSha256
                    });
                ids.Add(entry.Id);
            }

            var settled = await Task.WhenAll(ids.Select(id => manager.WaitForSettlementAsync(id)));
            Assert.All(settled, e =>
            {
                Assert.Equal(DownloadStatus.Completed, e.Status);
                Assert.Equal(DownloadCategory.Compressed, e.Category);
                Assert.True(File.Exists(e.FinalFilePath));
            });

            Assert.InRange(peakDownloading, 1, 2);
        }

        // Reload catalog in a brand-new DownloadManager instance and verify atomic persistence.
        await using (var reloaded = new DownloadManager(catalogPath, _tempDir, maxConcurrentDownloads: 2))
        {
            await reloaded.LoadCatalogAsync();
            var all = reloaded.GetAll();
            Assert.Equal(4, all.Count);
            Assert.All(all, e => Assert.Equal(DownloadStatus.Completed, e.Status));
        }
    }

    [Fact]
    public async Task DownloadManager_PauseRefreshUrlResumeAndRemoveCleanupWorkCleanly()
    {
        byte[] payload = TestHttpServer.CreateDeterministicPayload(1536 * 1024, seed: 1002);
        await using var server = new TestHttpServer(payload)
        {
            PerRequestBytesPerSecond = (_, _, _) => 400_000
        };

        string catalogPath = Path.Combine(_tempDir, "downloads.json");
        await using var manager = new DownloadManager(catalogPath, _tempDir, maxConcurrentDownloads: 2);

        // 1. Pause -> RefreshUrl -> Resume to completion
        string path1 = Path.Combine(_tempDir, "video.mp4");
        string state1 = DownloadStateStore.GetStateFilePath(path1);
        var ready1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += (_, snap) =>
        {
            if (snap.DownloadedBytes >= 64 * 1024 && File.Exists(state1))
            {
                ready1.TrySetResult();
            }
        };

        var entry1 = await manager.EnqueueAsync(
            server.GetUri("old/video.mp4"),
            path1,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                ProgressReportInterval = TimeSpan.FromMilliseconds(25),
                ExpectedSha256 = server.PayloadSha256
            });

        await ready1.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await manager.PauseAsync(entry1.Id);

        var paused = manager.Get(entry1.Id);
        Assert.NotNull(paused);
        Assert.Equal(DownloadStatus.Paused, paused!.Status);
        Assert.True(File.Exists(state1));

        Uri refreshed = server.GetUri("new/video.mp4");
        await manager.RefreshUrlAsync(entry1.Id, refreshed);

        server.PerRequestBytesPerSecond = null;
        await manager.ResumeAsync(entry1.Id);

        var completed = await manager.WaitForSettlementAsync(entry1.Id);
        Assert.Equal(DownloadStatus.Completed, completed.Status);
        Assert.Equal(DownloadCategory.Videos, completed.Category);
        Assert.Equal(refreshed.AbsoluteUri, completed.Url);

        // 2. Enqueue another slowed download, pause it, then RemoveAsync -> verify .fetchy.part and .fetchy.state are deleted.
        server.PerRequestBytesPerSecond = (_, _, _) => 200_000;
        string path2 = Path.Combine(_tempDir, "removable.bin");
        string state2 = DownloadStateStore.GetStateFilePath(path2);
        var ready2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ProgressChanged += (_, snap) =>
        {
            if (snap.DownloadedBytes >= 64 * 1024 && File.Exists(state2))
            {
                ready2.TrySetResult();
            }
        };

        var entry2 = await manager.EnqueueAsync(
            server.GetUri("removable.bin"),
            path2,
            new DownloadOptions
            {
                MaxConnections = 4,
                MinSplitBytes = 64 * 1024,
                ProgressReportInterval = TimeSpan.FromMilliseconds(25)
            });

        await ready2.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await manager.PauseAsync(entry2.Id);
        Assert.True(File.Exists(DirectDiskWriter.GetPartFilePath(path2)));
        Assert.True(File.Exists(state2));

        await manager.RemoveAsync(entry2.Id);
        Assert.Null(manager.Get(entry2.Id));
        Assert.False(File.Exists(DirectDiskWriter.GetPartFilePath(path2)));
        Assert.False(File.Exists(state2));
    }
}
