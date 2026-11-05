using Sockseek.Api;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Write;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Server.Persistence;

public sealed class DashboardAnalyticsFacade
{
    private const int TopCount = 10;
    private readonly PersistenceCoordinator persistence;
    private readonly TimeProvider clock;
    private readonly ILogger<DashboardAnalyticsFacade> logger;
    private readonly object warningGate = new();
    private DateTimeOffset? lastWarningAtUtc;
    private int suppressedWarnings;

    public DashboardAnalyticsFacade(
        PersistenceCoordinator persistence,
        ILogger<DashboardAnalyticsFacade>? logger = null)
        : this(persistence, TimeProvider.System, logger)
    {
    }

    internal DashboardAnalyticsFacade(
        PersistenceCoordinator persistence,
        TimeProvider clock,
        ILogger<DashboardAnalyticsFacade>? logger = null)
    {
        this.persistence = persistence;
        this.clock = clock;
        this.logger = logger ?? NullLogger<DashboardAnalyticsFacade>.Instance;
    }

    public async Task<DashboardAnalyticsDto> GetAsync(
        string? requestedRange,
        CancellationToken cancellationToken = default)
    {
        string range = NormalizeRange(requestedRange);
        // Accounting writes into five-minute buckets. Include the current
        // partial bucket so an open dashboard can show newly completed work.
        DateTimeOffset end = Ceiling(clock.GetUtcNow(), TimeSpan.FromMinutes(5));
        try
        {
            ITransferAnalyticsReader? reader = persistence.TransferAnalytics;
            if (reader is null || persistence.HealthSnapshot?.State == PersistenceHealthState.Unhealthy)
            {
                var unavailable = DefineRange(range, end, completeFrom: null);
                return Empty(unavailable, Coverage(unavailable.StartUtc, completeFrom: null));
            }

            var snapshot = await reader.GetSnapshotAsync(retainedCoverage =>
            {
                var definition = DefineRange(range, end, retainedCoverage.CompleteFromUtc);
                var queries = new List<TransferAnalyticsQuery>
                {
                    new(definition.StartUtc, definition.EndUtc, definition.BucketWidth, TopCount),
                };
                if (definition.ComparisonStartUtc is { } start && definition.ComparisonEndUtc is { } finish)
                    queries.Add(new(start, finish, definition.BucketWidth, TopCount, IncludeRankings: false));
                return queries;
            }, cancellationToken).ConfigureAwait(false);
            var definition = DefineRange(range, end, snapshot.Coverage.CompleteFromUtc);
            DashboardAnalyticsCoverageDto coverage = Coverage(
                definition.StartUtc,
                snapshot.Coverage.CompleteFromUtc);
            if (coverage.State == DashboardAnalyticsCoverageState.Unavailable)
                return Empty(definition, coverage);

            PersistedTransferAnalytics selected = snapshot.Ranges[0];

            DashboardAnalyticsComparisonDto? comparison = null;
            if (definition.ComparisonStartUtc is { } comparisonStart
                && definition.ComparisonEndUtc is { } comparisonEnd)
            {
                var comparisonCoverage = Coverage(comparisonStart, snapshot.Coverage.CompleteFromUtc);
                PersistedTransferAnalytics comparisonData = snapshot.Ranges[1];
                comparison = new(
                    comparisonStart,
                    comparisonEnd,
                    comparisonCoverage,
                    Summary(comparisonData));
            }

            return new DashboardAnalyticsDto(
                1,
                new DashboardAnalyticsRangeDto(
                    definition.Range,
                    definition.StartUtc,
                    definition.EndUtc,
                    checked((int)definition.BucketWidth.TotalSeconds),
                    coverage),
                selected.Buckets.Select(bucket => new DashboardBandwidthBucketDto(
                    bucket.StartUtc,
                    bucket.EndUtc,
                    bucket.DownloadBytes,
                    bucket.UploadBytes)).ToArray(),
                Summary(selected),
                Peers(selected, "Download"),
                Peers(selected, "Upload"),
                selected.Content.Select(item => new DashboardContentAggregateDto(
                    item.Identity,
                    item.DisplayPath,
                    item.DownloadCount,
                    item.DistinctPeerCount)).ToArray(),
                selected.Errors.Select(item => new DashboardErrorAggregateDto(
                    item.Reason,
                    item.Count,
                    item.LastSeenUtc)).ToArray(),
                comparison);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnavailable(exception);
            RangeDefinition definition = DefineRange(range, end, completeFrom: null);
            return Empty(
                definition,
                new DashboardAnalyticsCoverageDto(
                    DashboardAnalyticsCoverageState.Unavailable,
                    null,
                    false,
                    "Transfer accounting could not be read from persistence."));
        }
    }

    private void LogUnavailable(Exception exception)
    {
        lock (warningGate)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (lastWarningAtUtc is not null
                && now - lastWarningAtUtc < TimeSpan.FromSeconds(30))
            {
                suppressedWarnings++;
                return;
            }
            ServerLogMessages.DashboardAccountingUnavailable(
                logger,
                exception,
                suppressedWarnings);
            suppressedWarnings = 0;
            lastWarningAtUtc = now;
        }
    }

    private DashboardAnalyticsCoverageDto Coverage(
        DateTimeOffset start,
        DateTimeOffset? completeFrom)
    {
        PersistenceHealthState? health = persistence.HealthSnapshot?.State;
        if (completeFrom is null || health == PersistenceHealthState.Unhealthy)
        {
            return new(
                DashboardAnalyticsCoverageState.Unavailable,
                completeFrom,
                false,
                health == PersistenceHealthState.Unhealthy
                    ? "Transfer accounting is unavailable because persistence is unhealthy."
                    : "Transfer accounting is unavailable because persistence is disabled or not started.");
        }
        return new(
            health == PersistenceHealthState.Degraded
                ? DashboardAnalyticsCoverageState.Degraded
                : DashboardAnalyticsCoverageState.Available,
            completeFrom,
            start >= completeFrom,
            start >= completeFrom
                ? null
                : "The requested range begins before retained accounting coverage.");
    }

    private static DashboardAnalyticsSummaryDto Summary(PersistedTransferAnalytics data)
        => new(
            data.DownloadBytes,
            data.DownloadedFiles,
            data.UploadBytes,
            data.UploadedFiles,
            data.DistinctPeers,
            data.DownloadBytes == 0
                ? null
                : (double)data.UploadBytes / data.DownloadBytes);

    private static IReadOnlyList<DashboardPeerAggregateDto> Peers(
        PersistedTransferAnalytics data,
        string direction)
        => data.Peers.Where(peer => peer.Direction == direction)
            .Select(peer => new DashboardPeerAggregateDto(
                peer.Username,
                peer.Bytes,
                peer.SuccessfulFiles))
            .ToArray();

    private static DashboardAnalyticsDto Empty(
        RangeDefinition definition,
        DashboardAnalyticsCoverageDto coverage)
        => new(
            1,
            new DashboardAnalyticsRangeDto(
                definition.Range,
                definition.StartUtc,
                definition.EndUtc,
                checked((int)definition.BucketWidth.TotalSeconds),
                coverage),
            [],
            new(0, 0, 0, 0, 0, null),
            [],
            [],
            [],
            [],
            null);

    private static RangeDefinition DefineRange(
        string range,
        DateTimeOffset end,
        DateTimeOffset? completeFrom)
    {
        (TimeSpan? duration, TimeSpan width) = range switch
        {
            "24h" => ((TimeSpan?)TimeSpan.FromHours(24), TimeSpan.FromMinutes(30)),
            "7d" => ((TimeSpan?)TimeSpan.FromDays(7), TimeSpan.FromHours(4)),
            "30d" => ((TimeSpan?)TimeSpan.FromDays(30), TimeSpan.FromHours(12)),
            "90d" => ((TimeSpan?)TimeSpan.FromDays(90), TimeSpan.FromDays(2)),
            "1y" => ((TimeSpan?)TimeSpan.FromDays(365), TimeSpan.FromDays(7)),
            "all" => ((TimeSpan?)null, TimeSpan.FromMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(range)),
        };
        DateTimeOffset start;
        if (duration is null)
        {
            start = completeFrom is null
                ? end - TimeSpan.FromHours(24)
                : Ceiling(completeFrom.Value, TimeSpan.FromMinutes(5));
            if (start >= end)
                start = end - TimeSpan.FromMinutes(5);
            long baseBuckets = Math.Max(1, (long)Math.Ceiling(
                (end - start) / TimeSpan.FromMinutes(5)));
            long multiplier = Math.Max(1, (long)Math.Ceiling(
                (double)baseBuckets / 60));
            width = TimeSpan.FromMinutes(checked(5 * multiplier));
        }
        else
        {
            start = end - duration.Value;
        }
        return new(
            range,
            start,
            end,
            width,
            duration is null ? null : start - duration.Value,
            duration is null ? null : start);
    }

    private static string NormalizeRange(string? value)
        => (value ?? "24h").Trim().ToLowerInvariant() switch
        {
            "24h" => "24h",
            "7d" => "7d",
            "30d" => "30d",
            "90d" => "90d",
            "1y" => "1y",
            "all" => "all",
            _ => throw new ArgumentException("Range must be one of 24h, 7d, 30d, 90d, 1y, or all.", nameof(value)),
        };

    private static DateTimeOffset Ceiling(DateTimeOffset value, TimeSpan width)
    {
        long ticks = value.UtcTicks;
        long remainder = ticks % width.Ticks;
        return new DateTimeOffset(
            remainder == 0 ? ticks : checked(ticks + width.Ticks - remainder),
            TimeSpan.Zero);
    }

    private sealed record RangeDefinition(
        string Range,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        TimeSpan BucketWidth,
        DateTimeOffset? ComparisonStartUtc,
        DateTimeOffset? ComparisonEndUtc);
}
