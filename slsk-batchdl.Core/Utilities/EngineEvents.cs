using Soulseek;
using Sldl.Core.Jobs;
using Sldl.Core.Models;

namespace Sldl.Core;

/// <summary>
/// Multicast event bus for the download engine. Subscribe to any subset of events;
/// unsubscribed events are no-ops (null-conditional invocation).
///
/// CLI reporters and the future Server/SignalR hub both subscribe here.
/// 
/// TODO: Architectural Issue - Mutable Event Payloads
/// Currently, events pass mutable Job objects by reference (e.g., Action<Job, JobState>).
/// This causes race conditions for async consumers (like the local CLI progress reporter), 
/// because the Job's properties (like ResolvedTarget) can mutate before the consumer processes the event.
/// The Server/Remote CLI mode mitigates this by immediately projecting the Job into an immutable DTO 
/// on the publisher thread, effectively taking a snapshot. In the future, the core EngineEvents 
/// should be refactored to pass immutable state snapshots rather than live Job references.
/// </summary>
public class EngineEvents
{
    // ── Graph / lifecycle ───────────────────────────────────────────────────
    public event Action<Job, Job?>? JobRegistered;       // job, parent (if any)
    public event Action<Job, JobState>? JobStateChanged;     // job, new state
    // Fired when a job's own execution path is finished.
    // For ExtractJob this is raised immediately after the result job has been produced,
    // not after any optional automatic processing of that result.
    public event Action<Job>? JobExecutionCompleted;
    // Fired when an ExtractJob produces its semantic output job (possibly after upgrade
    // transforms). This happens at the same moment the ExtractJob itself completes.
    public event Action<ExtractJob, Job>? JobResultCreated;    // extract job, extracted/upgraded result
    public event Action<JobList>? EngineCompleted;

    // Fired for transient, human-readable status updates that don't warrant a formal state change
    // (e.g. "deleting files", "moving").
    public event Action<Job, string>? JobStatus;

    // ── Search ───────────────────────────────────────────────────────────────
    // Fired once per rate-limit window when the search semaphore is exhausted.
    public event Action? SearchRateLimited;

    // ── Song-level ───────────────────────────────────────────────────────────
    public event Action<SongJob>? OnCompleteStart;
    public event Action<SongJob>? OnCompleteEnd;

    // ── Download ─────────────────────────────────────────────────────────────
    // TODO: Once the engine is refactored to use immutable state snapshots, this event should be removed
    // and consumers should just read the snapshot from a generic JobStateChanged/TargetChanged event.
    public event Action<SongJob, FileCandidate>? DownloadStarted;
    public event Action<SongJob, long, long>? DownloadProgress;      // transferred, total
    public event Action<SongJob, TransferStates>? DownloadStateChanged;  // raw state, not string

    // ── List / overall ───────────────────────────────────────────────────────
    // Fired when a batch of songs has been resolved into:
    // - tracks still pending download
    // - tracks already satisfied by skip/existing logic
    // - tracks skipped because they were not found in a prior run
    // The owner job carries any rendering context (for example PrintOption).
    public event Action<Job, IReadOnlyList<SongJob>, IReadOnlyList<SongJob>, IReadOnlyList<SongJob>>? TrackBatchResolved;
    public event Action<IEnumerable<SongJob>>? TrackListReady;
    public event Action<JobList, int, int, int>? ListProgress;    // list, done, failed, total
    public event Action<int, int, int>? OverallProgress; // done, failed, total

    // ── Internal raise methods (same assembly only) ──────────────────────────
    internal void RaiseJobRegistered(Job job, Job? parent) => JobRegistered?.Invoke(job, parent);
    internal void RaiseJobStateChanged(Job job, JobState state) => JobStateChanged?.Invoke(job, state);
    internal void RaiseJobExecutionCompleted(Job job) => JobExecutionCompleted?.Invoke(job);
    internal void RaiseJobResultCreated(ExtractJob job, Job result) => JobResultCreated?.Invoke(job, result);
    internal void RaiseEngineCompleted(JobList queue) => EngineCompleted?.Invoke(queue);


    internal void RaiseJobStatus(Job job, string status) => JobStatus?.Invoke(job, status);
    internal void RaiseSearchRateLimited() => SearchRateLimited?.Invoke();

    internal void RaiseOnCompleteStart(SongJob song) => OnCompleteStart?.Invoke(song);
    internal void RaiseOnCompleteEnd(SongJob song) => OnCompleteEnd?.Invoke(song);

    internal void RaiseDownloadStarted(SongJob song, FileCandidate c) => DownloadStarted?.Invoke(song, c);
    internal void RaiseDownloadProgress(SongJob song, long xfer, long total) => DownloadProgress?.Invoke(song, xfer, total);
    internal void RaiseDownloadStateChanged(SongJob song, TransferStates s) => DownloadStateChanged?.Invoke(song, s);

    internal void RaiseTrackBatchResolved(Job job, IReadOnlyList<SongJob> pending, IReadOnlyList<SongJob> existing, IReadOnlyList<SongJob> notFound)
        => TrackBatchResolved?.Invoke(job, pending, existing, notFound);
    internal void RaiseTrackListReady(IEnumerable<SongJob> songs) => TrackListReady?.Invoke(songs);
    internal void RaiseListProgress(JobList list, int dl, int fl, int total) => ListProgress?.Invoke(list, dl, fl, total);
    internal void RaiseOverallProgress(int dl, int fl, int total) => OverallProgress?.Invoke(dl, fl, total);
}
