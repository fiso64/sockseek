using Sockseek.Core;
using Sockseek.Persistence.Read;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Write;
using Sockseek.Persistence.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Persistence;
using Sockseek.Persistence.PeerRestrictions;
using Sockseek.Persistence.Planning;

namespace Sockseek.Persistence.Runtime;

public sealed record PersistenceQueueSnapshot(
    int CriticalDepth,
    int OrdinaryDepth,
    int ProgressCount,
    int RetryMutationCount,
    int BufferedSearchResultCount);

public sealed record PersistenceRuntimeStartup(
    SqliteInitializationResult Initialization,
    StartupReconciliationResult Reconciliation,
    long MaximumRetainedDisplayId);

public sealed record PersistenceRuntimeStop(
    bool Drained,
    Guid? RuntimeId,
    PersistenceQueueSnapshot Remaining);

/// <summary>
/// Owns the complete SQLite runtime boundary. Callers can enqueue immutable
/// mutations, use history readers, and invoke bounded maintenance operations;
/// EF contexts, connections, ownership locks, and writer lifetime stay here.
/// </summary>
public sealed class PersistenceRuntimeHost
{
    private readonly SockseekSqliteOptions sqliteOptions;
    private readonly PersistenceWriterOptions writerOptions;
    private readonly PersistenceRetentionOptions retentionOptions;
    private readonly string version;
    private readonly SemaphoreSlim maintenanceGate = new(1, 1);
    private SqliteDatabaseOwner? owner;
    private PersistenceRuntimeSession? runtimeSession;
    private PersistenceInbox? inbox;
    private PersistenceWriter? writer;
    private Task? writerTask;
    private CancellationTokenSource? writerStop;
    private SqliteMaintenanceService? maintenance;
    private RetentionService? retention;
    private readonly object failureLogGate = new();
    private DateTimeOffset? lastFailureLogAtUtc;
    private PersistenceHealthState lastLoggedState = PersistenceHealthState.Healthy;
    private int suppressedFailureLogs;
    private readonly ILogger<PersistenceRuntimeHost> logger;
    private readonly IPersistenceMutationObserver? mutationObserver;
    private readonly TimeProvider clock;

    public PersistenceRuntimeHost(
        SockseekSqliteOptions sqliteOptions,
        PersistenceWriterOptions writerOptions,
        PersistenceRetentionOptions retentionOptions,
        string version,
        ILogger<PersistenceRuntimeHost>? logger = null,
        IPersistenceMutationObserver? mutationObserver = null,
        TimeProvider? timeProvider = null)
    {
        this.sqliteOptions = sqliteOptions;
        this.writerOptions = writerOptions;
        this.retentionOptions = retentionOptions;
        this.version = version;
        this.logger = logger ?? NullLogger<PersistenceRuntimeHost>.Instance;
        this.mutationObserver = mutationObserver;
        clock = timeProvider ?? TimeProvider.System;
        writerOptions.Validate();
        retentionOptions.Validate();
        Health.FailureRecorded += LogFailure;
        Health.CommitCompleted += LogRecovery;
    }

    public bool IsStarted { get; private set; }
    public SqliteInitializationResult? Initialization { get; private set; }
    public StartupReconciliationResult? Reconciliation { get; private set; }
    public PersistenceRuntimeInfo? Runtime => runtimeSession?.Current;
    public PersistenceHealth Health { get; } = new();
    public PersistenceHealthSnapshot? HealthSnapshot => inbox == null ? null : Health.Snapshot(inbox);
    public IPersistenceMutationSink? MutationSink => inbox;
    public IJobHistoryReader? JobHistory { get; private set; }
    public ISubmissionStore? Submissions { get; private set; }
    public ISearchHistoryReader? SearchHistory { get; private set; }
    public ITransferHistoryReader? TransferHistory { get; private set; }
    public ITransferAnalyticsReader? TransferAnalytics { get; private set; }
    public ChatPersistenceStore? Chat { get; private set; }
    public PeerRestrictionOverrideStore? PeerRestrictions { get; private set; }
    public InputArtifactStore? InputArtifacts { get; private set; }
    public long? DatabaseSizeBytes => FileSize(sqliteOptions.DatabasePath);
    public long? WalSizeBytes => FileSize(sqliteOptions.DatabasePath + "-wal");
    public PersistenceQueueSnapshot Queue => SnapshotQueue();

    public async Task<PersistenceRuntimeStartup> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsStarted)
            throw new InvalidOperationException("The persistence runtime host is already started.");

        owner = SqliteDatabaseOwner.Acquire(sqliteOptions);
        try
        {
            var contextFactory = new SockseekDbContextFactory(SockseekDbContextOptions.Create(sqliteOptions));
            var readers = new SockseekDbContextFactory(SockseekDbContextOptions.CreateReadOnly(sqliteOptions));
            JobHistory = new JobHistoryReader(readers);
            SearchHistory = new SearchHistoryReader(readers);
            // The reader and archive commands share the existing persistence
            // writer/inbox lifecycle; initialize the command-capable reader
            // after the inbox is created below.

            var initializer = new SqliteInitializer(contextFactory, sqliteOptions, owner);
            Initialization = await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

            maintenance = new SqliteMaintenanceService(readers, sqliteOptions);
            var integrity = await maintenance.CheckIntegrityAsync(cancellationToken).ConfigureAwait(false);
            if (!integrity.IsHealthy)
                throw new PersistenceDatabaseCorruptionException($"SQLite integrity check failed: {integrity.Result}");

            runtimeSession = new PersistenceRuntimeSession(contextFactory);
            var reconciliation = await runtimeSession.StartAsync(version, cancellationToken).ConfigureAwait(false);
            Reconciliation = reconciliation;
            long maximumDisplayId = await new PersistenceMetadataReader(contextFactory)
                .GetMaximumDisplayIdAsync(cancellationToken)
                .ConfigureAwait(false);

            inbox = new PersistenceInbox(writerOptions, Health, mutationObserver);
            retention = new RetentionService(inbox, retentionOptions);
            TransferHistory = new TransferHistoryReader(readers, inbox);
            TransferAnalytics = new TransferAnalyticsReader(readers);
            Submissions = new SubmissionStore(readers, inbox);
            writer = new PersistenceWriter(
                contextFactory,
                inbox,
                Health,
                writerOptions,
                mutationObserver: mutationObserver,
                logger: logger);
            writerStop = new CancellationTokenSource();
            writerTask = writer.RunAsync(writerStop.Token);
            Chat = new ChatPersistenceStore(readers, inbox);
            await Chat.ReconcilePendingMessagesAsync(cancellationToken).ConfigureAwait(false);
            PeerRestrictions = new PeerRestrictionOverrideStore(readers, Health, inbox);
            var inputArtifacts = new InputArtifactStore(
                Path.Combine(
                    Path.GetDirectoryName(sqliteOptions.GetFullDatabasePath())!,
                    "planning",
                    "input-artifacts"),
                readers,
                Health,
                inbox);
            try
            {
                await inputArtifacts.InitializeAsync(cancellationToken).ConfigureAwait(false);
                InputArtifacts = inputArtifacts;
            }
            catch (Exception exception)
            {
                await inputArtifacts.DisposeAsync().ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                PersistenceLogMessages.InputArtifactStorageUnavailable(logger, exception);
            }
            IsStarted = true;

            return new PersistenceRuntimeStartup(Initialization, reconciliation, maximumDisplayId);
        }
        catch (Exception ex)
        {
            inbox?.Complete();
            writerStop?.Cancel();
            if (writerTask is not null)
            {
                try { await writerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }
            writerStop?.Dispose();
            writerStop = null;
            Chat = null;
            PeerRestrictions = null;
            if (InputArtifacts is not null)
                await InputArtifacts.DisposeAsync().ConfigureAwait(false);
            InputArtifacts = null;
            ReleaseOwnership();
            throw PersistenceDatabaseErrors.Classify(ex, sqliteOptions.GetFullDatabasePath());
        }
    }

    public async Task<DatabaseIntegrityResult> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteMaintenanceAsync(
            ct => maintenance!.CheckIntegrityAsync(ct),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsHealthy)
            Health.RecordOperationalFailure(DateTimeOffset.UtcNow,
                new InvalidDataException($"SQLite integrity check failed: {result.Result}"));
        return result;
    }

    public Task<DatabaseBackupResult> BackupAsync(string backupPath, CancellationToken cancellationToken = default)
        => ExecuteMaintenanceAsync(
            ct => maintenance!.BackupAsync(backupPath, ct),
            cancellationToken);

    public Task<WalCheckpointResult> CheckpointAsync(CancellationToken cancellationToken = default)
        => ExecuteMaintenanceAsync(
            ct => inbox!.ExecuteAsync(SqliteMaintenanceService.CheckpointAsync, ct,
                background: true, requiresTransaction: false),
            cancellationToken);

    public Task<RetentionResult> RunRetentionAsync(CancellationToken cancellationToken = default)
        => ExecuteMaintenanceAsync(RunRetentionBatchAsync, cancellationToken);

    private async Task<RetentionResult> RunRetentionBatchAsync(CancellationToken cancellationToken)
    {
        RetentionResult result = await retention!.RunBatchAsync(cancellationToken)
            .ConfigureAwait(false);
        if (InputArtifacts is not null)
        {
            try
            {
                await InputArtifacts.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Blob cleanup does not invalidate committed history pruning or
                // database health. Failed files remain queued for the next pass.
                PersistenceLogMessages.InputArtifactCleanupFailed(logger, exception);
            }
        }
        return result;
    }

    private async Task<TResult> ExecuteMaintenanceAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        await maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Health.RecordOperationalFailure(DateTimeOffset.UtcNow, ex);
            throw;
        }
        finally
        {
            maintenanceGate.Release();
        }
    }

    public async Task<PersistenceRuntimeStop> StopAsync(
        TimeSpan drainTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!IsStarted)
            return new PersistenceRuntimeStop(true, null, SnapshotQueue());
        if (drainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));

        Guid? runtimeId = Runtime?.RuntimeId;
        inbox!.Complete();
        bool drained = false;
        try
        {
            if (writerTask != null)
                await writerTask.WaitAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
            drained = true;
            if (runtimeSession?.Current != null)
                await runtimeSession.StopAsync(
                    HealthSnapshot?.State == PersistenceHealthState.Unhealthy
                        ? "Unhealthy"
                        : "Clean",
                    cancellationToken).ConfigureAwait(false);
            return new PersistenceRuntimeStop(true, runtimeId, SnapshotQueue());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writerStop?.Cancel();
            if (writerTask != null)
            {
                try { await writerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            writerStop?.Cancel();
            if (writerTask != null)
            {
                try { await writerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            var remaining = SnapshotQueue();
            Health.RecordOperationalFailure(DateTimeOffset.UtcNow, new TimeoutException(
                $"Persistence drain did not complete. critical={remaining.CriticalDepth}, ordinary={remaining.OrdinaryDepth}, " +
                $"progress={remaining.ProgressCount}, retry={remaining.RetryMutationCount}, searchResults={remaining.BufferedSearchResultCount}.", ex));
            return new PersistenceRuntimeStop(false, runtimeId, remaining);
        }
        finally
        {
            if (!drained)
                writerStop?.Cancel();
            writerStop?.Dispose();
            writerStop = null;
            if (InputArtifacts is not null)
                await InputArtifacts.DisposeAsync().ConfigureAwait(false);
            InputArtifacts = null;
            ReleaseOwnership();
            IsStarted = false;
        }
    }

    private void EnsureStarted()
    {
        if (!IsStarted || maintenance == null || retention == null)
            throw new InvalidOperationException("Persistence maintenance is unavailable because the runtime host is not started.");
    }

    private void LogFailure()
    {
        var snapshot = HealthSnapshot;
        if (snapshot == null)
            return;
        lock (failureLogGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool stateChanged = snapshot.State != lastLoggedState;
            bool intervalElapsed = lastFailureLogAtUtc == null
                || now - lastFailureLogAtUtc >= TimeSpan.FromSeconds(30);
            if (!stateChanged && !intervalElapsed)
            {
                suppressedFailureLogs++;
                return;
            }

            if (snapshot.State == PersistenceHealthState.Unhealthy)
                PersistenceLogMessages.WriterUnhealthy(
                    logger,
                    snapshot.State,
                    FailureKind(snapshot.LastFailure),
                    suppressedFailureLogs);
            else
                PersistenceLogMessages.WriterDegraded(
                    logger,
                    snapshot.State,
                    FailureKind(snapshot.LastFailure),
                    suppressedFailureLogs);
            suppressedFailureLogs = 0;
            lastFailureLogAtUtc = now;
            lastLoggedState = snapshot.State;
        }
    }

    private void LogRecovery()
    {
        var snapshot = HealthSnapshot;
        if (snapshot?.State != PersistenceHealthState.Healthy)
            return;
        lock (failureLogGate)
        {
            if (lastLoggedState == PersistenceHealthState.Healthy)
                return;
            PersistenceLogMessages.WriterRecovered(logger);
            lastLoggedState = PersistenceHealthState.Healthy;
            suppressedFailureLogs = 0;
        }
    }

    private static string FailureKind(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
            return "UnknownFailure";
        int separator = failure.IndexOf(':');
        return separator > 0 ? failure[..separator] : "UnknownFailure";
    }

    private PersistenceQueueSnapshot SnapshotQueue()
        => inbox == null
            ? new PersistenceQueueSnapshot(0, 0, 0, 0, 0)
            : new PersistenceQueueSnapshot(
                inbox.CriticalDepth,
                inbox.OrdinaryDepth,
                inbox.ProgressCount,
                inbox.RetryMutationCount,
                inbox.BufferedSearchResultCount);

    private void ReleaseOwnership()
    {
        owner?.Dispose();
        owner = null;
        SqliteConnectionPool.Clear(SockseekDbContextOptions.CreateConnectionString(sqliteOptions));
        SqliteConnectionPool.Clear(SockseekDbContextOptions.CreateConnectionString(sqliteOptions, readOnly: true));
    }

    private static long? FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
