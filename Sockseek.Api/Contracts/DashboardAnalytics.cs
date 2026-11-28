using System.Text.Json.Serialization;

namespace Sockseek.Api;

[JsonConverter(typeof(JsonStringEnumConverter<DashboardAnalyticsCoverageState>))]
public enum DashboardAnalyticsCoverageState
{
    Available,
    Degraded,
    Unavailable,
}

public sealed record DashboardAnalyticsCoverageDto(
    DashboardAnalyticsCoverageState State,
    DateTimeOffset? CompleteFromUtc,
    bool IsComplete,
    string? Reason = null);

public sealed record DashboardAnalyticsRangeDto(
    string Range,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int BucketSeconds,
    DashboardAnalyticsCoverageDto Coverage);

public sealed record DashboardBandwidthBucketDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long DownloadBytes,
    long UploadBytes);

public sealed record DashboardPeerAggregateDto(
    string Username,
    long TransferredBytes,
    int SuccessfulFileCount);

public sealed record DashboardContentAggregateDto(
    string Identity,
    string DisplayPath,
    int DownloadCount,
    int DistinctPeerCount);

public sealed record DashboardErrorAggregateDto(
    string Reason,
    int Count,
    DateTimeOffset LastSeenUtc);

public sealed record DashboardAnalyticsSummaryDto(
    long DownloadedBytes,
    int DownloadedFiles,
    long UploadedBytes,
    int UploadedFiles,
    int DistinctPeers,
    double? ShareRatio);

public sealed record DashboardAnalyticsComparisonDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DashboardAnalyticsCoverageDto Coverage,
    DashboardAnalyticsSummaryDto Summary);

/// <summary>
/// Bounded analytics document. Accounting version 1 defines bytes as positive
/// per-attempt transport activity, files as successful logical transfers,
/// peers as exact usernames with byte activity, content as successful uploads
/// grouped by shared-directory identity, and errors as failed attempts.
/// </summary>
public sealed record DashboardAnalyticsDto(
    int AccountingVersion,
    DashboardAnalyticsRangeDto Range,
    IReadOnlyList<DashboardBandwidthBucketDto> Bandwidth,
    DashboardAnalyticsSummaryDto Summary,
    IReadOnlyList<DashboardPeerAggregateDto> DownloadPeers,
    IReadOnlyList<DashboardPeerAggregateDto> UploadPeers,
    IReadOnlyList<DashboardContentAggregateDto> Content,
    IReadOnlyList<DashboardErrorAggregateDto> Errors,
    DashboardAnalyticsComparisonDto? Comparison);
