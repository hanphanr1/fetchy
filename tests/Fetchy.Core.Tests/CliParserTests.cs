using Fetchy.Cli;
using Xunit;

namespace Fetchy.Core.Tests;

public sealed class CliParserTests
{
    [Theory]
    [InlineData("5MB/s", 5L * 1024 * 1024)]
    [InlineData("500KB/s", 500L * 1024)]
    [InlineData("10M", 10L * 1024 * 1024)]
    [InlineData("1.5MB/s", 1_572_864L)]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    [InlineData("4096", 4096L)]
    [InlineData("0", 0L)]
    public void ParseByteRate_ParsesValidFormats(string input, long expectedBytesPerSecond)
    {
        Assert.Equal(expectedBytesPerSecond, CliParser.ParseByteRate(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-5MB/s")]
    [InlineData("10TB/s")]
    [InlineData("abc")]
    public void ParseByteRate_RejectsInvalidFormats(string input)
    {
        Assert.Throws<CliParseException>(() => CliParser.ParseByteRate(input));
    }

    [Fact]
    public void Parse_DownloadCommand_ParsesAllFlags()
    {
        CliCommand cmd = CliParser.Parse([
            "download",
            "https://example.com/file.zip",
            "-o", "out.zip",
            "-c", "12",
            "--limit", "5MB/s",
            "--sha256", "abcd1234",
            "--md5", "ef5678",
            "--referer", "https://example.com/",
            "--ua", "CustomAgent/2.0",
            "--overwrite"
        ]);

        var dl = Assert.IsType<DownloadCommand>(cmd);
        Assert.Equal(new Uri("https://example.com/file.zip"), dl.Url);
        Assert.Equal("out.zip", dl.OutputPath);
        Assert.Equal(12, dl.Connections);
        Assert.Equal(5L * 1024 * 1024, dl.MaxBytesPerSecond);
        Assert.Equal("abcd1234", dl.ExpectedSha256);
        Assert.Equal("ef5678", dl.ExpectedMd5);
        Assert.Equal("https://example.com/", dl.Referer);
        Assert.Equal("CustomAgent/2.0", dl.UserAgent);
        Assert.True(dl.Overwrite);
    }

    [Fact]
    public void Parse_ResumeVerifyBenchmarkVersionHelp_WorkCorrectly()
    {
        var resume = Assert.IsType<ResumeCommand>(CliParser.Parse([
            "resume", "file.bin.fetchy.state", "--url", "https://mirror.example.com/file.bin", "--limit", "500KB/s"
        ]));
        Assert.Equal("file.bin.fetchy.state", resume.StateFilePath);
        Assert.Equal(new Uri("https://mirror.example.com/file.bin"), resume.RefreshedUrl);
        Assert.Equal(500L * 1024, resume.MaxBytesPerSecond);

        var verify = Assert.IsType<VerifyCommand>(CliParser.Parse(["verify", "archive.zip", "--sha256", "deadbeef"]));
        Assert.Equal("archive.zip", verify.FilePath);
        Assert.Equal("deadbeef", verify.ExpectedSha256);

        var bench = Assert.IsType<BenchmarkOptionsCommand>(CliParser.Parse(["benchmark", "--size-mb", "32", "--connections", "8"]));
        Assert.Equal(32, bench.SizeMb);
        Assert.Equal(8, bench.Connections);

        Assert.IsType<VersionCommand>(CliParser.Parse(["--version"]));
        Assert.IsType<HelpCommand>(CliParser.Parse(["--help"]));
        Assert.IsType<HelpCommand>(CliParser.Parse([]));
    }

    [Theory]
    [InlineData("download", "file:///C:/Windows/win.ini")]
    [InlineData("download", "ftp://example.com/a.bin")]
    [InlineData("download", "https://example.com/a.bin", "-c", "0")]
    [InlineData("download", "https://example.com/a.bin", "-c", "33")]
    [InlineData("verify", "file.bin")]
    [InlineData("benchmark", "--size-mb", "0")]
    [InlineData("unknown-cmd")]
    public void Parse_RejectsInvalidArguments(params string[] args)
    {
        Assert.Throws<CliParseException>(() => CliParser.Parse(args));
    }
}
