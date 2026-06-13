using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Tests.Cli;

[TestClass]
public class JsonStreamProgressReporterTests
{
    [TestMethod]
    public void TrackState_UsesSplitStateFields()
    {
        var writer = new StringWriter();
        var reporter = new JsonStreamProgressReporter(writer);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
        song.SetDone();

        typeof(JsonStreamProgressReporter)
            .GetMethod(
                "ReportStateChanged",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(SongJob)],
                modifiers: null)!
            .Invoke(reporter, [song]);

        using var document = JsonDocument.Parse(writer.ToString());
        var data = document.RootElement.GetProperty("data");

        Assert.IsTrue(data.TryGetProperty("lifecycleState", out var lifecycleState));
        Assert.AreEqual("Terminal", lifecycleState.GetString());
        Assert.IsTrue(data.TryGetProperty("activityPhase", out var activityPhase));
        Assert.AreEqual("None", activityPhase.GetString());
        Assert.IsTrue(data.TryGetProperty("terminalOutcome", out var terminalOutcome));
        Assert.AreEqual("Succeeded", terminalOutcome.GetString());
        Assert.IsFalse(data.TryGetProperty("state", out _));
    }
}
