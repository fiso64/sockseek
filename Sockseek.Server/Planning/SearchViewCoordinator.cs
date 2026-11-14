using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Planning;

// Read inputs survive execution retirement only until the existing persistence
// handoff finishes. No execution job or client selection is retained here.
internal sealed record SearchViewSource(
    SearchSession Session,
    SearchDefinition? FallbackDefinition,
    SearchSettings Settings,
    IReadOnlyDictionary<string, int> PeerFailureCounts,
    string? SubmissionSpecificationJson = null)
{
    public SearchDefinition Definition
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SubmissionSpecificationJson))
                return FallbackDefinition
                    ?? throw new InvalidOperationException("The live search has no accepted search definition.");
            SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationJson);
            return specification.Search ?? specification.Command.SearchDefinition ?? FallbackDefinition
                ?? throw new InvalidOperationException("The accepted search submission has no search definition.");
        }
    }

    public static SearchViewSource? Capture(Job job, IReadOnlyDictionary<string, int> peerFailureCountSnapshot)
    {
        if (job.Config == null || job.SearchObservationSession is not { } session)
            return null;
        SearchDefinition? fallback = job.ExecutedSearchDefinition;
        if (string.IsNullOrWhiteSpace(job.SubmissionSpecificationJson))
            fallback ??= (job as SearchJob)?.Definition;
        // Prepared settings are no longer mutated by execution. Each View
        // clones them before refinement; retirement need not copy/parse plans.
        return new(session, fallback, job.Config.ResolveSearchSettings(), peerFailureCountSnapshot,
            job.SubmissionSpecificationJson);
    }
}

public sealed class SearchViewCoordinator : IHostedService, IAsyncDisposable
{
    private readonly EngineSupervisor supervisor;
    private readonly PersistenceCoordinator persistence;
    private readonly SubmissionCommitCoordinator commits;
    private readonly SearchViewCursorCodec cursors;
    private readonly ILogger<SearchViewCoordinator> logger;
    private readonly TimeProvider clock;

    public SearchViewCoordinator(EngineSupervisor supervisor, PersistenceCoordinator persistence,
        SubmissionCommitCoordinator commits, SearchViewCursorCodec cursors,
        ILogger<SearchViewCoordinator> logger, TimeProvider? timeProvider = null)
    {
        this.supervisor = supervisor;
        this.persistence = persistence;
        this.commits = commits;
        this.cursors = cursors;
        this.logger = logger;
        clock = timeProvider ?? TimeProvider.System;
        Cache = new(clock);
        Selections = new(supervisor.Submissions, Cache, commits, logger);
    }

    private static readonly TimeSpan IdleKernelRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromSeconds(1);
    private static readonly ActivitySource ProjectionActivities = new("Sockseek.Server.SearchViews");
    private const int InitialProjectionBatchSize = 200;
    // Keep first results prompt and bound each turn of projection work. Sparse
    // streams publish without waiting for a full batch; publication does no SQL.
    private int ProjectionBatchSize => persistence.SearchResultBatchSize;
    // Directory pages fan out; embed up to 100 files per folder, then continue in 500-file pages.
    private const int InitialDirectoryFileLimit = ServerProtocol.DefaultNestedPageSize;
    private readonly ConcurrentDictionary<Guid, RuntimeView> runtimes = [];
    private readonly ConcurrentDictionary<Guid, byte> pending = [];
    private readonly ConcurrentDictionary<Guid, byte> requested = [];
    private readonly Dictionary<Guid, int> observers = [];
    private readonly ConcurrentDictionary<(Guid ViewId, Guid ObservationId), byte> replayFailureLogged = [];
    private readonly Channel<Guid> work = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource lifetime = new();
    internal SearchViewCache Cache { get; }
    private Task? worker;
    private int disposed;

    // Selection requests read snapshots; they never drive projection scheduling.
    public SearchViewSelections Selections { get; }

    internal event Action<SearchViewInfo>? ViewPublished;

    // The stream lifetime is demand, not a persisted subscription or a polling
    // timeout. Publications may catch up in several batches between displays.
    // Cancel/disconnect releases demand; at most the in-flight batch finishes.
    public async IAsyncEnumerable<SearchViewSummaryDto> ObserveAsync(
        Guid viewId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var observationLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = observationLifetime.Token;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResultsOrCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = Channel.CreateBounded<SearchViewInfo>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        long lastPublishedRevision = -1;
        void Published(SearchViewInfo view)
        {
            if (view.Id != viewId) return;
            // Commit continuations can notify out of order. A late older
            // notification must not displace the newest bounded-channel item.
            lock (changed)
            {
                if (view.Revision < lastPublishedRevision) return;
                lastPublishedRevision = view.Revision;
                changed.Writer.TryWrite(view);
                if (view.IsComplete) completed.TrySetResult();
                if (view.IsComplete || view.Counters.TopLevelItemCount > 0)
                    firstResultsOrCompletion.TrySetResult();
            }
        }
        SearchViewInfo? sourceView = null;
        SearchSession? liveSession = null;
        void LiveChanged(CoreChange _) => Schedule(viewId, observation: true);
        void SourceChanged(Guid jobId)
        {
            if (jobId == Volatile.Read(ref sourceView)?.SourceJobId) Schedule(viewId, observation: true);
        }
        bool observing = false;
        ViewPublished += Published;
        persistence.SearchSourceChanged += SourceChanged;
        try
        {
            SearchViewInfo latest = ReadObserved(viewId, cancellationToken);
            if (NeedsAdvance(latest))
            {
                lock (observers) observers[viewId] = observers.GetValueOrDefault(viewId) + 1;
                observing = true;
                Volatile.Write(ref sourceView, latest);
                liveSession = ObservationSession(latest.SourceJobId, supervisor, persistence);
                if (liveSession != null)
                {
                    liveSession.ResultsAdded += LiveChanged;
                    liveSession.SearchCompleted += LiveChanged;
                }
                // Subscribe before the read, then schedule after registering
                // demand: a commit racing either step cannot be missed.
                Schedule(viewId, observation: true);
            }
            long lastSent = clock.GetTimestamp();
            bool hasSentResults = latest.Counters.TopLevelItemCount > 0;
            yield return ToDto(latest);
            while (NeedsAdvance(latest))
            {
                SearchViewInfo next = await changed.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                // First usable results are immediate. Subsequent notifications
                // coalesce to the existing UI refresh cadence, independently of
                // how many batches the worker can process during that interval.
                // Completion bypasses this cadence, including for CLI printing.
                TimeSpan remaining = PublicationInterval - clock.GetElapsedTime(lastSent);
                Task immediate = hasSentResults ? completed.Task : firstResultsOrCompletion.Task;
                if (latest.Revision > 0 && remaining > TimeSpan.Zero && !immediate.IsCompleted)
                    await Task.WhenAny(Task.Delay(remaining, clock, cancellationToken), immediate)
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                while (changed.Reader.TryRead(out SearchViewInfo? newer)) next = newer;
                if (ReplayDirectoryObservations(viewId, cancellationToken))
                    next = Cache.Get(viewId, cancellationToken)
                        ?? throw new KeyNotFoundException("The Search View was not found.");
                if (next.Revision < latest.Revision || next.Revision == latest.Revision && NeedsAdvance(next)) continue;
                latest = next;
                hasSentResults |= latest.Counters.TopLevelItemCount > 0;
                lastSent = clock.GetTimestamp();
                yield return ToDto(latest);
            }
        }
        finally
        {
            observationLifetime.Cancel();
            ViewPublished -= Published;
            persistence.SearchSourceChanged -= SourceChanged;
            if (liveSession != null)
            {
                liveSession.ResultsAdded -= LiveChanged;
                liveSession.SearchCompleted -= LiveChanged;
            }
            if (observing)
                lock (observers)
                {
                    if (observers[viewId] == 1)
                    {
                        observers.Remove(viewId);
                        runtimes.GetValueOrDefault(viewId)?.Source.ReleaseLiveInputs();
                    }
                    else observers[viewId]--;
                }
        }
    }

    private bool IsObserved(Guid viewId)
    {
        lock (observers) return observers.ContainsKey(viewId);
    }

    private SearchViewInfo ReadObserved(Guid viewId, CancellationToken cancellationToken)
    {
        SearchViewInfo? view = Cache.Get(viewId, cancellationToken);
        if (view != null && ReplayDirectoryObservations(viewId, cancellationToken))
            view = Cache.Get(viewId, cancellationToken);
        return view ?? throw new KeyNotFoundException("The Search View was not found.");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        worker = RunWorkerAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        work.Writer.TryComplete();
        lifetime.Cancel();
        if (worker != null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        foreach (RuntimeView runtime in runtimes.Values) runtime.PublicationTimer?.Dispose();
    }

    public async Task<SearchViewSummaryDto?> CreateAsync(
        Guid sourceJobId,
        CreateSearchViewRequestDto request,
        CancellationToken cancellationToken)
    {
        SearchViewCache repository = Cache;
        ValidateRefinementSettings(request.RefinementSettings);
        // Expiry is enforced by reads; physical pruning belongs to startup and
        // scheduled retention, never to an interactive View creation/refinement.
        RuntimeSource? source;
        SearchDefinition definition;
        SearchViewProjectionDefinition projection;
        SearchSettings settings;
        IReadOnlyDictionary<string, int> workflowPeerFailureCounts;

        SearchViewSource? observation = supervisor.GetSearchViewSource(sourceJobId);
        if (observation != null)
        {
            definition = observation.Definition;
            projection = ResolveProjection(definition, request);
            settings = SettingsCloner.Clone(observation.Settings);
            source = new ObservedSource(sourceJobId, supervisor, persistence);
            // Freeze workflow failure feedback for this View; later transfers must
            // not silently change the ranking of an already reviewed snapshot.
            workflowPeerFailureCounts = observation.PeerFailureCounts;
        }
        else
        {
            source = await CreateHistoricalSourceAsync(
                sourceJobId,
                request,
                cancellationToken).ConfigureAwait(false);
            if (source is not HistoricalSource historical)
                return null;
            definition = historical.Definition;
            projection = historical.Projection;
            settings = definition.ProjectionSettings.ToSettings();
            workflowPeerFailureCounts = new Dictionary<string, int>(
                StringComparer.Ordinal);
        }

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, request.RefinementSettings);

        SearchViewInfo stored = repository.Create(sourceJobId, new SearchViewDefinition(definition, projection, request.RefinementSettings), cancellationToken: cancellationToken);
        var runtime = new RuntimeView(
            stored, source, projection, settings, workflowPeerFailureCounts, clock.GetUtcNow(),
            new SearchViewKernel(projection, settings, workflowPeerFailureCounts,
                retainProjectedRows: false, trackPeerIdentities: false));
        if (!runtimes.TryAdd(stored.Id, runtime))
            throw new InvalidOperationException("The search view already exists in runtime state.");
        Schedule(stored.Id);
        ServerLogMessages.SearchViewCreated(logger, stored.Id, sourceJobId);
        return ToDto(stored);
    }

    public SearchViewSummaryDto? Get(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        SearchViewInfo? view = Cache.Get(viewId, cancellationToken);
        if (view != null && ReplayDirectoryObservations(viewId, cancellationToken))
            view = Cache.Get(viewId, cancellationToken);
        if (view == null) return null;
        RequestAdvance(view);
        return ToDto(view);
    }

    public SearchViewDefinitionDto? GetDefinition(Guid viewId, CancellationToken cancellationToken)
    {
        SearchViewInfo? view = Cache.Get(viewId, cancellationToken);
        if (view == null)
            return null;
        (SearchDefinition source, SearchViewProjectionDefinition projection,
            SearchSettingsPatchDto? refinement) = view.Definition;
        SearchSettings settings = source.ProjectionSettings.ToSettings();
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, refinement);
        return new SearchViewDefinitionDto(
            viewId,
            ToDto(projection) with { RefinementSettings = refinement },
            EffectiveSettingsMapper.ToSearchSettings(settings));
    }

    public SearchViewFilePageDto? GetFiles(Guid viewId, long revision, string? cursor, int limit, ServerSearchViewSortOrder? sortOrder = null, CancellationToken cancellationToken = default)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewFilePosition? after = cursor == null
            ? null
            : cursors.DecodeFiles(cursor, viewId, revision, sortOrder);
        SearchViewFilePage? page = snapshot.GetFiles(after, limit, sortOrder?.ToCore(), cancellationToken: cancellationToken);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeFiles(viewId, revision, page.NextPosition, sortOrder));
    }

    public SearchViewDirectoryPageDto? GetDirectories(Guid viewId, long revision, string? cursor, int limit, ServerSearchViewSortOrder? sortOrder = null, CancellationToken cancellationToken = default)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewDirectoryPosition? after = cursor == null
            ? null
            : cursors.DecodeDirectories(cursor, viewId, revision, sortOrder);
        SearchViewDirectoryPage? page = snapshot.GetDirectories(after, limit, InitialDirectoryFileLimit, sortOrder?.ToCore(), cancellationToken: cancellationToken);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(item => ToDtoWithInitialFiles(viewId, revision, item, page.DirectoryFilePreviews)).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeDirectories(viewId, revision, page.NextPosition, sortOrder));
    }

    public SearchViewDirectoryFilePageDto? GetDirectoryFiles(Guid viewId, string directoryRef, long revision, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewDirectoryFilePosition? after = cursor == null
            ? null
            : cursors.DecodeDirectoryFiles(
                cursor,
                viewId,
                directoryRef,
                revision);
        SearchViewDirectoryFilePage? page = snapshot.GetDirectoryFiles(directoryRef, after, limit, cancellationToken);
        if (page == null)
            return null;
        var directory = new PeerDirectoryRefDto(
            page.DirectoryRef,
            page.Username,
            page.FolderPath);
        return new(
            ToDto(page.Revision),
            directory,
            page.Items.Select(item => new SearchViewDirectoryFileDto(
                item.Ref,
                item.RelativePath,
                ToDto(item.File))).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeDirectoryFiles(
                    viewId,
                    directoryRef,
                    revision,
                    page.NextPosition),
            ToDto(page.DirectorySummary));
    }

    public SearchViewTrackGroupPageDto? GetTrackGroups(Guid viewId, long revision, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewTrackGroupPosition? after = cursor == null
            ? null
            : cursors.DecodeTrackGroups(cursor, viewId, revision);
        SearchViewTrackGroupPage? page = snapshot.GetTrackGroups(after, limit, cancellationToken);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeTrackGroups(
                    viewId,
                    revision,
                    page.NextPosition));
    }

    public SearchViewTrackGroupOptionPageDto? GetTrackGroupOptions(Guid viewId, string groupRef, long revision, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewFilePosition? after = cursor == null
            ? null
            : cursors.DecodeTrackGroupOptions(
                cursor,
                viewId,
                groupRef,
                revision);
        SearchViewTrackGroupOptionPage? page = snapshot.GetTrackGroupOptions(groupRef, after, limit, cancellationToken);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.GroupRef,
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeTrackGroupOptions(
                    viewId,
                    groupRef,
                    revision,
                    page.NextPosition));
    }

    public SearchViewAlbumGroupPageDto? GetAlbumGroups(Guid viewId, long revision, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewAlbumGroupPosition? after = cursor == null
            ? null
            : cursors.DecodeAlbumGroups(cursor, viewId, revision);
        SearchViewAlbumGroupPage? page = snapshot.GetAlbumGroups(after, limit, InitialDirectoryFileLimit, cancellationToken);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(item => ToDto(item) with
            {
                Representative = ToDtoWithInitialFiles(
                    viewId, revision, item.Representative, page.DirectoryFilePreviews),
            }).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAlbumGroups(
                    viewId,
                    revision,
                    page.NextPosition));
    }

    public SearchViewAlbumGroupOptionPageDto? GetAlbumGroupOptions(Guid viewId, string groupRef, long revision, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var snapshot = Cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null) return null;
        SearchViewDirectoryPosition? after = cursor == null
            ? null
            : cursors.DecodeAlbumGroupOptions(
                cursor,
                viewId,
                groupRef,
                revision);
        SearchViewAlbumGroupOptionPage? page = snapshot.GetAlbumGroupOptions(groupRef, after, limit, InitialDirectoryFileLimit, cancellationToken);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.GroupRef,
            page.Items.Select(item => ToDtoWithInitialFiles(viewId, revision, item, page.DirectoryFilePreviews)).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAlbumGroupOptions(
                    viewId,
                    groupRef,
                    revision,
                    page.NextPosition));
    }

    public ResolveSearchViewChosenOptionsResponseDto? ResolveChosenOptions(Guid viewId, ResolveSearchViewChosenOptionsRequestDto request, CancellationToken cancellationToken)
    {
        if (request.Revision < 0 || request.ChosenOptions == null
            || request.ChosenOptions.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ArgumentException("A revision and non-empty group/option refs are required.", nameof(request));
        SearchViewChosenOptions? resolved = Cache.GetSnapshot(viewId, request.Revision, cancellationToken)?.ResolveChosenOptions(request.ChosenOptions, InitialDirectoryFileLimit, cancellationToken);
        if (resolved == null)
            return null;
        return new(ToDto(resolved.Revision), request.ChosenOptions.Select(pair =>
            new SearchViewChosenOptionDto(
                pair.Key,
                pair.Value,
                resolved.Tracks.TryGetValue(pair.Key, out SearchViewFile? track) ? ToDto(track) : null,
                resolved.Albums.TryGetValue(pair.Key, out SearchViewDirectory? album)
                    ? ToDtoWithInitialFiles(viewId, request.Revision, album, resolved.DirectoryFilePreviews) : null)).ToArray());
    }

    public async Task<SearchViewUpdateDto?> GetUpdatesAsync(
        Guid viewId,
        long afterRevision,
        bool waitForChange = false,
        CancellationToken cancellationToken = default)
    {
        SearchViewInfo? sourceView = null;
        SearchSession? liveSession = null;
        void LiveChanged(CoreChange _) => Schedule(viewId);
        void SourceChanged(Guid jobId)
        {
            if (jobId == Volatile.Read(ref sourceView)?.SourceJobId) Schedule(viewId);
        }
        if (afterRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(afterRevision));
        var publication = waitForChange
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        void Published(SearchViewInfo view)
        {
            if (view.Id == viewId && view.Revision > afterRevision) publication!.TrySetResult();
        }
        // Subscribe before reading so publication between the read and wait
        // cannot be missed. This request holds no database connection while idle.
        if (waitForChange)
        {
            ViewPublished += Published;
            persistence.SearchSourceChanged += SourceChanged;
        }
        try
        {
            SearchViewInfo? latest = Cache.Get(viewId, cancellationToken);
            if (latest == null) return null;
            if (afterRevision > latest.Revision)
                throw new ArgumentOutOfRangeException(nameof(afterRevision),
                    "The requested revision is newer than the Search View.");
            if (waitForChange && !latest.IsComplete)
            {
                Volatile.Write(ref sourceView, latest);
                liveSession = ObservationSession(latest.SourceJobId, supervisor, persistence);
                if (liveSession != null)
                {
                    liveSession.ResultsAdded += LiveChanged;
                    liveSession.SearchCompleted += LiveChanged;
                }
            }
            RequestAdvance(latest);
            if (waitForChange && latest.Revision == afterRevision && NeedsAdvance(latest))
            {
                try
                {
                    await publication!.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException) { }
                if (publication!.Task.IsCompletedSuccessfully)
                {
                    SearchViewInfo? updated = Cache.Get(viewId, cancellationToken);
                    if (updated == null) return null;
                    latest = updated;
                }
            }
            // A publication can create the first matching directory while this
            // read is waiting. Replay against that observed prefix before
            // returning it, especially when Complete will stop client polling.
            if (ReplayDirectoryObservations(viewId, cancellationToken))
            {
                latest = Cache.Get(viewId, cancellationToken);
                if (latest == null) return null;
            }
            return new(ToDto(latest), latest.Revision > afterRevision);
        }
        finally
        {
            if (waitForChange)
            {
                ViewPublished -= Published;
                persistence.SearchSourceChanged -= SourceChanged;
                if (liveSession != null)
                {
                    liveSession.ResultsAdded -= LiveChanged;
                    liveSession.SearchCompleted -= LiveChanged;
                }
            }
        }
    }

    private bool ReplayDirectoryObservations(Guid viewId, CancellationToken cancellationToken)
    {
        SearchViewCache repository = Cache;
        using IEnumerator<SearchDirectoryObservationTarget> targets = repository.ReadUnappliedDirectoryObservations(viewId, cancellationToken)
            .GetEnumerator();
        if (!targets.MoveNext())
            return false;
        SearchViewInfo? view = repository.Get(viewId, cancellationToken);
        if (view == null)
            return false;
        Guid sourceJobId = view.SourceJobId;
        bool changed = false;
        (SearchDefinition definition, SearchViewProjectionDefinition projection,
            SearchSettingsPatchDto? refinementSettings) =
            view.Definition;
        do
        {
            SearchDirectoryObservationTarget target = targets.Current;
            try
            {
                SearchDirectoryObservation? observation = repository.GetDirectoryObservation(sourceJobId, target.Identity, cancellationToken);
                if (observation == null || observation.Id != target.ObservationId)
                    continue;
                view = repository.Get(viewId, cancellationToken);
                if (view == null)
                    return changed;
                bool newerSourceFile = repository.GetSnapshot(viewId, view.Revision, cancellationToken)?.HasNewerDirectorySourceFile(target.DirectoryRef, observation.SourceRevision, cancellationToken) == true;
                PeerDirectorySnapshot snapshot = newerSourceFile && observation.Snapshot.IsComplete
                    ? new PeerDirectorySnapshot(
                        observation.Snapshot.Identity,
                        observation.Snapshot.Files,
                        isComplete: false)
                    : observation.Snapshot;
                ProjectedFileCandidate[] projected = ProjectRetrievedDirectoryFiles(
                    snapshot,
                    observation.PeerObservation,
                    view.ConsumedSequence,
                    definition,
                    projection,
                    refinementSettings);
                SearchViewDirectoryPublishResult published = repository.PublishRetrievedDirectory(viewId, target.DirectoryRef, snapshot, projected, observation.PeerObservation, cancellationToken, existingObservationId: observation.Id);
                if (published.View.Revision > view.Revision)
                {
                    changed = true;
                    NotifyPublished(published.View);
                }
                replayFailureLogged.TryRemove((viewId, observation.Id), out _);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (replayFailureLogged.TryAdd((viewId, target.ObservationId), 0))
                    ServerLogMessages.SearchViewRetrievalReplayFailed(
                        logger, exception, viewId, target.ObservationId);
            }
        } while (targets.MoveNext());
        return changed;
    }

    internal static string FingerprintDirectoryRetrieval(Guid viewId, long revision, PeerDirectoryRefDto directory)
        => SubmissionFingerprint.Create("search-view-directory-retrieval", new { ViewId = viewId, Revision = revision, Directory = directory });

    public async Task<JobSummaryDto?> StartDirectoryRetrievalAsync(
        Guid viewId,
        RetrieveSearchViewDirectoryRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request.Revision));
        ArgumentNullException.ThrowIfNull(request.Directory);
        if (string.IsNullOrEmpty(request.Directory.Ref))
            throw new ArgumentException("The search-view directory ref is required.");
        if (request.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.");

        var exactIdentity = new PeerDirectoryIdentity(
            request.Directory.Username,
            request.Directory.FolderPath);
        exactIdentity.RequireAdvertisedFolder();
        string fingerprint = FingerprintDirectoryRetrieval(
            viewId,
            request.Revision,
            request.Directory);
        return await commits.ExecuteAsync(
            request.IdempotencyKey,
            fingerprint,
            async operationToken =>
            {
                // An accepted retry is resolved by its durable receipt before
                // consulting the temporary issuing View. Only new work needs
                // revision membership and the source projection definition.
                SearchViewCache repository = Cache;
                var reviewed = repository.GetSnapshot(viewId, request.Revision, operationToken)
                    ?? throw new KeyNotFoundException("The search-view revision was not found.");
                var sourceView = reviewed.View;
                SearchViewDirectoryFilePage? membership = reviewed.GetDirectoryFiles(
                    request.Directory.Ref, after: null, limit: 1, operationToken);
                if (membership == null)
                    throw new KeyNotFoundException("The directory is not in the issuing Search View revision.");
                if (!StringComparer.Ordinal.Equals(membership.Username, exactIdentity.Username)
                    || !StringComparer.Ordinal.Equals(membership.FolderPath, exactIdentity.FolderPath))
                {
                    throw new ArgumentException(
                        "The exact peer-directory identity does not match the search-view ref.");
                }
                if (membership.Items.Count == 0)
                {
                    throw new InvalidDataException(
                        "The search-view directory has no retained child observation.");
                }

                (SearchDefinition definition, SearchViewProjectionDefinition projection,
                    SearchSettingsPatchDto? refinementSettings) =
                    sourceView.Definition;
                SearchProjectionInput peerObservation = membership.Items[0].File.Input;
                JobSummaryDto? job = await supervisor.StartSearchViewDirectoryRetrievalAsync(
                    sourceView.SourceJobId,
                    exactIdentity,
                    (snapshot, publishToken) =>
                    {
                        ProjectedFileCandidate[] projected = ProjectRetrievedDirectoryFiles(
                            snapshot,
                            peerObservation,
                            sourceView.ConsumedSequence,
                            definition,
                            projection,
                            refinementSettings);
                        SearchViewDirectoryPublishResult published = repository.PublishRetrievedDirectory(viewId, request.Directory.Ref, snapshot, projected, peerObservation, publishToken);
                        NotifyPublished(published.View);
                        return published.NewFileCount;
                    },
                    request.IdempotencyKey,
                    fingerprint,
                    operationToken).ConfigureAwait(false);
                return job ?? throw new KeyNotFoundException("The search source job was not found.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        // Cleanup must also run when no reader requests more results. Queue it on
        // the projection owner so releasing kernels cannot race their use.
        using ITimer cleanup = clock.CreateTimer(_ => Schedule(Guid.Empty, observation: true),
            null, CleanupInterval, CleanupInterval);
        try
        {
            await foreach (Guid viewId in work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Explicit reads request one batch. An observation requests catch-up
                // until idle; queued observation work is discarded after cancellation.
                pending.TryRemove(viewId, out _);
                bool explicitlyRequested = requested.TryRemove(viewId, out _);
                if (viewId == Guid.Empty)
                {
                    ReleaseIdleKernels();
                    continue;
                }
                if (!explicitlyRequested && !IsObserved(viewId)) continue;
                RuntimeView? runtime = null;
                try
                {
                    runtime = runtimes.GetValueOrDefault(viewId)
                        ?? await RestoreRequestedAsync(viewId, cancellationToken).ConfigureAwait(false);
                    if (runtime != null) await ProcessAsync(runtime, explicitlyRequested, cancellationToken).ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    if (runtime != null) Fail(runtime, exception, cancellationToken);
                    else
                    {
                        ServerLogMessages.SearchViewRestoreFailed(logger, exception, viewId);
                        var stored = Cache.Peek(viewId, cancellationToken);
                        if (stored != null) CompleteUnrestorable(Cache, stored, cancellationToken);
                    }
                }
                finally
                {
                    if (runtime != null && !IsObserved(viewId)) runtime.Source.ReleaseLiveInputs();
                }
            }
        }
        finally
        {
            while (work.Reader.TryRead(out _)) { }
        }
    }

    private void ReleaseIdleKernels()
    {
        Cache.PruneExpired();
        DateTimeOffset now = clock.GetUtcNow();
        // Only the projection worker uses these working kernels. Keep published
        // immutable snapshots in the temporary store and lightweight definitions here, not idle result graphs.
        foreach (RuntimeView runtime in runtimes.Values)
        {
            if (!Cache.IsActive(runtime.ViewId))
            {
                runtimes.TryRemove(runtime.ViewId, out _);
                runtime.PublicationTimer?.Dispose();
            }
            else if (now - runtime.LastUsedAt >= IdleKernelRetention)
            {
                runtime.Kernel = null;
                runtime.UnpublishedUsernames.Clear();
            }
        }
        foreach (var key in replayFailureLogged.Keys)
            if (!Cache.IsActive(key.ViewId)) replayFailureLogged.TryRemove(key, out _);
    }

    private void RequestAdvance(SearchViewInfo view)
    {
        if (NeedsAdvance(view)) Schedule(view.Id);
    }

    private static bool NeedsAdvance(SearchViewInfo view)
        => !view.IsComplete;

    private async Task ProcessAsync(
        RuntimeView runtime,
        bool explicitlyRequested,
        CancellationToken cancellationToken)
    {
        runtime.LastUsedAt = clock.GetUtcNow();
        if (runtime.Kernel == null)
        {
            SearchViewInfo? stored = Cache.Peek(runtime.ViewId, cancellationToken);
            if (stored == null || stored.ExpiresAtUtc <= clock.GetUtcNow())
            {
                runtimes.TryRemove(runtime.ViewId, out _);
                runtime.PublicationTimer?.Dispose();
                return;
            }
            runtime.Kernel = await RebuildKernelAsync(runtime.Source, runtime.Projection,
                runtime.Settings, stored, cancellationToken, runtime.PeerFailureCounts).ConfigureAwait(false);
        }
        SearchViewKernel kernel = runtime.Kernel;
        // Catch-up never depends on a client poll. Coalesce sparse live arrivals
        // to the display cadence instead of publishing an index per response.
        // Coalesce those inputs before mapping/evaluating them; first pages,
        // existing backlogs and completion remain immediate. No worker is held
        // while waiting, and an unobserved timer wakeup does no projection work.
        if (!explicitlyRequested && runtime.HasPublishedResults && !runtime.CatchingUp
            && runtime.LastPublicationTimestamp is { } lastPublished
            && runtime.Source.IsSparseAndLive(kernel.ConsumedSequence, ProjectionBatchSize))
        {
            TimeSpan remaining = PublicationInterval - clock.GetElapsedTime(lastPublished);
            if (remaining > TimeSpan.Zero)
            {
                ScheduleAfter(runtime, remaining);
                return;
            }
        }
        runtime.PublicationTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        long projectionStarted = Stopwatch.GetTimestamp();
        // Live searches benefit from a small first publication. Completed
        // sources use the normal bounded batch: readers need the final view,
        // so a tiny initial prefix only adds a read and an index publication.
        bool initialPublication = kernel.ConsumedSequence == 0;
        int batchSize = initialPublication && runtime.Source.CanReceiveInputs
            ? Math.Min(InitialProjectionBatchSize, ProjectionBatchSize) : ProjectionBatchSize;
        using var preparation = ProjectionActivities.StartActivity("advance");
        preparation?.SetTag("view.id", runtime.ViewId);
        preparation?.SetTag("initial", initialPublication);
        SourceBatch batch;
        using (ProjectionActivities.StartActivity("source-read"))
            batch = await runtime.Source.ReadAsync(
                kernel.ConsumedSequence, batchSize, cancellationToken).ConfigureAwait(false);
        preparation?.SetTag("input.count", batch.Items.Count);
        bool complete = batch.IsComplete;
        if (batch.Items.Count == 0 && !complete)
        {
            runtime.CatchingUp = false;
            if (runtime.UnpublishedUsernames.Count == 0) return;
        }
        SearchViewKernelUpdate update;
        using (ProjectionActivities.StartActivity("kernel"))
            update = kernel.Apply(batch.Items, batch.SourceRevision, complete);
        // Projection time excludes publication into the immutable read store.
        runtime.ProjectionDuration += Stopwatch.GetElapsedTime(projectionStarted);
        runtime.CatchingUp = batch.Items.Count == batchSize;
        // Filtered arrivals and not-yet-eligible groups change only counters.
        // Keep consuming them, but publish counters at the display cadence.
        // The kernel owns cumulative counters; retain only peer names until the
        // next publication. First rows, completion and explicit reads bypass it.
        TimeSpan publicationDelay = runtime.LastPublicationTimestamp is { } lastPublication
            ? PublicationInterval - clock.GetElapsedTime(lastPublication) : TimeSpan.Zero;
        if (!explicitlyRequested && !complete && !update.HasProjectionChanges
            && publicationDelay > TimeSpan.Zero)
        {
            runtime.UnpublishedUsernames.UnionWith(update.ObservedUsernames ?? []);
            if (runtime.CatchingUp) Schedule(runtime.ViewId, observation: true);
            else ScheduleAfter(runtime, publicationDelay);
            return;
        }
        if (runtime.UnpublishedUsernames.Count > 0)
        {
            runtime.UnpublishedUsernames.UnionWith(update.ObservedUsernames ?? []);
            update = update with { ObservedUsernames = runtime.UnpublishedUsernames.ToArray() };
        }
        long publicationStarted = Stopwatch.GetTimestamp();
        SearchViewInfo published = Cache.Publish(runtime.ViewId, update, batch.RetentionState, cancellationToken);
        runtime.PublicationDuration += Stopwatch.GetElapsedTime(publicationStarted);
        runtime.LastPublicationTimestamp = clock.GetTimestamp();
        runtime.UnpublishedUsernames.Clear();
        Volatile.Write(ref runtime.PublishedRevision, published.Revision);
        runtime.HasPublishedResults = published.Counters.TopLevelItemCount > 0;
        NotifyPublished(published);
        if (complete)
        {
            runtimes.TryRemove(runtime.ViewId, out _);
            runtime.PublicationTimer?.Dispose();
            ServerLogMessages.SearchViewCompleted(
                logger,
                runtime.ViewId,
                published.RetentionState,
                runtime.Lifetime.ElapsedMilliseconds,
                published.Revision,
                published.Counters.PublicFileCount,
                published.Counters.LockedFileCount,
                published.Counters.ProjectedFileCount);
            ServerLogMessages.SearchViewCompletionTiming(
                logger, runtime.ViewId,
                (long)runtime.ProjectionDuration.TotalMilliseconds,
                (long)runtime.PublicationDuration.TotalMilliseconds);
        }
        else if (batch.Items.Count == batchSize && IsObserved(runtime.ViewId))
            Schedule(runtime.ViewId, observation: true);
    }

    private void ScheduleAfter(RuntimeView runtime, TimeSpan delay)
    {
        runtime.PublicationTimer ??= clock.CreateTimer(
            _ => Schedule(runtime.ViewId, observation: true), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        runtime.PublicationTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Fail(RuntimeView runtime, Exception exception, CancellationToken cancellationToken)
    {
        runtimes.TryRemove(runtime.ViewId, out _);
        runtime.PublicationTimer?.Dispose();
        ServerLogMessages.SearchViewProjectionFailed(logger, exception, runtime.ViewId);
        try
        {
            // Failed publication leaves the previous immutable snapshot intact.
            // Finish from that prefix without accepting partially staged rows.
            SearchViewInfo current = Cache.Peek(runtime.ViewId, cancellationToken)
                ?? throw new KeyNotFoundException("The search view was not found.");
            var terminal = new SearchViewKernelUpdate(
                current.SourceRevision, current.ConsumedSequence, true, current.Counters, []);
            SearchViewInfo published = Cache.Publish(runtime.ViewId, terminal, "Incomplete", cancellationToken);
            NotifyPublished(published);
        }
        catch (Exception terminalException) when (!cancellationToken.IsCancellationRequested)
        {
            ServerLogMessages.SearchViewIncompleteStateFailed(
                logger,
                terminalException,
                runtime.ViewId);
        }
    }

    private void NotifyPublished(SearchViewInfo view)
    {
        foreach (Action<SearchViewInfo> observer in Delegate.EnumerateInvocationList(ViewPublished))
        {
            try
            {
                observer(view);
            }
            catch (Exception exception)
            {
                ServerLogMessages.SearchViewObserverFailed(
                    logger,
                    exception,
                    view.Id,
                    view.Revision);
            }
        }
    }

    private void Schedule(Guid viewId, bool observation = false)
    {
        if (!observation) requested.TryAdd(viewId, 0);
        if (!pending.TryAdd(viewId, 0)) return;
        if (!work.Writer.TryWrite(viewId)) pending.TryRemove(viewId, out _);
    }

    private async Task<RuntimeView?> RestoreRequestedAsync(Guid viewId, CancellationToken cancellationToken)
    {
        SearchViewCache repository = Cache;
        SearchViewInfo? stored = repository.Peek(viewId, cancellationToken);
        if (stored == null || stored.ExpiresAtUtc <= clock.GetUtcNow()) return null;
        if (stored.IsComplete) return null;
        (SearchDefinition definition, SearchViewProjectionDefinition projection,
            SearchSettingsPatchDto? refinementSettings) = stored.Definition;
        RuntimeSource? source = await CreateStoredSourceAsync(stored.SourceJobId, definition, projection, cancellationToken)
            .ConfigureAwait(false);
        if (source == null)
        {
            CompleteUnrestorable(repository, stored, cancellationToken);
            return null;
        }
        SearchSettings settings = definition.ProjectionSettings.ToSettings();
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, refinementSettings);
        IReadOnlyDictionary<string, int> peerFailures = supervisor.GetSearchViewSource(stored.SourceJobId)?.PeerFailureCounts
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var runtime = new RuntimeView(stored, source, projection, settings, peerFailures, clock.GetUtcNow());
        runtimes.TryAdd(stored.Id, runtime);
        return runtime;
    }

    private void CompleteUnrestorable(SearchViewCache repository, SearchViewInfo stored, CancellationToken cancellationToken)
    {
        try
        {
            var terminal = new SearchViewKernelUpdate(
                stored.SourceRevision,
                stored.ConsumedSequence,
                true,
                stored.Counters,
                []);
            SearchViewInfo published = repository.Publish(stored.Id, terminal, "Incomplete", cancellationToken);
            NotifyPublished(published);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServerLogMessages.SearchViewRestorePublicationFailed(
                logger,
                exception,
                stored.Id);
        }
    }

    private async Task<RuntimeSource?> CreateStoredSourceAsync(
        Guid sourceJobId,
        SearchDefinition definition,
        SearchViewProjectionDefinition projection,
        CancellationToken cancellationToken)
    {
        if (ObservationSession(sourceJobId, supervisor, persistence) != null)
            return new ObservedSource(sourceJobId, supervisor, persistence);
        if (persistence.SearchHistory == null)
            return null;
        await persistence.WaitForJobHandoffAsync(sourceJobId, cancellationToken)
            .ConfigureAwait(false);
        PersistedSearchMetadata? metadata = await persistence.SearchHistory.GetMetadataAsync(
            sourceJobId,
            cancellationToken).ConfigureAwait(false);
        return metadata == null
            ? null
            : new HistoricalSource(
                persistence.SearchHistory,
                metadata,
                definition,
                projection);
    }

    private static void ValidateRefinementSettings(SearchSettingsPatchDto? settings)
    {
        if (settings == null)
            return;
        if (settings.SearchTimeout != null
            || settings.DownrankOn != null
            || settings.IgnoreOn != null
            || settings.FastSearch != null
            || settings.FastSearchDelay != null
            || settings.FastSearchMinUpSpeed != null
            || settings.DesperateSearch != null
            || settings.NoRemoveSpecialChars != null
            || settings.RemoveSingleCharSearchTerms != null
            || settings.NoBrowseFolder != null
            || settings.Relax != null
            || settings.ArtistMaybeWrong != null
            || settings.GroupResults != null)
        {
            throw new ArgumentException(
                "Search View refinements may only change result conditions, ranking, and grouping.",
                nameof(settings));
        }
    }

    private async Task<SearchViewKernel> RebuildKernelAsync(
        RuntimeSource source,
        SearchViewProjectionDefinition projection,
        SearchSettings settings,
        SearchViewInfo stored,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, int>? peerFailures = null)
    {
        var kernel = new SearchViewKernel(
            projection,
            settings,
            peerFailures,
            retainProjectedRows: false,
            trackPeerIdentities: false);
        while (kernel.ConsumedSequence < stored.ConsumedSequence)
        {
            SourceBatch batch = await source.ReadAsync(
                kernel.ConsumedSequence,
                ProjectionBatchSize,
                cancellationToken).ConfigureAwait(false);
            SearchProjectionInput[] prefix = batch.Items
                .Where(item => item.Sequence <= stored.ConsumedSequence)
                .ToArray();
            if (prefix.Length == 0)
            {
                throw new InvalidDataException(
                    "The retained Search View source no longer contains its durable observed prefix.");
            }
            kernel.Apply(prefix, stored.SourceRevision, isComplete: false);
        }
        if (kernel.ConsumedSequence != stored.ConsumedSequence)
            throw new InvalidDataException("The Search View durable sequence boundary is invalid.");
        return kernel;
    }

    private async Task<RuntimeSource?> CreateHistoricalSourceAsync(
        Guid sourceJobId,
        CreateSearchViewRequestDto request,
        CancellationToken cancellationToken)
    {
        if (persistence.SearchHistory == null || persistence.JobHistory == null)
            return null;
        await persistence.WaitForJobHandoffAsync(sourceJobId, cancellationToken)
            .ConfigureAwait(false);
        PersistedSearchMetadata? metadata = await persistence.SearchHistory
            .GetMetadataAsync(sourceJobId, cancellationToken).ConfigureAwait(false);
        PersistedJob? job = await persistence.JobHistory.GetJobAsync(
            sourceJobId,
            cancellationToken).ConfigureAwait(false);
        if (metadata == null || job == null)
            return null;
        SearchDefinition definition = await HistoricalJobDtoMapper.SearchDefinitionAsync(
            persistence.Submissions,
            job,
            RetainedJobPayloadCodec.Read(job),
            cancellationToken).ConfigureAwait(false);
        SearchViewProjectionDefinition projection = ResolveProjection(
            definition,
            request);
        return new HistoricalSource(
            persistence.SearchHistory,
            metadata,
            definition,
            projection);
    }

    private static SearchViewProjectionDefinition ResolveProjection(
        SearchDefinition definition,
        CreateSearchViewRequestDto request,
        SongQuery? defaultSongQuery = null,
        AlbumQuery? defaultAlbumQuery = null)
    {
        SearchViewProjectionKind kind = request.Kind?.ToCore() ?? definition.DefaultProjection switch
        {
            SearchDefaultProjectionKind.GenericFile => SearchViewProjectionKind.GenericDirectories,
            SearchDefaultProjectionKind.Track => definition.GroupResults
                ? SearchViewProjectionKind.TrackGroups : SearchViewProjectionKind.Files,
            SearchDefaultProjectionKind.Album => definition.GroupResults
                ? SearchViewProjectionKind.AlbumGroups : SearchViewProjectionKind.AlbumDirectories,
            _ => throw new ArgumentOutOfRangeException(),
        };
        SongQuery? song = request.SongQuery == null
            ? defaultSongQuery ?? definition.FileQuery?.ToQuery()
            : JobRequestMapper.ToSongQuery(request.SongQuery);
        AlbumQuery? album = request.AlbumQuery == null
            ? defaultAlbumQuery ?? definition.AlbumQuery?.ToQuery()
            : JobRequestMapper.ToAlbumQuery(request.AlbumQuery);
        if (kind is SearchViewProjectionKind.Files
            or SearchViewProjectionKind.GenericDirectories
            or SearchViewProjectionKind.TrackGroups)
            song ??= new SongQuery { Title = definition.NetworkQuery };
        var resolved = new SearchViewProjectionDefinition(
            kind,
            song,
            album,
            request.IncludeFullResults,
            request.FilterText,
            request.SortOrder?.ToCore() ?? SearchViewSortOrder.Relevance);
        resolved.Validate();
        return resolved;
    }

    private static ProjectedFileCandidate[] ProjectRetrievedDirectoryFiles(
        PeerDirectorySnapshot snapshot,
        SearchProjectionInput peerObservation,
        long sequenceBase,
        SearchDefinition definition,
        SearchViewProjectionDefinition projection,
        SearchSettingsPatchDto? refinementSettings)
    {
        AlbumQuery album = projection.AlbumQuery
            ?? definition.AlbumQuery?.ToQuery()
            ?? new AlbumQuery();
        SongQuery query = projection.SongQuery ?? new SongQuery
        {
            Artist = album.Artist,
            Album = album.Album,
            URI = album.URI,
            ArtistMaybeWrong = album.ArtistMaybeWrong,
        };
        SearchSettings settings = definition.ProjectionSettings.ToSettings();
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, refinementSettings);
        var sorter = new IncrementalResultSorter(
            query,
            settings,
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal),
            albumMode: projection.Kind is SearchViewProjectionKind.AlbumDirectories
                or SearchViewProjectionKind.AlbumGroups,
            requireFileSatisfies: projection.Kind == SearchViewProjectionKind.GenericDirectories
                && !projection.IncludeFullResults,
            ignoreStringSortConditions:
                projection.Kind == SearchViewProjectionKind.AlbumGroups,
            retainProjectedRows: false);
        SearchProjectionInput[] inputs = snapshot.Files
            .Select((target, index) => new SearchProjectionInput(
                checked(sequenceBase + index + 1),
                peerObservation.Revision,
                target.Username,
                snapshot.Files.Count,
                target.Filename,
                target.Size ?? -1,
                target.BitRate,
                target.BitDepth,
                target.SampleRate,
                target.Length,
                target.Extension ?? string.Empty,
                peerObservation.UploadSpeed,
                peerObservation.HasFreeUploadSlot,
                target.Attributes,
                peerObservation.ObservedAtUtc,
                peerObservation.QueueLength,
                SearchResultVisibility.Public))
            .ToArray();
        string? filter = string.IsNullOrWhiteSpace(projection.FilterText)
            ? null : projection.FilterText.Trim();
        return sorter.AddRangeAndGetProjected(
            projection.Kind == SearchViewProjectionKind.GenericDirectories
                ? inputs.Where(input => SearchViewProjectionDefinition.MatchesFileInput(input, filter))
                : inputs).ToArray();
    }

    private static SearchViewSummaryDto ToDto(SearchViewInfo view)
        => new(
            view.Id,
            view.SourceJobId,
            view.ProjectionKind.ToServer(),
            view.CreatedAtUtc,
            view.ExpiresAtUtc,
            view.Revision,
            view.SourceRevision,
            view.ConsumedSequence,
            view.IsComplete,
            ParseRetention(view.RetentionState),
            ToDto(view.Counters),
            view.SortOrder.ToServer());

    private static SearchViewRevisionDto ToDto(SearchViewRevision revision)
        => new(
            revision.ViewId,
            revision.Revision,
            revision.SourceRevision,
            revision.ConsumedSequence,
            revision.IsComplete,
            ParseRetention(revision.RetentionState),
            ToDto(revision.Counters));

    private static SearchViewCountersDto ToDto(SearchViewCounters counters)
        => new(
            counters.PublicFileCount,
            counters.LockedFileCount,
            counters.PublicBytes,
            counters.LockedBytes,
            counters.ObservedPeerCount,
            counters.ProjectedFileCount,
            counters.ProjectedPublicFileCount,
            counters.ProjectedLockedFileCount,
            counters.PreferredFileCount,
            counters.OtherFileCount,
            counters.TopLevelItemCount,
            counters.SelectableOptionCount);

    private static SearchViewFileDto ToDto(SearchViewFile row)
        => new(
            row.Ref,
            row.Input.Visibility.ToServer(),
            row.PreferenceTier.ToServer(),
            row.NecessaryConditionsSatisfied,
            row.SatisfiedPreferredConditions.Select(condition => condition.ToServer()).ToArray(),
            row.ConfiguredPreferredConditions
                .Except(row.SatisfiedPreferredConditions)
                .Select(condition => condition.ToServer())
                .ToArray(),
            row.Input.Filename,
            new PeerInfoDto(
                row.Input.Username,
                row.Input.HasFreeUploadSlot,
                row.Input.UploadSpeed,
                row.Input.QueueLength,
                row.Input.ObservedAtUtc),
            new FileMetadataDto(
                Utils.GetFileNameSlsk(row.Input.Filename),
                row.Input.Size,
                row.Input.Extension,
                row.Input.BitRate,
                row.Input.BitDepth,
                row.Input.SampleRate,
                row.Input.Length,
                row.Input.Attributes?.Select(attribute => new FileAttributeDto(
                    attribute.Type,
                    attribute.Value)).ToArray()));

    private static SearchViewDirectoryDto ToDto(SearchViewDirectory row)
    {
        SearchViewFileDto best = ToDto(row.BestChild);
        SearchViewDirectoryVisibility visibility = row switch
        {
            { PublicMatchingFileCount: > 0, LockedMatchingFileCount: > 0 }
                => SearchViewDirectoryVisibility.Mixed,
            { PublicMatchingFileCount: > 0 } => SearchViewDirectoryVisibility.Public,
            _ => SearchViewDirectoryVisibility.Locked,
        };
        return new(
            new PeerDirectoryRefDto(row.Ref, row.Username, row.FolderPath),
            visibility,
            row.BestChild.PreferenceTier.ToServer(),
            row.BestChild.SatisfiedPreferredConditions.Select(condition => condition.ToServer()).ToArray(),
            row.PublicMatchingFileCount,
            row.LockedMatchingFileCount,
            row.PublicMatchingBytes,
            row.LockedMatchingBytes,
            row.RetrievedFileCount,
            row.RetrievedBytes,
            row.IsFullyRetrieved
                ? SearchViewDirectoryRetrievalState.Complete
                : row.RetrievedFileCount != null
                    ? SearchViewDirectoryRetrievalState.Incomplete
                    : SearchViewDirectoryRetrievalState.SearchResultsOnly,
            best.Peer,
            best);
    }

    private SearchViewDirectoryDto ToDtoWithInitialFiles(
        Guid viewId,
        long revision,
        SearchViewDirectory row,
        IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> previews)
    {
        SearchViewDirectoryDto directory = ToDto(row);
        if (!previews.TryGetValue(row.Ref, out SearchViewDirectoryFilePreview? preview))
            return directory;
        return directory with
        {
            InitialFiles = new SearchViewDirectoryFilePreviewDto(
                preview.Items.Select(item => new SearchViewDirectoryFileDto(
                    item.Ref, item.RelativePath, ToDto(item.File))).ToArray(),
                preview.NextPosition == null
                    ? null
                    : cursors.EncodeDirectoryFiles(
                        viewId, row.Ref, revision, preview.NextPosition)),
        };
    }

    private static SearchViewTrackGroupDto ToDto(
        SearchViewTrackGroup row)
    {
        SongQuery query = row.Query;
        return new(
            row.Ref,
            ServerSnapshotMapper.ToSongQueryDto(query),
            row.ShareCount,
            row.SelectableOptionCount,
            row.OptionCount,
            ToDto(row.Representative));
    }

    private static SearchViewAlbumGroupDto ToDto(
        SearchViewAlbumGroup row)
    {
        AlbumQuery query = row.Query;
        return new(
            row.Ref,
            ServerSnapshotMapper.ToAlbumQueryDto(query),
            row.ShareCount,
            row.SelectableOptionCount,
            row.OptionCount,
            ToDto(row.Representative));
    }

    private static SearchViewRetentionState ParseRetention(string value)
        => Enum.TryParse(value, ignoreCase: true, out SearchViewRetentionState parsed)
            ? parsed
            : SearchViewRetentionState.Incomplete;

    private static CreateSearchViewRequestDto ToDto(
        SearchViewProjectionDefinition projection)
        => new(
            projection.Kind.ToServer(),
            projection.SongQuery == null
                ? null
                : ServerSnapshotMapper.ToSongQueryDto(projection.SongQuery),
            projection.AlbumQuery == null
                ? null
                : ServerSnapshotMapper.ToAlbumQueryDto(projection.AlbumQuery),
            projection.IncludeFullResults,
            FilterText: projection.FilterText,
            SortOrder: projection.SortOrder.ToServer());

    private sealed class RuntimeView(
        SearchViewInfo stored, RuntimeSource source, SearchViewProjectionDefinition projection,
        SearchSettings settings, IReadOnlyDictionary<string, int> peerFailures,
        DateTimeOffset lastUsedAt, SearchViewKernel? kernel = null)
    {
        public Guid ViewId { get; } = stored.Id;
        public RuntimeSource Source { get; } = source;
        public SearchViewProjectionDefinition Projection { get; } = projection;
        public SearchSettings Settings { get; } = settings;
        public IReadOnlyDictionary<string, int> PeerFailureCounts { get; } = peerFailures;
        public long PublishedRevision = stored.Revision;
        public SearchViewKernel? Kernel { get; set; } = kernel;
        public DateTimeOffset LastUsedAt { get; set; } = lastUsedAt;
        public Stopwatch Lifetime { get; } = Stopwatch.StartNew();
        public TimeSpan ProjectionDuration { get; set; }
        public TimeSpan PublicationDuration { get; set; }
        public long? LastPublicationTimestamp { get; set; }
        public bool CatchingUp { get; set; }
        public bool HasPublishedResults { get; set; } = stored.Counters.TopLevelItemCount > 0;
        public ITimer? PublicationTimer { get; set; }
        public HashSet<string> UnpublishedUsernames { get; } = new(StringComparer.Ordinal);
    }

    private abstract class RuntimeSource
    {
        public virtual bool CanReceiveInputs => false;
        public abstract Task<SourceBatch> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken);
        public virtual bool IsSparseAndLive(long afterSequence, int batchSize) => false;
        public virtual void ReleaseLiveInputs() { }
    }

    private static SearchSession? ObservationSession(Guid jobId, EngineSupervisor supervisor, PersistenceCoordinator persistence)
    {
        SearchSession? live = supervisor.GetRuntimeJob<Job>(jobId)?.SearchObservationSession;
        return persistence.GetRetiringSearchSource(jobId)?.Session ?? live;
    }

    // Active catch-up may reuse the original immutable inputs after Core retires.
    // Demand owns this reference, not the idle kernel: last-reader disposal and
    // one-shot worker completion release it without retaining another input copy.
    private sealed class ObservedSource(Guid jobId, EngineSupervisor supervisor, PersistenceCoordinator persistence) : RuntimeSource
    {
        private SearchSession? liveInputs;
        public override bool CanReceiveInputs => ObservationSession(jobId, supervisor, persistence)?.IsComplete == false;
        public override void ReleaseLiveInputs() => Interlocked.Exchange(ref liveInputs, null);

        public override bool IsSparseAndLive(long afterSequence, int batchSize)
        {
            SearchSession? session = ObservationSession(jobId, supervisor, persistence);
            return session != null && !session.IsComplete
                && (long)session.PublicFileCount + session.LockedFileCount - afterSequence < batchSize;
        }

        public override async Task<SourceBatch> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = Volatile.Read(ref liveInputs) ?? ObservationSession(jobId, supervisor, persistence);
            if (session != null)
            {
                Interlocked.CompareExchange(ref liveInputs, session, null);
                var page = session.ReadProjectionPage(afterSequence, limit);
                return new(page.Items, page.Revision, page.IsComplete, page.IsComplete ? "Complete" : "Live");
            }
            var reader = persistence.SearchHistory
                ?? throw new KeyNotFoundException("The search observation source is unavailable.");
            var retained = await reader.GetRawResultsAsync(jobId, afterSequence, limit, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The retained search source was not found.");
            return ToSourceBatch(retained, canReceiveInputs: false);
        }
    }

    private sealed class HistoricalSource(
        ISearchHistoryReader reader,
        PersistedSearchMetadata metadata,
        SearchDefinition definition,
        SearchViewProjectionDefinition projection) : RuntimeSource
    {
        public SearchDefinition Definition { get; } = definition;
        public SearchViewProjectionDefinition Projection { get; } = projection;

        public override Task<SourceBatch> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken)
            => ReadHistoryBatchAsync(reader, metadata, afterSequence, limit, cancellationToken);
    }

    internal static async Task<SourceBatch> ReadHistoryBatchAsync(
        ISearchHistoryReader reader, PersistedSearchMetadata metadata,
        long afterSequence, int limit, CancellationToken cancellationToken)
    {
        // One bounded read owns metadata and rows from the same SQL snapshot.
        // Retained sources use durable facts; active readers use the Core session.
        PersistedSearchResultPage page = await reader.GetRawResultsAsync(
            metadata.JobId, afterSequence, limit, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The retained search source was not found.");
        return ToSourceBatch(page, canReceiveInputs: false);
    }

    private static SourceBatch ToSourceBatch(PersistedSearchResultPage page, bool canReceiveInputs)
        => ToSourceBatch(page.Metadata, page.Items,
            page.NextSequence, canReceiveInputs);

    private static SourceBatch ToSourceBatch(PersistedSearchMetadata metadata,
        IReadOnlyList<SearchProjectionInput> inputs, long? nextSequence, bool canReceiveInputs)
        => new(inputs, checked((int)metadata.Revision),
            nextSequence == null && (!canReceiveInputs || IsTerminalSource(metadata)),
            !canReceiveInputs || IsTerminalSource(metadata) ? metadata.ResultPersistenceState : "Live");

    private static bool IsTerminalSource(PersistedSearchMetadata metadata)
        => metadata.IsComplete || metadata.ResultPersistenceState is "Interrupted" or "Pruned";

    internal sealed record SourceBatch(
        IReadOnlyList<SearchProjectionInput> Items,
        int SourceRevision,
        bool IsComplete,
        string RetentionState);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifetime.Dispose();
        foreach (RuntimeView runtime in runtimes.Values) runtime.PublicationTimer?.Dispose();
        runtimes.Clear();
        replayFailureLogged.Clear();
        pending.Clear();
        requested.Clear();
        Cache.Clear();
    }
}
