using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using Sockseek.Core.Extractors;
using Sockseek.Core.Services;

namespace Tests.Core;

[TestClass]
public class YtDlpInterfaceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public async Task ProcessOutputIsDrainedThroughExit(int exitCode)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add($"echo first&echo diagnostic 1>&2&<nul set /p =last&exit /b {exitCode}");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"printf 'first\\n'; printf diagnostic >&2; printf last; exit {exitCode}");
        }
        var result = await YtDlpCommand.RunAsync(start, ExtractorContext.None.Log);
        Assert.AreEqual(exitCode, result.ExitCode);
        CollectionAssert.AreEqual(new[] { "first", "last" }, result.Stdout.ToArray());
        Assert.AreEqual("diagnostic", result.Stderr.Single().Trim());
    }

    [TestMethod]
    public void TryParseYtdlpSearchResult_ReadsJsonFields()
    {
        var json = """
            {"id":"abc-123_DEF","title":"Artist === Title \"Live\"","duration":235.4}
            """;

        var parsed = YtDlpCommand.TryParseSearchResult(json, out var result);

        Assert.IsTrue(parsed);
        Assert.AreEqual(235, result.length);
        Assert.AreEqual("abc-123_DEF", result.id);
        Assert.AreEqual("Artist === Title \"Live\"", result.title);
    }

    [TestMethod]
    public void TryParseYtdlpSearchResult_IgnoresMalformedOutput()
    {
        var parsed = YtDlpCommand.TryParseSearchResult("warning: not json", out _);

        Assert.IsFalse(parsed);
    }

    [TestMethod]
    public void TryParseYtdlpDownloadPath_ReadsJsonEscapedAfterMovePath()
    {
        var path = YtDlpCommand.TryParseDownloadPath(
            "sockseek-download-path:\"C:\\\\Music\\\\Artist - Title [x].opus\"");

        Assert.AreEqual(@"C:\Music\Artist - Title [x].opus", path);
    }

    [TestMethod]
    public void TryParseYtdlpDownloadResult_ReadsFormattedJson()
    {
        var result = YtDlpCommand.TryParseDownloadResult(
            "sockseek-download-result:[\"C:\\\\Music\\\\Artist - Title.opus\",\"abc123\",\"Artist - Title \\\"Live\\\" ||| remix\",\"https://example.test/watch?v=abc123\",\"youtube\",\"251\",\"opus\",235]");

        Assert.IsNotNull(result);
        Assert.AreEqual(@"C:\Music\Artist - Title.opus", result.Filepath);
        Assert.AreEqual("abc123", result.Id);
        Assert.AreEqual("Artist - Title \"Live\" ||| remix", result.Title);
        Assert.AreEqual("https://example.test/watch?v=abc123", result.WebpageUrl);
        Assert.AreEqual("youtube", result.Extractor);
        Assert.AreEqual("251", result.FormatId);
        Assert.AreEqual("opus", result.Ext);
        Assert.AreEqual(235, result.Duration);
    }

    [TestMethod]
    public void BuildYtdlpDownloadArguments_AppendsResultPrintWithCustomArguments()
    {
        var arguments = YtDlpCommand.BuildDownloadArguments(
            "abc123",
            @"C:\Music\Artist - Title",
            "\"{id}\" --cookies-from-browser firefox -o \"{savepath-noext}.%(ext)s\"");

        StringAssert.StartsWith(
            arguments,
            "\"abc123\" --cookies-from-browser firefox -o \"C:\\Music\\Artist - Title.%(ext)s\" --print \"after_move:sockseek-download-result:[%(filepath)j,%(id)j");
        StringAssert.Contains(arguments, ",%(title)j");
        StringAssert.Contains(arguments, ",%(webpage_url)j");
        StringAssert.Contains(arguments, ",%(format_id)j");
    }

    [TestMethod]
    public void StripInternalDownloadResultPrint_RemovesOnlySockseekPrintSuffix()
    {
        var arguments = YtDlpCommand.BuildDownloadArguments(
            "abc123",
            @"C:\Music\Artist - Title",
            "\"{id}\" --print title -o \"{savepath-noext}.%(ext)s\"");

        var logged = YtDlpCommand.StripInternalDownloadResultPrint(arguments);

        Assert.AreEqual(
            "\"abc123\" --print title -o \"C:\\Music\\Artist - Title.%(ext)s\"",
            logged);
    }

    [TestMethod]
    public void FormatDownloadSuccess_IncludesTitleIdAndRelativePath()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "downloads", "Artist - Title.opus");

        var message = YtDlpCommand.FormatDownloadSuccess(
            new YtDlpCommand.YtDlpDownloadResult(path, "abc123", "Artist - Title"));

        Assert.AreEqual(
            $"Artist - Title (abc123) -> {Path.Combine("downloads", "Artist - Title.opus")}",
            message);
    }

    [TestMethod]
    public void FormatDownloadSuccess_UsesIdWhenTitleMissing()
    {
        var message = YtDlpCommand.FormatDownloadSuccess(
            new YtDlpCommand.YtDlpDownloadResult("Artist - Title.opus", "abc123"));

        Assert.AreEqual("abc123 -> Artist - Title.opus", message);
    }

    [TestMethod]
    public void TryParseYtdlpDownloadPath_IgnoresUnrelatedOutput()
    {
        var path = YtDlpCommand.TryParseDownloadPath("[download] 100% of 3.2MiB");

        Assert.IsNull(path);
    }
}
