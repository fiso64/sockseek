using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.Reporting;

namespace Tests;

[TestClass]
public sealed class AutoProfileWorkflowReporterTests
{
    [TestMethod]
    public void FinalSummary_ReleasesPerWorkflowCountingState()
    {
        var events = new DownloadEvents();
        var reporter = new AutoProfileWorkflowReporter(events);
        var job = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            Config = new DownloadSettings
            {
                AppliedAutoProfiles = ["music"],
            },
        };
        var summaries = new List<string>();
        events.WorkflowMessage += change =>
        {
            if (change.Level == LogLevel.Debug)
                summaries.Add(change.Message);
        };

        reporter.ObservePreparedRoot(job);

        Assert.AreEqual(1, reporter.RetainedWorkflowCount);
        reporter.EmitFinalSummary(job);

        Assert.AreEqual(0, reporter.RetainedWorkflowCount);
        CollectionAssert.AreEqual(
            new[] { "Auto profiles applied: music (1 song)" },
            summaries);

        reporter.EmitFinalSummary(job);
        Assert.HasCount(1, summaries);

        var noProfiles = new SongJob(new SongQuery { Artist = "Other", Title = "Track" })
        {
            Config = new DownloadSettings(),
        };
        reporter.ObservePreparedRoot(noProfiles);
        Assert.AreEqual(1, reporter.RetainedWorkflowCount);

        reporter.EmitFinalSummary(noProfiles);

        Assert.AreEqual(0, reporter.RetainedWorkflowCount);
        Assert.HasCount(1, summaries);
    }
}
