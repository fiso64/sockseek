using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using System.Text.RegularExpressions;

namespace Sockseek.Persistence.Sharing;

public sealed record ShareScanState(
    Guid? GenerationId,
    ShareScanPhase Phase,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    ShareScanResult? Result,
    string? ErrorCode,
    ShareScanProgress? Progress = null);

public sealed class ShareScanAlreadyRunningException()
    : InvalidOperationException("A share scan is already running.");

/// <summary>
/// Owns the one-at-a-time scan transaction from a fresh SQLite generation
/// through artifact validation and atomic manager publication.
/// </summary>
public sealed class ShareScanCoordinator
{
    private readonly object sync = new();
    private readonly ShareCatalogManager manager;
    private readonly ShareScanner scanner;
    private readonly ISoulseekBrowseArtifactBuilder artifactBuilder;
    private CancellationTokenSource? activeCancellation;
    private ShareScanState state = new(null, ShareScanPhase.Idle, null, null, null, null);

    public ShareScanCoordinator(
        ShareCatalogManager manager,
        ShareScanner? scanner = null,
        ISoulseekBrowseArtifactBuilder? artifactBuilder = null)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.scanner = scanner ?? new ShareScanner();
        this.artifactBuilder = artifactBuilder ?? new SoulseekBrowseArtifactBuilder();
    }

    public event Action<ShareScanState>? StateChanged;

    public ShareScanState State
    {
        get
        {
            lock (sync)
                return state;
        }
    }

    public async ValueTask<ShareScanResult> ScanAsync(
        SharingSettings settings,
        string settingsHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsHash);

        Guid generationId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var totalDuration = System.Diagnostics.Stopwatch.StartNew();
        CancellationTokenSource linked;
        lock (sync)
        {
            if (activeCancellation is not null)
                throw new ShareScanAlreadyRunningException();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            activeCancellation = linked;
        }

        var paths = manager.GetGenerationPaths(generationId);
        SetState(new(
            generationId,
            ShareScanPhase.Preparing,
            startedAt,
            null,
            null,
            null));

        try
        {
            await using var writer = await SqliteShareCatalogBuilder.CreateAsync(
                paths.DatabasePath,
                linked.Token).ConfigureAwait(false);

            SetPhase(ShareScanPhase.Enumerating);
            ShareScanResult result = await scanner.ScanAsync(
                settings,
                writer,
                generationId,
                settingsHash,
                linked.Token,
                progress => SetProgress(progress)).ConfigureAwait(false);

            SetPhase(ShareScanPhase.FinalizingIndex);
            var phaseDuration = System.Diagnostics.Stopwatch.StartNew();
            await writer.PrepareForReadAsync(linked.Token).ConfigureAwait(false);
            phaseDuration.Stop();
            TimeSpan databaseFinalizationElapsed = phaseDuration.Elapsed;

            SetPhase(ShareScanPhase.BuildingBrowseArtifact);
            phaseDuration.Restart();
            await using var stagingReader = await SqliteShareCatalogReader.OpenStagingAsync(
                paths.DatabasePath,
                result.ProvisionalMetadata,
                linked.Token).ConfigureAwait(false);
            ShareCatalogMetadata finalMetadata;
            try
            {
                ShareBrowseArtifact artifact = await artifactBuilder.BuildAsync(
                    stagingReader,
                    paths.ArtifactPath,
                    linked.Token).ConfigureAwait(false);
                finalMetadata = result.ProvisionalMetadata with
                {
                    BrowseStatus = ShareBrowseStatus.Ready,
                    BrowseWireVersion = artifact.WireVersion,
                    BrowseLengthBytes = artifact.Length,
                    BrowseSha256 = artifact.Sha256,
                };
            }
            catch (BrowseArtifactOversizeException)
            {
                TryDelete(paths.ArtifactPath);
                finalMetadata = result.ProvisionalMetadata with
                {
                    BrowseStatus = ShareBrowseStatus.UnavailableOversize,
                    BrowseWireVersion = null,
                    BrowseLengthBytes = null,
                    BrowseSha256 = null,
                };
            }
            phaseDuration.Stop();
            TimeSpan browseArtifactBuildElapsed = phaseDuration.Elapsed;

            SetPhase(ShareScanPhase.Validating);
            phaseDuration.Restart();
            await writer.CompleteAsync(finalMetadata, linked.Token).ConfigureAwait(false);
            phaseDuration.Stop();
            databaseFinalizationElapsed += phaseDuration.Elapsed;

            SetPhase(ShareScanPhase.Publishing);
            ShareCatalogPublicationTiming publicationTiming =
                await manager.PublishAsync(
                new ShareCatalogPublication(
                    generationId,
                    paths.DatabasePath,
                    paths.ArtifactPath,
                    finalMetadata),
                linked.Token).ConfigureAwait(false);

            totalDuration.Stop();
            result = result with
            {
                ProvisionalMetadata = finalMetadata,
                DatabaseFinalizationElapsed = databaseFinalizationElapsed,
                BrowseArtifactBuildElapsed = browseArtifactBuildElapsed,
                ValidationElapsed = publicationTiming.ValidationElapsed,
                PublicationElapsed = publicationTiming.PublicationElapsed,
                TotalElapsed = totalDuration.Elapsed,
            };
            SetState(new(
                generationId,
                ShareScanPhase.Completed,
                startedAt,
                DateTimeOffset.UtcNow,
                result,
                null));
            SharingTelemetry.RecordScan(totalDuration.Elapsed);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            CleanupUnpublished(paths);
            SetState(new(
                generationId,
                ShareScanPhase.Cancelled,
                startedAt,
                DateTimeOffset.UtcNow,
                null,
                "Cancelled"));
            SharingTelemetry.RecordScanResult("cancelled");
            throw;
        }
        catch (RegexMatchTimeoutException)
        {
            CleanupUnpublished(paths);
            SetState(new(
                generationId,
                ShareScanPhase.Failed,
                startedAt,
                DateTimeOffset.UtcNow,
                null,
                "FilterTimeout"));
            SharingTelemetry.RecordScanResult("failed");
            throw;
        }
        catch
        {
            CleanupUnpublished(paths);
            SetState(new(
                generationId,
                ShareScanPhase.Failed,
                startedAt,
                DateTimeOffset.UtcNow,
                null,
                "ScanFailed"));
            SharingTelemetry.RecordScanResult("failed");
            throw;
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(activeCancellation, linked))
                    activeCancellation = null;
            }
            linked.Dispose();
        }
    }

    public bool Cancel()
    {
        CancellationTokenSource? cancellation;
        ShareScanState next;
        lock (sync)
        {
            cancellation = activeCancellation;
            if (cancellation is null
                || state.Phase is ShareScanPhase.Cancelling
                    or ShareScanPhase.Completed
                    or ShareScanPhase.Cancelled
                    or ShareScanPhase.Failed)
            {
                return false;
            }
            next = state with { Phase = ShareScanPhase.Cancelling };
            state = next;
        }

        Publish(next);
        cancellation.Cancel();
        return true;
    }

    private void SetPhase(ShareScanPhase phase)
    {
        ShareScanState next;
        lock (sync)
        {
            next = state with { Phase = phase };
            state = next;
        }
        Publish(next);
    }

    private void SetProgress(ShareScanProgress progress)
    {
        ShareScanState next;
        lock (sync)
        {
            if (state.Phase != ShareScanPhase.Enumerating)
                return;
            next = state with { Progress = progress };
            state = next;
        }
        Publish(next);
    }

    private void SetState(ShareScanState next)
    {
        lock (sync)
            state = next;
        Publish(next);
    }

    private void Publish(ShareScanState next)
    {
        Action<ShareScanState>? handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (Action<ShareScanState> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(next);
            }
            catch
            {
                // Scan state observers are projections and cannot arbitrate the
                // generation transaction.
            }
        }
    }

    private static void CleanupUnpublished((string DatabasePath, string ArtifactPath) paths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TryDelete(paths.DatabasePath);
        TryDelete(paths.DatabasePath + "-wal");
        TryDelete(paths.DatabasePath + "-shm");
        TryDelete(paths.ArtifactPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Startup orphan cleanup retries recognized generation files.
        }
        catch (UnauthorizedAccessException)
        {
            // Startup orphan cleanup retries recognized generation files.
        }
    }
}
