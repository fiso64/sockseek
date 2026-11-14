using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.State;
using Sockseek.Core.PeerBrowsing;
using Microsoft.Extensions.Logging;

namespace Sockseek.Core.Transfers.Downloads.Runtime;

internal sealed class DownloadRunScope : IDisposable, IAsyncDisposable
{
    private readonly EngineSettings settings;
    private readonly SoulseekClientManager clientManager;
    private readonly ActiveDownloadTracker activeDownloads;
    private readonly PeerFailureTracker peerFailures;
    private readonly DownloadEvents events;
    private readonly SearchEvents searchEvents;
    private readonly StaleDownloadCoordinator staleDownloads;
    private readonly TimeProvider timeProvider;
    private readonly IPeerDirectorySource? directorySource;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<DownloadRunScope> logger;
    private readonly CancellationTokenSource appCts = new();
    private readonly SemaphoreSlim jobSemaphore;
    private readonly SemaphoreSlim extractorSemaphore;
    private readonly DownloadFileOperations fileOperations;
    private Searcher? searcher;
    private ExactPeerFileTransferRunner? exactFileTransfers;
    private Task? staleDownloadTask;
    private bool servicesInitialized;
    private bool disposed;

    public DownloadRunScope(
        EngineSettings settings,
        SoulseekClientManager clientManager,
        ActiveDownloadTracker activeDownloads,
        PeerFailureTracker peerFailures,
        DownloadEvents events,
        SearchEvents searchEvents,
        StaleDownloadCoordinator staleDownloads,
        TimeProvider? timeProvider = null,
        IPeerDirectorySource? directorySource = null,
        ILoggerFactory? loggerFactory = null)
    {
        this.settings = settings;
        this.clientManager = clientManager;
        this.activeDownloads = activeDownloads;
        this.peerFailures = peerFailures;
        this.events = events;
        this.searchEvents = searchEvents;
        this.staleDownloads = staleDownloads;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.directorySource = directorySource;
        this.loggerFactory = loggerFactory
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<DownloadRunScope>();
        jobSemaphore = new SemaphoreSlim(settings.ConcurrentJobs);
        extractorSemaphore = new SemaphoreSlim(settings.ConcurrentExtractors);
        fileOperations = new DownloadFileOperations(settings.ConcurrentJobs,
            this.loggerFactory.CreateLogger<DownloadFileOperations>());
    }

    public DownloadFileOperations Files => fileOperations;

    public CancellationToken Token => appCts.Token;
    public bool IsCancellationRequested => appCts.IsCancellationRequested;
    public int ConcurrentJobLimit => settings.ConcurrentJobs;

    // This bounds orchestration callbacks, not leaf or network concurrency. Using
    // the larger value lets extractor-only work reach ConcurrentExtractors when it
    // exceeds ConcurrentJobs; resulting leaf work still acquires jobSemaphore, and
    // Soulseek searches separately obey Searcher's ConcurrentSearches semaphore.
    // TODO [LOW PRIORITY][PERFORMANCE]: The window is per fan-out. Concurrently
    // extracted nested JobLists can therefore each admit a window of execution
    // callbacks waiting for leaf slots, without exceeding the leaf/search
    // limits. Consider phase-specific scheduling only if load tests show this is
    // material in practice. Known-member registration is independent of this window.
    public int ConcurrentSchedulingLimit => Math.Max(settings.ConcurrentJobs, settings.ConcurrentExtractors);

    public Searcher Searcher => searcher
        ?? throw new InvalidOperationException("Engine search services have not been initialized.");

    public ExactPeerFileTransferRunner ExactFileTransfers => exactFileTransfers
        ?? throw new InvalidOperationException("Engine download services have not been initialized.");

    public async Task EnsureServicesInitializedAsync(CancellationToken ct, bool automaticStaleChecksEnabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (servicesInitialized)
            return;

        try
        {
            await clientManager.EnsureConnectedAndLoggedInAsync(settings, ct);
        }
        catch (SoulseekConnectionUnavailableException) when (clientManager.HasFatalError)
        {
            throw;
        }
        catch (Exception)
        {
            DownloadLogMessages.SessionInitializationDegraded(logger);
        }

        await clientManager.WaitUntilReadyAsync(ct);
        var client = clientManager.Client ?? throw new InvalidOperationException("Soulseek client is not available after login.");
        IPeerDirectorySource effectiveDirectorySource = directorySource
            ?? client as IPeerDirectorySource
            ?? new OneShotPeerDirectorySource(new SoulseekPeerBrowseTransport(clientManager));
        searcher = new Searcher(
            client,
            peerFailures,
            events,
            settings.SearchesPerTime,
            settings.SearchRenewTime,
            settings.ConcurrentSearches,
            searchEvents,
            timeProvider,
            effectiveDirectorySource,
            loggerFactory);
        exactFileTransfers = new ExactPeerFileTransferRunner(
            client,
            clientManager,
            activeDownloads,
            events,
            staleDownloads,
            fileOperations,
            loggerFactory.CreateLogger<ExactPeerFileTransferRunner>());

        if (automaticStaleChecksEnabled)
            staleDownloadTask ??= Task.Run(() => staleDownloads.RunAsync(appCts.Token), appCts.Token);

        DownloadLogMessages.ServicesInitialized(logger);
        servicesInitialized = true;
    }

    public async Task WithJobSlot(CancellationToken ct, Func<Task> action)
    {
        await jobSemaphore.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    public async Task<T> WithJobSlot<T>(CancellationToken ct, Func<Task<T>> action)
    {
        await jobSemaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            jobSemaphore.Release();
        }
    }

    public async Task<T> WithExtractorSlot<T>(CancellationToken ct, Func<Task<T>> action)
    {
        await extractorSemaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            extractorSemaphore.Release();
        }
    }

    public void Cancel()
    {
        if (!appCts.IsCancellationRequested)
            appCts.Cancel();
    }

    public async Task CancelAsync()
    {
        if (!appCts.IsCancellationRequested)
            await appCts.CancelAsync();

        await WaitForStaleDownloadTaskAsync();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Cancel();
        WaitForStaleDownloadTask();
        DisposeManaged();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        await CancelAsync();
        DisposeManaged();
        GC.SuppressFinalize(this);
    }

    private void DisposeManaged()
    {
        searcher?.Dispose();
        fileOperations.Dispose();
        jobSemaphore.Dispose();
        extractorSemaphore.Dispose();
        appCts.Dispose();
        disposed = true;
    }

    private async Task WaitForStaleDownloadTaskAsync()
    {
        if (staleDownloadTask == null)
            return;

        try { await staleDownloadTask; }
        catch (OperationCanceledException) when (appCts.IsCancellationRequested) { }
    }

    private void WaitForStaleDownloadTask()
    {
        if (staleDownloadTask == null)
            return;

        try { staleDownloadTask.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (appCts.IsCancellationRequested) { }
    }
}
