using Sockseek.Core.Models;
using Soulseek;
using Sockseek.Core.Transfers.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core.Services;

/// <summary>
/// Watches each Soulseek peer-transfer call while it runs. It ends a transfer
/// that has gone stale, and asks the peer where each remotely queued download
/// stands, which is both the position to show and, for a hand-picked file,
/// the confirmation that lets it keep waiting.
/// </summary>
internal sealed class PeerTransferMonitor
{
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PeerTransferMonitor> logger;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Attempt> attempts = new();
    // Keep recent peer activity after an attempt completes, so queued same-user
    // siblings don't become stale the moment the active transfer leaves tracking.
    private readonly Dictionary<string, PeerActivity> peers = new(StringComparer.Ordinal);
    // Keep one pending wakeup, even if activity arrives between inspecting the
    // deadlines and waiting. Bursts update attempt facts without allocating a
    // completion source or leaving an old timeout armed for every update.
    private readonly SemaphoreSlim deadlinesChanged = new(0, 1);
    // Nicotine+ asks for the position of every queued download at this rate.
    internal static readonly TimeSpan QueuePositionInterval = TimeSpan.FromMinutes(5);

    public PeerTransferMonitor(
        ActiveDownloadTracker activeDownloads,
        TimeProvider? timeProvider = null,
        ILogger<PeerTransferMonitor>? logger = null)
    {
        this.activeDownloads = activeDownloads;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<PeerTransferMonitor>.Instance;
    }

    // Arms stale detection only for the Soulseek peer-transfer call. Search,
    // setup, fallback, organization, and on-complete work must stay outside this scope.
    internal async Task<T> WatchPeerTransferAsync<T>(
        ActiveDownload download,
        int maxStaleTimeMs,
        Func<PeerTransferActivity, Task<T>> transfer,
        ISoulseekClient? client = null,
        Action<int>? queuePositionChanged = null)
    {
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(transfer);

        var attemptId = BeginPeerTransfer(download, maxStaleTimeMs, client, queuePositionChanged);
        try
        {
            return await transfer(new PeerTransferActivity(this, attemptId));
        }
        finally
        {
            CompletePeerTransfer(attemptId);
        }
    }

    private Guid BeginPeerTransfer(ActiveDownload download, int maxStaleTimeMs, ISoulseekClient? client,
        Action<int>? queuePositionChanged)
    {
        Attempt attempt;
        lock (gate)
        {
            string username = download.Target.Username;
            if (!peers.TryGetValue(username, out var peer)) peers.Add(username, peer = new PeerActivity());
            peer.AttemptCount++;
            peer.LastActivityTimestamp = timeProvider.GetTimestamp();
            attempt = new Attempt(Guid.NewGuid(), download, Math.Max(0, maxStaleTimeMs), peer, client, queuePositionChanged);
            attempts.Add(attempt.Id, attempt);
        }

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
            removed = RemoveAttemptLocked(attemptId);

        if (removed)
            SignalDeadlinesChanged();
    }

    private bool RemoveAttemptLocked(Guid attemptId)
    {
        if (!attempts.Remove(attemptId, out var removed)) return false;
        // Preserve completed sibling activity only while this peer has queued work.
        if (--removed.Peer.AttemptCount == 0)
            peers.Remove(removed.Download.Target.Username);
        return true;
    }

    // The first pause before asking a peer again, as in Nicotine+. Pauses double
    // up to the limit, so a peer that never recovers costs little.
    internal static readonly TimeSpan PeerRetryInterval = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan PeerRetryLimit = TimeSpan.FromHours(1);
    private readonly Dictionary<string, PresencePoll> presencePolls = new(StringComparer.Ordinal);

    private sealed class PresencePoll
    {
        public readonly CancellationTokenSource Stop = new();
        public Task Online = Task.CompletedTask;
        public int Waiters;
    }

    /// <summary>
    /// Waits until a peer that could not serve a download is worth asking
    /// again: until it is online, or for a pause that grows with each wait.
    /// </summary>
    public async Task WaitForPeerAsync(ISoulseekClient client, string username, bool untilOnline, int earlierWaits,
        CancellationToken cancellationToken)
    {
        // A peer reported online that still cannot be reached must not be asked
        // in a tight loop: every wait after the first begins with a pause.
        if (!untilOnline || earlierWaits > 0)
        {
            var pause = PeerRetryInterval * Math.Pow(2, Math.Min(untilOnline ? earlierWaits - 1 : earlierWaits, 10));
            await Task.Delay(pause < PeerRetryLimit ? pause : PeerRetryLimit, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            if (!untilOnline) return;
        }

        // One poll per peer, however many of their files are waiting.
        PresencePoll poll;
        lock (presencePolls)
        {
            if (!presencePolls.TryGetValue(username, out poll!))
            {
                presencePolls.Add(username, poll = new PresencePoll());
                poll.Online = PollUntilOnlineAsync(client, username, poll);
            }
            poll.Waiters++;
        }
        try
        {
            await poll.Online.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (presencePolls)
                if (--poll.Waiters == 0) poll.Stop.Cancel();
        }
    }

    private async Task PollUntilOnlineAsync(ISoulseekClient client, string username, PresencePoll poll)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                try
                {
                    var status = await client.GetUserStatusAsync(username, poll.Stop.Token).ConfigureAwait(false);
                    if (status.Presence != UserPresence.Offline) return;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Not logged in, or the server did not answer: ask again later.
                }
                await Task.Delay(PeerRetryInterval, timeProvider, poll.Stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (poll.Stop.IsCancellationRequested)
        {
            // Nobody is waiting for this peer any more.
        }
        finally
        {
            lock (presencePolls)
                if (presencePolls.GetValueOrDefault(username) == poll) presencePolls.Remove(username);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The scheduler owns one reschedulable timer. Racing Task.Delay against
        // activity leaves the losing delays alive until their original deadlines.
        using var timer = timeProvider.CreateTimer(
            static state => ((PeerTransferMonitor)state!).SignalDeadlinesChanged(),
            this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextStaleCheck();

                if (delay <= TimeSpan.Zero)
                {
                    CancelStaleDownloads();
                    RequestDueQueuePositions();
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
        List<Attempt> expired = [];
        List<Attempt> checkQueue = [];
        lock (gate)
        {
            foreach (var attempt in attempts.Values)
            {
                if (AwaitsQueueConfirmation(attempt) || !IsStale(attempt, now)) continue;
                // A hand-picked file can wait in its peer’s queue. Automated
                // jobs need the deadline to release the slot and try another candidate.
                if (CanWaitInQueue(attempt))
                {
                    attempt.QueueRequestPending = true;
                    checkQueue.Add(attempt);
                }
                else expired.Add(attempt);
            }
            foreach (var attempt in expired) RemoveAttemptLocked(attempt.Id);
        }

        foreach (var attempt in expired) Cancel(attempt);
        foreach (var attempt in checkQueue) _ = RequestQueuePositionAsync(attempt);
        return expired.Count;
    }

    // Every remotely queued download reports its place in the peer's queue:
    // once on entering it, then at Nicotine+'s rate.
    internal void RequestDueQueuePositions()
    {
        var now = timeProvider.GetTimestamp();
        List<Attempt> due = [];
        lock (gate)
        {
            foreach (var attempt in attempts.Values)
            {
                if (QueuePositionDelay(attempt, now) is not { } delay || delay > TimeSpan.Zero) continue;
                attempt.QueueRequestPending = true;
                due.Add(attempt);
            }
        }
        foreach (var attempt in due) _ = RequestQueuePositionAsync(attempt);
    }

    /// <summary>Time until this attempt next asks for its queue position, if it will.</summary>
    private TimeSpan? QueuePositionDelay(Attempt attempt, long now)
    {
        if (attempt.QueueRequestPending || attempt.Client == null || attempt.QueuePositionChanged == null
            || !IsQueuedRemotely(attempt)) return null;
        return attempt.LastQueueRequestTimestamp is { } last
            ? QueuePositionInterval - timeProvider.GetElapsedTime(last, now) : TimeSpan.Zero;
    }

    private static bool CanWaitInQueue(Attempt attempt)
        => attempt.Download.Owner.IsHandPicked && attempt.Client != null && IsQueuedRemotely(attempt);

    // A hand-picked file is not stale while its peer is being asked.
    private static bool AwaitsQueueConfirmation(Attempt attempt)
        => attempt.QueueRequestPending && CanWaitInQueue(attempt);

    private static bool IsQueuedRemotely(Attempt attempt)
    {
        const TransferStates queued = TransferStates.Queued | TransferStates.Remotely;
        return (attempt.State & queued) == queued;
    }

    // One request serves both purposes: the answer is the position to show,
    // and for a hand-picked file it also confirms that the peer still queues it.
    private async Task RequestQueuePositionAsync(Attempt attempt)
    {
        int? position = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Download.Cts.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(attempt.MaxStaleTimeMs));
            var target = attempt.Download.Target;
            int place = await attempt.Client!.GetDownloadPlaceInQueueAsync(target.Username, target.Filename, timeout.Token)
                .ConfigureAwait(false);
            if (place >= 0) position = place;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the queue position for transfer {TransferId}", attempt.Download.TransferId);
        }

        bool expired = false;
        bool report = false;
        lock (gate)
        {
            if (!attempts.ContainsKey(attempt.Id)) return;
            attempt.QueueRequestPending = false;
            var now = timeProvider.GetTimestamp();
            attempt.LastQueueRequestTimestamp = now;
            if (IsQueuedRemotely(attempt))
            {
                report = position.HasValue;
                // Only a hand-picked file's wait depends on the peer's answer.
                if (CanWaitInQueue(attempt))
                {
                    if (position.HasValue) attempt.LastOwnActivityTimestamp = now;
                    else if (IsStale(attempt, now) && !attempt.Download.Cts.IsCancellationRequested)
                        expired = RemoveAttemptLocked(attempt.Id);
                }
            }
        }
        if (expired) Cancel(attempt);
        else if (report) attempt.QueuePositionChanged?.Invoke(position!.Value);
        SignalDeadlinesChanged();
    }

    private void Cancel(Attempt attempt)
    {
        var download = attempt.Download;
        download.MarkStaleCancelled(attempt.MaxStaleTimeMs);
        try { download.Cts.Cancel(); } catch (Exception ex) { logger.LogDebug(ex, "Stale transfer {TransferId} cancellation failed", download.TransferId); }
        activeDownloads.TryRemove(download.TransferId, out _);
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
            // Each wait in the peer's queue asks for its position afresh.
            if (stateChanged && !IsQueuedRemotely(attempt)) attempt.LastQueueRequestTimestamp = null;

            if (changed)
            {
                var now = timeProvider.GetTimestamp();
                attempt.LastOwnActivityTimestamp = now;
                attempt.Peer.LastActivityTimestamp = now;
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

            foreach (var attempt in attempts.Values)
            {
                if (QueuePositionDelay(attempt, now) is { } positionDelay && (nextDelay == null || positionDelay < nextDelay))
                    nextDelay = positionDelay;
                if (AwaitsQueueConfirmation(attempt)) continue;
                var referenceTimestamp = GetReferenceTimestamp(attempt);
                var elapsed = timeProvider.GetElapsedTime(referenceTimestamp, now);
                var remaining = TimeSpan.FromMilliseconds(attempt.MaxStaleTimeMs) - elapsed;

                if (nextDelay == null || remaining < nextDelay)
                    nextDelay = remaining;
            }
        }

        return nextDelay < TimeSpan.Zero ? TimeSpan.Zero : nextDelay;
    }

    private bool IsStale(Attempt attempt, long now)
        => timeProvider.GetElapsedTime(GetReferenceTimestamp(attempt), now).TotalMilliseconds >= attempt.MaxStaleTimeMs;

    private static long GetReferenceTimestamp(Attempt attempt)
        // Queued siblings share activity; an active transfer must make its own progress.
        => IsInProgress(attempt.State) ? attempt.LastOwnActivityTimestamp
            : Math.Max(attempt.LastOwnActivityTimestamp, attempt.Peer.LastActivityTimestamp);

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
        private readonly PeerTransferMonitor coordinator;
        private readonly Guid attemptId;

        internal PeerTransferActivity(PeerTransferMonitor coordinator, Guid attemptId)
        {
            this.coordinator = coordinator;
            this.attemptId = attemptId;
        }

        public void ReportState(Transfer transfer)
            => coordinator.ReportState(attemptId, transfer);

        public void ReportProgress(Transfer transfer)
            => coordinator.ReportProgress(attemptId, transfer);
    }

    private sealed class PeerActivity
    {
        public int AttemptCount { get; set; }
        public long LastActivityTimestamp { get; set; }
    }

    private sealed class Attempt
    {
        public Attempt(Guid id, ActiveDownload download, int maxStaleTimeMs, PeerActivity peer, ISoulseekClient? client,
            Action<int>? queuePositionChanged)
        {
            QueuePositionChanged = queuePositionChanged;
            Id = id;
            Download = download;
            MaxStaleTimeMs = maxStaleTimeMs;
            Peer = peer;
            Client = client;
            LastOwnActivityTimestamp = peer.LastActivityTimestamp;
        }

        public bool QueueRequestPending { get; set; }
        public long? LastQueueRequestTimestamp { get; set; }
        public Action<int>? QueuePositionChanged { get; }
        public ISoulseekClient? Client { get; }
        public PeerActivity Peer { get; }
        public Guid Id { get; }
        public ActiveDownload Download { get; }
        public int MaxStaleTimeMs { get; }
        public long LastOwnActivityTimestamp { get; set; }
        public TransferStates? State { get; set; }
        public long? BytesTransferred { get; set; }
    }
}

public sealed class StaleDownloadException : TimeoutException
{
    private const string MessagePrefix = "Download attempt became stale after ";

    public StaleDownloadException(PeerFileTarget target, int maxStaleTimeMs)
        : base(
            $"{MessagePrefix}{maxStaleTimeMs}ms without peer transfer activity: " +
            $"{target.Username}\\{PeerIdentityValidator.ToDisplayText(target.Filename)}")
    {
        Target = target;
        MaxStaleTimeMs = maxStaleTimeMs;
    }

    public StaleDownloadException(FileCandidate candidate, int maxStaleTimeMs)
        : this(candidate.Target, maxStaleTimeMs)
    {
    }

    public PeerFileTarget Target { get; }
    public int MaxStaleTimeMs { get; }

    public static bool IsStaleFailureMessage(string? message)
        => message?.StartsWith(MessagePrefix, StringComparison.Ordinal) == true;
}
