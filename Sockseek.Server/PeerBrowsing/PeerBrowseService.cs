using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sockseek.Core;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.PeerBrowsing;
using Soulseek;

namespace Sockseek.Server.PeerBrowsing;

/// <summary>
/// Daemon-lifetime owner of peer browse acquisition. Public browsing and
/// directory retrieval both enter through this single-flight coordinator.
/// </summary>
public sealed class PeerBrowseService : IPeerDirectorySource, IAsyncDisposable
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    public const int DefaultNetworkConcurrency = 2;

    private readonly PeerBrowseArtifactStore store;
    private readonly IPeerBrowseTransport transport;
    private readonly Func<string?> localAccountProvider;
    private readonly ILogger<PeerBrowseService> logger;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly Dictionary<AcquisitionKey, ActiveBrowse> activeByKey = [];
    private readonly Dictionary<Guid, ActiveBrowse> activeById = [];
    private readonly HashSet<ActiveBrowse> activeExecutions = [];
    private readonly SemaphoreSlim networkGate;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object connectionGate = new();
    private CancellationTokenSource connectionLifetime = new();
    private int observedLoggedInSession;
    private int disposeState;

    public PeerBrowseService(
        PeerBrowseArtifactStore store,
        IPeerBrowseTransport transport,
        Func<string?> localAccountProvider,
        ILogger<PeerBrowseService> logger,
        int networkConcurrency = DefaultNetworkConcurrency)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.localAccountProvider = localAccountProvider ?? throw new ArgumentNullException(nameof(localAccountProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (networkConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(networkConcurrency));

        networkGate = new SemaphoreSlim(networkConcurrency, networkConcurrency);
        store.ResourceRemoved += OnResourceRemoved;
    }

    public event Action<PeerBrowseResource>? Changed;
    public event Action<Guid>? Removed;

    public void OnSoulseekStateChanged(SoulseekClientStates state)
    {
        if (Volatile.Read(ref disposeState) != 0)
            return;
        if (state.HasFlag(SoulseekClientStates.Connected)
            && state.HasFlag(SoulseekClientStates.LoggedIn))
        {
            Volatile.Write(ref observedLoggedInSession, 1);
            return;
        }
        if (Interlocked.Exchange(ref observedLoggedInSession, 0) == 0)
            return;

        CancellationTokenSource previous;
        lock (connectionGate)
        {
            previous = connectionLifetime;
            connectionLifetime = new CancellationTokenSource();
        }
        previous.Cancel();
        previous.Dispose();
    }

    public async Task<PeerBrowseResource> StartAsync(
        string username,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposeState) != 0, this);
        username = PeerUsername.Validate(username);
        string peerHash = LogIdentity.PeerHash(username);
        string? observedLocalAccount = localAccountProvider();
        if (observedLocalAccount is null)
        {
            PeerBrowseLogMessages.SessionUnavailable(logger, peerHash);
            throw new PeerBrowseUnavailableException("Soulseek is not logged in.");
        }
        string localAccount = PeerUsername.Validate(observedLocalAccount);
        var key = new AcquisitionKey(localAccount, username);

        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeByKey.TryGetValue(key, out ActiveBrowse? running))
            {
                PeerBrowseResource observed = await RequireResourceAsync(
                    running.BrowseId,
                    cancellationToken).ConfigureAwait(false);
                if (observed.State is PeerBrowseState.Queued or PeerBrowseState.Running)
                {
                    PeerBrowseTelemetry.RecordReuse("in-flight");
                    PeerBrowseLogMessages.ReusedActive(
                        logger,
                        observed.BrowseId,
                        peerHash);
                    return observed;
                }

                // Persistence becomes terminal before RunAsync finishes telemetry
                // and removes its in-memory entry. Do not let that short window make
                // a new caller join a failed generation or make refresh reuse a
                // completed one.
                activeByKey.Remove(key);
                activeById.Remove(running.BrowseId);
            }

            if (!refresh)
            {
                PeerBrowseResource? fresh = await store.FindFreshAsync(
                    localAccount,
                    username,
                    Freshness,
                    cancellationToken).ConfigureAwait(false);
                if (fresh is not null)
                {
                    PeerBrowseTelemetry.RecordReuse("fresh");
                    PeerBrowseLogMessages.ReusedFresh(
                        logger,
                        fresh.BrowseId,
                        peerHash,
                        (long)(DateTimeOffset.UtcNow - (fresh.CompletedAt ?? fresh.UpdatedAt)).TotalMilliseconds);
                    return fresh;
                }
            }

            PeerBrowseResource resource = await store.CreateQueuedAsync(
                localAccount,
                username,
                cancellationToken).ConfigureAwait(false);
            CancellationToken connectionToken;
            lock (connectionGate)
                connectionToken = connectionLifetime.Token;
            var active = new ActiveBrowse(resource.BrowseId, key, connectionToken);
            PeerBrowseTelemetry.RecordStarted();
            activeByKey.Add(key, active);
            activeById.Add(resource.BrowseId, active);
            activeExecutions.Add(active);
            active.Execution = RunAsync(active);
            Publish(resource);
            active.Begin.TrySetResult();
            return resource;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            PeerBrowseLogMessages.StartFailed(logger, exception, peerHash);
            throw;
        }
        finally
        {
            stateGate.Release();
        }
    }

    public Task<PeerBrowseResource?> GetAsync(
        Guid browseId,
        CancellationToken cancellationToken = default)
        => store.GetAsync(browseId, cancellationToken);

    public async Task<PeerBrowseResource?> GetAccessibleAsync(
        Guid browseId,
        CancellationToken cancellationToken = default)
    {
        PeerBrowseResource? resource = await store.GetAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !IsCurrentAccount(resource.LocalAccount))
            return null;
        return resource;
    }

    public async Task<PeerBrowseResourcePage> ListAsync(
        string? username,
        PeerBrowseState? state,
        DateTimeOffset? afterCreatedAt,
        Guid? afterBrowseId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be positive.");
        if (username is not null)
            username = PeerUsername.Validate(username);
        return await store.ListAsync(
            CurrentLocalAccount(),
            username,
            state,
            afterCreatedAt,
            afterBrowseId,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowseDirectoryEntry?> ReadDirectoryEntryAsync(
        Guid browseId,
        long directoryId,
        CancellationToken cancellationToken = default)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.ReadDirectoryEntryAsync(browseId, directoryId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowsePage<PeerBrowseDirectoryEntry>> ReadDirectoriesAsync(
        Guid browseId,
        long? parentId,
        string? query,
        bool recursive,
        string? afterSortKey,
        long? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.ReadDirectoriesAsync(
            browseId, parentId, query, recursive, afterSortKey, afterId, limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowsePage<PeerBrowseFileEntry>> ReadFilesAsync(
        Guid browseId,
        long directoryId,
        string? query,
        string? afterSortKey,
        long? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.ReadFilesAsync(
            browseId, directoryId, query, afterSortKey, afterId, limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowseFileEntry?> ReadFileEntryAsync(
        Guid browseId,
        long fileId,
        CancellationToken cancellationToken = default)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.ReadFileEntryAsync(browseId, fileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowseSearchPage> SearchAsync(
        Guid browseId,
        string query,
        string? afterSortKey,
        PeerBrowseSearchEntryKind? afterKind,
        long? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.SearchAsync(
            browseId,
            query,
            afterSortKey,
            afterKind,
            afterId,
            limit,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadSearchSortKeyAsync(
        Guid browseId,
        PeerBrowseSearchEntryKind kind,
        long entryId,
        CancellationToken cancellationToken = default)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.ReadSearchSortKeyAsync(
            browseId, kind, entryId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PeerBrowseDownloadResolution> ResolveDownloadSelectionAsync(
        Guid browseId,
        IReadOnlyList<long> directoryIds,
        IReadOnlyList<long> fileIds,
        CancellationToken cancellationToken = default,
        IReadOnlyList<long>? excludedFileIds = null,
        IReadOnlyList<long>? excludedDirectoryIds = null)
    {
        await RequireAccessibleCompleteAsync(browseId, cancellationToken).ConfigureAwait(false);
        return await store.ResolveDownloadSelectionAsync(
            browseId,
            directoryIds,
            fileIds,
            cancellationToken,
            excludedFileIds,
            excludedDirectoryIds).ConfigureAwait(false);
    }

    public async Task<PeerBrowseResource> WaitForCompletionAsync(
        string username,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        PeerBrowseResource resource = await StartAsync(username, refresh, cancellationToken).ConfigureAwait(false);
        if (resource.State == PeerBrowseState.Complete)
            return resource;
        if (resource.State is PeerBrowseState.Failed or PeerBrowseState.Cancelled)
            throw Failure(resource);

        Task<PeerBrowseResource>? execution = null;
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activeById.TryGetValue(resource.BrowseId, out ActiveBrowse? active))
                execution = active.Execution;
        }
        finally
        {
            stateGate.Release();
        }

        PeerBrowseResource terminal = execution is null
            ? await RequireResourceAsync(resource.BrowseId, cancellationToken).ConfigureAwait(false)
            : await execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (terminal.State != PeerBrowseState.Complete)
            throw Failure(terminal);
        return terminal;
    }

    public async Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.RequireAdvertisedFolder();
        // Keep the full-browse guarantee described by IPeerDirectorySource;
        // a targeted directory reply may omit descendants without telling us.
        PeerBrowseResource resource = await WaitForCompletionAsync(
            directory.Username,
            refresh: false,
            cancellationToken).ConfigureAwait(false);
        return await store.ReadDirectoryAsync(resource.BrowseId, directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Globally cancels the shared generation. Idempotent for terminal resources.</summary>
    public async Task<PeerBrowseResource?> CancelAsync(
        Guid browseId,
        CancellationToken cancellationToken = default)
    {
        PeerBrowseResource? resource = await GetAccessibleAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            return null;
        ActiveBrowse? active;
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            activeById.TryGetValue(browseId, out active);
            active?.Cancellation.Cancel();
        }
        finally
        {
            stateGate.Release();
        }

        if (active is not null)
            return await active.Execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await store.GetAsync(browseId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PeerBrowseResource> RunAsync(ActiveBrowse active)
    {
        await active.Begin.Task.ConfigureAwait(false);
        using OperationLogScope operationLog = OperationLogScope.Start(
            logger,
            "peer-browse",
            $"{active.BrowseId:D}/{LogIdentity.PeerHash(active.Key.Username)}");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            active.Cancellation.Token,
            lifetime.Token,
            active.ConnectionToken);
        CancellationToken token = cancellation.Token;
        bool networkAcquired = false;
        try
        {
            await networkGate.WaitAsync(token).ConfigureAwait(false);
            networkAcquired = true;
            if (active.MarkRunning())
                PeerBrowseTelemetry.RecordRunning();
            await store.MarkRunningAsync(active.BrowseId, token).ConfigureAwait(false);
            Publish(await RequireResourceAsync(active.BrowseId, token).ConfigureAwait(false));
            PeerBrowseResource resource = await RequireResourceAsync(active.BrowseId, token).ConfigureAwait(false);
            await using PeerBrowseArtifactWriter writer = await store.BeginWriteAsync(resource, token).ConfigureAwait(false);
            await using var progressSink = new ProgressSink(this, active, writer);
            await transport.ReceiveAsync(
                active.Key.Username,
                progressSink,
                progress => OnTransportProgress(active, progress),
                progress => OnIndexProgress(active, progress),
                token).ConfigureAwait(false);

            PeerBrowseResource terminal = await RequireResourceAsync(active.BrowseId, token).ConfigureAwait(false);
            if (terminal.State != PeerBrowseState.Complete)
                throw new InvalidDataException("The browse transport ended without completing its artifact.");
            Publish(terminal);
            PeerBrowseLogMessages.ArtifactCompleted(
                logger,
                terminal.BrowseId,
                terminal.DirectoryCount,
                terminal.FileCount,
                terminal.TotalFileBytes);
            operationLog.Succeeded(
                "complete",
                terminal.FileCount,
                terminal.TotalFileBytes);
            return terminal;
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested)
        {
            if (active.ConnectionToken.IsCancellationRequested
                && !active.Cancellation.IsCancellationRequested
                && !lifetime.IsCancellationRequested)
            {
                await store.MarkFailedAsync(
                    active.BrowseId,
                    "connection-lost",
                    "The Soulseek connection was lost during the peer browse.",
                    CancellationToken.None).ConfigureAwait(false);
                operationLog.Failed(exception, "connection-lost");
            }
            else
            {
                await store.MarkCancelledAsync(active.BrowseId, CancellationToken.None).ConfigureAwait(false);
                operationLog.Cancelled();
            }
            PeerBrowseResource terminal = await RequireResourceAsync(active.BrowseId, CancellationToken.None).ConfigureAwait(false);
            Publish(terminal);
            return terminal;
        }
        catch (Exception exception)
        {
            (string code, string message) = Classify(exception);
            operationLog.Failed(exception, code);
            await store.MarkFailedAsync(active.BrowseId, code, message, CancellationToken.None).ConfigureAwait(false);
            PeerBrowseResource terminal = await RequireResourceAsync(active.BrowseId, CancellationToken.None).ConfigureAwait(false);
            Publish(terminal);
            return terminal;
        }
        finally
        {
            if (networkAcquired)
                networkGate.Release();
            await active.WaitForProgressFlushAsync().ConfigureAwait(false);
            PeerBrowseResource? observed = null;
            try
            {
                observed = await store.GetAsync(active.BrowseId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Telemetry must not affect lifecycle cleanup.
            }
            PeerBrowseTelemetry.RecordTerminal(
                observed,
                active.ReachedRunning,
                Stopwatch.GetElapsedTime(active.StartTimestamp));
            await RemoveActiveAsync(active).ConfigureAwait(false);
        }
    }

    private async Task RemoveActiveAsync(ActiveBrowse active)
    {
        await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (activeById.TryGetValue(active.BrowseId, out ActiveBrowse? byId)
                && ReferenceEquals(byId, active))
            {
                activeById.Remove(active.BrowseId);
            }
            if (activeByKey.TryGetValue(active.Key, out ActiveBrowse? byKey)
                && ReferenceEquals(byKey, active))
            {
                activeByKey.Remove(active.Key);
            }
            activeExecutions.Remove(active);
        }
        finally
        {
            stateGate.Release();
            active.Cancellation.Dispose();
        }
    }

    private void OnTransportProgress(ActiveBrowse active, PeerBrowseTransportProgress progress)
    {
        active.SetTransportProgress(progress);
        QueueProgressFlush(active);
    }

    private void OnIndexProgress(ActiveBrowse active, PeerBrowseIndexProgress progress)
    {
        active.SetIndexProgress(progress);
        QueueProgressFlush(active);
    }

    private void QueueProgressFlush(ActiveBrowse active)
    {
        long now = Environment.TickCount64;
        if (!active.TryQueueFlush(now, 250))
            return;
        active.TrackProgressFlush(FlushProgressSafelyAsync(active));
    }

    private async Task FlushProgressSafelyAsync(ActiveBrowse active)
    {
        try
        {
            await FlushProgressAsync(active, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Progress durability is best effort; terminal persistence remains
            // authoritative and is handled by the acquisition task itself.
        }
        finally
        {
            active.FinishFlush();
        }
    }

    private async Task FlushProgressAsync(ActiveBrowse active, CancellationToken cancellationToken)
    {
        (PeerBrowseTransportProgress transportProgress, PeerBrowseIndexProgress indexProgress,
            bool indexingStarted) = active.Progress;
        PeerBrowsePhase phase = indexingStarted
            ? PeerBrowsePhase.Indexing
            : PeerBrowsePhase.Receiving;
        await store.UpdateProgressAsync(
            active.BrowseId,
            phase,
            transportProgress.CompressedBytesReceived,
            transportProgress.CompressedBytesExpected,
            indexProgress,
            cancellationToken).ConfigureAwait(false);
        PeerBrowseResource? resource = await store.GetAsync(active.BrowseId, cancellationToken).ConfigureAwait(false);
        if (resource is not null)
            Publish(resource);
    }

    private async Task<PeerBrowseResource> RequireResourceAsync(Guid browseId, CancellationToken cancellationToken)
        => await store.GetAsync(browseId, cancellationToken).ConfigureAwait(false)
           ?? throw new KeyNotFoundException($"Peer browse '{browseId}' has expired.");

    private async Task<PeerBrowseResource> RequireAccessibleCompleteAsync(
        Guid browseId,
        CancellationToken cancellationToken)
    {
        PeerBrowseResource? resource = await GetAccessibleAsync(browseId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            throw new KeyNotFoundException($"Peer browse '{browseId}' has expired.");
        if (resource.State != PeerBrowseState.Complete)
            throw new PeerBrowseNotReadyException(resource);
        return resource;
    }

    private string CurrentLocalAccount()
        => PeerUsername.Validate(
            localAccountProvider()
            ?? throw new PeerBrowseUnavailableException("Soulseek is not logged in."));

    private bool IsCurrentAccount(string localAccount)
        => string.Equals(localAccount, CurrentLocalAccount(), StringComparison.Ordinal);

    private void Publish(PeerBrowseResource resource)
    {
        Action<PeerBrowseResource>? handlers = Changed;
        if (handlers is null)
            return;
        foreach (Action<PeerBrowseResource> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(resource);
            }
            catch
            {
                // Observers do not own acquisition lifecycle.
            }
        }
    }

    private void OnResourceRemoved(Guid browseId)
    {
        Action<Guid>? handlers = Removed;
        if (handlers is null)
            return;
        foreach (Action<Guid> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(browseId);
            }
            catch
            {
                // Observers do not own artifact cleanup.
            }
        }
    }

    private static (string Code, string Message) Classify(Exception exception)
    {
        if (Contains<PeerBrowseProtocolException>(exception))
            return ("peer-response-invalid", "The peer returned an invalid browse response.");
        if (Contains<TimeoutException>(exception))
            return ("peer-timeout", "The peer did not respond before the browse timed out.");
        if (Contains<IOException>(exception))
            return ("peer-io-failed", "The peer browse could not be stored or received.");
        return ("browse-failed", "The peer browse failed.");
    }

    private static bool Contains<TException>(Exception exception)
        where TException : Exception
    {
        if (exception is TException)
            return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(Contains<TException>);
        return exception.InnerException is { } inner && Contains<TException>(inner);
    }

    internal static string PeerHash(string username)
        => LogIdentity.PeerHash(username);

    private static PeerBrowseAcquisitionException Failure(PeerBrowseResource resource)
        => new(
            resource.Failure?.Code ?? "browse-failed",
            resource.Failure?.Message ?? "The peer browse did not complete.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;
        store.ResourceRemoved -= OnResourceRemoved;
        lifetime.Cancel();
        Task<PeerBrowseResource>[] executions;
        await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A caller may observe the persisted terminal state and remove an
            // acquisition from the join dictionaries before RunAsync has closed
            // its SQLite writer and finished cleanup. Keep tracking that execution
            // independently so service disposal never abandons those resources.
            executions = activeExecutions.Select(static active => active.Execution).ToArray();
        }
        finally
        {
            stateGate.Release();
        }
        await Task.WhenAll(executions).ConfigureAwait(false);
        CancellationTokenSource connection;
        lock (connectionGate)
            connection = connectionLifetime;
        connection.Cancel();
        connection.Dispose();
        lifetime.Dispose();
        networkGate.Dispose();
        stateGate.Dispose();
    }

    private readonly record struct AcquisitionKey(string LocalAccount, string Username);

    private sealed class ActiveBrowse(
        Guid browseId,
        AcquisitionKey key,
        CancellationToken connectionToken)
    {
        private readonly Lock progressGate = new();
        private int flushQueued;
        private int reachedRunning;
        private long lastFlushTick = long.MinValue;

        public Guid BrowseId { get; } = browseId;
        public long StartTimestamp { get; } = Stopwatch.GetTimestamp();
        public AcquisitionKey Key { get; } = key;
        public CancellationToken ConnectionToken { get; } = connectionToken;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PeerBrowseResource> Execution { get; set; } = null!;
        private PeerBrowseTransportProgress TransportProgress { get; set; } = new(0, null);
        private PeerBrowseIndexProgress IndexProgress { get; set; } = new(0, 0, 0);
        private bool IndexingStarted { get; set; }
        private Task progressFlush = Task.CompletedTask;
        public bool ReachedRunning => Volatile.Read(ref reachedRunning) != 0;

        public bool MarkRunning() => Interlocked.Exchange(ref reachedRunning, 1) == 0;

        public (PeerBrowseTransportProgress Transport, PeerBrowseIndexProgress Index, bool IndexingStarted) Progress
        {
            get
            {
                lock (progressGate)
                    return (TransportProgress, IndexProgress, IndexingStarted);
            }
        }

        public void SetTransportProgress(PeerBrowseTransportProgress progress)
        {
            lock (progressGate)
                TransportProgress = progress;
        }

        public void SetIndexProgress(PeerBrowseIndexProgress progress)
        {
            lock (progressGate)
            {
                IndexProgress = progress;
                IndexingStarted = true;
            }
        }

        public void TrackProgressFlush(Task flush)
        {
            ArgumentNullException.ThrowIfNull(flush);
            lock (progressGate)
                progressFlush = flush;
        }

        public Task WaitForProgressFlushAsync()
        {
            lock (progressGate)
                return progressFlush;
        }

        public bool TryQueueFlush(long now, long minimumIntervalMilliseconds)
        {
            lock (progressGate)
            {
                if (flushQueued != 0
                    || lastFlushTick != long.MinValue
                       && now - lastFlushTick < minimumIntervalMilliseconds)
                    return false;
                flushQueued = 1;
                lastFlushTick = now;
                return true;
            }
        }

        public void FinishFlush()
        {
            lock (progressGate)
                flushQueued = 0;
        }
    }

    private sealed class ProgressSink(
        PeerBrowseService owner,
        ActiveBrowse active,
        IPeerBrowseRowSink inner) : IPeerBrowseRowSink
    {
        public ValueTask BeginDirectoryAsync(
            string wirePath,
            PeerShareVisibility visibility,
            int fileCount,
            CancellationToken cancellationToken = default)
            => inner.BeginDirectoryAsync(wirePath, visibility, fileCount, cancellationToken);

        public ValueTask BeginFileAsync(
            PeerBrowseWireFile file,
            CancellationToken cancellationToken = default)
            => inner.BeginFileAsync(file, cancellationToken);

        public ValueTask AddAttributeAsync(
            PeerBrowseWireAttribute attribute,
            CancellationToken cancellationToken = default)
            => inner.AddAttributeAsync(attribute, cancellationToken);

        public ValueTask EndFileAsync(CancellationToken cancellationToken = default)
            => inner.EndFileAsync(cancellationToken);

        public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            await active.WaitForProgressFlushAsync().ConfigureAwait(false);
            await owner.FlushProgressAsync(active, cancellationToken).ConfigureAwait(false);
            await owner.store.MarkIndexingAsync(active.BrowseId, cancellationToken).ConfigureAwait(false);
            owner.Publish(await owner.RequireResourceAsync(active.BrowseId, cancellationToken).ConfigureAwait(false));
            await inner.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class PeerBrowseUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class PeerBrowseAcquisitionException(string code, string message)
    : IOException(message)
{
    public string Code { get; } = code;
}

public sealed class PeerBrowseNotReadyException(PeerBrowseResource resource)
    : InvalidOperationException("The peer browse is not complete.")
{
    public PeerBrowseResource Resource { get; } = resource;
}

internal static partial class PeerBrowseLogMessages
{
    [LoggerMessage(
        EventId = 4200,
        EventName = "peer-browse.reused-fresh",
        Level = LogLevel.Debug,
        Message = "Reused fresh peer browse {BrowseId} for peer hash {PeerHash} at age {AgeMs} ms")]
    public static partial void ReusedFresh(
        ILogger logger, Guid browseId, string peerHash, long ageMs);

    [LoggerMessage(
        EventId = 4201,
        EventName = "peer-browse.reused-active",
        Level = LogLevel.Debug,
        Message = "Joined active peer browse {BrowseId} for peer hash {PeerHash}")]
    public static partial void ReusedActive(
        ILogger logger, Guid browseId, string peerHash);

    [LoggerMessage(
        EventId = 4202,
        EventName = "peer-browse.artifact-completed",
        Level = LogLevel.Debug,
        Message = "Peer browse {BrowseId} artifact contains {DirectoryCount} directories, {FileCount} files, and {TotalBytes} bytes")]
    public static partial void ArtifactCompleted(
        ILogger logger, Guid browseId, long directoryCount, long fileCount, long totalBytes);

    [LoggerMessage(
        EventId = 4203,
        EventName = "peer-browse.start-failed",
        Level = LogLevel.Error,
        Message = "Peer browse could not be created for peer hash {PeerHash}")]
    public static partial void StartFailed(
        ILogger logger, Exception exception, string peerHash);

    [LoggerMessage(
        EventId = 4204,
        EventName = "peer-browse.session-unavailable",
        Level = LogLevel.Warning,
        Message = "Peer browse could not start for peer hash {PeerHash} because Soulseek is not logged in")]
    public static partial void SessionUnavailable(ILogger logger, string peerHash);
}
