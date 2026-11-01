using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.IO;
using Sockseek.Core.Sharing;
using Sockseek.Core.Snapshots;
using Soulseek;

namespace Sockseek.Core.Transfers.Uploads;

public enum UploadTransferState
{
    Queued,
    Initializing,
    InProgress,
    Completed,
    Cancelled,
    Failed,
    Interrupted,
}

public enum UploadFailureReason
{
    None,
    NotShared,
    Unavailable,
    InvalidOffset,
    Denied,
    InternalFailure,
}

public enum UploadCancellationSource
{
    None,
    User,
    Peer,
    DaemonShutdown,
    CatalogInvalidation,
}

public enum UploadAdmissionRejection
{
    None,
    InvalidRequest,
    Denied,
    NotShared,
    Unavailable,
}

public sealed record UploadAttemptSnapshot(
    Guid AttemptId,
    int Number,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long BytesTransferred,
    double BytesPerSecond);

public sealed record UploadTransferSnapshot(
    Guid TransferId,
    long Revision,
    string Username,
    string RemotePath,
    long SizeBytes,
    DateTimeOffset RequestedAtUtc,
    UploadTransferState State,
    UploadFailureReason FailureReason,
    UploadCancellationSource CancellationSource,
    long BytesTransferred,
    double BytesPerSecond,
    DateTimeOffset? LastProgressAtUtc,
    UploadAttemptSnapshot? Attempt,
    DateTimeOffset? FinishedAtUtc,
    TransferFileMetadataSnapshot? File = null,
    string? GroupRef = null,
    string? GroupDisplayPath = null);

public sealed record UploadCoordinatorAdmission(
    UploadAdmissionResultKind Kind,
    Guid? TransferId,
    UploadAdmissionRejection Rejection);

public enum UploadProtocolOutcome
{
    Completed,
    Cancelled,
    Failed,
}

public interface IUploadProtocolInvoker
{
    Task<UploadProtocolOutcome> UploadAsync(
        string username,
        string remotePath,
        long sizeBytes,
        Func<long, Task<Stream>> streamFactory,
        Action initializingCompleted,
        Action<long, double> progress,
        CancellationToken cancellationToken);
}

public sealed class SoulseekUploadProtocolInvoker(
    Func<ISoulseekClient?> clientProvider) : IUploadProtocolInvoker
{
    public async Task<UploadProtocolOutcome> UploadAsync(
        string username,
        string remotePath,
        long sizeBytes,
        Func<long, Task<Stream>> streamFactory,
        Action initializingCompleted,
        Action<long, double> progress,
        CancellationToken cancellationToken)
    {
        ISoulseekClient client = clientProvider()
            ?? throw new InvalidOperationException("Soulseek client is unavailable.");
        var options = new TransferOptions(
            stateChanged: change =>
            {
                if (change.Transfer.State.HasFlag(TransferStates.InProgress))
                    initializingCompleted();
            },
            progressUpdated: update => progress(
                update.Transfer.BytesTransferred,
                update.Transfer.AverageSpeed),
            seekInputStreamAutomatically: false,
            disposeInputStreamOnCompletion: true);

        Transfer transfer = await client.UploadAsync(
            username,
            remotePath,
            sizeBytes,
            streamFactory,
            options: options,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (transfer.State.HasFlag(TransferStates.Completed)
            || transfer.State.HasFlag(TransferStates.Succeeded))
            return UploadProtocolOutcome.Completed;
        if (transfer.State.HasFlag(TransferStates.Cancelled)
            || cancellationToken.IsCancellationRequested)
            return UploadProtocolOutcome.Cancelled;
        return UploadProtocolOutcome.Failed;
    }
}

/// <summary>
/// Owns accepted upload lifecycle, stream validation, and the sole transition
/// from Sockseek's queue into Soulseek.NET.
/// </summary>
public sealed class UploadCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan AdmissionDeadline = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private readonly IShareCatalogProvider catalogs;
    private readonly IUploadProtocolInvoker protocol;
    private readonly PeerRestrictionPolicy restrictions;
    private readonly UploadScheduler scheduler;
    private readonly TimeSpan shutdownGrace;
    private readonly ILogger<UploadCoordinator> logger;
    private readonly Dictionary<Guid, Work> work = [];
    private string? lastAdmittedGroupUsername;
    private string? lastAdmittedGroupPath;
    private string? lastAdmittedGroupRef;
    private readonly ConcurrentDictionary<Guid, Task> activeTasks = [];
    private volatile bool stopping;

    public UploadCoordinator(
        IShareCatalogProvider catalogs,
        Func<ISoulseekClient?> clientProvider,
        PeerRestrictionPolicy restrictions,
        UploadScheduler scheduler,
        ILogger<UploadCoordinator>? logger = null)
        : this(
            catalogs,
            new SoulseekUploadProtocolInvoker(clientProvider),
            restrictions,
            scheduler,
            shutdownGrace: null,
            logger)
    {
    }

    public UploadCoordinator(
        IShareCatalogProvider catalogs,
        IUploadProtocolInvoker protocol,
        PeerRestrictionPolicy restrictions,
        UploadScheduler scheduler,
        TimeSpan? shutdownGrace = null,
        ILogger<UploadCoordinator>? logger = null)
    {
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        this.restrictions = restrictions ?? throw new ArgumentNullException(nameof(restrictions));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.logger = logger ?? NullLogger<UploadCoordinator>.Instance;
        this.shutdownGrace = shutdownGrace ?? TimeSpan.FromSeconds(10);
        if (this.shutdownGrace <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownGrace));
    }

    public event Action<UploadTransferSnapshot>? TransferChanged;
    public event Action? QueueChanged;

    public async ValueTask<UploadCoordinatorAdmission> AdmitAsync(
        string username,
        IPEndPoint? endpoint,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        string peerHash = LogIdentity.PeerHash(username ?? "");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AdmissionDeadline);
        try
        {
            if (string.IsNullOrWhiteSpace(username)
                || string.IsNullOrWhiteSpace(remotePath))
            {
                return Rejected(UploadAdmissionRejection.InvalidRequest, peerHash);
            }

            string exactUsername = PeerUsername.Validate(username);
            if (restrictions.IsUploadAccessBlocked(exactUsername, endpoint))
                return Rejected(UploadAdmissionRejection.Denied, peerHash);

            RemotePathKey pathKey;
            try
            {
                pathKey = RemotePathKey.Create(remotePath);
            }
            catch (ArgumentException)
            {
                return Rejected(UploadAdmissionRejection.InvalidRequest, peerHash);
            }

            ShareCatalogResolvedFile? resolved = await ResolveCurrentAsync(
                pathKey,
                deadline.Token).ConfigureAwait(false);
            if (resolved is null)
            {
                return Rejected(
                    catalogs.TryAcquire(out var unavailableLease)
                        ? DisposeAndReturn(unavailableLease, UploadAdmissionRejection.NotShared)
                        : UploadAdmissionRejection.Unavailable,
                    peerHash);
            }

            var request = new UploadAdmissionRequest(
                TransferIds.New(),
                exactUsername,
                resolved.File.RemotePath,
                pathKey,
                resolved.File.SizeBytes,
                DateTimeOffset.UtcNow);
            string directoryPath = ContainingDirectory(resolved.File.RemotePath);
            TransferFileMetadataSnapshot fileMetadata = ToTransferFileMetadata(resolved.File);
            Work? item = null;
            UploadAdmissionResult admitted;
            lock (sync)
            {
                if (stopping)
                    return Rejected(UploadAdmissionRejection.Unavailable, peerHash);
                request = request with { RequestedAtUtc = DateTimeOffset.UtcNow };
                admitted = scheduler.Admit(request);
                if (admitted.Kind == UploadAdmissionResultKind.Accepted)
                {
                    // A folder card represents one adjacent admission run, not
                    // every request for this path throughout daemon history.
                    string groupRef = string.Equals(lastAdmittedGroupUsername, exactUsername, StringComparison.Ordinal)
                        && string.Equals(lastAdmittedGroupPath, directoryPath, StringComparison.Ordinal)
                        ? lastAdmittedGroupRef!
                        : "upload-run:" + Guid.NewGuid().ToString("D");
                    lastAdmittedGroupUsername = exactUsername;
                    lastAdmittedGroupPath = directoryPath;
                    lastAdmittedGroupRef = groupRef;
                    item = new Work(
                        request,
                        endpoint,
                        resolved.File.ModifiedAtUtc,
                        fileMetadata,
                        groupRef,
                        directoryPath);
                    work.Add(request.TransferId, item);
                }
            }
            if (admitted.Kind == UploadAdmissionResultKind.Duplicate)
            {
                SharingTelemetry.RecordUploadDuplicate();
                UploadLogMessages.Duplicate(
                    logger,
                    admitted.Entry!.TransferId,
                    peerHash);
                return new UploadCoordinatorAdmission(
                    admitted.Kind,
                    admitted.Entry!.TransferId,
                    UploadAdmissionRejection.None);
            }
            if (item is null)
                throw new InvalidOperationException("An accepted upload is missing its transfer state.");
            PublishQueueChanged();
            Publish(item);
            UploadLogMessages.Accepted(
                logger,
                request.TransferId,
                peerHash,
                request.SizeBytes);
            Dispatch(admitted.Grants);
            return new UploadCoordinatorAdmission(
                UploadAdmissionResultKind.Accepted,
                request.TransferId,
                UploadAdmissionRejection.None);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Rejected(UploadAdmissionRejection.Unavailable, peerHash);
        }
    }

    public UploadQueueRuntimeSnapshot GetQueueSnapshot()
        => scheduler.GetRuntimeSnapshot();

    public bool CouldStartImmediately(string username)
        => scheduler.CouldStartImmediately(username);

    public UploadQueueEstimate GetQueueEstimate(string username, string remotePath)
    {
        try
        {
            string exactUsername = PeerUsername.Validate(username);
            var key = RemotePathKey.Create(remotePath);
            return scheduler.Estimate(exactUsername, key);
        }
        catch (ArgumentException)
        {
            return new UploadQueueEstimate(
                null,
                scheduler.GetRuntimeSnapshot().QueueRevision);
        }
    }

    public UploadQueueEstimate GetQueueEstimate(Guid transferId)
        => scheduler.Estimate(transferId);

    public UploadQueuePage GetQueuePage(
        DateTimeOffset? afterRequestedAtUtc,
        Guid? afterTransferId,
        int limit,
        long? previousQueueRevision = null,
        string? username = null)
        => scheduler.GetPage(
            afterRequestedAtUtc,
            afterTransferId,
            limit,
            previousQueueRevision,
            username);

    public UploadQueuePage GetNewestQueuePage(
        DateTimeOffset? beforeRequestedAtUtc,
        Guid? beforeTransferId,
        int limit,
        string? username = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
        => scheduler.GetNewestPage(
            beforeRequestedAtUtc,
            beforeTransferId,
            limit,
            username,
            fromUtc,
            toUtc);

    public UploadTransferSnapshot? GetTransfer(Guid transferId)
    {
        lock (sync)
            return work.TryGetValue(transferId, out Work? item)
                ? Snapshot(item)
                : null;
    }

    /// <summary>
    /// Releases coordinator presentation state after a terminal transfer has
    /// been exposed live and handed to durable history.
    /// </summary>
    public bool Forget(Guid transferId)
    {
        Work? removed;
        lock (sync)
        {
            if (!work.TryGetValue(transferId, out removed)
                || !IsTerminal(removed.State))
            {
                return false;
            }
            work.Remove(transferId);
        }
        removed.Cancellation.Dispose();
        return true;
    }

    public IReadOnlyList<UploadTransferSnapshot> Snapshot()
    {
        lock (sync)
            return work.Values.Select(Snapshot).ToArray();
    }

    public bool Cancel(
        Guid transferId,
        UploadCancellationSource source = UploadCancellationSource.User)
    {
        Work? item;
        lock (sync)
        {
            if (!work.TryGetValue(transferId, out item) || IsTerminal(item.State))
                return false;
            item.CancellationSource = source;
        }
        // Cancellation callbacks are owned by the protocol library. Never run
        // them while holding the coordinator lock: a synchronous callback may
        // re-enter progress or terminal arbitration.
        item.Cancellation.Cancel();

        UploadSchedulerMutationResult mutation = scheduler.CancelQueued(transferId);
        if (mutation.Removed is not null)
        {
            PublishQueueChanged();
            Terminalize(item, UploadTransferState.Cancelled, UploadFailureReason.None);
            Dispatch(mutation.Grants);
        }
        return true;
    }

    private void Dispatch(IReadOnlyList<UploadSchedulerGrant> grants)
    {
        foreach (var grant in grants)
        {
            Task? task;
            lock (sync)
            {
                if (stopping)
                {
                    task = null;
                }
                else
                {
                    task = Task.Run(() => RunGrantAsync(grant));
                    activeTasks[grant.Entry.TransferId] = task;
                }
            }
            if (task is null)
            {
                InterruptUndispatchedGrants([grant]);
                continue;
            }
            _ = task.ContinueWith(
                (completed, state) =>
                {
                    _ = completed.Exception;
                    activeTasks.TryRemove((Guid)state!, out Task? _);
                },
                grant.Entry.TransferId,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void InterruptUndispatchedGrants(
        IReadOnlyList<UploadSchedulerGrant> grants)
    {
        var pending = new Queue<UploadSchedulerGrant>(grants);
        while (pending.TryDequeue(out UploadSchedulerGrant? grant))
        {
            Work? item;
            lock (sync)
            {
                work.TryGetValue(grant.Entry.TransferId, out item);
                if (item is not null)
                    item.CancellationSource = UploadCancellationSource.DaemonShutdown;
            }
            if (item is not null)
            {
                item.Cancellation.Cancel();
                Terminalize(
                    item,
                    UploadTransferState.Interrupted,
                    UploadFailureReason.None);
            }
            UploadSchedulerMutationResult mutation =
                scheduler.Terminalize(grant.Entry.TransferId);
            if (mutation.Removed is not null)
                PublishQueueChanged();
            foreach (UploadSchedulerGrant next in mutation.Grants)
                pending.Enqueue(next);
        }
    }

    private async Task RunGrantAsync(UploadSchedulerGrant grant)
    {
        Work? found;
        lock (sync)
        {
            if (!work.TryGetValue(grant.Entry.TransferId, out found))
                return;
        }
        Work item = found;
        bool cancelled;
        lock (sync)
        {
            cancelled = item.Cancellation.IsCancellationRequested;
            if (!cancelled)
            {
                item.State = UploadTransferState.Initializing;
                item.Attempt = new MutableAttempt(TransferAttemptIds.New(), DateTimeOffset.UtcNow);
                item.Revision++;
            }
        }
        if (cancelled)
        {
            Terminalize(
                item,
                stopping ? UploadTransferState.Interrupted : UploadTransferState.Cancelled,
                UploadFailureReason.None);
            UploadSchedulerMutationResult mutation =
                scheduler.Terminalize(item.Request.TransferId);
            PublishQueueChanged();
            Dispatch(mutation.Grants);
            return;
        }
        Publish(item);

        try
        {
            if (restrictions.IsUploadAccessBlocked(item.Request.Username, item.Endpoint))
                throw new UploadDomainException(UploadFailureReason.Denied);

            ShareCatalogResolvedFile? current = await ResolveCurrentAsync(
                item.Request.RemotePathKey,
                item.Cancellation.Token).ConfigureAwait(false);
            EnsureSameFile(item, current);

            UploadProtocolOutcome outcome = await protocol.UploadAsync(
                item.Request.Username,
                item.Request.RemotePath,
                item.Request.SizeBytes,
                startOffset => OpenUploadStreamAsync(item, startOffset),
                () => SetInProgress(item),
                (bytes, speed) => SetProgress(item, bytes, speed),
                item.Cancellation.Token).ConfigureAwait(false);

            if (outcome == UploadProtocolOutcome.Completed)
            {
                Terminalize(item, UploadTransferState.Completed, UploadFailureReason.None);
            }
            else if (outcome == UploadProtocolOutcome.Cancelled
                     || item.Cancellation.IsCancellationRequested)
            {
                lock (sync)
                {
                    if (!stopping
                        && item.CancellationSource == UploadCancellationSource.None)
                    {
                        item.CancellationSource = UploadCancellationSource.Peer;
                    }
                }
                Terminalize(
                    item,
                    stopping ? UploadTransferState.Interrupted : UploadTransferState.Cancelled,
                    UploadFailureReason.None);
            }
            else
            {
                Terminalize(item, UploadTransferState.Failed, UploadFailureReason.InternalFailure);
            }
        }
        catch (OperationCanceledException) when (item.Cancellation.IsCancellationRequested)
        {
            Terminalize(
                item,
                stopping ? UploadTransferState.Interrupted : UploadTransferState.Cancelled,
                UploadFailureReason.None);
        }
        catch (UploadDomainException ex)
        {
            Terminalize(item, UploadTransferState.Failed, ex.Reason);
        }
        catch (SharedFileOpenException)
        {
            Terminalize(
                item,
                UploadTransferState.Failed,
                UploadFailureReason.Unavailable);
        }
        catch (Exception exception)
        {
            UploadLogMessages.InternalFailure(
                logger,
                exception,
                item.Request.TransferId,
                LogIdentity.PeerHash(item.Request.Username));
            Terminalize(item, UploadTransferState.Failed, UploadFailureReason.InternalFailure);
        }
        finally
        {
            UploadSchedulerMutationResult mutation =
                scheduler.Terminalize(item.Request.TransferId);
            if (mutation.Removed is not null)
                PublishQueueChanged();
            Dispatch(mutation.Grants);
        }
    }

    private async Task<Stream> OpenUploadStreamAsync(Work item, long startOffset)
    {
        if (startOffset < 0 || startOffset > item.Request.SizeBytes)
            throw new UploadDomainException(UploadFailureReason.InvalidOffset);

        ShareCatalogResolvedFile? current = await ResolveCurrentAsync(
            item.Request.RemotePathKey,
            item.Cancellation.Token).ConfigureAwait(false);
        EnsureSameFile(item, current);

        var expected = new SharedFileFingerprint(
            item.Request.SizeBytes,
            item.ExpectedModifiedAtUtc);
        OpenedSharedFile opened = SafeSharedFileOpener.Open(
            current!.Root.LocalPath,
            current.File.RelativePath,
            expected);
        try
        {
            opened.Stream.Position = startOffset;
            return new ExactLengthReadStream(
                opened.Stream,
                item.Request.SizeBytes - startOffset);
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ShareCatalogResolvedFile?> ResolveCurrentAsync(
        RemotePathKey path,
        CancellationToken cancellationToken)
    {
        if (!catalogs.TryAcquire(out IShareCatalogLease? lease) || lease is null)
            return null;
        await using (lease.ConfigureAwait(false))
            return await lease.Reader.ResolveFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSameFile(Work item, ShareCatalogResolvedFile? current)
    {
        if (current is null)
            throw new UploadDomainException(UploadFailureReason.NotShared);
        if (current.File.SizeBytes != item.Request.SizeBytes
            || current.File.ModifiedAtUtc != item.ExpectedModifiedAtUtc)
        {
            throw new UploadDomainException(UploadFailureReason.Unavailable);
        }
    }

    private void SetInProgress(Work item)
    {
        lock (sync)
        {
            if (item.State != UploadTransferState.Initializing)
                return;
            item.State = UploadTransferState.InProgress;
            item.Revision++;
        }
        Publish(item);
    }

    private void SetProgress(Work item, long bytes, double speed)
    {
        lock (sync)
        {
            if (IsTerminal(item.State))
                return;
            item.BytesTransferred = Math.Clamp(bytes, 0, item.Request.SizeBytes);
            item.BytesPerSecond = Math.Max(0, speed);
            item.LastProgressAtUtc = DateTimeOffset.UtcNow;
            if (item.Attempt is not null)
            {
                item.Attempt.BytesTransferred = item.BytesTransferred;
                item.Attempt.BytesPerSecond = item.BytesPerSecond;
            }
            item.Revision++;
        }
        Publish(item);
    }

    private bool Terminalize(
        Work item,
        UploadTransferState state,
        UploadFailureReason failure)
    {
        lock (sync)
        {
            if (IsTerminal(item.State))
                return false;
            item.State = state;
            item.FailureReason = failure;
            item.FinishedAtUtc = DateTimeOffset.UtcNow;
            if (item.Attempt is not null)
                item.Attempt.FinishedAtUtc = item.FinishedAtUtc;
            item.Revision++;
        }
        Publish(item);
        SharingTelemetry.RecordUploadTerminal(
            state,
            item.BytesTransferred);
        UploadLogMessages.Terminal(
            logger,
            item.Request.TransferId,
            LogIdentity.PeerHash(item.Request.Username),
            state,
            failure,
            item.BytesTransferred,
            (long)((item.FinishedAtUtc ?? DateTimeOffset.UtcNow) - item.Request.RequestedAtUtc).TotalMilliseconds);
        return true;
    }

    private void Publish(Work item)
    {
        UploadTransferSnapshot snapshot = Snapshot(item);
        Action<UploadTransferSnapshot>? handlers = TransferChanged;
        if (handlers is null)
            return;
        foreach (Action<UploadTransferSnapshot> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
                // Observability and history projections cannot affect a peer
                // transfer or scheduler accounting.
            }
        }
    }

    private void PublishQueueChanged()
    {
        Action? handlers = QueueChanged;
        if (handlers is null)
            return;
        foreach (Action handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler();
            }
            catch
            {
                // Runtime summary observers are non-authoritative.
            }
        }
    }

    private UploadTransferSnapshot Snapshot(Work item)
    {
        lock (sync)
        {
            return new UploadTransferSnapshot(
                item.Request.TransferId,
                item.Revision,
                item.Request.Username,
                item.Request.RemotePath,
                item.Request.SizeBytes,
                item.Request.RequestedAtUtc,
                item.State,
                item.FailureReason,
                item.CancellationSource,
                item.BytesTransferred,
                item.BytesPerSecond,
                item.LastProgressAtUtc,
                item.Attempt is null
                    ? null
                    : new UploadAttemptSnapshot(
                        item.Attempt.AttemptId,
                        1,
                        item.Attempt.StartedAtUtc,
                        item.Attempt.FinishedAtUtc,
                        item.Attempt.BytesTransferred,
                        item.Attempt.BytesPerSecond),
                item.FinishedAtUtc,
                item.File,
                item.GroupRef,
                item.GroupDisplayPath);
        }
    }

    private static TransferFileMetadataSnapshot ToTransferFileMetadata(
        ShareCatalogFile file)
    {
        int? Attribute(int type)
            => file.Attributes.FirstOrDefault(attribute => attribute.Type == type)?.Value;

        return new TransferFileMetadataSnapshot(
            FileName(file.RemotePath),
            file.SizeBytes,
            file.Extension,
            Attribute((int)FileAttributeType.BitRate),
            Attribute((int)FileAttributeType.BitDepth),
            Attribute((int)FileAttributeType.SampleRate),
            Attribute((int)FileAttributeType.Length),
            file.Attributes.Select(attribute => new FileAttributeSnapshot(
                ((FileAttributeType)attribute.Type).ToString(),
                attribute.Value,
                attribute.Type)).ToArray());
    }

    private static string FileName(string remotePath)
    {
        int separator = Math.Max(remotePath.LastIndexOf('\\'), remotePath.LastIndexOf('/'));
        return separator < 0 ? remotePath : remotePath[(separator + 1)..];
    }

    private static string ContainingDirectory(string remotePath)
    {
        int separator = Math.Max(remotePath.LastIndexOf('\\'), remotePath.LastIndexOf('/'));
        return separator < 0 ? "" : remotePath[..separator];
    }

    private static bool IsTerminal(UploadTransferState state)
        => state is UploadTransferState.Completed
            or UploadTransferState.Cancelled
            or UploadTransferState.Failed
            or UploadTransferState.Interrupted;

    private UploadCoordinatorAdmission Rejected(
        UploadAdmissionRejection rejection,
        string peerHash)
    {
        SharingTelemetry.RecordUploadRejected(rejection);
        UploadLogMessages.Rejected(logger, peerHash, rejection);
        return new(UploadAdmissionResultKind.Rejected, null, rejection);
    }

    private static UploadAdmissionRejection DisposeAndReturn(
        IShareCatalogLease? lease,
        UploadAdmissionRejection rejection)
    {
        lease?.Dispose();
        return rejection;
    }

    public async ValueTask DisposeAsync()
    {
        Work[] snapshot;
        lock (sync)
        {
            if (stopping)
                return;
            stopping = true;
            snapshot = work.Values.Where(item => !IsTerminal(item.State)).ToArray();
        }

        foreach (var item in snapshot.Where(
                     item => item.State == UploadTransferState.Queued))
        {
            lock (sync)
                item.CancellationSource = UploadCancellationSource.DaemonShutdown;
            item.Cancellation.Cancel();
            UploadSchedulerMutationResult mutation =
                scheduler.CancelQueued(item.Request.TransferId);
            if (mutation.Removed is not null)
            {
                PublishQueueChanged();
                Terminalize(item, UploadTransferState.Interrupted, UploadFailureReason.None);
            }
            Dispatch(mutation.Grants);
        }

        try
        {
            await Task.WhenAll(activeTasks.Values).WaitAsync(shutdownGrace)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Work[] active;
            lock (sync)
                active = work.Values.Where(item => !IsTerminal(item.State)).ToArray();
            foreach (var item in active)
            {
                lock (sync)
                    item.CancellationSource = UploadCancellationSource.DaemonShutdown;
                item.Cancellation.Cancel();
            }

            try
            {
                TimeSpan cancellationWait = shutdownGrace < TimeSpan.FromSeconds(1)
                    ? shutdownGrace
                    : TimeSpan.FromSeconds(1);
                await Task.WhenAll(activeTasks.Values)
                    .WaitAsync(cancellationWait)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Forced terminal arbitration below is idempotent. Late
                // callbacks cannot release scheduler accounting twice.
            }
        }
        foreach (var item in snapshot)
        {
            if (!Terminalize(
                    item,
                    UploadTransferState.Interrupted,
                    UploadFailureReason.None))
            {
                continue;
            }

            // Forced shutdown owns both terminal state and slot accounting.
            // A late protocol callback may repeat this operation, but scheduler
            // terminalization is idempotent and cannot release twice.
            UploadSchedulerMutationResult mutation =
                scheduler.Terminalize(item.Request.TransferId);
            if (mutation.Removed is not null)
                PublishQueueChanged();
            InterruptUndispatchedGrants(mutation.Grants);
        }
    }

    private sealed class Work(
        UploadAdmissionRequest request,
        IPEndPoint? endpoint,
        DateTimeOffset expectedModifiedAtUtc,
        TransferFileMetadataSnapshot file,
        string groupRef,
        string groupDisplayPath)
    {
        public UploadAdmissionRequest Request { get; } = request;
        public IPEndPoint? Endpoint { get; } = endpoint;
        public DateTimeOffset ExpectedModifiedAtUtc { get; } = expectedModifiedAtUtc;
        public TransferFileMetadataSnapshot File { get; } = file;
        public string GroupRef { get; } = groupRef;
        public string GroupDisplayPath { get; } = groupDisplayPath;
        public CancellationTokenSource Cancellation { get; } = new();
        public UploadTransferState State { get; set; } = UploadTransferState.Queued;
        public UploadFailureReason FailureReason { get; set; }
        public UploadCancellationSource CancellationSource { get; set; }
        public long BytesTransferred { get; set; }
        public double BytesPerSecond { get; set; }
        public DateTimeOffset? LastProgressAtUtc { get; set; }
        public MutableAttempt? Attempt { get; set; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public long Revision { get; set; } = 1;
    }

    private sealed class MutableAttempt(
        Guid attemptId,
        DateTimeOffset startedAtUtc)
    {
        public Guid AttemptId { get; } = attemptId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public long BytesTransferred { get; set; }
        public double BytesPerSecond { get; set; }
    }

    private sealed class UploadDomainException(UploadFailureReason reason) : Exception
    {
        public UploadFailureReason Reason { get; } = reason;
    }
}

internal static partial class UploadLogMessages
{
    [LoggerMessage(
        EventId = 4310,
        EventName = "upload.accepted",
        Level = LogLevel.Information,
        Message = "Accepted upload {TransferId} for peer hash {PeerHash} ({SizeBytes} bytes)")]
    public static partial void Accepted(
        ILogger logger, Guid transferId, string peerHash, long sizeBytes);

    [LoggerMessage(
        EventId = 4311,
        EventName = "upload.duplicate",
        Level = LogLevel.Debug,
        Message = "Coalesced duplicate upload {TransferId} for peer hash {PeerHash}")]
    public static partial void Duplicate(
        ILogger logger, Guid transferId, string peerHash);

    [LoggerMessage(
        EventId = 4312,
        EventName = "upload.rejected",
        Level = LogLevel.Debug,
        Message = "Rejected upload request for peer hash {PeerHash}: {Rejection}")]
    public static partial void Rejected(
        ILogger logger, string peerHash, UploadAdmissionRejection rejection);

    [LoggerMessage(
        EventId = 4313,
        EventName = "upload.terminal",
        Level = LogLevel.Information,
        Message = "Upload {TransferId} for peer hash {PeerHash} ended as {State}/{FailureReason} after {DurationMs} ms with {BytesTransferred} bytes transferred")]
    public static partial void Terminal(
        ILogger logger, Guid transferId, string peerHash, UploadTransferState state,
        UploadFailureReason failureReason, long bytesTransferred, long durationMs);

    [LoggerMessage(
        EventId = 4314,
        EventName = "upload.internal-failure",
        Level = LogLevel.Error,
        Message = "Upload {TransferId} for peer hash {PeerHash} failed internally")]
    public static partial void InternalFailure(
        ILogger logger, Exception exception, Guid transferId, string peerHash);
}
