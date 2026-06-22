using Soulseek;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Core;

/// <summary>
/// Multicast event bus for the download engine. Subscribe to any subset of events;
/// unsubscribed events are no-ops (null-conditional invocation).
///
/// CLI reporters and the future Server/SignalR hub both subscribe here.
/// 
/// TODO: Architectural Issue - Mutable Event Payloads
/// Currently, events pass mutable Job objects by reference.
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
    public event Action<Job>? JobStateChanged;     // job split-state fields changed
    public event Action<Job, JobActivityPhase, DateTimeOffset?>? JobActivityChanged; // job, phase, until
    public event Action<Job>? JobDiscoveryChanged; // search/browse result count changed
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

    // Fired for job-scoped log messages that should be rendered with the same prefix/color policy
    // as other job activity.
    public event Action<Job, LogLevel, string?, string>? JobMessage;
    // Fired for workflow-scoped messages in the jobs category that should not be attributed
    // to the first job that happened to discover the condition.
    public event Action<Guid, LogLevel, string?, string>? WorkflowMessage;

    // ── Search ───────────────────────────────────────────────────────────────
    // Fired once per rate-limit window when the search semaphore is exhausted.
    public event Action<DateTimeOffset>? SearchRateLimited;
    // Fired when the rate-limit window resets and searching resumes.
    public event Action? SearchResumed;

    // ── Download ─────────────────────────────────────────────────────────────
    // TODO: Once the engine is refactored to use immutable state snapshots, this event should be removed
    // and consumers should just read the snapshot from a generic JobStateChanged/TargetChanged event.
    public event Action<SongJob, FileCandidate>? DownloadStarted;
    public event Action<SongJob, long, long>? DownloadProgress;      // transferred, total
    public event Action<SongJob, TransferStates>? DownloadStateChanged;  // raw state, not string
    public event Action<SongJob, FileCandidate, string, int, int, Exception>? DownloadAttemptFailed;

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
    internal void RaiseJobStateChanged(Job job) => JobStateChanged?.Invoke(job);
    internal void RaiseJobActivityChanged(Job job, JobActivityPhase phase, DateTimeOffset? untilUtc)
        => JobActivityChanged?.Invoke(job, phase, untilUtc);
    internal void RaiseJobDiscoveryChanged(Job job) => JobDiscoveryChanged?.Invoke(job);
    internal void RaiseJobExecutionCompleted(Job job) => JobExecutionCompleted?.Invoke(job);
    internal void RaiseJobResultCreated(ExtractJob job, Job result) => JobResultCreated?.Invoke(job, result);
    internal void RaiseEngineCompleted(JobList queue) => EngineCompleted?.Invoke(queue);


    internal void RaiseJobStatus(Job job, string status) => JobStatus?.Invoke(job, status);
    internal void RaiseJobMessage(Job job, LogLevel level, string? source, string message) => JobMessage?.Invoke(job, level, source, message);
    internal void RaiseWorkflowMessage(Guid workflowId, LogLevel level, string? source, string message) => WorkflowMessage?.Invoke(workflowId, level, source, message);
    internal void RaiseSearchRateLimited(DateTimeOffset resetsAt) => SearchRateLimited?.Invoke(resetsAt);
    internal void RaiseSearchResumed() => SearchResumed?.Invoke();

    internal void RaiseDownloadStarted(SongJob song, FileCandidate c) => DownloadStarted?.Invoke(song, c);
    internal void RaiseDownloadProgress(SongJob song, long xfer, long total) => DownloadProgress?.Invoke(song, xfer, total);
    internal void RaiseDownloadStateChanged(SongJob song, TransferStates s) => DownloadStateChanged?.Invoke(song, s);
    internal void RaiseDownloadAttemptFailed(SongJob song, FileCandidate c, string outputPath, int attempt, int maxAttempts, Exception ex)
        => DownloadAttemptFailed?.Invoke(song, c, outputPath, attempt, maxAttempts, ex);

    internal void RaiseTrackBatchResolved(Job job, IReadOnlyList<SongJob> pending, IReadOnlyList<SongJob> existing, IReadOnlyList<SongJob> notFound)
        => TrackBatchResolved?.Invoke(job, pending, existing, notFound);
    internal void RaiseTrackListReady(IEnumerable<SongJob> songs) => TrackListReady?.Invoke(songs);
    internal void RaiseListProgress(JobList list, int dl, int fl, int total) => ListProgress?.Invoke(list, dl, fl, total);
    internal void RaiseOverallProgress(int dl, int fl, int total) => OverallProgress?.Invoke(dl, fl, total);
}
