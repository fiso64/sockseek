using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Transfers.Downloads.State;
using System.Threading.Channels;

namespace Tests.Core;

[TestClass]
public class StaleDownloadCoordinatorTests
{
    private static readonly TimeSpan MaxStaleTime = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task SchedulerTracksProgressWithoutAccumulatingDeadlines()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.InProgress, bytesTransferred: 0);
        using var stop = new CancellationTokenSource();
        Task scheduler = scenario.Coordinator.RunAsync(stop.Token);
        try
        {
            await scenario.Clock.WaitForDeadlineAsync(MaxStaleTime);
            for (int second = 1; second <= 4; second++)
            {
                scenario.Advance(TimeSpan.FromSeconds(1));
                scenario.ReportProgress(attempt, second * 4096);
                await scenario.Clock.WaitForDeadlineAsync(MaxStaleTime + TimeSpan.FromSeconds(second));
                Assert.AreEqual(1, scenario.Clock.ActiveTimers,
                    "Activity must replace the deadline instead of retaining a timeout for every update.");
            }
            Assert.AreEqual(1, scenario.Clock.CreatedTimers,
                "The scheduler must reuse its timer through an activity burst.");

            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = attempt.Download.Cts.Token.Register(() => cancelled.TrySetResult());
            scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
            Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);
            scenario.Advance(TimeSpan.FromMilliseconds(1));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertStaleTransferCancelled(attempt);
        }
        finally
        {
            stop.Cancel();
            await scheduler.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(0, scenario.Clock.ActiveTimers, "Stopping must release the armed deadline.");
    }

    [TestMethod]
    public void QueuedAttempt_CancelsTransferAfterMaxStaleTimeWithoutActivity()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
        Assert.IsFalse(scenario.ActiveDownloads.Contains(attempt.Download.TransferId));
    }

    [TestMethod]
    public void StateChangesBeforeMaxStaleTimeRefreshDeadline()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
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
        AssertStaleTransferCancelled(attempt);
    }

    [TestMethod]
    public void ProgressBeforeMaxStaleTimeRefreshesInProgressDeadline()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(attempt, bytesTransferred: 4096);
        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);

        scenario.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
    }

    [TestMethod]
    public void UnchangedStateAndBytesDoNotRefreshDeadline()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(attempt);
    }

    [TestMethod]
    public void QueuedAttemptUsesFreshActivityFromSameUserSibling()
    {
        using var scenario = new Scenario();
        var queued = scenario.Start("user-a", @"Music\Artist - Queued.mp3");
        var active = scenario.Start("user-a", @"Music\Artist - Active.mp3");
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
        AssertStaleTransferCancelled(queued);
        AssertStaleTransferCancelled(active);
    }

    [TestMethod]
    public void QueuedAttemptUsesFreshActivityFromCompletedSameUserSibling()
    {
        using var scenario = new Scenario();
        var queued = scenario.Start("user-a", @"Music\Artist - Queued.mp3");
        var active = scenario.Start("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Complete(active);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(0, scenario.CancelStaleDownloads(),
            "A queued attempt should inherit recent activity from a same-user sibling even after that sibling completes.");
        Assert.IsFalse(queued.Download.Cts.IsCancellationRequested);

        scenario.Advance(MaxStaleTime);
        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(queued);
    }

    [TestMethod]
    public void InProgressAttemptDoesNotUseSiblingActivity()
    {
        using var scenario = new Scenario();
        var stalled = scenario.Start("user-a", @"Music\Artist - Stalled.mp3");
        var active = scenario.Start("user-a", @"Music\Artist - Active.mp3");
        scenario.ReportState(stalled, TransferStates.InProgress, bytesTransferred: 0);
        scenario.ReportState(active, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(active, bytesTransferred: 4096);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(stalled);
        Assert.IsFalse(active.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void ActivityFromDifferentUserDoesNotProtectQueuedAttempt()
    {
        using var scenario = new Scenario();
        var queued = scenario.Start("user-a", @"Music\Artist - Queued.mp3");
        var otherUser = scenario.Start("user-b", @"Music\Artist - Other.mp3");
        scenario.ReportState(queued, TransferStates.Queued | TransferStates.Remotely, bytesTransferred: 0);
        scenario.ReportState(otherUser, TransferStates.InProgress, bytesTransferred: 0);

        scenario.Advance(MaxStaleTime - TimeSpan.FromMilliseconds(1));
        scenario.ReportProgress(otherUser, bytesTransferred: 4096);
        scenario.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreEqual(1, scenario.CancelStaleDownloads());
        AssertStaleTransferCancelled(queued);
        Assert.IsFalse(otherUser.Download.Cts.IsCancellationRequested);
    }

    [TestMethod]
    public void CompletedAttemptIsRemovedFromStaleTracking()
    {
        using var scenario = new Scenario();
        var attempt = scenario.Start("user-a", @"Music\Artist - Song.mp3");
        scenario.ReportState(attempt, TransferStates.Queued, bytesTransferred: 0);
        scenario.Complete(attempt);

        scenario.Advance(MaxStaleTime);

        Assert.AreEqual(0, scenario.CancelStaleDownloads());
        Assert.IsFalse(attempt.Download.Cts.IsCancellationRequested);
    }

    private static void AssertStaleTransferCancelled(AttemptHandle attempt)
    {
        Assert.IsFalse(attempt.Song.Cts?.IsCancellationRequested == true);
        Assert.IsTrue(attempt.Download.Cts.IsCancellationRequested);
        Assert.IsTrue(attempt.Download.IsStaleCancelled);
        Assert.AreEqual((int)MaxStaleTime.TotalMilliseconds, attempt.Download.StaleMaxStaleTimeMs);
    }

    [TestMethod]
    [TestCategory("Load")]
    [TestCategory("CI")]
    [DoNotParallelize] // Process-wide allocation counts must exclude other test classes.
    public async Task CompletedPeerHistoryDoesNotAmplifyActiveDeadlineChecks()
    {
        using var scenario = new Scenario();
        for (int index = 0; index < 10_000; index++)
        {
            var completed = scenario.Start($"peer-{index}", @"Music\Complete.mp3");
            scenario.ReportProgress(completed, 50_000);
            scenario.Complete(completed);
        }
        var active = scenario.Start("active-peer", @"Music\Active.mp3");
        scenario.ReportState(active, TransferStates.Queued, bytesTransferred: 0);
        using var stop = new CancellationTokenSource();
        long before = GC.GetTotalAllocatedBytes(true);
        Task scheduler = scenario.Coordinator.RunAsync(stop.Token);
        try
        {
            await scenario.Clock.WaitForDeadlineAsync(MaxStaleTime);
            Assert.IsFalse(active.Download.Cts.IsCancellationRequested);
        }
        finally
        {
            stop.Cancel();
            await scheduler.WaitAsync(TimeSpan.FromSeconds(5));
        }
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.IsTrue(allocated < 256 * 1024,
            $"Checking one active peer allocated {allocated:N0} bytes; completed peers must not be copied on every deadline check.");
    }

    private sealed class Scenario : IDisposable
    {
        private readonly ManualTimeProvider clock = new();
        private readonly List<AttemptHandle> attempts = [];

        public Scenario()
        {
            Coordinator = new StaleDownloadCoordinator(ActiveDownloads, clock);
        }

        public ManualTimeProvider Clock => clock;
        public ActiveDownloadTracker ActiveDownloads { get; } = new();
        public StaleDownloadCoordinator Coordinator { get; }

        public AttemptHandle Start(string username, string filename, Job? parentJob = null)
        {
            var response = new SearchResponse(username, 1, true, 100_000, 0, []);
            var file = TestHelpers.CreateSlFile(filename, size: 50_000, length: 180);
            var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
            var song = new SongJob(new SongQuery { Artist = "Artist", Title = Path.GetFileNameWithoutExtension(filename) })
            {
                Cts = new CancellationTokenSource(),
            };
            var activeDownload = new ActiveDownload(Guid.NewGuid(), song, candidate, "C:/downloads/song.mp3", new CancellationTokenSource(), parentJob);
            ActiveDownloads.TryAdd(activeDownload);
            var activityReady = new TaskCompletionSource<StaleDownloadCoordinator.PeerTransferActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Coordinator.WatchPeerTransferAsync(
                activeDownload,
                (int)MaxStaleTime.TotalMilliseconds,
                async activity =>
                {
                    activityReady.TrySetResult(activity);
                    await release.Task;
                    return true;
                });

            var attempt = new AttemptHandle(
                song,
                activeDownload,
                activityReady.Task.GetAwaiter().GetResult(),
                release,
                task);
            attempts.Add(attempt);
            return attempt;
        }

        public void ReportState(AttemptHandle attempt, TransferStates state, long bytesTransferred)
            => attempt.Activity.ReportState(CreateTransfer(attempt, state, bytesTransferred));

        public void ReportProgress(AttemptHandle attempt, long bytesTransferred)
            => attempt.Activity.ReportProgress(CreateTransfer(attempt, TransferStates.InProgress, bytesTransferred));

        public void Complete(AttemptHandle attempt)
            => attempt.Complete();

        public int CancelStaleDownloads()
            => Coordinator.CancelStaleDownloads();

        public void Advance(TimeSpan timeSpan)
            => clock.Advance(timeSpan);

        private static Transfer CreateTransfer(AttemptHandle attempt, TransferStates state, long bytesTransferred)
            => new(
                TransferDirection.Download,
                attempt.Download.Target.Username,
                attempt.Download.Target.Filename,
                1,
                state,
                attempt.Download.Target.Size ?? 0,
                0,
                bytesTransferred);

        public void Dispose()
        {
            foreach (var attempt in attempts)
                attempt.Complete();
        }
    }

    private sealed record AttemptHandle(
        SongJob Song,
        ActiveDownload Download,
        StaleDownloadCoordinator.PeerTransferActivity Activity,
        TaskCompletionSource Release,
        Task Task)
    {
        public void Complete()
        {
            Release.TrySetResult();
            Task.GetAwaiter().GetResult();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private readonly Channel<long> deadlines = Channel.CreateUnbounded<long>();
        private long timestamp;

        public int CreatedTimers { get { lock (gate) return timers.Count; } }
        public int ActiveTimers { get { lock (gate) return timers.Count(timer => timer.DueAt != null); } }
        public override DateTimeOffset GetUtcNow()
            => new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (gate) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public async Task WaitForDeadlineAsync(TimeSpan absoluteDeadline)
        {
            while (await deadlines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
                != absoluteDeadline.Ticks) { }
        }

        public void Advance(TimeSpan timeSpan)
        {
            long now = Interlocked.Add(ref timestamp, timeSpan.Ticks);
            ManualTimer[] due;
            lock (gate)
            {
                due = timers.Where(timer => timer.DueAt <= now).ToArray();
                foreach (var timer in due) timer.DueAt = null;
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public long? DueAt { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot deadlines are used.");
                lock (clock.gate)
                {
                    if (disposed) return false;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetTimestamp() + dueTime.Ticks;
                    if (DueAt is { } deadline) clock.deadlines.Writer.TryWrite(deadline);
                    return true;
                }
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock.gate) { disposed = true; DueAt = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
