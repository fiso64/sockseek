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
    internal static bool IsQueued(TransferPhase phase)
        => phase is TransferPhase.QueuedLocally or TransferPhase.QueuedRemotely
            or TransferPhase.QueuedForPeer;

    /// <summary>Stored phase names; every waiting phase shares this prefix.</summary>
    internal static bool IsQueued(string storedPhase) => storedPhase.StartsWith("Queued", StringComparison.OrdinalIgnoreCase);

    internal static TransferPhase Phase(UploadTransferSnapshot upload)
        => upload.State switch
        {
            // Every byte is written, but the upload ends only when the peer
            // closes the connection: slow peers are still receiving the tail,
            // and some clients keep the connection open for a while.
            UploadTransferState.InProgress when upload.SizeBytes > 0 && upload.BytesTransferred >= upload.SizeBytes
                => TransferPhase.Finalizing,
            // An upload waits only in this daemon's own queue.
            UploadTransferState.Queued => TransferPhase.QueuedLocally,
            UploadTransferState.Initializing => TransferPhase.Initializing,
            UploadTransferState.InProgress => TransferPhase.InProgress,
            _ => TransferPhase.Ended,
        };

    public static TransferDeltaDto Difference(TransferStateDto? previous, TransferStateDto current)
    {
        // Identity and file facts are cohesive: changes replace the row rather
        // than leaving a subscriber with metadata from the previous candidate.
        if (previous is null || previous.Identity != current.Identity || previous.CreatedAtUtc != current.CreatedAtUtc || !SameFile(previous.File, current.File))
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

    internal static DateTimeOffset? Milliseconds(DateTimeOffset? value)
        => value is { } time ? DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds()) : null;

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

    internal static ResourceActionDto[] RetryAction(Guid id) => [new(ServerResourceActionKind.Retry, "POST", $"/api/transfers/{id:D}/retry")];

    // TODO [ARCHITECTURE]: Persistence stores transfer enums (phase, outcome, direction,
    // cancellation and failure) as free strings. Readers parse them here, SQL
    // matches them by literal and prefix, and tests seed raw text. Type the
    // persisted model so a renamed value fails to compile, not at read time.
    // TODO [ARCHITECTURE]: A transfer is described five times (Core snapshot,
    // write mutation, entity, read record, DTO), and the middle three carry
    // nearly the same fields. Collapse the mutation and read record into one
    // persisted-transfer record the entity maps to and from. This touches the
    // write path's replay, batching and ordering: run the Load tests before and after.
    public static TransferStateDto FromRetained(PersistedTransfer transfer)
    {
        var outcome = Enum.Parse<TransferTerminalOutcome>(transfer.TerminalOutcome, ignoreCase: true);
        var failure = PublicFailureReason(transfer);
        return new(transfer.Id, transfer.Revision,
            new(transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source,
                transfer.Username, transfer.RemotePath, null, transfer.GroupRef,
                transfer.GroupDisplayPath, GroupRelativePath: transfer.GroupRelativePath, GroupKind: Enum.Parse<Sockseek.Api.TransferGroupKind>(transfer.GroupKind)),
            new(Enum.Parse<TransferPhase>(transfer.State), transfer.LocalPath, transfer.AttemptCount, outcome != TransferTerminalOutcome.None,
                outcome, failure, PublicCancellationSource(transfer.CancellationSource),
                transfer.CanRetry ? RetryAction(transfer.Id) : [],
                transfer.CompletedAtUtc, transfer.FailureMessage, transfer.RemovedAtUtc),
            new(transfer.TransferredBytes, transfer.TotalBytes ?? -1, transfer.BytesPerSecond, transfer.LastProgressAtUtc),
            new(transfer.CreatedAtUtc, transfer.StartedAtUtc), ToFileMetadata(transfer.File), transfer.CreatedAtUtc);
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

    private static TransferFailureReason PublicFailureReason(PersistedTransfer transfer)
    {
        if (transfer.Direction.Equals("Upload", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<UploadFailureReason>(transfer.FailureReason, true, out var upload) && Enum.IsDefined(upload))
            return upload.ToServer();
        if (transfer.Direction.Equals("Download", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<Sockseek.Core.Events.TransferFailureReason>(transfer.FailureReason, true, out var download)
            && Enum.IsDefined(download))
            return ApiEnumMapper.ToServer(download);
        return Enum.TryParse<TransferFailureReason>(transfer.FailureReason, out var reason) ? reason : TransferFailureReason.Unknown;
    }

    internal static FileMetadataDto? ToFileMetadata(
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

    public static TransferStateDto Merge(TransferStateDto retained, TransferStateDto current)
        => retained.Revision > current.Revision ? retained : current with
        {
            CreatedAtUtc = retained.CreatedAtUtc,
            Scheduling = current.Scheduling is { } scheduling
                ? scheduling with { StartedAtUtc = scheduling.StartedAtUtc ?? retained.Scheduling?.StartedAtUtc }
                : retained.Scheduling,
            File = current.File ?? retained.File,
            Identity = current.Identity with
            {
                GroupRef = current.Identity.GroupRef ?? retained.Identity.GroupRef,
                GroupDisplayPath = current.Identity.GroupDisplayPath ?? retained.Identity.GroupDisplayPath,
                GroupRelativePath = current.Identity.GroupRelativePath ?? retained.Identity.GroupRelativePath,
                GroupKind = current.Identity.GroupRef is null ? retained.Identity.GroupKind : current.Identity.GroupKind,
            },
            Progress = current.Progress with { LastProgressAtUtc = current.Progress.LastProgressAtUtc ?? retained.Progress.LastProgressAtUtc },
            Status = current.Status with
            {
                AvailableActions = current.Status.IsTerminal && retained.Status.IsTerminal
                    ? retained.Status.AvailableActions : current.Status.AvailableActions,
                CompletedAtUtc = current.Status.CompletedAtUtc ?? retained.Status.CompletedAtUtc,
                FailureMessage = current.Status.FailureMessage ?? retained.Status.FailureMessage,
                RemovedAtUtc = retained.Status.RemovedAtUtc,
            },
        };

    // The same revision, removal and metadata fallback rules as full rows,
    // projected directly from captured facts without creating a wire row.
    public static TransferGroupMember ForGroup(TransferReadSnapshot current, TransferGroupMember? retained)
        => retained is not null && retained.Revision > current.Revision ? retained : new(
            current.TransferId, current.Revision, current.Direction, current.Username,
            current.GroupRef ?? retained?.GroupRef, current.State, current.TerminalOutcome.ToString(),
            current.TotalBytes < 0 ? null : current.TotalBytes,
            current.File?.Size ?? retained?.FileSize,
            current.BytesTransferred, current.BytesPerSecond, retained?.RemovedAtUtc);

    // Record equality covers every scalar; only the action list needs its contents compared.
    private static bool TransferStatusEquals(TransferStatusFieldsDto left, TransferStatusFieldsDto right)
        => left with { AvailableActions = right.AvailableActions } == right
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
    private readonly bool retainedHistory;
    private TransferStateDto? wire;
    internal bool IsReplacement { get; }
    internal TransferFields ChangedFields { get; }

    internal TransferReadSnapshot(TransferSnapshot transfer, bool isTerminal = false,
        DateTimeOffset? completedAtUtc = null, string? failureMessage = null, TransferReadSnapshot? previous = null,
        bool retainedHistory = false)
    {
        this.retainedHistory = retainedHistory;
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
        FailureMessage = transfer.FailureMessage;
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
    internal string? GroupDisplayPath => download?.GroupDisplayPath ?? upload?.GroupDisplayPath;
    internal string? GroupRelativePath => download?.GroupRelativePath;
    internal Sockseek.Core.Snapshots.TransferGroupKind GroupKind => download?.GroupKind ?? Sockseek.Core.Snapshots.TransferGroupKind.Files;
    private int? PeerUploadSpeed => download?.Candidate?.Peer.UploadSpeed;
    private bool? PeerHasFreeUploadSlot => download?.Candidate?.Peer.HasFreeUploadSlot;
    internal TransferFileMetadataSnapshot? File => download?.File ?? upload?.File;
    internal TransferPhase Phase => download?.Phase ?? TransferReadModel.Phase(upload!);
    /// <summary>The stored phase name.</summary>
    internal string State => Phase.ToString();
    internal string? LocalPath => download?.LocalPath;
    internal int AttemptCount => download?.AttemptCount ?? upload?.Attempt?.Number ?? 0;
    private int? QueuePosition => download?.QueuePosition;
    internal bool IsTerminal { get; }
    internal TransferTerminalOutcome TerminalOutcome => download is not null
        ? TransferReadModel.Outcome(download.TerminalOutcome) : TransferReadModel.Outcome(upload!.State);
    internal string StoredFailureReason => download is not null
        ? download.FailureReason?.ToString() ?? "None" : upload!.FailureReason.ToString();
    internal string StoredCancellationSource => download is not null
        ? download.CancellationReason?.ToString() ?? "None" : upload!.CancellationSource.ToString();
    private TransferFailureReason FailureReason => download is not null
        ? download.FailureReason.ToServer() : upload!.FailureReason.ToServer();
    private TransferCancellationSource CancellationSource => download is not null
        ? download.CancellationReason.ToServer() : upload!.CancellationSource.ToServer();

    /// <summary>
    /// Every fact of the wire status. Change detection and projection both read
    /// this one value, so a new status fact is declared here and nowhere else.
    /// </summary>
    private readonly record struct StatusFacts(
        TransferPhase Phase, string? LocalPath, int AttemptCount, bool IsTerminal,
        TransferTerminalOutcome TerminalOutcome, TransferFailureReason FailureReason,
        TransferCancellationSource CancellationSource, DateTimeOffset? CompletedAtUtc, string? FailureMessage,
        int? QueuePosition);

    private StatusFacts Status => new(Phase, LocalPath, AttemptCount, IsTerminal, TerminalOutcome, FailureReason,
        CancellationSource, CompletedAtUtc, FailureMessage, QueuePosition);
    internal DateTimeOffset? CompletedAtUtc { get; }
    internal string? FailureMessage { get; }
    internal DateTimeOffset? RequestedAtUtc => download is not null ? download.RequestedAtUtc : upload!.RequestedAtUtc;
    internal DateTimeOffset? StartedAtUtc => download is not null ? download.StartedAtUtc : upload!.Attempt?.StartedAtUtc;
    internal DateTimeOffset? LastProgressAtUtc => download?.LastProgressAtUtc ?? upload?.LastProgressAtUtc;
    internal long BytesTransferred => download?.BytesTransferred ?? upload!.BytesTransferred;
    internal long TotalBytes => download?.TotalBytes ?? upload!.SizeBytes;
    internal long? BytesPerSecond { get; }

    public TransferStateDto ToDto()
    {
        if (Volatile.Read(ref wire) is { } current) return current;
        // Components the change did not touch are reused from the previous row.
        bool Kept(TransferFields field) => previousWire is not null && (wireFields & field) == 0;
        var projected = new TransferStateDto(
            TransferId,
            Revision,
            previousWire?.Identity ?? new TransferIdentityFieldsDto(
                JobId, WorkflowId, Direction, Source, Username, RemotePath, CandidateKey, GroupRef, GroupDisplayPath,
                PeerUploadSpeed, PeerHasFreeUploadSlot, GroupRelativePath, (Api.TransferGroupKind)GroupKind,
                ServerSnapshotMapper.ToFallbackSourceDto(download?.Fallback)),
            Kept(TransferFields.Status) ? previousWire!.Status : ToDto(Status),
            Kept(TransferFields.Progress) ? previousWire!.Progress
                : new TransferProgressFieldsDto(BytesTransferred, TotalBytes, BytesPerSecond, LastProgressAtUtc),
            Kept(TransferFields.Scheduling) ? previousWire!.Scheduling
                : RequestedAtUtc is { } requested ? new TransferSchedulingFieldsDto(requested, StartedAtUtc) : null,
            previousWire?.File ?? TransferReadModel.ToFileMetadata(File),
            TransferReadModel.Milliseconds(RequestedAtUtc) ?? previousWire?.CreatedAtUtc ?? DateTimeOffset.UnixEpoch);
        return Interlocked.CompareExchange(ref wire, projected, null) ?? projected;
    }

    private TransferStatusFieldsDto ToDto(StatusFacts status)
        => new(status.Phase, status.LocalPath, status.AttemptCount, status.IsTerminal, status.TerminalOutcome,
            status.FailureReason, status.CancellationSource, AvailableActions(), status.CompletedAtUtc,
            status.FailureMessage, QueuePosition: status.QueuePosition);

    private ResourceActionDto[] AvailableActions()
    {
        if (!IsTerminal)
            return [new(ServerResourceActionKind.Cancel, "POST", $"/api/transfers/{TransferId:D}/cancel")];
        // Only a retained failed peer download with a known job and output can be retried.
        return retainedHistory && download is { TerminalOutcome: TransferSnapshotTerminalOutcome.Failed,
                Source: TransferSnapshotSource.SoulseekPeer, JobId: not null, LocalPath: not null }
            ? TransferReadModel.RetryAction(TransferId) : [];
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
            || RequestedAtUtc?.ToUnixTimeMilliseconds() != previous.RequestedAtUtc?.ToUnixTimeMilliseconds()
            || Direction != previous.Direction || Source != previous.Source
            || Username != previous.Username || RemotePath != previous.RemotePath
            || CandidateKey != previous.CandidateKey || GroupRef != previous.GroupRef
            || GroupDisplayPath != previous.GroupDisplayPath || GroupRelativePath != previous.GroupRelativePath || GroupKind != previous.GroupKind
            || PeerUploadSpeed != previous.PeerUploadSpeed || PeerHasFreeUploadSlot != previous.PeerHasFreeUploadSlot
            || !SameFile(File, previous.File);

    internal TransferFields Difference(TransferReadSnapshot previous)
    {
        var fields = TransferFields.None;
        if (Status != previous.Status) fields |= TransferFields.Status;
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
