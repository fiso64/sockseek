using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Tests.Cli;

[TestClass]
public class JsonStreamProgressReporterTests
{
    [TestMethod]
    public void SearchStart_TracksPhaseTransitionsPerJob_AndPreservesTerminalState()
    {
        using var writer = new StringWriter();
        var events = new DownloadEvents();
        new JsonStreamProgressReporter(writer).Attach(events);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });
        var other = new SongJob(new SongQuery { Artist = "Artist", Title = "Title" });

        void Publish(SongJob job, JobActivityPhase phase)
        {
            job.UpdateActivity(phase);
            events.RaiseJobStateChanged(job);
        }

        Publish(song, JobActivityPhase.Searching);
        Publish(song, JobActivityPhase.Searching); // Result/progress update in the same phase.
        Publish(other, JobActivityPhase.Searching); // Same query, independent job.
        Publish(song, JobActivityPhase.None);
        Publish(song, JobActivityPhase.Searching); // A genuine new search attempt.
        Publish(song, JobActivityPhase.Searching);
        song.SetDone();
        events.RaiseJobStateChanged(song);

        using var document = JsonDocument.Parse("[" + string.Join(',',
            writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)) + "]");
        var rows = document.RootElement.EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "search_start", "search_start", "search_start", "track_state" },
            rows.Select(row => row.GetProperty("type").GetString()).ToArray());
        var data = rows[^1].GetProperty("data");
        Assert.AreEqual("Terminal", data.GetProperty("lifecycleState").GetString());
        Assert.AreEqual("None", data.GetProperty("activityPhase").GetString());
        Assert.AreEqual("Succeeded", data.GetProperty("terminalOutcome").GetString());
        Assert.IsFalse(data.TryGetProperty("state", out _));
    }
}
