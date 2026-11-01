using Sockseek.Api;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Persistence.Read;

namespace Sockseek.Server;

/// <summary>
/// One transfer read model across execution, live deltas and durable history.
/// Revision belongs to the transfer source, not the transport or storage tier.
/// </summary>
internal static class TransferReadModel
{
    public static TransferDeltaDto Difference(TransferStateDto? previous, TransferStateDto current)
    {
        // Identity and file facts are cohesive: changes replace the row rather
        // than leaving a subscriber with metadata from the previous candidate.
        if (previous is null || previous.Identity != current.Identity || !SameFile(previous.File, current.File))
            return new(current.TransferId, current.Revision, Added: current);
        return new(current.TransferId, current.Revision,
            Status: TransferStatusEquals(previous.Status, current.Status) ? null : current.Status,
            Progress: previous.Progress == current.Progress ? null : current.Progress,
            Scheduling: previous.Scheduling == current.Scheduling ? null : current.Scheduling);
    }

    private static bool SameFile(FileMetadataDto? left, FileMetadataDto? right)
        => ReferenceEquals(left, right) || left is not null && right is not null
            && left.Name == right.Name && left.Size == right.Size && left.Extension == right.Extension
            && left.BitRate == right.BitRate && left.BitDepth == right.BitDepth
            && left.SampleRate == right.SampleRate && left.Length == right.Length
            && (left.Attributes ?? []).SequenceEqual(right.Attributes ?? []);

    private static DateTimeOffset? Milliseconds(DateTimeOffset? value)
        => value is { } time ? DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds()) : null;

    public static TransferStateDto FromDownload(TransferSnapshot transfer, bool isTerminal,
        DateTimeOffset? completedAtUtc, string? failureMessage,
        TransferStateDto? previous = null, TransferFields fields = TransferFields.All)
    {
        bool terminal = isTerminal
            || transfer.TerminalOutcome != TransferSnapshotTerminalOutcome.None;
        return new(
            transfer.Id,
            transfer.Revision,
            previous?.Identity ?? new TransferIdentityFieldsDto(
                transfer.JobId,
                transfer.WorkflowId,
                transfer.Direction.ToString(),
                transfer.Source.ToString(),
                transfer.Username,
                transfer.RemotePath,
                transfer.CandidateKey,
                transfer.GroupRef,
                transfer.GroupDisplayPath,
                transfer.Candidate?.Peer.UploadSpeed,
                transfer.Candidate?.Peer.HasFreeUploadSlot,
                transfer.GroupRelativePath),
            previous is not null && (fields & TransferFields.Status) == 0 ? previous.Status : new TransferStatusFieldsDto(
                transfer.State ?? "",
                transfer.LocalPath,
                transfer.AttemptCount,
                terminal,
                Outcome(transfer.TerminalOutcome),
                transfer.FailureReason.ToServer(),
                transfer.CancellationReason.ToServer(),
                terminal
                    ? []
                    :
                    [
                        new ResourceActionDto(
                            ServerResourceActionKind.Cancel,
                            "POST",
                            $"/api/transfers/{transfer.Id:D}/cancel"),
                    ],
                completedAtUtc,
                failureMessage),
            previous is not null && (fields & TransferFields.Progress) == 0 ? previous.Progress : new TransferProgressFieldsDto(
                transfer.BytesTransferred,
                transfer.TotalBytes,
                transfer.BytesPerSecond,
                transfer.LastProgressAtUtc),
            previous is not null && (fields & TransferFields.Scheduling) == 0 ? previous.Scheduling
                : transfer.RequestedAtUtc is { } requested
                ? new TransferSchedulingFieldsDto(requested, transfer.StartedAtUtc)
                : null,
            previous?.File ?? ToFileMetadata(transfer.File));
    }

    public static TransferStateDto FromUpload(
        UploadTransferSnapshot transfer, TransferStateDto? previous = null, TransferFields fields = TransferFields.All)
    {
        bool terminal = transfer.State is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;
        var terminalOutcome = Outcome(transfer.State);
        return new TransferStateDto(
            transfer.TransferId,
            transfer.Revision,
            previous?.Identity ?? new TransferIdentityFieldsDto(
                null,
                null,
                "Upload",
                "SoulseekPeer",
                transfer.Username,
                transfer.RemotePath,
                null,
                transfer.GroupRef,
                transfer.GroupDisplayPath),
            previous is not null && (fields & TransferFields.Status) == 0 ? previous.Status : new TransferStatusFieldsDto(
                transfer.State.ToString(),
                null,
                transfer.Attempt?.Number ?? 0,
                terminal,
                terminalOutcome,
                transfer.FailureReason.ToServer(),
                transfer.CancellationSource.ToServer(),
                terminal
                    ? []
                    :
                    [
                        new ResourceActionDto(
                            ServerResourceActionKind.Cancel,
                            "POST",
                            $"/api/transfers/{transfer.TransferId:D}/cancel"),
                    ],
                transfer.FinishedAtUtc),
            previous is not null && (fields & TransferFields.Progress) == 0 ? previous.Progress : new TransferProgressFieldsDto(
                transfer.BytesTransferred,
                transfer.SizeBytes,
                checked((long)Math.Max(0, transfer.BytesPerSecond)),
                transfer.LastProgressAtUtc),
            previous is not null && (fields & TransferFields.Scheduling) == 0 ? previous.Scheduling : new TransferSchedulingFieldsDto(
                transfer.RequestedAtUtc,
                transfer.Attempt?.StartedAtUtc),
            previous?.File ?? ToFileMetadata(transfer.File));
    }

    internal static TransferTerminalOutcome Outcome(TransferSnapshotTerminalOutcome outcome)
        => outcome switch
        {
            TransferSnapshotTerminalOutcome.Succeeded => TransferTerminalOutcome.Succeeded,
            TransferSnapshotTerminalOutcome.Cancelled => TransferTerminalOutcome.Cancelled,
            TransferSnapshotTerminalOutcome.Failed => TransferTerminalOutcome.Failed,
            TransferSnapshotTerminalOutcome.Interrupted => TransferTerminalOutcome.Interrupted,
            _ => TransferTerminalOutcome.None,
        };

    internal static TransferTerminalOutcome Outcome(UploadTransferState state)
        => state switch
        {
            UploadTransferState.Completed => TransferTerminalOutcome.Succeeded,
            UploadTransferState.Cancelled => TransferTerminalOutcome.Cancelled,
            UploadTransferState.Failed => TransferTerminalOutcome.Failed,
            UploadTransferState.Interrupted => TransferTerminalOutcome.Interrupted,
            _ => TransferTerminalOutcome.None,
        };

    public static TransferHistoryDto FromRetained(PersistedTransfer transfer)
        => new(
            transfer.Id, transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source, transfer.Username,
            transfer.RemotePath, transfer.LocalPath, transfer.State, transfer.TerminalOutcome, transfer.TotalBytes,
            transfer.TransferredBytes, transfer.AttemptCount, transfer.CreatedAtUtc, transfer.CompletedAtUtc,
            PublicFailureReason(transfer), transfer.FailureMessage,
            PublicCancellationSource(transfer.CancellationSource),
            transfer.Revision,
            RequestedAtUtc: transfer.CreatedAtUtc,
            transfer.StartedAtUtc,
            transfer.LastProgressAtUtc,
            transfer.BytesPerSecond,
            ToFileMetadata(transfer.File),
            AvailableActions: [],
            transfer.ArchivedAtUtc,
            transfer.GroupRef,
            transfer.GroupDisplayPath,
            transfer.GroupRelativePath);

    public static TransferHistoryDto FromLive(TransferStateDto transfer)
    {
        DateTimeOffset created = transfer.Scheduling?.RequestedAtUtc
            ?? DateTimeOffset.UnixEpoch;
        created = DateTimeOffset.FromUnixTimeMilliseconds(
            created.ToUnixTimeMilliseconds());
        return new TransferHistoryDto(
            transfer.TransferId,
            transfer.Identity.JobId,
            transfer.Identity.WorkflowId,
            transfer.Identity.Direction,
            transfer.Identity.Source,
            transfer.Identity.Username,
            transfer.Identity.RemotePath,
            transfer.Status.LocalPath,
            transfer.Status.State,
            transfer.Status.TerminalOutcome.ToString(),
            transfer.Progress.TotalBytes < 0 ? null : transfer.Progress.TotalBytes,
            transfer.Progress.BytesTransferred,
            transfer.Status.AttemptCount,
            created,
            Milliseconds(transfer.Status.CompletedAtUtc),
            transfer.Status.FailureReason.ToString(),
            transfer.Status.FailureMessage,
            transfer.Status.CancellationSource,
            transfer.Revision,
            Milliseconds(transfer.Scheduling?.RequestedAtUtc),
            Milliseconds(transfer.Scheduling?.StartedAtUtc),
            Milliseconds(transfer.Progress.LastProgressAtUtc),
            transfer.Progress.BytesPerSecond,
            transfer.File,
            transfer.Status.AvailableActions,
            GroupRef: transfer.Identity.GroupRef,
            GroupDisplayPath: transfer.Identity.GroupDisplayPath,
            GroupRelativePath: transfer.Identity.GroupRelativePath);
    }

    private static TransferCancellationSource PublicCancellationSource(string source)
    {
        if (Enum.TryParse<Sockseek.Core.Events.TransferCancellationReason>(source, true, out var download)
            && Enum.IsDefined(download))
            return ((Sockseek.Core.Events.TransferCancellationReason?)download).ToServer();
        if (Enum.TryParse<UploadCancellationSource>(source, true, out var upload) && Enum.IsDefined(upload))
            return upload.ToServer();
        return TransferCancellationSource.None;
    }

    private static string PublicFailureReason(PersistedTransfer transfer)
    {
        // Older download adapters also put the cancellation reason into the
        // failure column. Preserve existing history without a data rewrite.
        if (transfer.Direction.Equals("Download", StringComparison.OrdinalIgnoreCase)
            && transfer.TerminalOutcome.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<Sockseek.Core.Events.TransferCancellationReason>(transfer.FailureReason, true, out var cancelled)
            && Enum.IsDefined(cancelled))
            return TransferFailureReason.None.ToString();
        if (transfer.Direction.Equals("Upload", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<UploadFailureReason>(transfer.FailureReason, true, out var upload) && Enum.IsDefined(upload))
            return upload.ToServer().ToString();
        if (transfer.Direction.Equals("Download", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<Sockseek.Core.Events.TransferFailureReason>(transfer.FailureReason, true, out var download)
            && Enum.IsDefined(download))
            return ((Sockseek.Core.Events.TransferFailureReason?)download).ToServer().ToString();
        return transfer.FailureReason;
    }

    private static FileMetadataDto? ToFileMetadata(
        Sockseek.Core.Snapshots.TransferFileMetadataSnapshot? file)
        => file is null
            ? null
            : new FileMetadataDto(
                file.Name,
                file.Size,
                file.Extension,
                file.BitRate,
                file.BitDepth,
                file.SampleRate,
                file.Length,
                file.Attributes?.Select(attribute => new FileAttributeDto(
                    attribute.Type,
                    attribute.Value)).ToArray());

    public static TransferHistoryDto Merge(
        TransferHistoryDto retained,
        TransferHistoryDto current)
        => retained.Revision > current.Revision ? retained : current with
        {
            // Creation order is immutable once retained. Mutable live state
            // must never move an existing row to a different keyset page.
            CreatedAtUtc = retained.CreatedAtUtc,
            RequestedAtUtc = current.RequestedAtUtc ?? retained.RequestedAtUtc,
            StartedAtUtc = current.StartedAtUtc ?? retained.StartedAtUtc,
            LastProgressAtUtc = current.LastProgressAtUtc ?? retained.LastProgressAtUtc,
            CompletedAtUtc = current.CompletedAtUtc ?? retained.CompletedAtUtc,
            FailureMessage = current.FailureMessage ?? retained.FailureMessage,
            File = current.File ?? retained.File,
            ArchivedAtUtc = retained.ArchivedAtUtc,
            GroupRef = current.GroupRef ?? retained.GroupRef,
            GroupDisplayPath = current.GroupDisplayPath ?? retained.GroupDisplayPath,
            GroupRelativePath = current.GroupRelativePath ?? retained.GroupRelativePath,
        };

    // The same revision, archive and metadata fallback rules as full rows,
    // projected directly from captured facts without creating a wire row.
    public static TransferGroupMember ForGroup(TransferReadSnapshot current, TransferGroupMember? retained)
        => retained is not null && retained.Revision > current.Revision ? retained : new(
            current.TransferId, current.Revision, current.Direction, current.Username,
            current.GroupRef ?? retained?.GroupRef, current.State, current.TerminalOutcome.ToString(),
            current.TotalBytes < 0 ? null : current.TotalBytes,
            current.File?.Size ?? retained?.FileSize,
            current.BytesTransferred, current.BytesPerSecond, retained?.ArchivedAtUtc);

    private static bool TransferStatusEquals(
        TransferStatusFieldsDto left,
        TransferStatusFieldsDto right)
        => left.State == right.State
            && left.LocalPath == right.LocalPath
            && left.AttemptCount == right.AttemptCount
            && left.IsTerminal == right.IsTerminal
            && left.TerminalOutcome == right.TerminalOutcome
            && left.FailureReason == right.FailureReason
            && left.CancellationSource == right.CancellationSource
            && left.CompletedAtUtc == right.CompletedAtUtc
            && left.FailureMessage == right.FailureMessage
            && left.AvailableActions.SequenceEqual(right.AvailableActions);

}

/// <summary>
/// A captured immutable Core revision, not a wire row. Counters and observers
/// consume scalar facts; metadata/actions are projected only for an actual read
/// or after transport coalescing. Never retain mutable engine transfer objects.
/// </summary>
public sealed class TransferReadSnapshot
{
    private readonly TransferSnapshot? download;
    private readonly UploadTransferSnapshot? upload;
    private readonly TransferStateDto? previousWire;
    private readonly TransferFields wireFields;
    private TransferStateDto? wire;
    internal bool IsReplacement { get; }
    internal TransferFields ChangedFields { get; }

    internal TransferReadSnapshot(TransferSnapshot transfer, bool isTerminal = false,
        DateTimeOffset? completedAtUtc = null, string? failureMessage = null, TransferReadSnapshot? previous = null)
    {
        download = transfer;
        IsTerminal = isTerminal || transfer.TerminalOutcome != TransferSnapshotTerminalOutcome.None;
        CompletedAtUtc = transfer.CompletedAtUtc ?? completedAtUtc;
        FailureMessage = failureMessage;
        BytesPerSecond = transfer.BytesPerSecond;
        (IsReplacement, ChangedFields, previousWire, wireFields) = Projection(previous);
    }

    internal TransferReadSnapshot(UploadTransferSnapshot transfer, TransferReadSnapshot? previous = null)
    {
        upload = transfer;
        IsTerminal = transfer.State is UploadTransferState.Completed or UploadTransferState.Cancelled
            or UploadTransferState.Failed or UploadTransferState.Interrupted;
        CompletedAtUtc = transfer.FinishedAtUtc;
        // Preserve validation at admission rather than deferring invalid numeric
        // input until every subsequent reader attempts to project it.
        BytesPerSecond = checked((long)Math.Max(0, transfer.BytesPerSecond));
        (IsReplacement, ChangedFields, previousWire, wireFields) = Projection(previous);
    }

    public Guid TransferId => download?.Id ?? upload!.TransferId;
    public long Revision => download?.Revision ?? upload!.Revision;
    internal Guid? JobId => download?.JobId;
    internal Guid? WorkflowId => download?.WorkflowId;
    internal string Direction => download?.Direction.ToString() ?? "Upload";
    internal string Source => download?.Source.ToString() ?? "SoulseekPeer";
    internal string? Username => download?.Username ?? upload?.Username;
    internal string? RemotePath => download?.RemotePath ?? upload?.RemotePath;
    private string? CandidateKey => download?.CandidateKey;
    internal string? GroupRef => download?.GroupRef ?? upload?.GroupRef;
    private string? GroupDisplayPath => download?.GroupDisplayPath ?? upload?.GroupDisplayPath;
    private string? GroupRelativePath => download?.GroupRelativePath;
    private int? PeerUploadSpeed => download?.Candidate?.Peer.UploadSpeed;
    private bool? PeerHasFreeUploadSlot => download?.Candidate?.Peer.HasFreeUploadSlot;
    internal TransferFileMetadataSnapshot? File => download?.File ?? upload?.File;
    internal string State => download is not null ? download.State ?? "" : upload!.State.ToString();
    private string? LocalPath => download?.LocalPath;
    private int AttemptCount => download?.AttemptCount ?? upload?.Attempt?.Number ?? 0;
    internal bool IsTerminal { get; }
    internal TransferTerminalOutcome TerminalOutcome => download is not null
        ? TransferReadModel.Outcome(download.TerminalOutcome) : TransferReadModel.Outcome(upload!.State);
    private TransferFailureReason FailureReason => download is not null
        ? download.FailureReason.ToServer() : upload!.FailureReason.ToServer();
    private TransferCancellationSource CancellationSource => download is not null
        ? download.CancellationReason.ToServer() : upload!.CancellationSource.ToServer();
    internal DateTimeOffset? CompletedAtUtc { get; }
    private string? FailureMessage { get; }
    internal DateTimeOffset? RequestedAtUtc => download is not null ? download.RequestedAtUtc : upload!.RequestedAtUtc;
    private DateTimeOffset? StartedAtUtc => download is not null ? download.StartedAtUtc : upload!.Attempt?.StartedAtUtc;
    private DateTimeOffset? LastProgressAtUtc => download?.LastProgressAtUtc ?? upload?.LastProgressAtUtc;
    internal long BytesTransferred => download?.BytesTransferred ?? upload!.BytesTransferred;
    internal long TotalBytes => download?.TotalBytes ?? upload!.SizeBytes;
    internal long? BytesPerSecond { get; }

    public TransferStateDto ToDto()
    {
        if (Volatile.Read(ref wire) is { } current) return current;
        var projected = download is not null
            ? TransferReadModel.FromDownload(download, IsTerminal, CompletedAtUtc, FailureMessage, previousWire, wireFields)
            : TransferReadModel.FromUpload(upload!, previousWire, wireFields);
        return Interlocked.CompareExchange(ref wire, projected, null) ?? projected;
    }

    private (bool Added, TransferFields Changed, TransferStateDto? Baseline, TransferFields Wire) Projection(TransferReadSnapshot? previous)
    {
        if (previous is null || Replaces(previous)) return (true, TransferFields.All, null, TransferFields.All);
        TransferFields changed = Difference(previous);
        // Reuse already-projected components without forcing an intermediate
        // revision to project. Changes accumulate from that one baseline; no
        // previous snapshot is retained, so unobserved progress cannot form a chain.
        var projected = Volatile.Read(ref previous.wire);
        return (false, changed, projected ?? previous.previousWire,
            projected is null ? previous.wireFields | changed : changed);
    }

    internal bool Replaces(TransferReadSnapshot previous)
        => JobId != previous.JobId || WorkflowId != previous.WorkflowId
            || Direction != previous.Direction || Source != previous.Source
            || Username != previous.Username || RemotePath != previous.RemotePath
            || CandidateKey != previous.CandidateKey || GroupRef != previous.GroupRef
            || GroupDisplayPath != previous.GroupDisplayPath || GroupRelativePath != previous.GroupRelativePath
            || PeerUploadSpeed != previous.PeerUploadSpeed || PeerHasFreeUploadSlot != previous.PeerHasFreeUploadSlot
            || !SameFile(File, previous.File);

    internal TransferFields Difference(TransferReadSnapshot previous)
    {
        var fields = TransferFields.None;
        if (State != previous.State || LocalPath != previous.LocalPath || AttemptCount != previous.AttemptCount
            || IsTerminal != previous.IsTerminal || TerminalOutcome != previous.TerminalOutcome
            || FailureReason != previous.FailureReason || CancellationSource != previous.CancellationSource
            || CompletedAtUtc != previous.CompletedAtUtc || FailureMessage != previous.FailureMessage)
            fields |= TransferFields.Status;
        if (BytesTransferred != previous.BytesTransferred || TotalBytes != previous.TotalBytes
            || BytesPerSecond != previous.BytesPerSecond || LastProgressAtUtc != previous.LastProgressAtUtc)
            fields |= TransferFields.Progress;
        if (RequestedAtUtc != previous.RequestedAtUtc || StartedAtUtc != previous.StartedAtUtc)
            fields |= TransferFields.Scheduling;
        return fields;
    }

    private static bool SameFile(TransferFileMetadataSnapshot? left, TransferFileMetadataSnapshot? right)
        => ReferenceEquals(left, right) || left is not null && right is not null
            && left.Name == right.Name && left.Size == right.Size && left.Extension == right.Extension
            && left.BitRate == right.BitRate && left.BitDepth == right.BitDepth
            && left.SampleRate == right.SampleRate && left.Length == right.Length
            && SameAttributes(left.Attributes, right.Attributes);

    private static bool SameAttributes(IReadOnlyList<FileAttributeSnapshot>? left, IReadOnlyList<FileAttributeSnapshot>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if ((left?.Count ?? 0) != (right?.Count ?? 0)) return false;
        for (int index = 0; index < (left?.Count ?? 0); index++)
            if (left![index].Type != right![index].Type || left[index].Value != right[index].Value) return false;
        return true;
    }
}

/// <summary>
/// The live transfer facts and their read revision have one mutation boundary.
/// EngineStateStore holds its gate; callers receive read-only dictionaries so a
/// new write path cannot silently bypass observation/freshness invalidation.
/// </summary>
internal sealed class TransferReadState
{
    private readonly Dictionary<Guid, TransferReadSnapshot> active = [];
    private readonly Dictionary<Guid, TransferReadSnapshot> uploads = [];
    private readonly Dictionary<Guid, HashSet<Guid>> transfersByJob = [];
    private readonly Dictionary<TransferOwner, Counts> ownerCounts = [];
    private readonly HashSet<Guid> visibleWorkflows = [];
    private Counts totals;
    private LiveTransferSummaryDto? summary;
    private readonly Guid epoch = Guid.NewGuid();
    private long revision;
    public IReadOnlyDictionary<Guid, TransferReadSnapshot> Active => active;
    public IReadOnlyDictionary<Guid, TransferReadSnapshot> Uploads => uploads;
    public IReadOnlyList<TransferReadSnapshot> ForJobs(IReadOnlySet<Guid> jobIds)
        => jobIds.Where(transfersByJob.ContainsKey).SelectMany(id => transfersByJob[id])
            .Select(id => active[id]).ToArray();
    public StateStreamPositionDto Position => new(epoch, revision);
    public LiveTransferSummaryDto Summary => summary ??= new(totals.Active, totals.Downloads,
        totals.Uploads, totals.Queued, totals.Failed, totals.DownloadRate, totals.UploadRate);

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

    public void UpsertActive(TransferReadSnapshot row, bool structural)
    {
        active.TryGetValue(row.TransferId, out var previous);
        active[row.TransferId] = row;
        IndexJob(previous, row);
        ReplaceCounts(previous, row);
        Publish(previous, row, structural);
    }
    public void UpsertUpload(TransferReadSnapshot row, bool replicated, bool structural)
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
    public bool RemoveUpload(Guid id)
    {
        if (!uploads.Remove(id, out var previous)) return false;
        Publish(previous, null, false);
        return true;
    }
    public bool RemoveActive(Guid id, out TransferReadSnapshot row)
    {
        if (!active.Remove(id, out row!)) return false;
        IndexJob(row, null);
        ReplaceCounts(row, null);
        Publish(row, null, false);
        return true;
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
                Queued = active && row.State.Equals("Queued", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
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
