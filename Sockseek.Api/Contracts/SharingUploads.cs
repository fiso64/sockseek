using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<StartShareScanResult>))]
public enum StartShareScanResult
{
    Started,
    AlreadyRunning,
}

public sealed record StartShareScanResponseDto(
    StartShareScanResult Result,
    ShareScanStateDto Scan);

public sealed record TransferQueueEstimateDto(
    int? AheadCount,
    long QueueRevision);

public sealed record LiveTransferFilter(
    string? Direction = null,
    string? State = null,
    string? Username = null);

public sealed record LiveTransferPageDto(
    IReadOnlyList<TransferStateDto> Items,
    string? NextCursor,
    long ObservedQueueRevision,
    bool QueueChanged);

/// <summary>
/// One current transfer record, including retained metadata after runtime retirement.
/// Complete attempt history is available from the paged transfer-attempts resource.
/// </summary>
public sealed record TransferDetailDto(
    TransferStateDto Transfer,
    TransferQueueEstimateDto? QueueEstimate,
    TransferAttemptHistoryDto? LatestAttempt);
