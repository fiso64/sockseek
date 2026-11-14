using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<TransferRetainedCoverageState>))]
public enum TransferRetainedCoverageState
{
    Available,
    Degraded,
    Unavailable,
}

public sealed record TransferRetainedCoverageDto(
    TransferRetainedCoverageState State,
    string? Reason = null);

/// <summary>Looks up current visible timeline rows by exact transfer ID.</summary>
public sealed record TransferLookupRequestDto(
    IReadOnlyList<Guid> TransferIds,
    string? Direction = null);

[JsonConverter(typeof(JsonStringEnumConverter<TransferGroupKind>))]
public enum TransferGroupKind { Directory, Files }

/// <summary>An exact transfer presentation group identity; GroupRef is opaque.</summary>
public sealed record TransferGroupKeyDto(
    TransferCommandDirection Direction,
    string? Username,
    string GroupRef);

public sealed record TransferGroupSummariesRequestDto(
    IReadOnlyList<TransferGroupKeyDto> Groups);

/// <summary>
/// Moving aggregate over all visible members of one transfer presentation group.
/// Counts and bytes include members outside the current transfer timeline page.
/// </summary>
public sealed record TransferGroupSummaryDto(
    TransferGroupKeyDto Key,
    long FileCount,
    long KnownTotalBytes,
    long TransferredBytes,
    long SpeedBytesPerSecond,
    long UnknownTotalCount,
    long KnownRemainingActiveBytes,
    long UnknownActiveTotalCount,
    long QueuedCount,
    long InProgressCount,
    long SucceededCount,
    long FailedCount,
    long CancelledCount,
    long InterruptedCount);

/// <summary>
/// Group totals and their transfer-fact read position. Compare this position with
/// TransferTimelineUpdateDto.ReadPosition, not the daemon's live stream sequence.
/// </summary>
public sealed record TransferGroupSummariesResponseDto(
    IReadOnlyList<TransferGroupSummaryDto> Items,
    TransferRetainedCoverageDto RetainedCoverage,
    StateStreamPositionDto? ReadPosition = null);

[JsonConverter(typeof(JsonStringEnumConverter<TransferCommandDirection>))]
public enum TransferCommandDirection
{
    Download,
    Upload,
}

[JsonConverter(typeof(JsonStringEnumConverter<TransferCancellationScope>))]
public enum TransferCancellationScope
{
    All,
    Queued,
    InProgress,
}

/// <summary>
/// Cancels a current transfer snapshot. Username and GroupRef are exact,
/// optional filters; pair them to target one peer's directory group.
/// </summary>
public sealed record BulkCancelTransfersRequestDto(
    TransferCommandDirection Direction,
    TransferCancellationScope Scope = TransferCancellationScope.All,
    string? Username = null,
    string? GroupRef = null);

public sealed record TransferCommandReasonCountDto(string Reason, int Count);

/// <summary>
/// Fixed-size result for a command resolved against a target snapshot. A no-op
/// means the requested state was already true when that target was processed.
/// </summary>
public sealed record TransferCommandReceiptDto(
    int ResolvedCount,
    int SucceededCount,
    int NoOpCount,
    int RejectedCount,
    int FailedCount,
    IReadOnlyList<TransferCommandReasonCountDto> Reasons);

/// <summary>
/// Removes terminal transfers from ordinary listings; retained statistics are preserved. Username and GroupRef are exact,
/// optional filters; pair them to target one peer's directory group.
/// </summary>
public sealed record RemoveTransfersRequestDto(
    string? Direction = null,
    string? TerminalOutcome = null,
    string? Username = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? GroupRef = null);

/// <summary>
/// Moving newest-first keyset page. New transfers can appear above a cursor;
/// status changes do not move an existing transfer in the traversal.
/// ReadPosition is captured before all sources; compare it with the initial
/// or recovered live snapshot to detect a gap before live subscription.
/// </summary>
public sealed record TransferTimelinePageDto(
    IReadOnlyList<TransferStateDto> Items,
    string? NextCursor,
    TransferRetainedCoverageDto RetainedCoverage,
    StateStreamPositionDto? ReadPosition = null);

/// <summary>
/// A moving timeline extent. Known revisions suppress retransmission when the
/// reader expands or reconnects; they do not change which rows belong to the page.
/// AdditionalTransferIds keeps explicitly fetched folder members observed even
/// when newer arrivals move them outside the prefix. No selection is stored.
/// </summary>
public sealed record TransferTimelineScopeDto(
    TransferCommandDirection Direction,
    int Limit = 500,
    IReadOnlyDictionary<Guid, long>? KnownRevisions = null,
    string? Username = null,
    string? GroupRef = null,
    IReadOnlyList<Guid>? AdditionalTransferIds = null,
    bool IncludeGroupSummaries = false);

/// <summary>
/// Changed rows within the requested timeline extent. Removed IDs leave that
/// extent; they need not be deleted from history. WindowTransferIds supplies its
/// initial membership; later membership follows row additions/removals. NextCursor
/// and BoundaryTransferId end the moving prefix, independently of
/// additional members. Changes carries coalesced patches to those same rows;
/// runtime retirement after persistence does not remove a row from the extent.
/// GroupSummaries, when present, replaces totals for all groups in this extent.
/// It is supplied with the initial rows and when relevant group facts change;
/// its independent coverage keeps valid rows usable if totals are unavailable.
/// ReadPosition uses the transfer-fact epoch/sequence shared with group summaries;
/// it advances for queued work too, independently of the daemon live stream.
/// </summary>
public sealed record TransferTimelineUpdateDto(
    IReadOnlyList<TransferStateDto> Items,
    IReadOnlyList<Guid> RemovedTransferIds,
    TransferRetainedCoverageDto RetainedCoverage,
    IReadOnlyList<Guid>? WindowTransferIds = null,
    string? NextCursor = null,
    Guid? BoundaryTransferId = null,
    StateStreamPositionDto? ReadPosition = null,
    IReadOnlyList<TransferDeltaDto>? Changes = null,
    TransferGroupSummariesResponseDto? GroupSummaries = null);

public sealed record TransferAttemptHistoryDto(
    Guid AttemptId,
    Guid TransferId,
    int AttemptNumber,
    string Source,
    string State,
    string? SourceUsername,
    string? SourcePath,
    string? OutputPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string FailureReason,
    string? FailureMessage,
    long Revision);
