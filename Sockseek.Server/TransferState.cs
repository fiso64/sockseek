using Sockseek.Api;
using Sockseek.Core.Events;
using Sockseek.Persistence.Write;
using Sockseek.Server.Persistence;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Uploads;

namespace Sockseek.Server;

/// <summary>
/// The live transfer facts and their read revision have one mutation boundary.
/// EngineStateStore holds its gate; callers receive read-only dictionaries so a
/// new write path cannot silently bypass observation/freshness invalidation.
/// </summary>
internal sealed class TransferState
{
    private readonly Dictionary<Guid, TransferReadSnapshot> active = [];
    private readonly Dictionary<Guid, SortedList<int, TransferAttemptHistoryDto>> attempts = [];
    private readonly Dictionary<Guid, TransferReadSnapshot> uploads = [];
    private readonly Dictionary<Guid, HashSet<Guid>> transfersByJob = [];
    private readonly Dictionary<TransferOwner, Counts> ownerCounts = [];
    private readonly HashSet<Guid> visibleWorkflows = [];
    private Counts totals;
    private LiveTransferSummaryDto? summary;
    private readonly Guid epoch = Guid.NewGuid();
    private long revision;
    internal TransferPersistence? Persistence { get; set; }
    private readonly Dictionary<Guid, Guid> recordedUploadAttempts = [];
    private readonly Dictionary<Guid, AttemptAccounting> activeAttempts = [];
    private readonly Dictionary<Guid, PendingAttempt> pendingAttempts = [];
    private sealed record AttemptAccounting(Guid Id, long BaselineBytes);
    private sealed record PendingAttempt(TransferReadSnapshot Transfer, TransferAttemptHistoryDto Attempt,
        IReadOnlyList<TransferAccountingObservation>? Accounting);
    public IReadOnlyDictionary<Guid, TransferReadSnapshot> Active => active;
    public IReadOnlyDictionary<Guid, TransferReadSnapshot> Uploads => uploads;
    public IReadOnlyList<TransferReadSnapshot> ForJobs(IReadOnlySet<Guid> jobIds)
        => jobIds.Where(transfersByJob.ContainsKey).SelectMany(id => transfersByJob[id])
            .Select(id => active[id]).ToArray();
    public StateStreamPositionDto Position => new(epoch, revision);
    public LiveTransferSummaryDto Summary => summary ??= new(totals.Active, totals.Downloads,
        totals.Uploads, totals.Queued, totals.Failed, totals.DownloadRate, totals.UploadRate);

    public int AttemptCount => attempts.Count;
    public IReadOnlyList<TransferAttemptHistoryDto>? ReadAttempts(Guid id, int after, int limit)
        => attempts.TryGetValue(id, out var rows)
            ? rows.Values.Where(row => row.AttemptNumber > after).Take(limit).ToArray()
            : active.ContainsKey(id) || uploads.ContainsKey(id) ? [] : null;

    public TransferChange? ApplyDownload(TransferSnapshot source, bool terminal,
        DateTimeOffset? completedAt = null, string? failureMessage = null, CoreChange? context = null)
    {
        if (active.TryGetValue(source.Id, out var previous) && previous.Revision >= source.Revision) return null;
        var row = new TransferReadSnapshot(source, terminal, completedAt,
            failureMessage ?? (context is TransferOutputChangedChange ? previous?.FailureMessage : null), previous);
        var change = new TransferChange(row);
        UpsertActive(row, change.Added);
        if (Persistence is { } writer)
        {
            bool metadataOnly = context is TransferOutputChangedChange;
            var occurredAt = context?.OccurredAtUtc ?? completedAt ?? source.LastProgressAtUtc
                ?? source.StartedAtUtc ?? source.RequestedAtUtc ?? DateTimeOffset.UtcNow;
            PendingAttempt? final = null;
            if (row.IsTerminal && !metadataOnly) pendingAttempts.Remove(source.Id, out final);
            writer.Write(row, occurredAt, row.IsTerminal ? PersistenceMutationPriority.Terminal
                : context is DownloadProgressedChange ? PersistenceMutationPriority.Progress
                : previous is null ? PersistenceMutationPriority.Structural : PersistenceMutationPriority.Ordinary,
                final?.Attempt, metadataOnly ? null : final?.Accounting ?? Accounting(source, occurredAt), metadataOnly);
        }
        return change;
    }

    public TransferChange? ApplyUpload(UploadTransferSnapshot source)
    {
        if (uploads.TryGetValue(source.TransferId, out var previous) && previous.Revision >= source.Revision) return null;
        // Queued uploads are readable but absent from the compact daemon stream.
        // Their first replicated state needs a complete row, not a progress patch.
        active.TryGetValue(source.TransferId, out var previousActive);
        var row = new TransferReadSnapshot(source, previousActive);
        if (ToUploadAttempt(source) is { } attempt) RecordAttempt(attempt);
        bool replicated = source.State != UploadTransferState.Queued
            && (row.IsTerminal || source.Attempt is not null || active.ContainsKey(source.TransferId));
        var change = new TransferChange(row);
        UpsertUpload(row, replicated, change.Added);
        if (Persistence is { } writer)
        {
            var occurredAt = source.FinishedAtUtc ?? source.LastProgressAtUtc
                ?? source.Attempt?.StartedAtUtc ?? source.RequestedAtUtc;
            IReadOnlyList<TransferAccountingObservation>? accounting = source.Attempt is { } currentAttempt
                ? [new(currentAttempt.AttemptId, source.Revision, occurredAt, Math.Max(0, currentAttempt.BytesTransferred))]
                : null;
            writer.Write(row, occurredAt, row.IsTerminal ? PersistenceMutationPriority.Terminal
                : source.State == UploadTransferState.Queued ? PersistenceMutationPriority.Structural
                : source.State == UploadTransferState.InProgress ? PersistenceMutationPriority.Progress
                : PersistenceMutationPriority.Ordinary, row.IsTerminal ? LatestAttempt(row.TransferId) : null, accounting);
            if (row.IsTerminal) recordedUploadAttempts.Remove(row.TransferId);
            else if (source.Attempt is { } started && recordedUploadAttempts.GetValueOrDefault(row.TransferId) != started.AttemptId
                && writer.WriteAttempt(row, LatestAttempt(row.TransferId)!, accounting))
                recordedUploadAttempts[row.TransferId] = started.AttemptId;
        }
        return replicated ? change : null;
    }

    // Called only while gate is held. Attempt revisions are independent of the
    // transfer revision; a late older notification must not restore Started.
    private bool RecordAttempt(TransferAttemptHistoryDto attempt)
    {
        if (!attempts.TryGetValue(attempt.TransferId, out var rows))
            attempts.Add(attempt.TransferId, rows = new());
        if (rows.TryGetValue(attempt.AttemptNumber, out var previous) && previous.Revision >= attempt.Revision)
            return false;
        rows[attempt.AttemptNumber] = attempt;
        return true;
    }

    public void StartAttempt(TransferAttemptStartedChange change)
    {
        var attempt = new TransferAttemptHistoryDto(change.AttemptId, change.Transfer.Id,
            change.AttemptNumber, change.Source.ToString(), "Started", change.Transfer.Username,
            change.Transfer.RemotePath, change.OutputPath, change.OccurredAtUtc, null, "None", null, change.AttemptRevision);
        if (!RecordAttempt(attempt)) return;
        if (pendingAttempts.Remove(change.Transfer.Id, out var pending))
            Persistence?.WriteAttempt(pending.Transfer, pending.Attempt, pending.Accounting);
        if (Persistence is { } writer)
        {
            activeAttempts[change.Transfer.Id] = new(change.AttemptId, change.Transfer.BytesTransferred);
            writer.WriteAttempt(new(change.Transfer), attempt, Accounting(change.Transfer, change.OccurredAtUtc));
        }
    }

    private IReadOnlyList<TransferAccountingObservation>? Accounting(TransferSnapshot transfer, DateTimeOffset occurredAt)
        => activeAttempts.TryGetValue(transfer.Id, out var attempt)
            ? [new(attempt.Id, transfer.Revision, occurredAt, Math.Max(0, transfer.BytesTransferred - attempt.BaselineBytes))]
            : null;

    public TransferAttemptHistoryDto? LatestAttempt(Guid transferId)
        => attempts.TryGetValue(transferId, out var rows) && rows.Count > 0
            ? rows.Values[^1] : null;

    private static TransferAttemptHistoryDto? ToUploadAttempt(UploadTransferSnapshot transfer)
    {
        if (transfer.Attempt is not { } attempt)
            return null;

        bool terminal = transfer.State is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;
        string state = terminal
            ? transfer.State switch
            {
                UploadTransferState.Completed => "Completed",
                UploadTransferState.Cancelled => "Cancelled",
                UploadTransferState.Interrupted => "Interrupted",
                _ => "Failed",
            }
            : "Started";
        return new TransferAttemptHistoryDto(
            attempt.AttemptId,
            transfer.TransferId,
            attempt.Number,
            "SoulseekPeer",
            state,
            transfer.Username,
            transfer.RemotePath,
            null,
            attempt.StartedAtUtc,
            terminal
                ? attempt.FinishedAtUtc ?? transfer.FinishedAtUtc
                : null,
            transfer.FailureReason.ToString(),
            null,
            transfer.Revision);
    }

    public void CompleteAttempt(
        TransferSnapshot transfer,
        Guid attemptId,
        int attemptNumber,
        long attemptRevision,
        DateTimeOffset occurredAtUtc,
        string state,
        string failureReason,
        string? failureMessage)
    {
        var previous = attempts.GetValueOrDefault(transfer.Id)?.GetValueOrDefault(attemptNumber);
        var attempt = new TransferAttemptHistoryDto(
            attemptId,
            transfer.Id,
            attemptNumber,
            transfer.Source.ToString(),
            state,
            transfer.Username,
            transfer.RemotePath,
            transfer.LocalPath ?? previous?.OutputPath,
            previous is { AttemptId: var previousId } && previousId == attemptId
                ? previous.StartedAtUtc
                : occurredAtUtc,
            occurredAtUtc,
            failureReason,
            failureMessage,
            attemptRevision);
        if (!RecordAttempt(attempt)) return;
        var accounting = Accounting(transfer, occurredAtUtc);
        if (activeAttempts.GetValueOrDefault(transfer.Id)?.Id == attemptId)
        {
            activeAttempts.Remove(transfer.Id);
            if (Persistence is not null) pendingAttempts[transfer.Id] = new(new(transfer), attempt, accounting);
        }
        else Persistence?.WriteAttempt(new(transfer), attempt);
    }

    // The global snapshot contains unowned transfers and live workflows only.
    // Retiring/reappearing a workflow adjusts its aggregate in O(1), without
    // visiting its files or changing ownership of the underlying transfer facts.
    public void SetWorkflowVisible(Guid workflowId, bool visible)
    {
        bool changed = visible ? visibleWorkflows.Add(workflowId) : visibleWorkflows.Remove(workflowId);
        if (changed && ownerCounts.TryGetValue(new(workflowId), out var counts))
        {
            totals.Add(counts, visible ? 1 : -1);
            summary = null;
        }
    }
    // Subscribers buffer facts only. Reads and transport publication run after
    // the engine gate is released, on the existing broadcaster's coalesced wake.
    public event Action<TransferReadChange>? Changed;

    private void UpsertActive(TransferReadSnapshot row, bool structural)
    {
        active.TryGetValue(row.TransferId, out var previous);
        active[row.TransferId] = row;
        IndexJob(previous, row);
        ReplaceCounts(previous, row);
        Publish(previous, row, structural);
    }
    private void UpsertUpload(TransferReadSnapshot row, bool replicated, bool structural)
    {
        uploads.TryGetValue(row.TransferId, out var previous);
        uploads[row.TransferId] = row;
        if (replicated)
        {
            active.TryGetValue(row.TransferId, out var previousActive);
            active[row.TransferId] = row;
            IndexJob(previousActive, row);
            ReplaceCounts(previousActive, row);
        }
        Publish(previous, row, structural);
    }
    public TransferReadSnapshot? Remove(Guid id)
    {
        uploads.Remove(id, out var upload);
        active.Remove(id, out var current);
        attempts.Remove(id);
        activeAttempts.Remove(id);
        recordedUploadAttempts.Remove(id);
        if (pendingAttempts.Remove(id, out var pending))
            Persistence?.WriteAttempt(pending.Transfer, pending.Attempt, pending.Accounting);
        if (current is not null)
        {
            IndexJob(current, null);
            ReplaceCounts(current, null);
        }
        var previous = current ?? upload;
        if (previous is not null) Publish(previous, null, false);
        return previous;
    }
    public void MarkHistoryChanged() => Publish(null, null, true);

    private void IndexJob(TransferReadSnapshot? previous, TransferReadSnapshot? current)
    {
        if (previous?.JobId == current?.JobId) return;
        if (previous?.JobId is Guid oldJob && transfersByJob.TryGetValue(oldJob, out var oldRows))
        {
            oldRows.Remove(previous.TransferId);
            if (oldRows.Count == 0) transfersByJob.Remove(oldJob);
        }
        if (current?.JobId is Guid job)
        {
            if (!transfersByJob.TryGetValue(job, out var rows)) transfersByJob[job] = rows = [];
            rows.Add(current.TransferId);
        }
    }

    private void ReplaceCounts(TransferReadSnapshot? previous, TransferReadSnapshot? current)
    {
        Counts before = previous is null ? default : Counts.From(previous);
        Counts after = current is null ? default : Counts.From(current);
        if (previous?.WorkflowId == current?.WorkflowId && before == after) return;
        if (previous != null) Add(previous.WorkflowId, before, -1);
        if (current != null) Add(current.WorkflowId, after, 1);
    }

    private void Add(Guid? workflowId, Counts contribution, int sign)
    {
        var owner = new TransferOwner(workflowId);
        Counts counts = ownerCounts.GetValueOrDefault(owner);
        counts.Add(contribution, sign);
        if (counts.Rows == 0) ownerCounts.Remove(owner);
        else ownerCounts[owner] = counts;
        if (owner.WorkflowId is null || visibleWorkflows.Contains(owner.WorkflowId.Value))
        {
            totals.Add(contribution, sign);
            summary = null;
        }
    }

    private readonly record struct TransferOwner(Guid? WorkflowId);

    private record struct Counts
    {
        public int Rows, Active, Downloads, Uploads, Queued, Failed;
        public long DownloadRate, UploadRate;

        public static Counts From(TransferReadSnapshot row)
        {
            bool active = !row.IsTerminal;
            bool download = active && row.Direction.Equals("Download", StringComparison.OrdinalIgnoreCase);
            bool upload = active && row.Direction.Equals("Upload", StringComparison.OrdinalIgnoreCase);
            return new()
            {
                Rows = 1, Active = active ? 1 : 0, Downloads = download ? 1 : 0, Uploads = upload ? 1 : 0,
                Queued = active && row.State.StartsWith("Queued", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Failed = row.TerminalOutcome == TransferTerminalOutcome.Failed ? 1 : 0,
                DownloadRate = download ? row.BytesPerSecond ?? 0 : 0,
                UploadRate = upload ? row.BytesPerSecond ?? 0 : 0,
            };
        }

        public void Add(Counts value, int sign)
        {
            Rows += sign * value.Rows; Active += sign * value.Active;
            Downloads += sign * value.Downloads; Uploads += sign * value.Uploads;
            Queued += sign * value.Queued; Failed += sign * value.Failed;
            DownloadRate += sign * value.DownloadRate; UploadRate += sign * value.UploadRate;
        }
    }
    private void Publish(TransferReadSnapshot? previous, TransferReadSnapshot? current, bool structural)
    {
        revision++;
        Changed?.Invoke(new(previous, current, structural, revision));
    }
}

internal readonly record struct TransferReadChange(
    TransferReadSnapshot? Previous, TransferReadSnapshot? Current, bool Structural, long Revision);
