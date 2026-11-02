using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public sealed class JobInfoPrinterTests
{
    [TestMethod]
    public void SourceDetailFromSharedProjectionPrintsInJobInfo()
    {
        using var writer = new StringWriter();
        using (Printing.RedirectOutput(writer))
        {
            JobInfoPrinter.Print(new JobDetailDto(
                new JobSummaryDto
                {
                    Kind = ServerJobKind.Song,
                    SourceDetail = "List item 2",
                },
                new SongJobPayloadDto(
                    new SongQueryDto(),
                    null,
                    new FileDownloadStateDto(null, 0, null, null),
                    ResolvedSampleRate: -1,
                    ResolvedAttributes: [new("Length", int.MinValue), new("BitRate", -1)]), 0), []);
        }

        StringAssert.Contains(writer.ToString(), "  Source            List item 2");
        Assert.IsFalse(writer.ToString().Contains("Attributes", StringComparison.Ordinal));
    }

}
