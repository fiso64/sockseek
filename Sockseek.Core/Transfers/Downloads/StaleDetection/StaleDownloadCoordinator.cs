using Soulseek;
using Sockseek.Core.Transfers.Downloads.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core.Services;

internal sealed class StaleDownloadCoordinator
{
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<StaleDownloadCoordinator> logger;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Attempt> attempts = new();
    // Keep recent peer activity after an attempt completes, so queued same-user
    // siblings don't become stale the moment the active transfer leaves tracking.
    private readonly Dictionary<string, long> latestActivityByUser = new(StringComparer.OrdinalIgnoreCase);
    // Keep one pending wakeup, even if activity arrives between inspecting the
    // deadlines and waiting. Bursts update attempt facts without allocating a
    // completion source or leaving an old timeout armed for every update.
    private readonly SemaphoreSlim deadlinesChanged = new(0, 1);

    public StaleDownloadCoordinator(
        ActiveDownloadTracker activeDownloads,
        TimeProvider? timeProvider = null,
        ILogger<StaleDownloadCoordinator>? logger = null)
    {
        this.activeDownloads = activeDownloads;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<StaleDownloadCoordinator>.Instance;
    }

    // Arms stale detection only for the Soulseek peer-transfer call. Search,
    // setup, fallback, organization, and on-complete work must stay outside this scope.
    internal async Task<T> WatchPeerTransferAsync<T>(
        ActiveDownload download,
        int maxStaleTimeMs,
        Func<PeerTransferActivity, Task<T>> transfer)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(transfer);

        var attemptId = BeginPeerTransfer(download, maxStaleTimeMs);
        try
        {
            return await transfer(new PeerTransferActivity(this, attemptId));
        }
        finally
        {
            CompletePeerTransfer(attemptId);
        }
    }

    private Guid BeginPeerTransfer(ActiveDownload download, int maxStaleTimeMs)
    {
        var attempt = new Attempt(
            Guid.NewGuid(),
            download,
            Math.Max(0, maxStaleTimeMs),
            timeProvider.GetTimestamp());

        lock (gate)
            attempts[attempt.Id] = attempt;

        SignalDeadlinesChanged();
        return attempt.Id;
    }

    private void ReportState(Guid attemptId, Transfer transfer)
        => ReportActivity(attemptId, transfer);

    private void ReportProgress(Guid attemptId, Transfer transfer)
        => ReportActivity(attemptId, transfer);

    private void CompletePeerTransfer(Guid attemptId)
    {
        bool removed;
        lock (gate)
            removed = attempts.Remove(attemptId);

        if (removed)
            SignalDeadlinesChanged();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The scheduler owns one reschedulable timer. Racing Task.Delay against
        // activity leaves the losing delays alive until their original deadlines.
        using var timer = timeProvider.CreateTimer(
            static state => ((StaleDownloadCoordinator)state!).SignalDeadlinesChanged(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextStaleCheck();

                if (delay <= TimeSpan.Zero)
                {
                    CancelStaleDownloads();
                    continue;
                }

                timer.Change(delay ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                await deadlinesChanged.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DownloadLogMessages.ComponentFailed(
                    logger,
                    ex,
                    "stale-download-scheduler",
                    null);
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            }
        }
    }

    public int CancelStaleDownloads()
    {
        var now = timeProvider.GetTimestamp();
        List<Attempt> staleAttempts;

        lock (gate)
        {
            staleAttempts = GetStaleAttempts(now).ToList();

            foreach (var attempt in staleAttempts)
                attempts.Remove(attempt.Id);
        }

        if (staleAttempts.Count > 0)
            SignalDeadlinesChanged();

        foreach (var attempt in staleAttempts)
        {
            var download = attempt.Download;
            download.MarkStaleCancelled(attempt.MaxStaleTimeMs);
            try { download.Cts.Cancel(); } catch { }
            activeDownloads.TryRemove(download.TransferId, out _);
        }

        return staleAttempts.Count;
    }

    private void ReportActivity(Guid attemptId, Transfer transfer)
    {
        Attempt? attempt;
        bool changed;

        lock (gate)
        {
            if (!attempts.TryGetValue(attemptId, out attempt))
                return;

            attempt.Download.Transfer = transfer;

            var stateChanged = attempt.State != transfer.State;
            var bytesChanged = attempt.BytesTransferred != transfer.BytesTransferred;
            changed = stateChanged || bytesChanged;

            attempt.State = transfer.State;
            attempt.BytesTransferred = transfer.BytesTransferred;

            if (changed)
            {
                var now = timeProvider.GetTimestamp();
                attempt.LastOwnActivityTimestamp = now;
                latestActivityByUser[attempt.Download.Target.Username] = now;
            }
        }

        if (changed)
            SignalDeadlinesChanged();
    }

    private TimeSpan? GetDelayUntilNextStaleCheck()
    {
        TimeSpan? nextDelay = null;

        lock (gate)
        {
            // This scan includes all facts published before taking the gate.
            // Consume their pending notification; later activity retains a wake.
            deadlinesChanged.Wait(0);
            var now = timeProvider.GetTimestamp();
            if (attempts.Count == 0)
                return null;

            var latestActivityByUser = GetLatestActivePeerActivity();
            foreach (var attempt in attempts.Values)
            {
                var referenceTimestamp = GetReferenceTimestamp(attempt, latestActivityByUser);
                var elapsed = timeProvider.GetElapsedTime(referenceTimestamp, now);
                var remaining = TimeSpan.FromMilliseconds(attempt.MaxStaleTimeMs) - elapsed;

                if (nextDelay == null || remaining < nextDelay)
                    nextDelay = remaining;
            }
        }

        return nextDelay < TimeSpan.Zero ? TimeSpan.Zero : nextDelay;
    }

    private List<Attempt> GetStaleAttempts(long now)
    {
        var staleAttempts = new List<Attempt>();
        var latestActivityByUser = GetLatestActivePeerActivity();
        foreach (var attempt in attempts.Values)
        {
            var referenceTimestamp = GetReferenceTimestamp(attempt, latestActivityByUser);
            if (timeProvider.GetElapsedTime(referenceTimestamp, now).TotalMilliseconds < attempt.MaxStaleTimeMs)
                continue;

            staleAttempts.Add(attempt);
        }

        return staleAttempts;
    }

    private Dictionary<string, long> GetLatestActivePeerActivity()
    {
        // Only tracked peers participate in deadline checks. Copying the entire
        // recent-activity history makes each check grow with every peer the
        // long-running engine has ever downloaded from.
        var latest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var attempt in attempts.Values)
        {
            var username = attempt.Download.Target.Username;
            long timestamp = attempt.LastOwnActivityTimestamp;
            if (latestActivityByUser.TryGetValue(username, out long reported) && reported > timestamp)
                timestamp = reported;
            if (!latest.TryGetValue(username, out var latestActivity)
                || timestamp > latestActivity)
            {
                latest[username] = timestamp;
            }
        }

        return latest;
    }

    private static long GetReferenceTimestamp(
        Attempt attempt,
        IReadOnlyDictionary<string, long> latestActivityByUser)
    {
        var referenceTimestamp = attempt.LastOwnActivityTimestamp;
        // Queued siblings from the same user are protected by any fresh same-user activity;
        // an in-progress transfer must make progress on its own.
        if (!IsInProgress(attempt.State)
            && latestActivityByUser.TryGetValue(attempt.Download.Target.Username, out var latestUserActivity)
            && latestUserActivity > referenceTimestamp)
        {
            referenceTimestamp = latestUserActivity;
        }

        return referenceTimestamp;
    }

    private void SignalDeadlinesChanged()
    {
        lock (gate)
        {
            if (deadlinesChanged.CurrentCount == 0)
                deadlinesChanged.Release();
        }
    }

    private static bool IsInProgress(TransferStates? state)
        => state is { } value && (value & TransferStates.InProgress) == TransferStates.InProgress;

    internal readonly struct PeerTransferActivity
    {
        private readonly StaleDownloadCoordinator coordinator;
        private readonly Guid attemptId;

        internal PeerTransferActivity(StaleDownloadCoordinator coordinator, Guid attemptId)
        {
            this.coordinator = coordinator;
            this.attemptId = attemptId;
        }

        public void ReportState(Transfer transfer)
            => coordinator.ReportState(attemptId, transfer);

        public void ReportProgress(Transfer transfer)
            => coordinator.ReportProgress(attemptId, transfer);
    }

    private sealed class Attempt
    {
        public Attempt(Guid id, ActiveDownload download, int maxStaleTimeMs, long registeredAtTimestamp)
        {
            Id = id;
            Download = download;
            MaxStaleTimeMs = maxStaleTimeMs;
            LastOwnActivityTimestamp = registeredAtTimestamp;
        }

        public Guid Id { get; }
        public ActiveDownload Download { get; }
        public int MaxStaleTimeMs { get; }
        public long LastOwnActivityTimestamp { get; set; }
        public TransferStates? State { get; set; }
        public long? BytesTransferred { get; set; }
    }
}
