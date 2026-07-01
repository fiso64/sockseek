using Soulseek;
using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

internal sealed class StaleDownloadCoordinator
{
    private readonly IDownloadRegistry registry;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Attempt> attempts = new();
    private TaskCompletionSource deadlinesChanged = NewSignal();

    public StaleDownloadCoordinator(IDownloadRegistry registry, TimeProvider? timeProvider = null)
    {
        this.registry = registry;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid Register(ActiveDownload download, int maxStaleTimeMs)
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

    public void ReportState(Guid attemptId, Transfer transfer)
        => ReportActivity(attemptId, transfer);

    public void ReportProgress(Guid attemptId, Transfer transfer)
        => ReportActivity(attemptId, transfer);

    public void Complete(Guid attemptId)
    {
        bool removed;
        lock (gate)
            removed = attempts.Remove(attemptId);

        if (removed)
            SignalDeadlinesChanged();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextStaleCheck();

                if (delay == null)
                {
                    await WaitForDeadlinesChangedAsync(cancellationToken);
                    continue;
                }

                if (delay <= TimeSpan.Zero)
                {
                    CancelStaleDownloads();
                    continue;
                }

                await WaitForDeadlinesChangedOrDelayAsync(delay.Value, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SockseekLog.Jobs.Error(ex, "Error in stale download scheduler");
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
            SockseekLog.Jobs.Info(
                $"[{download.Song.DisplayId}] SongJob: cancelling stale download after {attempt.MaxStaleTimeMs}ms without activity: " +
                $"{download.Song} ({download.Candidate.Username}\\{download.Candidate.Filename})");
            try { download.Song.Cts?.Cancel(); } catch { }
            try { download.Cts.Cancel(); } catch { }
            registry.Downloads.TryRemove(download.Candidate.Filename, out _);
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
                attempt.LastOwnActivityTimestamp = timeProvider.GetTimestamp();
        }

        if (changed)
            SignalDeadlinesChanged();
    }

    private TimeSpan? GetDelayUntilNextStaleCheck()
    {
        var now = timeProvider.GetTimestamp();
        TimeSpan? nextDelay = null;

        lock (gate)
        {
            if (attempts.Count == 0)
                return null;

            var latestActivityByUser = GetLatestActivityByUser();
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
        var latestActivityByUser = GetLatestActivityByUser();
        foreach (var attempt in attempts.Values)
        {
            var referenceTimestamp = GetReferenceTimestamp(attempt, latestActivityByUser);
            if (timeProvider.GetElapsedTime(referenceTimestamp, now).TotalMilliseconds < attempt.MaxStaleTimeMs)
                continue;

            staleAttempts.Add(attempt);
        }

        return staleAttempts;
    }

    private Dictionary<string, long> GetLatestActivityByUser()
    {
        var latestActivityByUser = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var attempt in attempts.Values)
        {
            var username = attempt.Download.Candidate.Username;
            if (!latestActivityByUser.TryGetValue(username, out var latestActivity)
                || attempt.LastOwnActivityTimestamp > latestActivity)
            {
                latestActivityByUser[username] = attempt.LastOwnActivityTimestamp;
            }
        }

        return latestActivityByUser;
    }

    private static long GetReferenceTimestamp(
        Attempt attempt,
        IReadOnlyDictionary<string, long> latestActivityByUser)
    {
        var referenceTimestamp = attempt.LastOwnActivityTimestamp;
        // Queued siblings from the same user are protected by any fresh same-user activity;
        // an in-progress transfer must make progress on its own.
        if (!IsInProgress(attempt.State)
            && latestActivityByUser.TryGetValue(attempt.Download.Candidate.Username, out var latestUserActivity)
            && latestUserActivity > referenceTimestamp)
        {
            referenceTimestamp = latestUserActivity;
        }

        return referenceTimestamp;
    }

    private async Task WaitForDeadlinesChangedOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var signal = GetDeadlinesChangedTask();
        var signalTask = signal.WaitAsync(cancellationToken);
        var delayTask = Task.Delay(delay, timeProvider, cancellationToken);
        await await Task.WhenAny(signalTask, delayTask);
    }

    private async Task WaitForDeadlinesChangedAsync(CancellationToken cancellationToken)
    {
        var signal = GetDeadlinesChangedTask();
        await signal.WaitAsync(cancellationToken);
    }

    private Task GetDeadlinesChangedTask()
    {
        lock (gate)
            return deadlinesChanged.Task;
    }

    private void SignalDeadlinesChanged()
    {
        TaskCompletionSource previousSignal;
        lock (gate)
        {
            previousSignal = deadlinesChanged;
            deadlinesChanged = NewSignal();
        }

        previousSignal.TrySetResult();
    }

    private static bool IsInProgress(TransferStates? state)
        => state.HasValue && state.Value.HasFlag(TransferStates.InProgress);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
