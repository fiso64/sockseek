using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Extractors;

namespace Tests.Core;

[TestClass]
public class YtDlpInterfaceTests
{
    [TestMethod]
    public void TryParseYtdlpSearchResult_ReadsJsonFields()
    {
        var json = """
            {"id":"abc-123_DEF","title":"Artist === Title \"Live\"","duration":235.4}
            """;

        var parsed = YouTube.TryParseYtdlpSearchResult(json, out var result);

        Assert.IsTrue(parsed);
        Assert.AreEqual(235, result.length);
        Assert.AreEqual("abc-123_DEF", result.id);
        Assert.AreEqual("Artist === Title \"Live\"", result.title);
    }

    [TestMethod]
    public void TryParseYtdlpSearchResult_IgnoresMalformedOutput()
    {
        var parsed = YouTube.TryParseYtdlpSearchResult("warning: not json", out _);

        Assert.IsFalse(parsed);
    }

    [TestMethod]
    public void TryParseYtdlpDownloadPath_ReadsJsonEscapedAfterMovePath()
    {
        var path = YouTube.TryParseYtdlpDownloadPath(
            "sockseek-download-path:\"C:\\\\Music\\\\Artist - Title [x].opus\"");

        Assert.AreEqual(@"C:\Music\Artist - Title [x].opus", path);
    }

    [TestMethod]
    public void TryParseYtdlpDownloadPath_IgnoresUnrelatedOutput()
    {
        var path = YouTube.TryParseYtdlpDownloadPath("[download] 100% of 3.2MiB");

        Assert.IsNull(path);
    }
}
