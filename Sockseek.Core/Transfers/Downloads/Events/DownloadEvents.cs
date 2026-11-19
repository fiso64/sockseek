using System.Runtime.CompilerServices;
using Soulseek;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core;

/// <summary>
/// Multicast event bus for download workflows. Public subscribers receive immutable
/// Sockseek-owned change records captured synchronously by the publisher.
/// </summary>
public class DownloadEvents
{
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DownloadEvents> logger;
    // Each job/transfer owns its clocks and retirement metadata together. In
    // particular, progress never rewrites shared workflow-ID maps or acquires
    // dictionary write locks merely to rediscover an existing owner.
    private readonly ConcurrentDictionary<Guid, JobEventState> jobs = new();
    private readonly ConcurrentDictionary<Guid, TransferFacts> transferFacts = new();

    // ── Graph / lifecycle ───────────────────────────────────────────────────
    public event Action<JobRegisteredChange>? JobRegistered;
    public event Action<JobStateChangedChange>? JobStateChanged;
    public event Action<JobStructureChangedChange>? JobStructureChanged;
    public event Action<JobActivityChangedChange>? JobActivityChanged;
    /// <summary>
    /// Fired when a download job's discovery snapshot changes, such as raw search result
    /// or locked-file counts. Generic search-service events live in <see cref="SearchEvents"/>.
    /// </summary>
    public event Action<JobDiscoveryChangedChange>? JobDiscoveryChanged;
    // Fired when a job's own execution path is finished.
    // For ExtractJob this is raised immediately after the result job has been produced,
    // not after any optional automatic processing of that result.
    public event Action<JobExecutionCompletedChange>? JobExecutionCompleted;
    // Fired when an ExtractJob produces its semantic output job (possibly after upgrade
    // transforms). This happens at the same moment the ExtractJob itself completes.
    public event Action<JobResultCreatedChange>? JobResultCreated;
    public event Action<EngineCompletedChange>? EngineCompleted;
    public event Action<WorkflowRetiredChange>? WorkflowRetired;

    // Fired for transient, human-readable status updates that don't warrant a formal state change
    // (e.g. "deleting files", "moving").
    public event Action<JobStatusChange>? JobStatus;

    // Fired for job-scoped log messages that should be rendered with the same prefix/color policy
    // as other job activity.
    public event Action<JobMessageChange>? JobMessage;
    // Fired for workflow-scoped messages in the jobs category that should not be attributed
    // to the first job that happened to discover the condition.
    public event Action<WorkflowMessageChange>? WorkflowMessage;

    // ── Download ─────────────────────────────────────────────────────────────
    public event Action<DownloadStartedChange>? DownloadStarted;
    public event Action<FallbackTransferStartedChange>? FallbackTransferStarted;
    public event Action<DownloadProgressedChange>? DownloadProgress;
    public event Action<DownloadStateChangedChange>? DownloadStateChanged;
    public event Action<DownloadAttemptFailedChange>? DownloadAttemptFailed;
    public event Action<TransferCompletedChange>? TransferCompleted;
    public event Action<TransferOutputChangedChange>? TransferOutputChanged;
    public event Action<TransferFailedChange>? TransferFailed;
    public event Action<TransferCancelledChange>? TransferCancelled;
    public event Action<TransferAttemptStartedChange>? TransferAttemptStarted;
    public event Action<TransferAttemptCompletedChange>? TransferAttemptCompleted;
    public event Action<TransferAttemptFailedChange>? TransferAttemptFailed;
    public event Action<TransferAttemptCancelledChange>? TransferAttemptCancelled;
    public event Action<SearchResultsAddedChange>? SearchResultsAdded;
    public event Action<SearchCompletedChange>? SearchCompleted;

    // ── List / overall ───────────────────────────────────────────────────────
    // Fired when a batch of songs has been resolved into:
    // - tracks still pending download
    // - tracks already satisfied by skip/existing logic
    // - tracks skipped because they were not found in a prior run
    // The owner job carries any rendering context (for example PrintOption).
    public event Action<TrackBatchResolvedChange>? TrackBatchResolved;
    public event Action<TrackListReadyChange>? TrackListReady;
    public event Action<ListProgressChange>? ListProgress;
    public event Action<OverallProgressChange>? OverallProgress;

    public event Action<CoreChange>? ChangePublished;

    public DownloadEvents(
        TimeProvider? timeProvider = null,
        ILogger<DownloadEvents>? logger = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<DownloadEvents>.Instance;
    }

    // ── Internal raise methods (same assembly only) ──────────────────────────
    internal void RaiseJobRegistered(Job job, Job? parent, Guid? sourceJobId, DirectoryTransferEntry? directoryEntry = null)
    {
        DateTimeOffset registeredAtUtc = UtcNow();
        job.CreatedAtUtc ??= registeredAtUtc;
        UpdateDirectoryGroup(job, parent, directoryEntry);
        Publish(new JobRegisteredChange(
            CoreChange.NextSequence(),
            registeredAtUtc,
            Snapshot(job, incrementRevision: true),
            parent?.Id,
            sourceJobId), JobRegistered);
    }

    internal void UpdateDirectoryGroup(Job job, Job? parent, DirectoryTransferEntry? directoryEntry)
    {
        if (job is FileDownloadJob && parent is DirectoryDownloadJob directory
            && directory.ActiveAttempt?.Plan.RepresentsDirectorySelection == true)
            JobState(job).DirectoryGroup = new DownloadTransferGroup(
                directory.Id.ToString("D"),
                // The transfer path is remote, so its group root must use the
                // same namespace to retain nested member paths in every client.
                directory.ActiveAttempt.Plan.SourceDirectory?.FolderPath ?? directory.ActiveAttempt.Plan.DisplayRoot,
                directoryEntry is null ? null : string.Join("/",
                    directoryEntry.RelativeDirectoryComponents.Append(Utils.GetFileNameSlsk(directoryEntry.Target.Filename))));
        else if (jobs.TryGetValue(job.Id, out var state))
            state.DirectoryGroup = null;
    }

    internal void RaiseJobStructureChanged(Job job)
        => Publish(new JobStructureChangedChange(CoreChange.NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true)), JobStructureChanged);

    internal void RaiseJobStateChanged(Job job, JobStateTransition? transition = null)
    {
        var snapshot = transition is { } captured
            ? CoreSnapshotFactory.CreateExecution(job, captured.After)
            : Execution(job, incrementRevision: true);
        Publish(new JobStateChangedChange(CoreChange.NextSequence(), UtcNow(), snapshot), JobStateChanged);
        // An activity notification cannot be published without its owning state
        // transition. Consumers persist the state once and may render this edge.
        if (transition is { ActivityChanged: true }
            && snapshot.LifecycleState == JobLifecycleState.Running
            && snapshot.ActivityPhase != JobActivityPhase.None)
        {
            var reference = new JobReferenceSnapshot(snapshot.Id, snapshot.DisplayId,
                snapshot.WorkflowId, snapshot.Kind, snapshot.Revision, snapshot.ItemName, snapshot.PrintOption);
            Publish(new JobActivityChangedChange(CoreChange.NextSequence(), UtcNow(), reference,
                snapshot.ActivityPhase, snapshot.ActivityUntilUtc), JobActivityChanged);
        }
    }

    internal void RaiseJobDiscoveryChanged(Job job)
        => Publish(new JobDiscoveryChangedChange(CoreChange.NextSequence(), UtcNow(), Execution(job, incrementRevision: true)), JobDiscoveryChanged);

    internal void RaiseJobExecutionCompleted(Job job)
        => Publish(new JobExecutionCompletedChange(CoreChange.NextSequence(), UtcNow(), Execution(job, incrementRevision: true)), JobExecutionCompleted);

    internal void RaiseJobResultCreated(ExtractJob job, Job result)
        => Publish(new JobResultCreatedChange(CoreChange.NextSequence(), UtcNow(), Snapshot(job, incrementRevision: true), Snapshot(result, incrementRevision: true)), JobResultCreated);

    internal void RaiseEngineCompleted(JobList queue)
        => Publish(new EngineCompletedChange(CoreChange.NextSequence(), UtcNow(), Reference(queue)), EngineCompleted);

    internal void RaiseWorkflowRetired(Guid workflowId, int jobCount)
    {
        Publish(new WorkflowRetiredChange(CoreChange.NextSequence(), UtcNow(), workflowId), WorkflowRetired);
        RetireWorkflowState(workflowId);
        DownloadLogMessages.WorkflowRetired(logger, workflowId, jobCount);
    }


    internal void RaiseJobStatus(Job job, string status)
        => Publish(new JobStatusChange(CoreChange.NextSequence(), UtcNow(), Reference(job), status), JobStatus);

    internal void RaiseJobMessage(Job job, LogLevel level, string? source, string message)
        => Publish(new JobMessageChange(CoreChange.NextSequence(), UtcNow(), Reference(job), level, source, message), JobMessage);

    internal void RaiseWorkflowMessage(Guid workflowId, LogLevel level, string? source, string message)
        => Publish(new WorkflowMessageChange(CoreChange.NextSequence(), UtcNow(), workflowId, level, source, message), WorkflowMessage);

    internal void RaiseDownloadStarted(Guid transferId, FileDownloadJob owner, PeerFileTarget target, string outputPath)
        => PublishNonTerminalTransfer(transferId, facts =>
        {
            // Capture semantic attempt evidence at the actual transfer-start
            // publication, not on candidate selection or local-cache reuse.
            // The exact transfer runner remains independent of semantic jobs.
            if (owner is SongJob { ActiveCandidate: { } candidate } semanticSong
                && candidate.Target.Identity == target.Identity)
                semanticSong.LastAttemptedCandidate = candidate;
            Publish(new DownloadStartedChange(
                CoreChange.NextSequence(),
                UtcNow(),
                Execution(owner),
                SnapshotTransfer(facts, transferId, owner, target, outputPath, state: "Started", bytesTransferred: owner.BytesTransferred, totalBytes: target.Size ?? 0, attemptCount: 0,
                    transition: TransferLifecycleTransition.Requested)), DownloadStarted);
        });

    internal void RaiseFallbackTransferStarted(Guid transferId, SongJob song, string sourceReference, string outputPath)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new FallbackTransferStartedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "Started", 0, 0, 0,
                transition: TransferLifecycleTransition.Requested)), FallbackTransferStarted));

    internal void RaiseDownloadProgress(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget c,
        string outputPath,
        long xfer,
        long total,
        double? bytesPerSecond = null)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new DownloadProgressedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            CoreSnapshotFactory.CreateFileDownloadState(song),
            SnapshotTransfer(facts, transferId, song, c, outputPath, state: "InProgress", bytesTransferred: xfer, totalBytes: total, attemptCount: 0,
                transition: TransferLifecycleTransition.Progress, bytesPerSecond: bytesPerSecond)), DownloadProgress));

    internal void RaiseDownloadStateChanged(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget c,
        string outputPath,
        TransferStates s,
        long bytesTransferred,
        long totalBytes,
        double? bytesPerSecond = null)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new DownloadStateChangedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            CoreSnapshotFactory.CreateFileDownloadState(song),
            SnapshotTransfer(facts, transferId, song, c, outputPath, s.ToString(), bytesTransferred, totalBytes, attemptCount: 0,
                transition: s.HasFlag(TransferStates.InProgress)
                    ? TransferLifecycleTransition.Started
                    : TransferLifecycleTransition.Observed,
                bytesPerSecond: bytesPerSecond)), DownloadStateChanged));

    internal void RaiseDownloadAttemptFailed(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget c,
        string transferOutputPath,
        string attemptOutputPath,
        int attempt,
        int maxAttempts,
        Exception ex)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new DownloadAttemptFailedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotTransfer(facts, transferId, song, c, transferOutputPath, state: "AttemptFailed", bytesTransferred: song.BytesTransferred, totalBytes: c.Size ?? 0, attemptCount: attempt),
            attemptOutputPath,
            attempt,
            maxAttempts,
            CoreSnapshotFactory.CreateException(ex)), DownloadAttemptFailed));

    internal void RaiseTransferCompleted(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string finalLocalPath,
        long totalBytes,
        int attemptCount)
        => PublishTerminalTransfer(transferId, (facts, completedAtUtc) => Publish(new TransferCompletedChange(
            CoreChange.NextSequence(),
            completedAtUtc,
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, finalLocalPath, "Completed", totalBytes, totalBytes, attemptCount,
                transition: TransferLifecycleTransition.Completed),
            finalLocalPath), TransferCompleted));

    internal void RaiseTransferFailed(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string outputPath,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferFailureReason reason,
        Exception exception)
        => PublishTerminalTransfer(transferId, (facts, completedAtUtc) => Publish(new TransferFailedChange(
            CoreChange.NextSequence(),
            completedAtUtc,
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, outputPath, "Failed", bytesTransferred, totalBytes, attemptCount,
                transition: TransferLifecycleTransition.Failed,
                failureReason: reason),
            reason,
            CoreSnapshotFactory.CreateException(exception)), TransferFailed));

    internal void RaiseTransferCancelled(
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string outputPath,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferCancellationReason reason)
        => PublishTerminalTransfer(transferId, (facts, completedAtUtc) => Publish(new TransferCancelledChange(
            CoreChange.NextSequence(),
            completedAtUtc,
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, outputPath, "Cancelled", bytesTransferred, totalBytes, attemptCount,
                transition: TransferLifecycleTransition.Cancelled,
                cancellationReason: reason),
            reason), TransferCancelled));

    internal void RaiseTransferOutputChanged(FileDownloadJob job, string path)
    {
        JobEventState state = JobState(job);
        Guid? transferId;
        lock (state) transferId = state.LastTransferId;
        if (transferId is not { } id || !transferFacts.TryGetValue(id, out var facts)) return;
        lock (facts)
        {
            if (!facts.IsTerminal || facts.Metadata is not { TerminalOutcome: TransferSnapshotTerminalOutcome.Succeeded } previous
                || string.Equals(previous.LocalPath, path, StringComparison.Ordinal)) return;
            var updated = previous with { Revision = ++facts.Revision, LocalPath = path };
            facts.Metadata = updated;
            Publish(new TransferOutputChangedChange(CoreChange.NextSequence(), UtcNow(), Reference(job), updated), TransferOutputChanged);
        }
    }

    internal void RaiseFallbackTransferCompleted(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string finalLocalPath,
        long totalBytes,
        int attemptCount)
        => PublishTerminalTransfer(transferId, (facts, completedAtUtc) => Publish(new TransferCompletedChange(
            CoreChange.NextSequence(),
            completedAtUtc,
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, finalLocalPath, "Completed", totalBytes, totalBytes, attemptCount,
                transition: TransferLifecycleTransition.Completed),
            finalLocalPath), TransferCompleted));

    internal void RaiseFallbackTransferFailed(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string? outputPath,
        int attemptCount,
        TransferFailureReason reason,
        Exception exception)
        => PublishTerminalTransfer(transferId, (facts, completedAtUtc) => Publish(new TransferFailedChange(
            CoreChange.NextSequence(),
            completedAtUtc,
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "Failed", 0, 0, attemptCount,
                transition: TransferLifecycleTransition.Failed,
                failureReason: reason),
            reason,
            CoreSnapshotFactory.CreateException(exception)), TransferFailed));

    internal void RaiseFallbackTransferCancelled(
        Guid transferId,
        SongJob song,
        string sourceReference,
        string? outputPath,
        int attemptCount,
        TransferCancellationReason reason)
        => PublishTerminalTransfer(transferId, (facts, completedAtUtc) => Publish(new TransferCancelledChange(
            CoreChange.NextSequence(),
            completedAtUtc,
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "Cancelled", 0, 0, attemptCount,
                transition: TransferLifecycleTransition.Cancelled,
                cancellationReason: reason),
            reason), TransferCancelled));

    internal void RaiseTransferAttemptStarted(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath,
        string attemptOutputPath)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptStartedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, transferOutputPath, "AttemptStarted", song.BytesTransferred, candidate.Size ?? 0, attemptNumber,
                transition: TransferLifecycleTransition.Started),
            attemptId,
            attemptNumber,
            NextAttemptRevision(facts, attemptId),
            TransferAttemptSource.SoulseekPeer,
            attemptOutputPath), TransferAttemptStarted));

    internal void RaiseTransferAttemptCompleted(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptCompletedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, transferOutputPath, "AttemptCompleted", song.BytesTransferred, candidate.Size ?? 0, attemptNumber),
            attemptId,
            attemptNumber,
            NextAttemptRevision(facts, attemptId)), TransferAttemptCompleted));

    internal void RaiseTransferAttemptFailed(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath,
        Exception exception)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptFailedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, transferOutputPath, "AttemptFailed", song.BytesTransferred, candidate.Size ?? 0, attemptNumber),
            attemptId,
            attemptNumber,
            NextAttemptRevision(facts, attemptId),
            CoreSnapshotFactory.CreateException(exception)), TransferAttemptFailed));

    internal void RaiseTransferAttemptCancelled(
        Guid transferId,
        Guid attemptId,
        int attemptNumber,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string transferOutputPath,
        TransferCancellationReason reason)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptCancelledChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotTransfer(facts, transferId, song, candidate, transferOutputPath, "AttemptCancelled", song.BytesTransferred, candidate.Size ?? 0, attemptNumber),
            attemptId,
            attemptNumber,
            NextAttemptRevision(facts, attemptId),
            reason), TransferAttemptCancelled));

    internal void RaiseFallbackTransferAttemptStarted(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptStartedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "AttemptStarted", 0, 0, 1,
                transition: TransferLifecycleTransition.Started),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(facts, attemptId),
            Source: TransferAttemptSource.Fallback,
            OutputPath: outputPath), TransferAttemptStarted));

    internal void RaiseFallbackTransferAttemptCompleted(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptCompletedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "AttemptCompleted", 0, 0, 1),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(facts, attemptId)), TransferAttemptCompleted));

    internal void RaiseFallbackTransferAttemptFailed(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath,
        Exception exception)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptFailedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "AttemptFailed", 0, 0, 1),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(facts, attemptId),
            Exception: CoreSnapshotFactory.CreateException(exception)), TransferAttemptFailed));

    internal void RaiseFallbackTransferAttemptCancelled(
        Guid transferId,
        Guid attemptId,
        SongJob song,
        string sourceReference,
        string outputPath,
        TransferCancellationReason reason)
        => PublishNonTerminalTransfer(transferId, facts => Publish(new TransferAttemptCancelledChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(song),
            SnapshotFallbackTransfer(facts, transferId, song, sourceReference, outputPath, "AttemptCancelled", 0, 0, 1),
            attemptId,
            AttemptNumber: 1,
            AttemptRevision: NextAttemptRevision(facts, attemptId),
            Reason: reason), TransferAttemptCancelled));

    internal void RaiseTrackBatchResolved(Job job, IReadOnlyList<SongJob> pending, IReadOnlyList<SongJob> existing, IReadOnlyList<SongJob> notFound)
        => Publish(new TrackBatchResolvedChange(
            CoreChange.NextSequence(),
            UtcNow(),
            Reference(job),
            SnapshotCollections.Freeze(pending.Select(song => Reference(song))),
            SnapshotCollections.Freeze(existing.Select(song => Reference(song))),
            SnapshotCollections.Freeze(notFound.Select(song => Reference(song)))), TrackBatchResolved);

    internal void RaiseTrackListReady(IEnumerable<SongJob> songs)
        => Publish(new TrackListReadyChange(CoreChange.NextSequence(), UtcNow(), SnapshotSongs(songs)), TrackListReady);

    internal void RaiseListProgress(JobList list, int dl, int fl, int total)
        => Publish(new ListProgressChange(CoreChange.NextSequence(), UtcNow(), Reference(list), dl, fl, total), ListProgress);

    internal void RaiseOverallProgress(int dl, int fl, int total)
        => Publish(new OverallProgressChange(CoreChange.NextSequence(), UtcNow(), dl, fl, total), OverallProgress);

    internal void RaiseSearchChange(CoreChange change)
    {
        switch (change)
        {
            case SearchResultsAddedChange results: Publish(results, SearchResultsAdded); break;
            case SearchCompletedChange completed: Publish(completed, SearchCompleted); break;
            default: throw new ArgumentException($"Unsupported search change {change.GetType().Name}.", nameof(change));
        }
    }


    private DateTimeOffset UtcNow() => timeProvider.GetUtcNow();

    private static long NextAttemptRevision(TransferFacts facts, Guid attemptId)
    {
        // Attempt changes already hold this transfer's publication gate.
        var revisions = facts.AttemptRevisions ??= [];
        return revisions[attemptId] = revisions.GetValueOrDefault(attemptId) + 1;
    }

    private JobEventState JobState(Job job)
        => jobs.GetOrAdd(job.Id, static (_, workflowId) => new JobEventState(workflowId), job.WorkflowId);

    private JobStateSnapshot CaptureJobState(Job job, bool incrementRevision)
    {
        var state = JobState(job);
        lock (state)
        {
            state.WorkflowId = job.WorkflowId;
            return job.CaptureState(incrementRevision);
        }
    }

    private JobSnapshot Snapshot(Job job, bool incrementRevision = false)
        => CoreSnapshotFactory.CreateJob(job, CaptureJobState(job, incrementRevision));

    private JobExecutionSnapshot Execution(Job job, bool incrementRevision = false)
        => CoreSnapshotFactory.CreateExecution(job, CaptureJobState(job, incrementRevision));

    private JobReferenceSnapshot Reference(Job job, bool incrementRevision = false)
        => CoreSnapshotFactory.CreateJobReference(job, CaptureJobState(job, incrementRevision).Revision);

    private IReadOnlyList<JobSnapshot> SnapshotSongs(IEnumerable<SongJob> songs)
        => SnapshotCollections.Freeze(songs.Select(song => Snapshot(song)));

    private TransferSnapshot SnapshotTransfer(
        TransferFacts facts,
        Guid transferId,
        FileDownloadJob song,
        PeerFileTarget candidate,
        string outputPath,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferLifecycleTransition transition = TransferLifecycleTransition.Observed,
        double? bytesPerSecond = null,
        TransferFailureReason? failureReason = null,
        TransferCancellationReason? cancellationReason = null)
    {
        long revision = ++facts.Revision;
        var evidence = CoreSnapshotFactory.DownloadCandidate(song, candidate);
        if (!ReferenceEquals(facts.Target, candidate) || !ReferenceEquals(facts.Candidate, evidence)
            || facts.Metadata is not { Source: TransferSnapshotSource.SoulseekPeer } metadata
            || metadata.JobId != song.Id || metadata.WorkflowId != song.WorkflowId)
        {
            facts.Target = candidate;
            // Exact peer and folder spelling are preserved. The group key also
            // contains the peer; submission scope prevents unrelated downloads
            // of the same directory from merging. Core-only runs use workflow.
            int separator = Math.Max(candidate.Filename.LastIndexOf('\\'), candidate.Filename.LastIndexOf('/'));
            facts.FileGroup = separator > 0
                ? new DownloadTransferGroup(
                    $"files:{song.SubmissionId ?? song.WorkflowId:D}:{candidate.Filename[..separator]}",
                    candidate.Filename[..separator], candidate.Filename[(separator + 1)..], TransferGroupKind.Files)
                : null;
            facts.Candidate = evidence;
            facts.Metadata = CoreSnapshotFactory.CreateDownloadTransfer(
                transferId, song, candidate, outputPath, revision, state, bytesTransferred, totalBytes, attemptCount, evidence);
            RememberTransfer(song, transferId);
        }
        return CaptureTransfer(facts, revision, outputPath, state, bytesTransferred, totalBytes, attemptCount,
            transition, bytesPerSecond, failureReason, cancellationReason);
    }

    private TransferSnapshot SnapshotFallbackTransfer(
        TransferFacts facts,
        Guid transferId,
        SongJob song,
        string sourceReference,
        string? outputPath,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferLifecycleTransition transition = TransferLifecycleTransition.Observed,
        double? bytesPerSecond = null,
        TransferFailureReason? failureReason = null,
        TransferCancellationReason? cancellationReason = null)
    {
        long revision = ++facts.Revision;
        if (facts.Metadata is not { Source: TransferSnapshotSource.Fallback } metadata
            || metadata.JobId != song.Id || metadata.WorkflowId != song.WorkflowId
            || metadata.RemotePath != sourceReference)
        {
            facts.Target = null;
            facts.FileGroup = null;
            facts.Candidate = null;
            facts.Metadata = CoreSnapshotFactory.CreateFallbackTransfer(
                transferId, song, sourceReference, outputPath, revision, state, bytesTransferred, totalBytes, attemptCount);
            RememberTransfer(song, transferId);
        }
        return CaptureTransfer(facts, revision, outputPath, state, bytesTransferred, totalBytes, attemptCount,
            transition, bytesPerSecond, failureReason, cancellationReason);
    }

    private void RememberTransfer(FileDownloadJob job, Guid transferId)
    {
        var state = JobState(job);
        lock (state) state.LastTransferId = transferId;
    }

    private TransferSnapshot CaptureTransfer(
        TransferFacts facts,
        long revision,
        string? outputPath,
        string? state,
        long bytesTransferred,
        long totalBytes,
        int attemptCount,
        TransferLifecycleTransition transition,
        double? bytesPerSecond,
        TransferFailureReason? failureReason,
        TransferCancellationReason? cancellationReason)
    {
        DateTimeOffset now = UtcNow();
        facts.RequestedAtUtc = facts.RequestedAtUtc is { } requested && requested < now ? requested : now;

        if (transition is TransferLifecycleTransition.Started
            or TransferLifecycleTransition.Progress)
            facts.StartedAtUtc ??= now;
        if (transition == TransferLifecycleTransition.Progress)
            facts.LastProgressAtUtc = now;
        if (bytesPerSecond is { } speed && double.IsFinite(speed))
            facts.BytesPerSecond = speed <= 0
                ? 0
                : speed >= long.MaxValue
                    ? long.MaxValue
                    : checked((long)speed);

        switch (transition)
        {
            case TransferLifecycleTransition.Completed:
                facts.TerminalOutcome = TransferSnapshotTerminalOutcome.Succeeded;
                break;
            case TransferLifecycleTransition.Failed:
                facts.TerminalOutcome = TransferSnapshotTerminalOutcome.Failed;
                facts.FailureReason = failureReason;
                break;
            case TransferLifecycleTransition.Cancelled:
                facts.TerminalOutcome = TransferSnapshotTerminalOutcome.Cancelled;
                facts.CancellationReason = cancellationReason;
                break;
        }

        var metadata = facts.Metadata!;
        var group = jobs.GetValueOrDefault(metadata.JobId!.Value)?.DirectoryGroup ?? facts.FileGroup;
        // PeerFileTarget and FileCandidate are immutable. Capture their metadata
        // once at this transfer owner; each event then creates only its current
        // scalar state. A new target/evidence reference replaces that baseline.
        // Grouping and lifecycle join that same capture, avoiding intermediate
        // record copies. Nothing published here is subsequently mutated.
        var snapshot = metadata with
        {
            Revision = revision,
            LocalPath = outputPath,
            State = state,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            AttemptCount = attemptCount,
            RequestedAtUtc = facts.RequestedAtUtc,
            StartedAtUtc = facts.StartedAtUtc,
            LastProgressAtUtc = facts.LastProgressAtUtc,
            BytesPerSecond = facts.BytesPerSecond,
            TerminalOutcome = facts.TerminalOutcome,
            CompletedAtUtc = facts.CompletedAtUtc,
            FailureReason = facts.FailureReason,
            CancellationReason = facts.CancellationReason,
            GroupRef = group?.Ref,
            GroupKind = group?.Kind ?? TransferGroupKind.Directory,
            GroupDisplayPath = group?.DisplayPath,
            GroupRelativePath = group?.RelativePath,
        };
        facts.Metadata = snapshot;
        return snapshot;
    }

    private void PublishNonTerminalTransfer(Guid transferId, Action<TransferFacts> publish)
    {
        var facts = transferFacts.GetOrAdd(transferId, static _ => new TransferFacts());
        lock (facts)
        {
            if (facts.IsTerminal)
                return;

            publish(facts);
        }
    }

    private void PublishTerminalTransfer(Guid transferId, Action<TransferFacts, DateTimeOffset> publish)
    {
        var facts = transferFacts.GetOrAdd(transferId, static _ => new TransferFacts());
        lock (facts)
        {
            if (facts.IsTerminal)
                throw new InvalidOperationException($"Transfer {transferId} already has a terminal outcome.");

            facts.IsTerminal = true;
            // The original outcome owns its time; later output-path updates retain it.
            var completedAtUtc = facts.CompletedAtUtc = UtcNow();
            publish(facts, completedAtUtc.Value);
        }
        if (facts.Metadata is { JobId: { } jobId } terminal)
            DownloadLogMessages.TransferEnded(logger, transferId, jobId, terminal.TerminalOutcome, terminal.BytesTransferred);
    }

    private enum TransferLifecycleTransition
    {
        Observed,
        Requested,
        Started,
        Progress,
        Completed,
        Failed,
        Cancelled,
    }

    private sealed class JobEventState(Guid workflowId)
    {
        // The small event gate protects workflow metadata. Job owns its coherent
        // lifecycle/revision capture; neither gate is held while notifying observers.
        public Guid WorkflowId = workflowId;
        public volatile DownloadTransferGroup? DirectoryGroup;
        public Guid? LastTransferId;
    }

    private sealed class TransferFacts
    {
        // This private owner is also the publication gate. Transfer mutations
        // and attempt-clock changes hold that same gate.
        public long Revision;
        public bool IsTerminal;
        public Dictionary<Guid, long>? AttemptRevisions;
        public DownloadTransferGroup? FileGroup;
        public PeerFileTarget? Target { get; set; }
        public FileCandidate? Candidate { get; set; }
        // Retirement reads only this immutable publication, without entering
        // another transfer's gate from inside an event observer.
        public volatile TransferSnapshot? Metadata;
        public DateTimeOffset? RequestedAtUtc { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? LastProgressAtUtc { get; set; }
        public long? BytesPerSecond { get; set; }
        public TransferSnapshotTerminalOutcome TerminalOutcome { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public TransferFailureReason? FailureReason { get; set; }
        public TransferCancellationReason? CancellationReason { get; set; }
    }

    private void Publish<T>(T change, Action<T>? observers,
        [CallerArgumentExpression(nameof(observers))] string eventName = "") where T : CoreChange
    {
        InvokeObservers(observers, change, eventName);
        InvokeObservers(ChangePublished, (CoreChange)change, nameof(ChangePublished));
    }

    private void RetireWorkflowState(Guid workflowId)
    {
        foreach (var pair in jobs)
        {
            lock (pair.Value)
                if (pair.Value.WorkflowId == workflowId)
                    jobs.TryRemove(pair);
        }
        foreach (var pair in transferFacts)
        {
            if (pair.Value.Metadata?.WorkflowId == workflowId)
                transferFacts.TryRemove(pair);
        }
    }

    internal (int Jobs, int Transfers, int Attempts, int Gates, int TerminalTransfers, int DirectoryGroups) RetainedStateCounts
        => (jobs.Count, transferFacts.Count,
            transferFacts.Values.Sum(facts => facts.AttemptRevisions?.Count ?? 0),
            transferFacts.Count, transferFacts.Values.Count(facts => facts.IsTerminal),
            jobs.Values.Count(state => state.DirectoryGroup is not null));

    private sealed record DownloadTransferGroup(string Ref, string? DisplayPath, string? RelativePath,
        TransferGroupKind Kind = TransferGroupKind.Directory);

    private void InvokeObservers<T>(Action<T>? observers, T value, string eventName)
    {
        if (observers == null)
            return;

        foreach (Action<T> observer in Delegate.EnumerateInvocationList(observers))
        {
            try
            {
                observer(value);
            }
            catch (Exception ex)
            {
                try
                {
                    DownloadLogMessages.ObserverFailed(logger, ex, eventName);
                }
                catch
                {
                    // Observational failures, including logging failures, never affect domain work.
                }
            }
        }
    }
}
