using Sockseek.Core;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Transfers.Uploads;
using Sockseek.Api;
using Sockseek.Persistence.Sharing;
using Soulseek;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Server;

/// <summary>
/// Daemon-lifetime owner of the public catalog, scanner, and upload runtime.
/// The neutral daemon Soulseek runtime owns the shared network session.
/// </summary>
public sealed class SharingRuntime : IAsyncDisposable
{
    private readonly EngineSettings settings;
    private sealed record ShareConfiguration(SharingSettings Settings, string Hash);
    private ShareConfiguration shareConfiguration;
    private UploadSettings uploadSettings;
    private readonly ILogger<SharingRuntime> logger;
    private readonly FeatureHealthLogger sharingHealthLog;
    private readonly FeatureHealthLogger uploadHealthLog;
    private readonly ShareCatalogManager catalogs;
    private readonly ShareScanCoordinator scans;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object countSync = new();
    private bool countPublicationRequested;
    private Task? countPublicationWorker;
    private Task? periodicTask;
    private Task? sessionStartupTask;
    private bool started;
    private readonly object stateSync = new();
    private readonly Dictionary<Guid, ShareScanStateDto> recentScans = [];
    private readonly Queue<Guid> recentScanOrder = [];
    private long scanRevision;
    private ShareScanStateDto? activeScan;
    private ShareScanStateDto? lastScan;
    private Task? manuallyStartedScan;
    private readonly DaemonSoulseekRuntime soulseek;
    private readonly IDisposable inboundRegistration;

    public SharingRuntime(
        EngineSettings settings,
        string dataDirectory,
        DaemonSoulseekRuntime soulseek,
        ILogger<SharingRuntime>? logger = null,
        ILogger<UploadCoordinator>? uploadLogger = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        shareConfiguration = new(settings.Sharing, SharingSettingsFingerprint.Compute(settings.Sharing));
        uploadSettings = settings.Uploads;
        this.soulseek = soulseek ?? throw new ArgumentNullException(nameof(soulseek));
        this.logger = logger ?? NullLogger<SharingRuntime>.Instance;
        sharingHealthLog = new FeatureHealthLogger(this.logger, "sharing");
        uploadHealthLog = new FeatureHealthLogger(this.logger, "uploads");
        string catalogDirectory = Path.Combine(
            Path.GetFullPath(dataDirectory),
            "sharing");
        catalogs = new ShareCatalogManager(catalogDirectory);
        scans = new ShareScanCoordinator(catalogs);

        SoulseekClientManager manager = soulseek.ClientManager;
        Restrictions = soulseek.Restrictions;
        Scheduler = new UploadScheduler(settings.Uploads);
        Uploads = new UploadCoordinator(
            catalogs,
            () => manager.Client,
            Restrictions,
            Scheduler,
            uploadLogger);
        Adapter = new SoulseekSharingAdapter(
            catalogs,
            Uploads,
            Restrictions,
            settings.Uploads,
            () => manager.Client,
            soulseek.LocalProfile,
            uploadServingEnabled: settings.ListenPort is not null,
            logger: this.logger);
        ClientManager = manager;
        inboundRegistration = soulseek.InboundRequests.Attach(Adapter);
        ClientManager.StateChanged += OnClientStateChanged;
        scans.StateChanged += OnScanStateChanged;
        Uploads.QueueChanged += OnUploadQueueChanged;
    }

    public SoulseekClientManager ClientManager { get; }
    public ShareCatalogManager Catalogs => catalogs;
    public ShareScanCoordinator Scans => scans;
    public PeerRestrictionPolicy Restrictions { get; }
    public UploadScheduler Scheduler { get; }
    public UploadCoordinator Uploads { get; }
    public SoulseekSharingAdapter Adapter { get; }
    public string SettingsHash => Volatile.Read(ref shareConfiguration).Hash;
    public event Action<SharingStateDto, UploadRuntimeStateDto>? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started)
            return;
        started = true;
        bool ready = await catalogs.InitializeAsync(
            SettingsHash,
            cancellationToken).ConfigureAwait(false);

        ShareCatalogMetadata? initializedCatalog = catalogs.CurrentMetadata;
        if (settings.Sharing.Roots.Count > 0 && initializedCatalog is not null)
        {
            SharingLogMessages.CatalogLoaded(
                logger,
                initializedCatalog.GenerationId,
                initializedCatalog.DirectoryCount,
                initializedCatalog.FileCount,
                initializedCatalog.TotalBytes);
        }

        if (settings.Sharing.Roots.Count > 0)
        {
            // Session startup and a potentially long first scan must not delay
            // the daemon API or download engine. Both have their own bounded,
            // observed lifetime and publish readiness as they complete.
            sessionStartupTask = StartSessionForSharingAsync(lifetime.Token);
            if (!ready || settings.Sharing.ScanOnStart)
                _ = StartScan();
        }

        if (settings.Sharing.RescanInterval is not null)
            periodicTask = RunPeriodicScansAsync(lifetime.Token);
        PublishState();
    }

    private async Task StartSessionForSharingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await soulseek.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            await RequestCountPublication().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The manager keeps retrying transient failures. Permanent session
            // failures remain visible through readiness without taking down the
            // daemon or preventing ordinary non-network API work.
            SharingLogMessages.SessionStartupFailed(logger, ex);
            PublishState();
        }
    }

    public async ValueTask<ShareScanResult> ScanNowAsync(
        CancellationToken cancellationToken = default)
    {
        // One scan retains one configuration even when the file changes midway.
        ShareConfiguration configuration = Volatile.Read(ref shareConfiguration);
        SharingSettings current = configuration.Settings;
        ShareScanResult result = await scans.ScanAsync(
            current,
            configuration.Hash,
            cancellationToken).ConfigureAwait(false);
        if (current.Roots.Count > 0 && !ClientManager.IsConnectedAndLoggedIn)
            sessionStartupTask = StartSessionForSharingAsync(lifetime.Token);
        // Catalog publication is authoritative even if the connected server
        // rejects or delays this derived count update. Reconnect retries it.
        await RequestCountPublication().ConfigureAwait(false);
        return result;
    }

    public (bool Started, ShareScanStateDto Scan)? StartScan()
    {
        if (lifetime.IsCancellationRequested)
            return null;

        lock (stateSync)
        {
            if (activeScan is not null)
                return (false, activeScan);
        }

        Task scanTask;
        try
        {
            scanTask = ScanNowAsync(lifetime.Token).AsTask();
        }
        catch (ShareScanAlreadyRunningException)
        {
            lock (stateSync)
                return activeScan is null ? null : (false, activeScan);
        }

        ShareScanStateDto? scan;
        lock (stateSync)
        {
            manuallyStartedScan = scanTask;
            scan = activeScan;
            if (scan is null
                && scans.State.GenerationId is { } completedId)
            {
                scan = recentScans.GetValueOrDefault(completedId);
            }
        }
        _ = ObserveManualScanAsync(scanTask);
        return scan is null ? null : (true, scan);
    }

    private async Task ObserveManualScanAsync(Task scanTask)
    {
        try
        {
            await scanTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SharingLogMessages.ScanFailed(logger, ex, "manual");
        }
        finally
        {
            lock (stateSync)
            {
                if (ReferenceEquals(manuallyStartedScan, scanTask))
                    manuallyStartedScan = null;
            }
        }
    }

    private async Task RunPeriodicScansAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(settings.Sharing.RescanInterval!.Value);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ScanNowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ShareScanAlreadyRunningException)
                {
                    // One elapsed interval coalesces behind the active scan.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SharingLogMessages.ScanFailed(logger, ex, "periodic");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnClientStateChanged(SoulseekClientStates state)
    {
        PublishState();
        if (!state.HasFlag(SoulseekClientStates.Connected)
            || !state.HasFlag(SoulseekClientStates.LoggedIn))
            return;
        _ = RequestCountPublication();
    }

    private void OnUploadQueueChanged()
    {
        PublishState();
    }

    private void OnScanStateChanged(ShareScanState state)
    {
        if (state.GenerationId is not { } scanId)
        {
            PublishState();
            return;
        }

        long revision = Interlocked.Increment(ref scanRevision);
        bool terminal = state.Phase is ShareScanPhase.Completed
            or ShareScanPhase.Cancelled
            or ShareScanPhase.Failed;
        bool cancellable = !terminal && state.Phase != ShareScanPhase.Cancelling;
        ShareScanResult? result = state.Result;
        ShareScanProgress? progress = state.Progress;
        if (state.Phase == ShareScanPhase.Preparing)
            SharingLogMessages.ScanStarted(logger, scanId);
        else if (state.Phase == ShareScanPhase.Completed && result is not null)
        {
            SharingLogMessages.ScanCompleted(
                logger,
                scanId,
                (long)result.TotalElapsed.TotalMilliseconds,
                result.DirectoriesVisited,
                result.FilesIndexed,
                result.BytesIndexed,
                result.Errors.Count);
        }
        else if (state.Phase == ShareScanPhase.Cancelled)
        {
            SharingLogMessages.ScanCancelled(
                logger,
                scanId,
                state.StartedAtUtc is { } started
                    ? (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds
                    : 0);
        }
        var dto = new ShareScanStateDto(
            scanId,
            revision,
            state.Phase.ToServer(),
            state.StartedAtUtc ?? DateTimeOffset.UtcNow,
            state.FinishedAtUtc,
            result?.DirectoriesVisited ?? progress?.DirectoriesDiscovered ?? 0,
            result?.FilesIndexed ?? progress?.FilesDiscovered ?? 0,
            result?.BytesIndexed ?? progress?.BytesDiscovered ?? 0,
            result?.Errors.Count ?? progress?.ErrorCount ?? (state.ErrorCode is null ? 0 : 1),
            result?.Errors.Select(error => new ShareScanErrorSampleDto(
                    error.Code,
                    error.RelativePath,
                    error.Message))
                .ToArray() ?? [],
            cancellable
                ?
                [
                    new ResourceActionDto(
                        ServerResourceActionKind.Cancel,
                        "POST",
                        $"/api/sharing/scans/{scanId:D}/cancel"),
                ]
                : []);

        lock (stateSync)
        {
            if (terminal)
            {
                activeScan = null;
                lastScan = dto;
                recentScans[scanId] = dto;
                recentScanOrder.Enqueue(scanId);
                while (recentScanOrder.Count > 32)
                    recentScans.Remove(recentScanOrder.Dequeue());
            }
            else
            {
                activeScan = dto;
                recentScans[scanId] = dto;
            }
        }
        PublishState();
    }

    public SharingStateDto GetSharingState()
    {
        ShareConfiguration configuration = Volatile.Read(ref shareConfiguration);
        SharingSettings current = configuration.Settings;
        ShareCatalogMetadata? metadata = catalogs.CurrentMetadata;
        ShareScanStateDto? active;
        ShareScanStateDto? last;
        lock (stateSync)
        {
            active = activeScan;
            last = lastScan;
        }
        bool configured = current.Roots.Count > 0;
        bool catalogReady = metadata is not null;
        bool catalogStale = catalogReady && last?.Phase == ServerShareScanPhase.Failed;
        DaemonFeatureState state;
        string? reason;
        if (catalogReady && metadata!.SettingsHash != configuration.Hash)
        {
            state = DaemonFeatureState.Degraded;
            reason = "RescanRequired";
        }
        else if (!configured)
        {
            state = DaemonFeatureState.Disabled;
            reason = "NotConfigured";
        }
        else if (!catalogReady)
        {
            state = DaemonFeatureState.Starting;
            reason = "CatalogUnavailable";
        }
        else if (catalogStale)
        {
            state = DaemonFeatureState.Degraded;
            reason = "LastScanFailed";
        }
        else if (metadata!.BrowseStatus != ShareBrowseStatus.Ready)
        {
            state = DaemonFeatureState.Degraded;
            reason = "BrowseUnavailable";
        }
        else if (!ClientManager.IsConnectedAndLoggedIn)
        {
            state = DaemonFeatureState.Degraded;
            reason = "SessionUnavailable";
        }
        else
        {
            state = DaemonFeatureState.Ready;
            reason = null;
        }
        PeerRestrictionSnapshot restrictionSnapshot = Restrictions.Snapshot;
        return new SharingStateDto(
            state,
            reason,
            current.Roots
                .Select(root => root.EffectiveAlias)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            restrictionSnapshot.UploadAccess.BlockedUsernameCount,
            restrictionSnapshot.ConfiguredUploadBlockedIpAddresses.Count,
            new ShareCatalogStateDto(
                metadata?.GenerationId,
                metadata?.DirectoryCount ?? 0,
                metadata?.FileCount ?? 0,
                metadata?.TotalBytes ?? 0,
                metadata?.BrowseStatus == ShareBrowseStatus.Ready,
                metadata?.BrowseLengthBytes,
                metadata?.CreatedAtUtc),
            active,
            last);
    }

    public UploadRuntimeStateDto GetUploadRuntimeState()
    {
        bool configured = Volatile.Read(ref shareConfiguration).Settings.Roots.Count > 0;
        bool listenerReady = ClientManager.IsConnectedAndLoggedIn
                             && settings.ListenPort is not null;
        bool catalogReady = catalogs.CurrentMetadata is not null;
        UploadQueueRuntimeSnapshot queue = Uploads.GetQueueSnapshot();
        bool accepting = configured
                         && listenerReady
                         && catalogReady
                         && queue.AcceptingUploads;
        DaemonFeatureState state = !configured
            ? DaemonFeatureState.Disabled
            : !catalogReady || !ClientManager.IsConnectedAndLoggedIn
                ? DaemonFeatureState.Starting
                : accepting
                    ? DaemonFeatureState.Ready
                    : DaemonFeatureState.Degraded;
        string? reason = accepting
            ? null
            : !configured
                ? "NotConfigured"
                : !catalogReady
                    ? "CatalogUnavailable"
                    : !listenerReady
                        ? "PeerListenerUnavailable"
                        : "QueueCapacity";
        return new UploadRuntimeStateDto(
            state,
            reason,
            accepting,
            queue.TotalSlots,
            queue.ActiveSlots,
            queue.QueuedFiles,
            queue.QueuedBytes,
            queue.QueueRevision,
            Volatile.Read(ref uploadSettings).SpeedLimitKiBPerSecond);
    }

    public async Task ReloadConfigurationAsync(EngineSettings candidate, CancellationToken cancellationToken = default)
    {
        // The client also reconciles an uncertain limit after a failed network
        // notification. Normally this is a no-op when the speed is unchanged.
        await ClientManager.UpdateUploadSpeedLimitAsync(candidate.Uploads.SpeedLimitKiBPerSecond, cancellationToken).ConfigureAwait(false);
        if (await soulseek.ReloadProfileAsync(candidate, cancellationToken).ConfigureAwait(false))
            Adapter.UpdateLocalProfile(soulseek.LocalProfile);

        bool uploadsChanged = !SettingsSchema.ValuesEqual(uploadSettings, candidate.Uploads);
        bool slotsChanged = uploadSettings.Slots != candidate.Uploads.Slots;
        if (uploadsChanged)
        {
            Volatile.Write(ref uploadSettings, candidate.Uploads);
            Adapter.UpdateUploadSettings(candidate.Uploads);
        }

        bool sharesChanged = false;
        if (!SettingsSchema.ValuesEqual(shareConfiguration.Settings, candidate.Sharing))
        {
            string hash = SharingSettingsFingerprint.Compute(candidate.Sharing);
            sharesChanged = hash != SettingsHash;
            Volatile.Write(ref shareConfiguration, new(candidate.Sharing, hash));
            if (sharesChanged) SharingLogMessages.RescanRequired(logger);
        }
        if (slotsChanged) Uploads.SetSlots(candidate.Uploads.Slots); // Publishes the changed queue and runtime state.
        else if (uploadsChanged || sharesChanged) PublishState();
    }

    public ShareScanStateDto? GetScan(Guid scanId)
    {
        lock (stateSync)
            return recentScans.GetValueOrDefault(scanId);
    }

    public bool CancelScan(Guid scanId)
    {
        lock (stateSync)
        {
            if (activeScan?.ScanId != scanId)
                return false;
        }
        return scans.Cancel();
    }

    public void PublishPeerRestrictionsChanged() => PublishState();

    private void PublishState()
    {
        SharingStateDto sharing = GetSharingState();
        UploadRuntimeStateDto uploads = GetUploadRuntimeState();
        sharingHealthLog.Observe(
            sharing.State.ToString(),
            sharing.Reason ?? "Available");
        uploadHealthLog.Observe(
            uploads.State.ToString(),
            uploads.Reason ?? "Available");
        SharingTelemetry.UpdateCatalog(catalogs.CurrentMetadata);
        SharingTelemetry.UpdateQueue(Uploads.GetQueueSnapshot());
        Action<SharingStateDto, UploadRuntimeStateDto>? handlers = StateChanged;
        if (handlers is null)
            return;
        foreach (Action<SharingStateDto, UploadRuntimeStateDto> handler
                 in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                handler(sharing, uploads);
            }
            catch
            {
                // Live-state observers are non-authoritative projections.
            }
        }
    }

    private Task RequestCountPublication()
    {
        lock (countSync)
        {
            countPublicationRequested = true;
            return countPublicationWorker ??= RunCountPublicationWorkerAsync();
        }
    }

    private async Task RunCountPublicationWorkerAsync()
    {
        // Ensure the request method can install the task before this worker
        // takes the same lock, even when no asynchronous client work is needed.
        await Task.Yield();
        while (true)
        {
            lock (countSync)
            {
                if (!countPublicationRequested)
                {
                    countPublicationWorker = null;
                    return;
                }
                countPublicationRequested = false;
            }

            await PublishCountsOnceSafeAsync().ConfigureAwait(false);
        }
    }

    private async Task PublishCountsOnceSafeAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await PublishCountsAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
                SharingLogMessages.CountPublicationTimedOut(logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SharingLogMessages.CountPublicationFailed(logger, ex);
        }
    }

    private Task PublishCountsAsync(CancellationToken cancellationToken)
    {
        ShareCatalogMetadata? metadata = catalogs.CurrentMetadata;
        ISoulseekClient? client = ClientManager.Client;
        if (client is null || !ClientManager.IsConnectedAndLoggedIn)
            return Task.CompletedTask;
        SharingLogMessages.PublishingCounts(
            logger,
            metadata?.DirectoryCount ?? 0,
            metadata?.FileCount ?? 0);
        return client.SetSharedCountsAsync(
            checked((int)(metadata?.DirectoryCount ?? 0)),
            checked((int)(metadata?.FileCount ?? 0)),
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        scans.Cancel();
        if (periodicTask is not null)
        {
            try
            {
                await periodicTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task? scanTask;
        lock (stateSync)
            scanTask = manuallyStartedScan;
        if (scanTask is not null)
        {
            try
            {
                await scanTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                // The observer has already recorded and logged the terminal
                // scan failure; shutdown only needs to join the worker.
            }
        }

        if (sessionStartupTask is not null)
            await sessionStartupTask.ConfigureAwait(false);

        ClientManager.StateChanged -= OnClientStateChanged;
        scans.StateChanged -= OnScanStateChanged;
        Uploads.QueueChanged -= OnUploadQueueChanged;
        Task? countTask;
        lock (countSync)
            countTask = countPublicationWorker;
        if (countTask is not null)
            await countTask.ConfigureAwait(false);
        await Uploads.DisposeAsync().ConfigureAwait(false);
        inboundRegistration.Dispose();
        Adapter.Dispose();
        await catalogs.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}

internal static partial class SharingLogMessages
{
    [LoggerMessage(4310, LogLevel.Information, "Shared folders or filters changed; rescan shares to apply the new configuration", EventName = "sharing.rescan-required")]
    public static partial void RescanRequired(ILogger logger);
    [LoggerMessage(
        EventId = 4301,
        EventName = "sharing.catalog-loaded",
        Level = LogLevel.Information,
        Message = "Loaded share catalog {GenerationId} with {DirectoryCount} directories, {FileCount} files, and {TotalBytes} bytes")]
    public static partial void CatalogLoaded(
        ILogger logger, Guid generationId, long directoryCount, long fileCount, long totalBytes);

    [LoggerMessage(
        EventId = 4302,
        EventName = "sharing.session-startup-failed",
        Level = LogLevel.Error,
        Message = "Initial sharing session startup failed")]
    public static partial void SessionStartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4303,
        EventName = "sharing.scan-started",
        Level = LogLevel.Information,
        Message = "Share scan {ScanId} started")]
    public static partial void ScanStarted(ILogger logger, Guid scanId);

    [LoggerMessage(
        EventId = 4304,
        EventName = "sharing.scan-completed",
        Level = LogLevel.Information,
        Message = "Share scan {ScanId} completed in {DurationMs} ms with {DirectoryCount} directories, {FileCount} files, {TotalBytes} bytes, and {ErrorCount} isolated errors")]
    public static partial void ScanCompleted(
        ILogger logger, Guid scanId, long durationMs, long directoryCount,
        long fileCount, long totalBytes, int errorCount);

    [LoggerMessage(
        EventId = 4305,
        EventName = "sharing.scan-cancelled",
        Level = LogLevel.Information,
        Message = "Share scan {ScanId} was cancelled after {DurationMs} ms")]
    public static partial void ScanCancelled(ILogger logger, Guid scanId, long durationMs);

    [LoggerMessage(
        EventId = 4306,
        EventName = "sharing.scan-failed",
        Level = LogLevel.Error,
        Message = "{ScanKind} share scan failed")]
    public static partial void ScanFailed(
        ILogger logger, Exception exception, string scanKind);

    [LoggerMessage(
        EventId = 4307,
        EventName = "sharing.count-publication-timeout",
        Level = LogLevel.Warning,
        Message = "Publishing Soulseek share counts timed out")]
    public static partial void CountPublicationTimedOut(ILogger logger);

    [LoggerMessage(
        EventId = 4308,
        EventName = "sharing.count-publication-failed",
        Level = LogLevel.Warning,
        Message = "Publishing Soulseek share counts failed")]
    public static partial void CountPublicationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4309,
        EventName = "sharing.count-publication-started",
        Level = LogLevel.Debug,
        Message = "Publishing {DirectoryCount} shared directories and {FileCount} shared files to Soulseek")]
    public static partial void PublishingCounts(
        ILogger logger, long directoryCount, long fileCount);
}
