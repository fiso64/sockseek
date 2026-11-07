using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Sockseek.Api;

public enum LiveSubscriptionMode
{
    None,
    Daemon,
    Workflow,
    Chat,
    DaemonAndChat,
}

/// <summary>
/// Reusable SignalR live client. It owns subscriptions, buffering, HTTP snapshot
/// handoff, gap recovery, reconnect recovery, and a shared <see cref="DaemonClientStore"/>.
/// </summary>
public sealed class SockseekLiveClient : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SockseekApiClient api;
    private readonly HubConnection connection;
    private readonly DaemonStateDetail detail;
    private readonly ConcurrentDictionary<StateStreamScopeDto, ScopeSession> sessions = [];
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private bool protocolChecked;
    private bool disposed;

    public DaemonClientStore Store { get; } = new();
    public LiveSubscriptionMode Mode { get; private set; }

    public event Action<DaemonClientUpdate>? Updated;
    public event Action<StateSnapshotDto>? SnapshotApplied;
    public event Action<ActivityEventDto>? ActivityReceived;
    public event Action<StateStreamScopeDto, Exception>? SynchronizationFailed;
    /// <summary>The live connection ended after recovery was exhausted; callers must not wait for further updates.</summary>
    public event Action<Exception>? ConnectionFailed;

    public SockseekLiveClient(string serverUrl, JsonSerializerOptions? jsonOptions = null, DaemonStateDetail detail = DaemonStateDetail.Full)
        : this(SockseekApiClient.CreateHttpClient(serverUrl), ownsHttp: true, jsonOptions, detail)
    {
    }

    public SockseekLiveClient(
        HttpClient http,
        bool ownsHttp = false,
        JsonSerializerOptions? jsonOptions = null,
        DaemonStateDetail detail = DaemonStateDetail.Full)
    {
        this.http = http;
        this.ownsHttp = ownsHttp;
        this.detail = detail;
        var options = jsonOptions ?? SockseekApiJson.CreateSerializerOptions();
        api = new SockseekApiClient(http, options);
        var baseUri = http.BaseAddress
            ?? throw new ArgumentException("The HTTP client requires a daemon BaseAddress.", nameof(http));

        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, $"{LiveProtocol.HubPath}?liveProtocol={LiveProtocol.Version}"), connectionOptions =>
            {
                if (http.DefaultRequestHeaders.TryGetValues("X-API-Key", out var keys))
                    connectionOptions.Headers["X-API-Key"] = keys.Single();
                if (http.DefaultRequestHeaders.TryGetValues("Cookie", out var cookies))
                    connectionOptions.Headers["Cookie"] = string.Join("; ", cookies);
                connectionOptions.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler) clientHandler.AllowAutoRedirect = false;
                    // SignalR can redirect in its JSON negotiation response independently of HTTP redirects.
                    return new ServerOriginHandler(baseUri, handler);
                };
            })
            .AddJsonProtocol(json =>
            {
                json.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                SockseekApiJson.ConfigureSerializerOptions(json.PayloadSerializerOptions);
            })
            .WithAutomaticReconnect(new AuthenticatedReconnectPolicy())
            .Build();
        connection.On<StateUpdateBatchDto>(LiveProtocol.StateUpdateBatchMethod, HandleBatch);
        connection.Reconnected += _ => RecoverAfterReconnectAsync();
        connection.Closed += error =>
        {
            if (!disposed && Mode != LiveSubscriptionMode.None)
                ConnectionFailed?.Invoke(error ?? new IOException("The daemon live connection closed."));
            return Task.CompletedTask;
        };
    }

    public Task StartDaemonAsync(CancellationToken ct = default)
        => StartScopeAsync(
            StateStreamScopeDto.Daemon,
            static mode => mode != LiveSubscriptionMode.Workflow,
            static mode => mode is LiveSubscriptionMode.Daemon or LiveSubscriptionMode.DaemonAndChat,
            static mode => mode == LiveSubscriptionMode.Chat
                ? LiveSubscriptionMode.DaemonAndChat
                : LiveSubscriptionMode.Daemon,
            "Cannot mix daemon and workflow subscriptions on one live client.",
            token => connection.InvokeAsync(LiveProtocol.SubscribeAllMethod, detail, token),
            ct);

    /// <summary>Processing demand lasts exactly as long as this enumeration.</summary>
    public async IAsyncEnumerable<SearchViewSummaryDto> ObserveSearchViewAsync(
        Guid viewId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        finally { lifecycleGate.Release(); }
        await foreach (var summary in connection.StreamAsync<SearchViewSummaryDto>(
            LiveProtocol.ObserveSearchViewMethod, viewId, ct).ConfigureAwait(false))
            yield return summary;
    }

    public async IAsyncEnumerable<JobListUpdateDto> ObserveJobListAsync(
        JobListScopeDto scope,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        finally { lifecycleGate.Release(); }
        await foreach (var update in connection.StreamAsync<JobListUpdateDto>(
            LiveProtocol.ObserveJobListMethod, scope, ct).ConfigureAwait(false))
            yield return update;
    }

    public async IAsyncEnumerable<JobObservationUpdateDto> ObserveJobsAsync(
        Guid jobId,
        IReadOnlyList<Guid> jobIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        finally { lifecycleGate.Release(); }
        await foreach (var update in connection.StreamAsync<JobObservationUpdateDto>(
            LiveProtocol.ObserveJobsMethod, jobId, jobIds, ct).ConfigureAwait(false))
            yield return update;
    }

    public async IAsyncEnumerable<TransferTimelineUpdateDto> ObserveTransferTimelineAsync(
        TransferTimelineScopeDto scope, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        finally { lifecycleGate.Release(); }
        await foreach (var update in connection.StreamAsync<TransferTimelineUpdateDto>(
            LiveProtocol.ObserveTransferTimelineMethod, scope, ct).ConfigureAwait(false))
            yield return update;
    }

    public Task StartWorkflowAsync(Guid workflowId, CancellationToken ct = default)
        => StartScopeAsync(
            StateStreamScopeDto.Workflow(workflowId),
            static mode => mode is LiveSubscriptionMode.None or LiveSubscriptionMode.Workflow,
            static _ => false,
            static _ => LiveSubscriptionMode.Workflow,
            "Cannot mix workflow and daemon/chat subscriptions on one live client.",
            token => connection.InvokeAsync(LiveProtocol.SubscribeWorkflowMethod, workflowId, token),
            ct);

    /// <summary>
    /// Takes a fresh snapshot for an existing workflow subscription. Call this
    /// before starting a new live generation that deliberately reuses a retired
    /// workflow ID, so its new epoch is established before the first delta.
    /// </summary>
    public async Task RefreshWorkflowAsync(Guid workflowId, CancellationToken ct = default)
    {
        var scope = StateStreamScopeDto.Workflow(workflowId);
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Mode != LiveSubscriptionMode.Workflow
                || !sessions.TryGetValue(scope, out ScopeSession? session))
            {
                throw new InvalidOperationException("The workflow must be subscribed before it can be refreshed.");
            }

            session.BeginBuffering();
            await RecoverScopeAsync(scope, ct).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public Task StartConversationAsync(Guid conversationId, CancellationToken ct = default)
        => StartChatAsync(StateStreamScopeDto.ChatConversation(conversationId), ct);

    public Task StartRoomAsync(Guid roomId, CancellationToken ct = default)
        => StartChatAsync(StateStreamScopeDto.ChatRoom(roomId), ct);

    public Task StartUserBrowseAsync(Guid browseId, CancellationToken ct = default)
        => StartAuxiliaryScopeAsync(
            StateStreamScopeDto.UserBrowse(browseId),
            "Cannot mix user-browse and workflow subscriptions on one live client.",
            token => connection.InvokeAsync(LiveProtocol.SubscribeUserBrowseMethod, browseId, token),
            ct);

    public async Task StopUserBrowseAsync(Guid browseId, CancellationToken ct = default)
    {
        var scope = StateStreamScopeDto.UserBrowse(browseId);
        await lifecycleGate.WaitAsync(ct);
        try
        {
            if (sessions.TryRemove(scope, out ScopeSession? session))
            {
                if (connection.State == HubConnectionState.Connected)
                    await connection.InvokeAsync(LiveProtocol.UnsubscribeUserBrowseMethod, browseId, ct);
                session.Dispose();
                Store.RemoveUserBrowse(browseId);
            }
            RecomputeMode();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private Task StartChatAsync(StateStreamScopeDto scope, CancellationToken ct)
        => StartAuxiliaryScopeAsync(
            scope,
            "Cannot mix chat and workflow subscriptions on one live client.",
            token => connection.InvokeAsync(LiveProtocol.SubscribeChatMethod, scope, token),
            ct);

    private Task StartAuxiliaryScopeAsync(
        StateStreamScopeDto scope,
        string incompatibleModeMessage,
        Func<CancellationToken, Task> subscribe,
        CancellationToken cancellationToken)
        => StartScopeAsync(
            scope,
            static mode => mode != LiveSubscriptionMode.Workflow,
            static _ => false,
            static mode => mode == LiveSubscriptionMode.Daemon
                ? LiveSubscriptionMode.DaemonAndChat
                : mode == LiveSubscriptionMode.None
                    ? LiveSubscriptionMode.Chat
                    : mode,
            incompatibleModeMessage,
            subscribe,
            cancellationToken);

    private async Task StartScopeAsync(
        StateStreamScopeDto scope,
        Func<LiveSubscriptionMode, bool> isCompatible,
        Func<LiveSubscriptionMode, bool> isAlreadySubscribed,
        Func<LiveSubscriptionMode, LiveSubscriptionMode> nextMode,
        string incompatibleModeMessage,
        Func<CancellationToken, Task> subscribe,
        CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!isCompatible(Mode))
                throw new InvalidOperationException(incompatibleModeMessage);
            if (isAlreadySubscribed(Mode))
                return;

            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            Mode = nextMode(Mode);
            if (sessions.ContainsKey(scope))
                return;

            ScopeSession session = sessions.GetOrAdd(scope, static _ => new ScopeSession());
            bool subscribed = false;
            try
            {
                session.BeginBuffering();
                await subscribe(cancellationToken).ConfigureAwait(false);
                subscribed = true;
                await RecoverScopeAsync(scope, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RollBackInitialSubscriptionAsync(scope, session, subscribed).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopChatAsync(StateStreamScopeDto scope, CancellationToken ct = default)
    {
        if (scope.Kind is not (StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
            throw new ArgumentException("A chat scope is required.", nameof(scope));
        await lifecycleGate.WaitAsync(ct);
        try
        {
            if (sessions.TryRemove(scope, out var session))
            {
                if (connection.State == HubConnectionState.Connected)
                    await connection.InvokeAsync(LiveProtocol.UnsubscribeChatMethod, scope, ct);
                session.Dispose();
                Store.RemoveChatTarget(scope);
            }
            bool hasDaemon = sessions.ContainsKey(StateStreamScopeDto.Daemon);
            bool hasAuxiliary = sessions.Keys.Any(item => item.Kind is
                StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom or StateStreamScopeKind.UserBrowse);
            Mode = (hasDaemon, hasAuxiliary) switch
            {
                (true, true) => LiveSubscriptionMode.DaemonAndChat,
                (true, false) => LiveSubscriptionMode.Daemon,
                (false, true) => LiveSubscriptionMode.Chat,
                _ => LiveSubscriptionMode.None,
            };
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public SharingStateDto? GetSharing()
        => Store.GetSharing();

    public UploadRuntimeStateDto? GetUploadRuntime()
        => Store.GetUploadRuntime();

    public IReadOnlyList<TransferStateDto> GetActiveTransfers()
        => Store.GetActiveTransfers();

    public Task<SharingStateDto> GetSharingAsync(CancellationToken ct = default)
        => api.GetSharingAsync(ct);

    public Task<StartShareScanResponseDto> StartShareScanAsync(
        CancellationToken ct = default)
        => api.StartShareScanAsync(ct);

    public Task<ShareScanStateDto?> GetShareScanAsync(
        Guid scanId,
        CancellationToken ct = default)
        => api.GetShareScanAsync(scanId, ct);

    public async Task CancelShareScanAsync(
        Guid scanId,
        CancellationToken ct = default)
        => _ = await api.CancelShareScanAsync(scanId, ct);

    public async Task CancelTransferAsync(
        Guid transferId,
        CancellationToken ct = default)
        => _ = await api.CancelTransferAsync(transferId, ct);

    public Task<TransferCommandReceiptDto> CancelTransfersAsync(
        BulkCancelTransfersRequestDto request,
        CancellationToken ct = default)
        => api.CancelTransfersAsync(request, ct);

    public Task<TransferCommandReceiptDto> RemoveTransferAsync(
        Guid transferId,
        CancellationToken ct = default)
        => api.RemoveTransferAsync(transferId, ct);

    public Task<TransferCommandReceiptDto> RemoveTransfersAsync(
        RemoveTransfersRequestDto request,
        CancellationToken ct = default)
        => api.RemoveTransfersAsync(request, ct);

    public Task<LiveTransferPageDto> LoadLiveTransferPageAsync(
        LiveTransferFilter? filter = null,
        string? cursor = null,
        int limit = 100,
        CancellationToken ct = default)
        => api.LoadLiveTransferPageAsync(filter, cursor, limit, ct);

    public Task<TransferTimelinePageDto> LoadTransferHistoryPageAsync(
        TransferHistoryFilter? filter = null,
        string? cursor = null,
        int limit = 100,
        CancellationToken ct = default)
        => api.GetTransfersPageAsync(filter, cursor, limit, ct: ct);

    public Task<DashboardAnalyticsDto> LoadDashboardAnalyticsAsync(
        string range = "24h",
        CancellationToken ct = default)
        => api.GetDashboardAnalyticsAsync(range, ct);

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!protocolChecked)
        {
            var info = await api.GetServerInfoAsync(ct);
            if (info.LiveProtocolVersion != LiveProtocol.Version)
            {
                throw new InvalidOperationException(
                    $"Incompatible live protocol: client requires {LiveProtocol.Version}, "
                    + $"server exposes {info.LiveProtocolVersion}.");
            }
            protocolChecked = true;
        }

        if (connection.State == HubConnectionState.Disconnected)
            await connection.StartAsync(ct);
    }

    private void HandleBatch(StateUpdateBatchDto batch)
    {
        if (!sessions.TryGetValue(batch.Scope, out var session))
            return;

        if (session.TryBuffer(batch))
            return;
        if (session.IsDisposed)
            return;

        var update = Store.Apply(batch);
        Publish(update);
        if (update.Status == DaemonClientApplyStatus.RecoveryRequired)
        {
            session.BeginBuffering(batch);
            ScheduleRecovery(batch.Scope);
        }
    }

    private async Task RecoverAfterReconnectAsync()
    {
        var scopes = sessions.Keys.ToList();
        foreach (var scope in scopes)
            if (sessions.TryGetValue(scope, out ScopeSession? session))
                session.BeginBuffering();

        if (Mode is LiveSubscriptionMode.Daemon or LiveSubscriptionMode.DaemonAndChat)
            await connection.InvokeAsync(LiveProtocol.SubscribeAllMethod, detail);
        if (Mode == LiveSubscriptionMode.Workflow)
        {
            foreach (var scope in scopes.Where(scope => scope.Kind == StateStreamScopeKind.Workflow))
                await connection.InvokeAsync(LiveProtocol.SubscribeWorkflowMethod, scope.WorkflowId!.Value);
        }
        if (Mode is LiveSubscriptionMode.Chat or LiveSubscriptionMode.DaemonAndChat)
        {
            foreach (var scope in scopes.Where(scope => scope.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom))
                await connection.InvokeAsync(LiveProtocol.SubscribeChatMethod, scope);
            foreach (var scope in scopes.Where(scope => scope.Kind == StateStreamScopeKind.UserBrowse))
                await connection.InvokeAsync(LiveProtocol.SubscribeUserBrowseMethod, scope.UserBrowseId!.Value);
        }

        await Task.WhenAll(scopes.Select(RecoverScopeSafelyAsync));
    }

    private void ScheduleRecovery(StateStreamScopeDto scope)
    {
        if (!sessions.TryGetValue(scope, out ScopeSession? session))
            return;
        if (!session.TryScheduleRecovery())
            return;
        _ = RecoverScopeSafelyAsync(scope);
    }

    private async Task RecoverScopeSafelyAsync(StateStreamScopeDto scope)
    {
        if (!sessions.TryGetValue(scope, out ScopeSession? session))
            return;
        try
        {
            int attempt = 0;
            while (!lifetime.IsCancellationRequested && !session.IsDisposed)
            {
                try
                {
                    await RecoverScopeAsync(scope, lifetime.Token);
                    return;
                }
                catch (OperationCanceledException) when (
                    lifetime.IsCancellationRequested || session.IsDisposed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SynchronizationFailed?.Invoke(scope, ex);
                    if (AuthenticationRejected(ex))
                    {
                        ConnectionFailed?.Invoke(ex);
                        return;
                    }
                    int delayMs = Math.Min(5_000, 100 * (1 << Math.Min(attempt++, 5)));
                    try
                    {
                        await Task.Delay(delayMs, lifetime.Token);
                    }
                    catch (OperationCanceledException) when (
                        lifetime.IsCancellationRequested || session.IsDisposed)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            session.CompleteScheduledRecovery();
        }
    }

    private async Task RecoverScopeAsync(StateStreamScopeDto scope, CancellationToken ct)
    {
        if (!sessions.TryGetValue(scope, out ScopeSession? session))
            return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, session.CancellationToken);
        CancellationToken recoveryToken = linked.Token;
        await session.RecoveryGate.WaitAsync(recoveryToken);
        try
        {
            while (true)
            {
                var snapshot = scope.Kind switch
                {
                    StateStreamScopeKind.Daemon => await api.GetDaemonSnapshotAsync(recoveryToken, detail),
                    StateStreamScopeKind.Workflow => await api.GetWorkflowSnapshotAsync(scope.WorkflowId!.Value, recoveryToken),
                    StateStreamScopeKind.ChatConversation => await api.GetConversationSnapshotAsync(scope.ChatTargetId!.Value, recoveryToken),
                    StateStreamScopeKind.ChatRoom => await api.GetRoomSnapshotAsync(scope.ChatTargetId!.Value, recoveryToken),
                    StateStreamScopeKind.UserBrowse => await api.GetUserBrowseSnapshotAsync(scope.UserBrowseId!.Value, recoveryToken),
                    _ => throw new ArgumentOutOfRangeException(),
                };

                var snapshotUpdate = Store.ApplySnapshot(snapshot);
                SnapshotApplied?.Invoke(snapshot);
                Publish(snapshotUpdate);

                bool restartRecovery = false;
                while (true)
                {
                    BufferedDrain drain = session.Drain();
                    if (drain.Overflowed)
                    {
                        restartRecovery = true;
                        break;
                    }
                    if (drain.Batches.Count == 0)
                    {
                        if (session.EndBufferingIfEmpty())
                            return;
                        continue;
                    }

                    foreach (var batch in drain.Batches
                        .OrderBy(batch => batch.Sequence)
                        .ThenBy(batch => batch.PreviousSequence))
                    {
                        var update = Store.Apply(batch);
                        Publish(update);
                        if (update.Status == DaemonClientApplyStatus.RecoveryRequired)
                        {
                            restartRecovery = true;
                            break;
                        }
                    }

                    if (restartRecovery)
                        break;
                }
            }
        }
        finally
        {
            session.RecoveryGate.Release();
        }
    }

    private async Task RollBackInitialSubscriptionAsync(
        StateStreamScopeDto scope,
        ScopeSession session,
        bool subscribed)
    {
        if (subscribed && connection.State == HubConnectionState.Connected)
        {
            try
            {
                if (scope.Kind == StateStreamScopeKind.Daemon)
                    await connection.InvokeAsync(LiveProtocol.UnsubscribeAllMethod);
                else if (scope.Kind == StateStreamScopeKind.Workflow)
                    await connection.InvokeAsync(LiveProtocol.UnsubscribeWorkflowMethod, scope.WorkflowId!.Value);
                else if (scope.Kind == StateStreamScopeKind.UserBrowse)
                    await connection.InvokeAsync(LiveProtocol.UnsubscribeUserBrowseMethod, scope.UserBrowseId!.Value);
                else
                    await connection.InvokeAsync(LiveProtocol.UnsubscribeChatMethod, scope);
            }
            catch
            {
                // Stopping the connection below clears any server-side group membership.
            }
        }

        if (sessions.TryRemove(scope, out var removed))
            removed.Dispose();
        else
            session.Dispose();
        if (scope.Kind is StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom)
            Store.RemoveChatTarget(scope);
        else if (scope.Kind == StateStreamScopeKind.UserBrowse)
            Store.RemoveUserBrowse(scope.UserBrowseId!.Value);

        RecomputeMode();
        if (!sessions.IsEmpty)
            return;

        if (connection.State != HubConnectionState.Disconnected)
        {
            try
            {
                await connection.StopAsync();
            }
            catch
            {
                // Preserve the original subscription/snapshot exception.
            }
        }
    }

    private void Publish(DaemonClientUpdate update)
    {
        Updated?.Invoke(update);
        foreach (var activity in update.Activity)
            ActivityReceived?.Invoke(activity);
    }

    private void RecomputeMode()
    {
        bool hasDaemon = sessions.ContainsKey(StateStreamScopeDto.Daemon);
        bool hasWorkflow = sessions.Keys.Any(item => item.Kind == StateStreamScopeKind.Workflow);
        bool hasChat = sessions.Keys.Any(item => item.Kind is
            StateStreamScopeKind.ChatConversation or StateStreamScopeKind.ChatRoom or StateStreamScopeKind.UserBrowse);
        Mode = hasWorkflow
            ? LiveSubscriptionMode.Workflow
            : (hasDaemon, hasChat) switch
            {
                (true, true) => LiveSubscriptionMode.DaemonAndChat,
                (true, false) => LiveSubscriptionMode.Daemon,
                (false, true) => LiveSubscriptionMode.Chat,
                _ => LiveSubscriptionMode.None,
            };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        await lifetime.CancelAsync();
        await connection.DisposeAsync();
        lifecycleGate.Dispose();
        foreach (var session in sessions.Values)
            session.Dispose();
        lifetime.Dispose();
        if (ownsHttp)
            http.Dispose();
    }

    private sealed class ScopeSession : IDisposable
    {
        private const int MaximumBufferedBatches = 2_048;
        private readonly object gate = new();
        private readonly List<StateUpdateBatchDto> buffered = [];
        private readonly CancellationTokenSource lifetime = new();
        private bool buffering = true;
        private bool recoveryScheduled;
        private bool overflowed;

        public SemaphoreSlim RecoveryGate { get; } = new(1, 1);
        public CancellationToken CancellationToken => lifetime.Token;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        private int disposed;

        public void BeginBuffering(StateUpdateBatchDto? first = null)
        {
            lock (gate)
            {
                buffering = true;
                if (first != null)
                    AddBounded(first);
            }
        }

        public bool TryBuffer(StateUpdateBatchDto batch)
        {
            lock (gate)
            {
                if (IsDisposed)
                    return false;
                if (!buffering)
                    return false;
                AddBounded(batch);
                return true;
            }
        }

        public BufferedDrain Drain()
        {
            lock (gate)
            {
                var result = buffered.ToList();
                buffered.Clear();
                bool hadOverflow = overflowed;
                overflowed = false;
                return new BufferedDrain(result, hadOverflow);
            }
        }

        public bool EndBufferingIfEmpty()
        {
            lock (gate)
            {
                if (buffered.Count != 0)
                    return false;
                buffering = false;
                return true;
            }
        }

        public bool TryScheduleRecovery()
        {
            lock (gate)
            {
                if (recoveryScheduled)
                    return false;
                recoveryScheduled = true;
                return true;
            }
        }

        public void CompleteScheduledRecovery()
        {
            lock (gate)
                recoveryScheduled = false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lifetime.Cancel();
        }

        private void AddBounded(StateUpdateBatchDto batch)
        {
            if (buffered.Count == MaximumBufferedBatches)
            {
                buffered.Clear();
                overflowed = true;
            }
            buffered.Add(batch);
        }
    }

    private sealed record BufferedDrain(
        IReadOnlyList<StateUpdateBatchDto> Batches,
        bool Overflowed);

    private sealed class AuthenticatedReconnectPolicy : IRetryPolicy
    {
        private static readonly int[] Delays = [0, 2, 10, 30];
        public TimeSpan? NextRetryDelay(RetryContext context)
            => AuthenticationRejected(context.RetryReason) || context.PreviousRetryCount >= Delays.Length
                ? null : TimeSpan.FromSeconds(Delays[context.PreviousRetryCount]);

    }

    private static bool AuthenticationRejected(Exception? error)
        => error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            || error is SockseekApiRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            || error is AggregateException aggregate && aggregate.InnerExceptions.Any(AuthenticationRejected)
            || error?.InnerException is { } inner && AuthenticationRejected(inner);
}
