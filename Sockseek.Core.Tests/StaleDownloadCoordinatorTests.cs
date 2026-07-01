using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Tests.Core;

[TestClass]
public class StaleDownloadCoordinatorTests
{
    private static readonly TimeSpan MaxStaleTime = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void QueuedAttempt_CancelsAfterMaxStaleTimeWithoutActivity()
    {
        var scenario = new Scenario();
        var attempt = scenario.Register("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertCancelled(attempt);
        Assert.IsFalse(scenario.Registry.Downloads.ContainsKey(attempt.Download.Candidate.Filename));
    }

    [TestMethod]
    public void StateChangesBeforeMaxStaleTimeRefreshDeadline()
    {
        var scenario = new Scenario();
        var attempt = scenario.Register("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportState(attempt, TransferStates.Initializing, bytesTransferred: 0);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());

        scenario.ReportState(attempt, TransferStates.InProgress, bytesTransferred: 0);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertCancelled(attempt);
    }

    [TestMethod]
    public void ProgressBeforeMaxStaleTimeRefreshesInProgressDeadline()
    {
        var scenario = new Scenario();
        var attempt = scenario.Register("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(attempt, bytesTransferred: 4096);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertCancelled(attempt);
    }

    [TestMethod]
    public void UnchangedStateAndBytesDoNotRefreshDeadline()
    {
        var scenario = new Scenario();
        var attempt = scenario.Register("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertCancelled(attempt);
    }

    [TestMethod]
    public void QueuedAttemptUsesFreshActivityFromSameUserSibling()
    {
        var scenario = new Scenario();
        var queued = scenario.Register("user-a", @"Music\Artist - Queued.mp3");
        var active = scenario.Register("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(queued.Download.Cts.IsCancellationRequested);
        Assert.IsFalse(active.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(2, scenario.CancelStaleDownloads());
        AssertCancelled(queued);
        AssertCancelled(active);
    }

    [TestMethod]
    public void InProgressAttemptDoesNotUseSiblingActivity()
    {
        var scenario = new Scenario();
        var stalled = scenario.Register("user-a", @"Music\Artist - Stalled.mp3");
        var active = scenario.Register("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(stalled, TransferStates.InProgress, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertCancelled(stalled);
        Assert.IsFalse(active.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void ActivityFromDifferentUserDoesNotProtectQueuedAttempt()
    {
        var scenario = new Scenario();
        var queued = scenario.Register("user-a", @"Music\Artist - Queued.mp3");
        var otherUser = scenario.Register("user-b", @"Music\Artist - Other.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(otherUser, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(otherUser, bytesTransferred: 4096);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertCancelled(queued);
        Assert.IsFalse(otherUser.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void CompletedAttemptIsRemovedFromStaleTracking()
    {
        var scenario = new Scenario();
        var attempt = scenario.Register("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);
        scenario.Coordinator.Complete(attempt.Id);

        scenario.Advance(MaxStaleTime);

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);
    }

    private static void AssertCancelled(AttemptHandle attempt)
    {
        Assert.IsTrue(attempt.Song.Cts?.IsCancellationRequested == true);
        Assert.IsTrue(attempt.Download.Cts.IsCancellationRequested);
    }

    private sealed class Scenario
    {
        private readonly ManualTimeProvider clock = new();

        public Scenario()
        {
            Coordinator = new StaleDownloadCoordinator(Registry, clock);
        }

        public SessionRegistry Registry { get; } = new();
        public StaleDownloadCoordinator Coordinator { get; }

        public AttemptHandle Register(string username, string filename)
        {
            var response = new SearchResponse(username, 1, true, 100_000, 0, []);
            var file = TestHelpers.CreateSlFile(filename, size: 50_000, length: 180);
            var candidate = new FileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = Path.GetFileNameWithoutExtension(filename) })
            {
                Cts = new CancellationTokenSource(),
            };
            var activeDownload = new ActiveDownload(song, candidate, new CancellationTokenSource());
            Registry.Downloads[candidate.Filename] = activeDownload;
            var attemptId = Coordinator.Register(activeDownload, (int)MaxStaleTime.TotalMilliseconds);
            return new AttemptHandle(attemptId, song, activeDownload);
        }

        public void ReportState(AttemptHandle attempt, TransferStates state, long bytesTransferred)
            => Coordinator.ReportState(attempt.Id, CreateTransfer(attempt, state, bytesTransferred));

        public void ReportProgress(AttemptHandle attempt, long bytesTransferred)
            => Coordinator.ReportProgress(attempt.Id, CreateTransfer(attempt, TransferStates.InProgress, bytesTransferred));

        public int CancelStaleDownloads()
            => Coordinator.CancelStaleDownloads();

        public void Advance(TimeSpan timeSpan)
            => clock.Advance(timeSpan);

        private static Transfer CreateTransfer(AttemptHandle attempt, TransferStates state, long bytesTransferred)
            => new(
                TransferDirection.Download,
                attempt.Download.Candidate.Username,
                attempt.Download.Candidate.Filename,
                1,
                state,
                attempt.Download.Candidate.File.Size,
                0,
                bytesTransferred);
    }

    private sealed record AttemptHandle(Guid Id, SongJob Song, ActiveDownload Download);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan timeSpan)
        {
            utcNow += timeSpan;
            timestamp += timeSpan.Ticks;
        }
    }
}
