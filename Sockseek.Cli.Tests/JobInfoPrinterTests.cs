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

    [TestMethod]
    public void LegacyContainerOutcomesPrintUnknownInsteadOfZero()
    {
        using var writer = new StringWriter();
        using (Printing.RedirectOutput(writer))
        {
            JobInfoPrinter.Print(new JobDetailDto(
                new JobSummaryDto() with { Kind = ServerJobKind.JobList },
                new JobListPayloadDto(3, null, null, null, null), 0), []);
            JobInfoPrinter.Print(new JobDetailDto(
                new JobSummaryDto() with { Kind = ServerJobKind.Aggregate },
                new AggregateJobPayloadDto(new SongQueryDto(), 2, null, null, null), 0), []);
        }

        StringAssert.Contains(writer.ToString(), "3 total  •  unknown active  •  unknown ok  •  unknown failed");
        StringAssert.Contains(writer.ToString(), "2 total  •  unknown completed  •  unknown ok  •  unknown failed");
        Assert.IsFalse(writer.ToString().Contains("0 ok", StringComparison.Ordinal));
    }
}
