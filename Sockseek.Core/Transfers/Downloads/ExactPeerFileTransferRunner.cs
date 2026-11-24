using System.Net.Sockets;
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

internal sealed class DownloadCancelledException(OperationCanceledException cause)
    : OperationCanceledException("The download was cancelled.", cause, cause.CancellationToken);


public sealed record ExactFileTransferResult(
    string OutputPath,
    PeerFileTarget Target,
    Guid? TransferId = null,
    int AttemptCount = 0)
{
    // Completed exact-file work without a network transfer came from a local copy.
    public bool Reused => TransferId == null;
}

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
    private readonly PeerTransferMonitor peerTransfers;
    private readonly DownloadFileOperations fileOperations;
    private readonly ILogger<ExactPeerFileTransferRunner> logger;

    private readonly CancellationToken shutdown;

    internal ExactPeerFileTransferRunner(ISoulseekClient client,
                        SoulseekClientManager clientManager,
                        ActiveDownloadTracker activeDownloads,
                        DownloadEvents events,
                        PeerTransferMonitor peerTransfers,
                        DownloadFileOperations fileOperations,
                        ILogger<ExactPeerFileTransferRunner>? logger = null, CancellationToken shutdown = default)
    {
        this.shutdown = shutdown;
        this.client = client;
        this.clientManager = clientManager;
        this.activeDownloads = activeDownloads;
        this.events = events;
        this.peerTransfers = peerTransfers;
        this.fileOperations = fileOperations;
        this.logger = logger ?? NullLogger<ExactPeerFileTransferRunner>.Instance;
    }

    internal static string IncompletePath(string outputPath, bool noIncompleteExt, Guid? selectionId = null)
    {
        if (noIncompleteExt) return outputPath;
        string name = selectionId?.ToString("N") ?? Path.GetFileName(outputPath);
        return Path.Combine(Path.GetDirectoryName(outputPath)!, Utils.FitPathComponent(name + OutputStaging.IncompleteExtension));
    }

    // TODO [ARCHITECTURE]: Split this method. Output staging, the attempt/retry loop and the
    // terminal classification (who cancelled, why it failed) are interleaved
    // through nested lambdas, local functions and three catch blocks.
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
        bool allowReuse = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        if (maxStaleTimeMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStaleTimeMs));

        // Interactive selections settle filename collisions with the finished bytes.
        bool selectionLayout = owner.IsHandPicked || parentJob?.IsHandPicked == true;
        CancellationToken cancellationToken = ct ?? CancellationToken.None;
        using var outputClaim = await fileOperations.ClaimOutputPathAsync(
            outputPath,
            cancellationToken).ConfigureAwait(false);

        var transferId = TransferIds.New();
        string directory = Path.GetDirectoryName(outputPath)!;
        string incompleteOutputPath = IncompletePath(outputPath, transfer.NoIncompleteExt, selectionLayout ? transferId : null);
        DownloadFileOperations.DownloadOutput preparedOutput;
        try
        {
            // Cached work remains usable while disconnected. The cheap presence
            // probe is only an optimization; PrepareAsync rechecks under the owner.
            if (!clientManager.IsConnectedAndLoggedIn || (allowReuse && fileOperations.MayHaveReusable(target, owner.WorkflowId)))
            {
                var reused = await fileOperations.TryReuseAsync(target, outputPath, owner.Id, owner.WorkflowId,
                    publishToDuplicateCache, allowOverwrite, allowReuse, owner as SongJob, selectionLayout);
                if (reused != null) return reused;
            }

            await clientManager.WaitUntilReadyAsync(cancellationToken);
            var prepared = await fileOperations.PrepareAsync(target, outputPath, incompleteOutputPath, owner.Id, owner.WorkflowId,
                publishToDuplicateCache, allowOverwrite, allowReuse, owner as SongJob, selectionLayout);
            if (prepared.Reused != null) return prepared.Reused;
            preparedOutput = prepared.Output!;
        }
        catch (DownloadDestinationException ex)
        {
            events.RaiseTransferFailed(transferId, owner, target, outputPath, 0, target.Size ?? 0, 0,
                TransferFailureReason.LocalStorageError, ex);
            throw;
        }
        incompleteOutputPath = preparedOutput.Path;
        int attemptCount = 0;
        Guid currentAttemptId = Guid.Empty;
        long totalBytes = target.Size ?? 0;
        long lastProgressPublished = long.MinValue;

        PeerTransferMonitor.PeerTransferActivity? staleActivity = null;
        var transferOptions = new TransferOptions(
            disposeOutputStreamOnCompletion: false,
            stateChanged: (state) =>
            {
                staleActivity?.ReportState(state.Transfer);
                if (state.Transfer.Size is { } negotiatedSize && negotiatedSize >= 0)
                {
                    totalBytes = negotiatedSize;
                    owner.FileSize = negotiatedSize;
                }
                // The peer can finish before local writes, placement, or an attempt retry.
                // Only the owner of the output publishes the download’s terminal state.
                if (state.Transfer.State.HasFlag(TransferStates.Completed))
                {
                    owner.BytesTransferred = state.Transfer.BytesTransferred;
                    if (owner.BytesTransferred > 0)
                        events.RaiseDownloadProgress(transferId, owner, target, outputPath,
                            owner.BytesTransferred, totalBytes, state.Transfer.AverageSpeed);
                    return;
                }
                events.RaiseDownloadStateChanged(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    state.Transfer.State,
                    state.Transfer.BytesTransferred,
                    totalBytes,
                    state.Transfer.AverageSpeed);
            },
            progressUpdated: (progress) =>
            {
                staleActivity?.ReportProgress(progress.Transfer);
                owner.BytesTransferred = progress.Transfer.BytesTransferred;
                // Soulseek reports every network read. Keep liveness and byte
                // tracking exact, but don't rebuild public/persistent state for
                // every chunk. State transitions publish final progress as well.
                long now = Environment.TickCount64;
                if (lastProgressPublished != long.MinValue && now - lastProgressPublished < 250
                    && progress.Transfer.BytesTransferred != totalBytes)
                    return;
                lastProgressPublished = now;
                events.RaiseDownloadProgress(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    progress.Transfer.BytesTransferred,
                    totalBytes,
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

        void CancelAttempt(TransferCancellationReason reason)
            => events.RaiseTransferAttemptCancelled(transferId, currentAttemptId, Math.Max(attemptCount, 1),
                owner, target, outputPath, reason);

        void CancelTransfer(TransferCancellationReason reason)
        {
            CancelAttempt(reason);
            events.RaiseTransferCancelled(transferId, owner, target, outputPath, owner.BytesTransferred,
                totalBytes, Math.Max(attemptCount, 1), reason);
        }

        try
        {
            using var downloadCts = ct != null
                ? CancellationTokenSource.CreateLinkedTokenSource((CancellationToken)ct)
                : new CancellationTokenSource();

            await using var output = preparedOutput;
            Stream outputStream = output.Stream;

            owner.UpdateActivity(JobActivityPhase.Downloading);
            owner.FileSize = target.Size;
            activeDownload = new ActiveDownload(transferId, owner, target, outputPath, downloadCts, parentJob);
            activeDownloads.TryAdd(activeDownload);

            events.RaiseDownloadStarted(transferId, owner, target, outputPath);

            int maxRetries = checked(transfer.UnknownErrorRetries + 1);
            bool correctedSize = false;
            int peerWaits = 0;
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
                    var completed = await peerTransfers.WatchPeerTransferAsync(
                        activeDownload,
                        maxStaleTimeMs,
                        async activity =>
                        {
                            staleActivity = activity;
                            try
                            {
                                // Resume from the same peer to preserve progress. We accept that
                                // a same-size replacement at this path can mix two revisions.
                                return await client.DownloadAsync(target.Username, target.Filename,
                                    () => Task.FromResult((Stream)outputStream),
                                    target.Size,
                                    startOffset: outputStream.Position,
                                    options: transferOptions,
                                    cancellationToken: downloadCts.Token);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && FailureReason(ex) == TransferFailureReason.Denied)
                            {
                                throw new TransferRejectedException($"Peer '{target.Username}' refused the download. {DownloadOutcomes.FailureMessage(ex)}", ex);
                            }
                            finally
                            {
                                staleActivity = null;
                            }
                        }, client, position => events.RaiseDownloadQueuePosition(
                            transferId, owner, target, outputPath, position));
                    if (completed.Size is { } completedSize && completedSize != target.Size)
                    {
                        target = target.WithSize(completedSize);
                        owner.FileSize = totalBytes = completedSize;
                    }
                    if (target.Size is >= 0 && outputStream.Position != target.Size.Value)
                        throw new EndOfStreamException(
                            $"Received {outputStream.Position} of {target.Size.Value} expected bytes.");
                    await output.DisposeAsync();
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
                    var sizeChange = e as TransferSizeMismatchException;
                    bool correctSize = !correctedSize && sizeChange is { RemoteSize: > 0 }
                        && sizeChange.RemoteSize != target.Size;
                    if (correctSize) maxRetries = checked(maxRetries + 1);
                    bool canRetry = correctSize || IsRetryableTransferFailure(e)
                        && attemptCount < maxRetries;
                    // A download someone chose from this peer has no other source
                    // to fall back on. While the peer may still serve it, keep it
                    // and ask again later, as other clients do.
                    PeerWait wait = !correctSize && owner.IsHandPicked
                        ? PeerMayServeLater(e, retriesLeft: canRetry, peerWaits) : PeerWait.None;
                    canRetry |= wait != PeerWait.None;

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
                        e, FailureReason(e));
                    events.RaiseDownloadAttemptFailed(transferId, owner, target, outputPath, incompleteOutputPath, attemptCount, maxRetries, e);

                    if (!canRetry)
                        throw;

                    if (correctSize)
                    {
                        // Like Nicotine+, treat a positive negotiated size as newer
                        // than the search/browse listing, and discard partial data:
                        // resuming it could mix two versions of the remote file.
                        // Soulseek.NET throws before opening the stream, so this
                        // needs a new request. Allow one correction independently
                        // of network retries; a repeatedly changing file must fail.
                        // TODO: Soulseek.NET also rejects malformed zero sizes
                        // from some peers. Nicotine retains the known size in that
                        // case; we need upstream negotiation support to do so safely.
                        target = target.WithSize(sizeChange!.RemoteSize);
                        owner.FileSize = totalBytes = sizeChange.RemoteSize;
                        await fileOperations.ResetPartialAsync(outputStream);
                        owner.BytesTransferred = 0;
                        lastProgressPublished = long.MinValue;
                        correctedSize = true;
                    }

                    if (wait != PeerWait.None)
                    {
                        // TODO [BEHAVIOUR]: A waiting download keeps its job slot, as one
                        // queued remotely does. Twenty files waiting for one peer stall
                        // every other download; waiting should not count against
                        // ConcurrentJobs.
                        events.RaiseDownloadWaitingForPeer(transferId, owner, target, outputPath);
                        await peerTransfers.WaitForPeerAsync(client, target.Username,
                            untilOnline: wait == PeerWait.UntilOnline, peerWaits++, downloadCts.Token);
                    }

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
            CancelAttempt(TransferCancellationReason.Stale);
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
            bool stopped = ex is OperationCanceledException && shutdown.IsCancellationRequested
                && activeDownload?.UserCancelled != true
                && owner.CancellationSource == JobCancellationSource.None
                && (parentJob is null || parentJob.CancellationSource == JobCancellationSource.None);
            await DeleteIncompleteDownloadAfterFailureAsync();

            if (activeDownloads.TryRemove(transferId, out var ad) && ad.IsManuallySkipped)
            {
                CancelTransfer(TransferCancellationReason.ManualSkip);
                return ExactFileTransferOutcome.ManuallySkipped(target);
            }

            if (ex is OperationCanceledException cancelled
                && (activeDownload?.Cts.IsCancellationRequested == true || ct?.IsCancellationRequested == true))
            {
                // A cancelled token alone does not say who asked. Only an explicit
                // request on this transfer or its jobs is the user's.
                bool requested = activeDownload?.UserCancelled == true
                    || IsUserRequest(owner.CancellationSource) || IsUserRequest(parentJob?.CancellationSource);
                CancelTransfer(stopped ? TransferCancellationReason.DaemonShutdown
                    : requested ? TransferCancellationReason.Requested : TransferCancellationReason.OwningJob);
                if (ct?.IsCancellationRequested != true)
                    throw new DownloadCancelledException(cancelled);
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
                        ex, FailureReason(ex));
                }
                events.RaiseTransferFailed(
                    transferId,
                    owner,
                    target,
                    outputPath,
                    owner.BytesTransferred,
                    totalBytes,
                    Math.Max(attemptCount, 1),
                    FailureReason(ex),
                    ex);
            }

            throw;
        }


        long completedBytes;
        long PublishOutput(DownloadFileOperations.Cache cache)
        {
            if (selectionLayout)
            {
                var placed = cache.PlaceFile(incompleteOutputPath, outputPath, directory);
                if (placed.Matched && !DownloadFileOperations.PathEquals(incompleteOutputPath, placed.Path))
                    Utils.DeleteFileAndParentsIfEmpty(incompleteOutputPath, directory);
                outputPath = placed.Path;
            }
            else
            {
                cache.InvalidatePath(outputPath);
                Utils.MoveAndDeleteParent(incompleteOutputPath, outputPath, directory);
            }
            if (publishToDuplicateCache) cache.Publish(outputPath, target, owner.WorkflowId);
            return !deferTerminalCompletion && File.Exists(outputPath)
                ? new FileInfo(outputPath).Length : target.Size ?? owner.BytesTransferred;
        }
        try
        {
            completedBytes = await fileOperations.WithCacheAsync(PublishOutput);
        }
        catch (Exception ex)
        {
            activeDownloads.TryRemove(transferId, out _);
            try
            {
                await fileOperations.DeleteIncompleteAsync(incompleteOutputPath, Path.GetDirectoryName(outputPath));
            }
            catch (Exception cleanupEx)
            {
                DownloadLogMessages.CleanupFailed(
                    logger,
                    "finalization-incomplete-transfer",
                    cleanupEx.GetType().Name);
            }

            var finalizationException = new DownloadDestinationException(
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
                TransferFailureReason.LocalStorageError,
                finalizationException);
            throw finalizationException;
        }

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

    private static bool IsUserRequest(JobCancellationSource? source)
        => source is JobCancellationSource.UserRequestedJob or JobCancellationSource.UserRequestedWorkflow
            or JobCancellationSource.UserRequestedAllJobs;

    private static TransferFailureReason FailureReason(Exception exception)
    {
        var reason = TransferFailureReason.PeerFailure;
        for (Exception? cause = exception; cause != null; cause = cause.InnerException)
        {
            if (cause is DownloadDestinationException) return TransferFailureReason.LocalStorageError;
            if (cause is TransferRejectedException) return TransferFailureReason.Denied;
            if (cause is TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut })
                return TransferFailureReason.TimedOut;
            if (cause is TransferSizeMismatchException) return TransferFailureReason.FileChanged;
            if (cause is ConnectionException or UserOfflineException or UserEndPointException
                or UserNotFoundException or OperationCanceledException or SocketException)
                reason = TransferFailureReason.ConnectionFailed;
        }
        return reason;
    }

    private enum PeerWait
    {
        /// <summary>Waiting cannot help, or ordinary retries remain.</summary>
        None,
        UntilOnline,
        /// <summary>Pause, then ask again.</summary>
        Pause,
    }

    /// <summary>Whether a failed attempt should wait for the peer instead of ending the download.</summary>
    private static PeerWait PeerMayServeLater(Exception exception, bool retriesLeft, int earlierWaits)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is UserOfflineException) return PeerWait.UntilOnline;
            // The peer's own queue limits, in the words clients send them.
            if (cause is TransferRejectedException && (cause.Message.Contains("Too many files", StringComparison.OrdinalIgnoreCase)
                || cause.Message.Contains("Too many megabytes", StringComparison.OrdinalIgnoreCase)
                || cause.Message.Contains("User limit of", StringComparison.OrdinalIgnoreCase)
                || cause.Message.Contains("Pending shutdown", StringComparison.OrdinalIgnoreCase)))
                return PeerWait.Pause;
        }
        // A peer that is online but cannot be reached may never be: give its
        // connection a limited number of further chances (about an hour and a half).
        return !retriesLeft && earlierWaits < UnreachablePeerWaits && IsRetryableTransferFailure(exception)
            && FailureReason(exception) is TransferFailureReason.ConnectionFailed or TransferFailureReason.TimedOut
            ? PeerWait.Pause : PeerWait.None;
    }

    private const int UnreachablePeerWaits = 5;

    private static bool IsRetryableTransferFailure(Exception exception)
    {
        // Soulseek.NET wraps stream/open failures. Retrying the same unavailable
        // file, permissions or size contract cannot recover a network interruption.
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is OperationCanceledException or FileNotFoundException or DirectoryNotFoundException
                or UnauthorizedAccessException or UserNotFoundException or TransferRejectedException
                or TransferSizeMismatchException or TransferStreamException or StaleDownloadException or DownloadDestinationException)
                return false;
        }
        return true;
    }

    private static string CleanupRootForIncompletePath(string path, string? parentDir)
    {
        if (string.IsNullOrWhiteSpace(parentDir))
            return "";

        var stagingRoot = Path.Join(parentDir, OutputStaging.DirectoryName);
        return Utils.IsInDirectory(path, stagingRoot, strict: true)
            ? stagingRoot
            : parentDir;
    }
}
