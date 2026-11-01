using Microsoft.EntityFrameworkCore;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Read;

public sealed record TransferAnalyticsQuery(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimeSpan BucketWidth,
    int TopCount = 10,
    bool IncludeRankings = true);

public sealed record PersistedTransferAnalyticsCoverage(
    DateTimeOffset CompleteFromUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PersistedTransferAnalyticsBucket(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long DownloadBytes,
    long UploadBytes);

public sealed record PersistedTransferPeerAggregate(
    string Direction,
    string Username,
    long Bytes,
    int SuccessfulFiles);

public sealed record PersistedTransferContentAggregate(
    string Identity,
    string DisplayPath,
    int DownloadCount,
    int DistinctPeerCount);

public sealed record PersistedTransferErrorAggregate(
    string Reason,
    int Count,
    DateTimeOffset LastSeenUtc);

public sealed record PersistedTransferAnalytics(
    IReadOnlyList<PersistedTransferAnalyticsBucket> Buckets,
    long DownloadBytes,
    long UploadBytes,
    int DownloadedFiles,
    int UploadedFiles,
    int DistinctPeers,
    IReadOnlyList<PersistedTransferPeerAggregate> Peers,
    IReadOnlyList<PersistedTransferContentAggregate> Content,
    IReadOnlyList<PersistedTransferErrorAggregate> Errors);

public sealed record PersistedTransferAnalyticsSnapshot(
    PersistedTransferAnalyticsCoverage Coverage,
    IReadOnlyList<PersistedTransferAnalytics> Ranges);

public interface ITransferAnalyticsReader
{
    /// <summary>Plan ranges from retained coverage, then read them from that same snapshot.</summary>
    Task<PersistedTransferAnalyticsSnapshot> GetSnapshotAsync(
        Func<PersistedTransferAnalyticsCoverage, IReadOnlyList<TransferAnalyticsQuery>> planRanges,
        CancellationToken cancellationToken = default);
}

public sealed class TransferAnalyticsReader(
    IDbContextFactory<SockseekDbContext> contextFactory) : ITransferAnalyticsReader
{
    public const int MaximumTopCount = 25;
    public const int MaximumBucketCount = 120;

    public async Task<PersistedTransferAnalyticsSnapshot> GetSnapshotAsync(
        Func<PersistedTransferAnalyticsCoverage, IReadOnlyList<TransferAnalyticsQuery>> planRanges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planRanges);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var state = await context.TransferAccountingStates.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
            throw new InvalidOperationException("Transfer accounting coverage state is unavailable.");
        var coverage = new PersistedTransferAnalyticsCoverage(
            DateTimeOffset.FromUnixTimeMilliseconds(state.CompleteFromUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(state.UpdatedAtUtc));
        // The planner only constructs query values. No external I/O or public
        // resource lease spans this short database read transaction.
        var queries = planRanges(coverage);
        ArgumentNullException.ThrowIfNull(queries);
        foreach (var query in queries) Validate(query);
        var ranges = new List<PersistedTransferAnalytics>(queries.Count);
        foreach (var query in queries)
            ranges.Add(await ReadRangeAsync(context, query, cancellationToken).ConfigureAwait(false));
        await snapshot.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(coverage, ranges);
    }

    public async Task<PersistedTransferAnalytics> GetAsync(
        TransferAnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        Validate(query);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await SqliteReadSnapshot.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        var range = await ReadRangeAsync(context, query, cancellationToken).ConfigureAwait(false);
        await snapshot.CommitAsync(cancellationToken).ConfigureAwait(false);
        return range;
    }

    private static async Task<PersistedTransferAnalytics> ReadRangeAsync(
        SockseekDbContext context, TransferAnalyticsQuery query, CancellationToken cancellationToken)
    {
        long start = query.StartUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        long end = query.EndUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        long width = checked((long)query.BucketWidth.TotalMilliseconds);
        int bucketCount = checked((int)Math.Ceiling((double)(end - start) / width));

        var byteRows = await context.TransferByteBuckets.AsNoTracking()
            .Where(row => row.BucketStartUtc >= start && row.BucketStartUtc < end)
            .GroupBy(row => new
            {
                Bucket = (row.BucketStartUtc - start) / width,
                row.Direction,
            })
            .Select(group => new
            {
                group.Key.Bucket,
                group.Key.Direction,
                Bytes = group.Sum(row => row.Bytes),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var bucketBytes = byteRows.ToDictionary(
            row => (checked((int)row.Bucket), row.Direction),
            row => row.Bytes);
        var buckets = Enumerable.Range(0, bucketCount)
            .Select(index =>
            {
                long bucketStart = checked(start + index * width);
                long bucketEnd = Math.Min(end, checked(bucketStart + width));
                return new PersistedTransferAnalyticsBucket(
                    DateTimeOffset.FromUnixTimeMilliseconds(bucketStart),
                    DateTimeOffset.FromUnixTimeMilliseconds(bucketEnd),
                    bucketBytes.GetValueOrDefault((index, "Download")),
                    bucketBytes.GetValueOrDefault((index, "Upload")));
            })
            .ToArray();

        var successfulTransfers = context.Transfers.AsNoTracking()
            .Where(row => row.TerminalOutcome == "Succeeded"
                && row.CompletedAtUtc >= start
                && row.CompletedAtUtc < end);
        IReadOnlyList<PersistedTransferPeerAggregate> peers = [];
        IReadOnlyList<PersistedTransferContentAggregate> content = [];
        IReadOnlyList<PersistedTransferErrorAggregate> errors = [];
        if (query.IncludeRankings)
        {
            var peerBytes = await context.TransferByteBuckets.AsNoTracking()
                .Where(row => row.BucketStartUtc >= start
                    && row.BucketStartUtc < end
                    && row.Username != "")
                .GroupBy(row => new { row.Direction, row.Username })
                .Select(group => new
                {
                    group.Key.Direction,
                    group.Key.Username,
                    Bytes = group.Sum(row => row.Bytes),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var successfulByPeer = await successfulTransfers
                .Where(row => row.Username != null)
                .GroupBy(row => new { row.Direction, row.Username })
                .Select(group => new
                {
                    group.Key.Direction,
                    Username = group.Key.Username!,
                    Count = group.Count(),
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var successfulCounts = successfulByPeer.ToDictionary(
                row => (row.Direction, row.Username),
                row => row.Count);
            peers = peerBytes
                .GroupBy(row => row.Direction, StringComparer.Ordinal)
                .SelectMany(group => group
                    .OrderByDescending(row => row.Bytes)
                    .ThenBy(row => row.Username, StringComparer.Ordinal)
                    .Take(query.TopCount))
                .Select(row => new PersistedTransferPeerAggregate(
                    row.Direction,
                    row.Username,
                    row.Bytes,
                    successfulCounts.GetValueOrDefault((row.Direction, row.Username))))
                .ToArray();

            var contentRows = await successfulTransfers
                .Where(row => row.Direction == "Upload" && row.GroupRef != null)
                .GroupBy(row => row.GroupRef!)
                .Select(group => new
                {
                    Identity = group.Key,
                    DisplayPath = group.Max(row => row.GroupDisplayPath) ?? group.Key,
                    DownloadCount = group.Count(),
                    DistinctPeerCount = group.Where(row => row.Username != null)
                        .Select(row => row.Username)
                        .Distinct()
                        .Count(),
                })
                .OrderByDescending(row => row.DownloadCount)
                .ThenBy(row => row.Identity)
                .Take(query.TopCount)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var errorRows = await context.TransferAttempts.AsNoTracking()
                .Where(row => row.State == "Failed"
                    && row.CompletedAtUtc >= start
                    && row.CompletedAtUtc < end)
                .GroupBy(row => row.FailureReason)
                .Select(group => new
                {
                    Reason = group.Key,
                    Count = group.Count(),
                    LastSeen = group.Max(row => row.CompletedAtUtc)!.Value,
                })
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Reason)
                .Take(query.TopCount)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            content = contentRows.Select(row => new PersistedTransferContentAggregate(
                row.Identity, row.DisplayPath, row.DownloadCount, row.DistinctPeerCount)).ToArray();
            errors = errorRows.Select(row => new PersistedTransferErrorAggregate(
                row.Reason, row.Count, DateTimeOffset.FromUnixTimeMilliseconds(row.LastSeen))).ToArray();
        }

        long downloadBytes = buckets.Sum(row => row.DownloadBytes);
        long uploadBytes = buckets.Sum(row => row.UploadBytes);
        int downloadedFiles = await successfulTransfers.CountAsync(
            row => row.Direction == "Download",
            cancellationToken).ConfigureAwait(false);
        int uploadedFiles = await successfulTransfers.CountAsync(
            row => row.Direction == "Upload",
            cancellationToken).ConfigureAwait(false);
        int distinctPeers = await context.TransferByteBuckets.AsNoTracking()
            .Where(row => row.BucketStartUtc >= start
                && row.BucketStartUtc < end
                && row.Username != "")
            .Select(row => row.Username)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        return new(
            buckets,
            downloadBytes,
            uploadBytes,
            downloadedFiles,
            uploadedFiles,
            distinctPeers,
            peers,
            content,
            errors);
    }

    private static void Validate(TransferAnalyticsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.StartUtc >= query.EndUtc)
            throw new ArgumentException("The analytics range must have positive duration.", nameof(query));
        if (query.BucketWidth < TimeSpan.FromMinutes(5)
            || query.BucketWidth.Ticks % TimeSpan.FromMinutes(5).Ticks != 0)
            throw new ArgumentException("Bucket width must be a positive multiple of five minutes.", nameof(query));
        double bucketCount = Math.Ceiling((query.EndUtc - query.StartUtc) / query.BucketWidth);
        if (bucketCount > MaximumBucketCount)
            throw new ArgumentException($"The analytics response may contain at most {MaximumBucketCount} buckets.", nameof(query));
        if (query.TopCount is < 1 or > MaximumTopCount)
            throw new ArgumentOutOfRangeException(nameof(query));
    }
}
