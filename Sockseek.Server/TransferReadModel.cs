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
                transfer.GroupRelativePath, (Sockseek.Api.TransferGroupKind)transfer.GroupKind),
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
            previous?.File ?? ToFileMetadata(transfer.File),
            Milliseconds(transfer.RequestedAtUtc) ?? previous?.CreatedAtUtc ?? DateTimeOffset.UnixEpoch);
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
                transfer.GroupDisplayPath, GroupKind: Sockseek.Api.TransferGroupKind.Files),
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
                transfer.FinishedAtUtc, transfer.FailureMessage),
            previous is not null && (fields & TransferFields.Progress) == 0 ? previous.Progress : new TransferProgressFieldsDto(
                transfer.BytesTransferred,
                transfer.SizeBytes,
                checked((long)Math.Max(0, transfer.BytesPerSecond)),
                transfer.LastProgressAtUtc),
            previous is not null && (fields & TransferFields.Scheduling) == 0 ? previous.Scheduling : new TransferSchedulingFieldsDto(
                transfer.RequestedAtUtc,
                transfer.Attempt?.StartedAtUtc),
            previous?.File ?? ToFileMetadata(transfer.File),
            Milliseconds(transfer.RequestedAtUtc) ?? previous?.CreatedAtUtc ?? DateTimeOffset.UnixEpoch);
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

    public static TransferStateDto FromRetained(PersistedTransfer transfer)
    {
        var outcome = Enum.Parse<TransferTerminalOutcome>(transfer.TerminalOutcome, ignoreCase: true);
        var failure = Enum.TryParse<TransferFailureReason>(PublicFailureReason(transfer), out var reason)
            ? reason : TransferFailureReason.Unknown;
        return new(transfer.Id, transfer.Revision,
            new(transfer.JobId, transfer.WorkflowId, transfer.Direction, transfer.Source,
                transfer.Username, transfer.RemotePath, null, transfer.GroupRef,
                transfer.GroupDisplayPath, GroupRelativePath: transfer.GroupRelativePath, GroupKind: Enum.Parse<Sockseek.Api.TransferGroupKind>(transfer.GroupKind)),
            new(transfer.State, transfer.LocalPath, transfer.AttemptCount, outcome != TransferTerminalOutcome.None,
                outcome, failure, PublicCancellationSource(transfer.CancellationSource), [],
                transfer.CompletedAtUtc, transfer.FailureMessage, transfer.ArchivedAtUtc),
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

    private static string PublicFailureReason(PersistedTransfer transfer)
    {
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
                CompletedAtUtc = current.Status.CompletedAtUtc ?? retained.Status.CompletedAtUtc,
                FailureMessage = current.Status.FailureMessage ?? retained.Status.FailureMessage,
                ArchivedAtUtc = retained.Status.ArchivedAtUtc,
            },
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
            && left.ArchivedAtUtc == right.ArchivedAtUtc
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
    internal string State => download is not null ? download.State ?? "" : upload!.State.ToString();
    internal string? LocalPath => download?.LocalPath;
    internal int AttemptCount => download?.AttemptCount ?? upload?.Attempt?.Number ?? 0;
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
