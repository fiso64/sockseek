using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;

using File = System.IO.File;
using Directory = System.IO.Directory;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers;
using Sockseek.Core.Transfers.Downloads.State;
using Sockseek.Core.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Core.Services;


public sealed record ExactFileTransferResult(
    string OutputPath,
    PeerFileTarget Target,
    Guid? TransferId = null,
    int AttemptCount = 0);

public enum ExactFileTransferStatus
{
    Completed,
    AlreadyExists,
    ManuallySkipped,
}

public sealed record ExactFileTransferOutcome(
    ExactFileTransferStatus Status,
    ExactFileTransferResult? Result,
    PeerFileTarget Target)
{
    public static ExactFileTransferOutcome Completed(ExactFileTransferResult result)
        => new(ExactFileTransferStatus.Completed, result, result.Target);

    public static ExactFileTransferOutcome ManuallySkipped(PeerFileTarget target)
        => new(ExactFileTransferStatus.ManuallySkipped, null, target);

    public static ExactFileTransferOutcome AlreadyExists(PeerFileTarget target, string outputPath)
        => new(
            ExactFileTransferStatus.AlreadyExists,
            new ExactFileTransferResult(outputPath, target),
            target);
}

public sealed class ExactPeerFileTransferRunner
{
    private readonly ISoulseekClient client;
    private readonly SoulseekClientManager clientManager;
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly DownloadEvents events;
    private readonly StaleDownloadCoordinator staleDownloads;
    private readonly DownloadFileOperations fileOperations;
    private readonly ILogger<ExactPeerFileTransferRunner> logger;

    internal ExactPeerFileTransferRunner(ISoulseekClient client,
                        SoulseekClientManager clientManager,
                        ActiveDownloadTracker activeDownloads,
                        DownloadEvents events,
                        StaleDownloadCoordinator staleDownloads,
                        DownloadFileOperations fileOperations,
                        ILogger<ExactPeerFileTransferRunner>? logger = null)
    {
        this.client = client;
        this.clientManager = clientManager;
        this.activeDownloads = activeDownloads;
        this.events = events;
        this.staleDownloads = staleDownloads;
        this.fileOperations = fileOperations;
        this.logger = logger ?? NullLogger<ExactPeerFileTransferRunner>.Instance;
    }

    public async Task<ExactFileTransferOutcome> DownloadFile(
        PeerFileTarget target,
        string outputPath,
        FileDownloadJob owner,
        TransferSettings transfer,
        string? parentDir,
        int maxStaleTimeMs,
        CancellationToken? ct = null,
        bool publishToDuplicateCache = true,
        Job? parentJob = null,
        bool deferTerminalCompletion = false,
        bool allowOverwrite = true,
        bool protectPublishedOutput = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        if (maxStaleTimeMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStaleTimeMs));

        CancellationToken cancellationToken = ct ?? CancellationToken.None;
        using var outputClaim = await fileOperations.ClaimOutputPathAsync(
            outputPath,
            cancellationToken).ConfigureAwait(false);

        // Cached work remains usable while disconnected. The cheap presence
        // probe is only an optimization; PrepareAsync rechecks under the owner.
        if (!clientManager.IsConnectedAndLoggedIn || fileOperations.MayHaveReusable(target))
        {
            var reused = await fileOperations.TryReuseAsync(target, outputPath, owner.Id,
                publishToDuplicateCache, allowOverwrite, protectPublishedOutput);
            if (reused != null) return reused;
        }

        await clientManager.WaitUntilReadyAsync(cancellationToken);
        string incompleteOutputPath = transfer.NoIncompleteExt ? outputPath : outputPath + ".incomplete";
        var transferId = TransferIds.New();
        int attemptCount = 0;
        Guid currentAttemptId = Guid.Empty;
        long totalBytes = target.Size ?? 0;

        StaleDownloadCoordinator.PeerTransferActivity? staleActivity = null;
        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                staleActivity?.ReportState(state.Transfer);
                events.RaiseDownloadStateChangedWithSpeed(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    state.Transfer.State,
                    state.Transfer.BytesTransferred,
                    target.Size ?? 0,
                    state.Transfer.AverageSpeed);
            },
            progressUpdated: (progress) =>
            {
                staleActivity?.ReportProgress(progress.Transfer);
                owner.BytesTransferred = progress.Transfer.BytesTransferred;
                events.RaiseDownloadProgressWithSpeed(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    progress.Transfer.BytesTransferred,
                    target.Size ?? 0,
                    progress.Transfer.AverageSpeed);
            }
        );

        ActiveDownload? activeDownload = null;
        async ValueTask DeleteIncompleteDownloadAfterFailureAsync()
        {
            try
            {
                await fileOperations.DeleteIncompleteAsync(
                    incompleteOutputPath, CleanupRootForIncompletePath(incompleteOutputPath, parentDir));
            }
            catch (Exception ex)
            {
                DownloadLogMessages.CleanupFailed(
                    logger,
                    "incomplete-transfer",
                    ex.GetType().Name);
            }
        }

        var prepared = await fileOperations.PrepareAsync(target, outputPath, incompleteOutputPath, owner.Id,
            publishToDuplicateCache, allowOverwrite, protectPublishedOutput);
        if (prepared.Reused != null) return prepared.Reused;

        try
        {
            using var downloadCts = ct != null
                ? CancellationTokenSource.CreateLinkedTokenSource((CancellationToken)ct)
                : new CancellationTokenSource();

            await using var output = new DownloadOutput(
                prepared.Output!, fileOperations);
            Stream outputStream = output.Stream;

            owner.UpdateActivity(JobActivityPhase.Downloading);
            owner.FileSize = target.Size;
            activeDownload = new ActiveDownload(transferId, owner, target, outputPath, downloadCts, parentJob);
            activeDownloads.TryAdd(activeDownload);

            events.RaiseDownloadStarted(transferId, owner, target, outputPath);

            int maxRetries = checked(transfer.UnknownErrorRetries + 1);
            while (true)
            {
                try
                {
                    attemptCount++;
                    currentAttemptId = TransferAttemptIds.New();
                    events.RaiseTransferAttemptStarted(
                        transferId,
                        currentAttemptId,
                        attemptCount,
                        owner,
                        target,
                        outputPath,
                        incompleteOutputPath);
                    await staleDownloads.WatchPeerTransferAsync(
                        activeDownload,
                        maxStaleTimeMs,
                        async activity =>
                        {
                            staleActivity = activity;
                            try
                            {
                                return await client.DownloadAsync(target.Username, target.Filename,
                                    () => Task.FromResult((Stream)outputStream),
                                    target.Size,
                                    startOffset: outputStream.Position,
                                    options: transferOptions,
                                    cancellationToken: downloadCts.Token);
                            }
                            finally
                            {
                                staleActivity = null;
                            }
                        });
                    events.RaiseTransferAttemptCompleted(
                        transferId,
                        currentAttemptId,
                        attemptCount,
                        owner,
                        target,
                        outputPath);
                    break;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    bool canRetry = IsRetryableTransferFailure(e)
                        && attemptCount < maxRetries;

                    DownloadLogMessages.TransferAttemptFailed(
                        logger,
                        e,
                        transferId,
                        attemptCount,
                        maxRetries,
                        owner.Id);
                    events.RaiseTransferAttemptFailed(
                        transferId,
                        currentAttemptId,
                        attemptCount,
                        owner,
                        target,
                        outputPath,
                        e);
                    events.RaiseDownloadAttemptFailed(transferId, owner, target, outputPath, incompleteOutputPath, attemptCount, maxRetries, e);

                    if (!canRetry)
                        throw;

                    if (!clientManager.IsConnectedAndLoggedIn)
                        await clientManager.WaitUntilReadyAsync(downloadCts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (activeDownload?.IsStaleCancelled == true)
        {
            await DeleteIncompleteDownloadAfterFailureAsync();

            var staleException = new StaleDownloadException(
                target,
                activeDownload.StaleMaxStaleTimeMs ?? maxStaleTimeMs);
            events.RaiseDownloadAttemptFailed(transferId, owner, target, outputPath, incompleteOutputPath, Math.Max(attemptCount, 1), Math.Max(attemptCount, 1), staleException);
            events.RaiseTransferAttemptCancelled(
                transferId,
                currentAttemptId,
                Math.Max(attemptCount, 1),
                owner,
                target,
                outputPath,
                TransferCancellationReason.Stale);
            events.RaiseTransferFailed(
                transferId,
                owner,
                target,
                outputPath,
                owner.BytesTransferred,
                totalBytes,
                Math.Max(attemptCount, 1),
                TransferFailureReason.Stale,
                staleException);
            throw staleException;
        }
        catch (Exception ex)
        {
            await DeleteIncompleteDownloadAfterFailureAsync();

            if (activeDownloads.TryRemove(transferId, out var ad) && ad.IsManuallySkipped)
            {
                events.RaiseTransferAttemptCancelled(
                    transferId,
                    currentAttemptId,
                    Math.Max(attemptCount, 1),
                    owner,
                    target,
                    outputPath,
                    TransferCancellationReason.ManualSkip);
                events.RaiseTransferCancelled(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    owner.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferCancellationReason.ManualSkip);
                return ExactFileTransferOutcome.ManuallySkipped(target);
            }

            bool cancellationRequested = ex is OperationCanceledException
                && (activeDownload?.Cts.IsCancellationRequested == true
                    || ct?.IsCancellationRequested == true);
            if (cancellationRequested)
            {
                events.RaiseTransferAttemptCancelled(
                    transferId,
                    currentAttemptId,
                    Math.Max(attemptCount, 1),
                    owner,
                    target,
                    outputPath,
                    TransferCancellationReason.Requested);
                events.RaiseTransferCancelled(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    owner.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferCancellationReason.Requested);
            }
            else
            {
                if (ex is OperationCanceledException)
                {
                    events.RaiseTransferAttemptFailed(
                        transferId,
                        currentAttemptId,
                        Math.Max(attemptCount, 1),
                        owner,
                        target,
                        outputPath,
                        ex);
                }
                events.RaiseTransferFailed(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    owner.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferFailureReason.PeerFailure,
                    ex);
            }

            throw;
        }


        long completedBytes;
        long PublishOutput(DownloadFileOperations.Cache cache)
        {
            if (!transfer.NoIncompleteExt)
            {
                cache.InvalidatePath(outputPath);
                Utils.Move(incompleteOutputPath, outputPath);
            }
            if (publishToDuplicateCache) cache.Publish(outputPath, target);
            return !deferTerminalCompletion && File.Exists(outputPath)
                ? new FileInfo(outputPath).Length : target.Size ?? owner.BytesTransferred;
        }
        if (!transfer.NoIncompleteExt)
        {
            try
            {
                completedBytes = await fileOperations.WithCacheAsync(PublishOutput);
            }
            catch (Exception ex)
            {
                activeDownloads.TryRemove(transferId, out _);
                try
                {
                    await fileOperations.DeleteIncompleteAsync(incompleteOutputPath);
                }
                catch (Exception cleanupEx)
                {
                    DownloadLogMessages.CleanupFailed(
                        logger,
                        "finalization-incomplete-transfer",
                        cleanupEx.GetType().Name);
                }

                var finalizationException = new IOException(
                    $"Failed to rename incomplete file from '{incompleteOutputPath}' to '{outputPath}'.",
                    ex);
                events.RaiseTransferFailed(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    owner.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    TransferFailureReason.Finalization,
                    finalizationException);
                throw finalizationException;
            }
        }

        else completedBytes = await fileOperations.WithCacheAsync(PublishOutput);

        var result = new ExactFileTransferResult(outputPath, target, transferId, Math.Max(attemptCount, 1));
        activeDownloads.TryRemove(transferId, out _);

        if (target.Size is > 0)
            owner.BytesTransferred = target.Size.Value;

        if (!deferTerminalCompletion)
        {
            events.RaiseTransferCompleted(
                transferId,
                owner,
                target,
                outputPath,
                completedBytes,
                Math.Max(attemptCount, 1));
        }

        return ExactFileTransferOutcome.Completed(result);
    }

    private static bool IsRetryableTransferFailure(Exception exception)
        => exception is not (OperationCanceledException
            or FileNotFoundException
            or UserNotFoundException
            or TransferRejectedException
            or StaleDownloadException);

    private sealed class DownloadOutput(Stream stream, DownloadFileOperations files) : IAsyncDisposable
    {
        public Stream Stream { get; } = stream;

        public ValueTask DisposeAsync()
        {
            if (!Stream.CanWrite)
                return ValueTask.CompletedTask;

            // Native close can block even through FileStream.DisposeAsync.
            // Reuse the run's isolated workers and await close before cleanup,
            // rename or terminal publication.
            return files.CloseAsync(Stream);
        }
    }


    static string GetStateLabel(TransferStates s)
    {
        if (s.HasFlag(TransferStates.InProgress))   return "InProgress";
        if (s.HasFlag(TransferStates.Queued))
            return s.HasFlag(TransferStates.Remotely) ? "Queued (R)" :
                   s.HasFlag(TransferStates.Locally)  ? "Queued (L)" : "Queued";
        if (s.HasFlag(TransferStates.Initializing)) return "Initialising";
        return "Requested";
    }

    private static string CleanupRootForIncompletePath(string path, string? parentDir)
    {
        if (string.IsNullOrWhiteSpace(parentDir))
            return "";

        var stagingRoot = Path.Join(parentDir, ".sockseek-staging");
        return Utils.IsInDirectory(path, stagingRoot, strict: true)
            ? stagingRoot
            : parentDir;
    }
}
