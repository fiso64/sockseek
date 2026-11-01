using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class SearchViewUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

// Read inputs survive execution retirement only until the existing persistence
// handoff finishes. No execution job or client selection is retained here.
internal sealed record SearchViewSource(
    SearchSession Session,
    SearchDefinition? FallbackDefinition,
    SearchSettings Settings,
    IReadOnlyDictionary<string, int> UserSuccessCounts,
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

    public static SearchViewSource? Capture(Job job, IReadOnlyDictionary<string, int> userSuccessCountSnapshot)
    {
        if (job.Config == null || job.SearchObservationSession is not { } session)
            return null;
        SearchDefinition? fallback = job.ExecutedSearchDefinition;
        if (string.IsNullOrWhiteSpace(job.SubmissionSpecificationJson))
            fallback ??= (job as SearchJob)?.Definition;
        // Prepared settings are no longer mutated by execution. Each View
        // clones them before refinement; retirement need not copy/parse plans.
        return new(session, fallback, job.Config.Search, userSuccessCountSnapshot,
            job.SubmissionSpecificationJson);
    }
}

public sealed class SearchViewCoordinator(
    EngineSupervisor supervisor,
    PersistenceCoordinator persistence,
    SubmissionCommitCoordinator commits,
    SearchViewCursorCodec cursors,
    ILogger<SearchViewCoordinator> logger,
    TimeProvider? timeProvider = null) : IHostedService, IAsyncDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan IdleKernelRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PublicationInterval = TimeSpan.FromSeconds(1);
    private static readonly ActivitySource ProjectionActivities = new("Sockseek.Server.SearchViews");
    private const int InitialProjectionBatchSize = 200;
    // Coalesce queued bursts: small commits repeatedly rewrite the hash-keyed
    // indexes as a view grows. Keep the first page small and never wait for a
    // sparse stream to fill this batch; each publication releases the write gate.
    // Bound processing and writer occupancy, independently of source commit order.
    private int ProjectionBatchSize => persistence.SearchResultBatchSize;
    private const int InitialDirectoryFileLimit = 24;
    private readonly ConcurrentDictionary<Guid, RuntimeView> runtimes = [];
    private readonly ConcurrentDictionary<Guid, byte> pending = [];
    private readonly ConcurrentDictionary<Guid, byte> requested = [];
    private readonly Dictionary<Guid, int> observers = [];
    private readonly ConcurrentDictionary<(Guid ViewId, Guid ObservationId), byte> replayFailureLogged = [];
    private readonly Channel<(Guid ViewId, IDisposable? Preparation)> work = Channel.CreateUnbounded<(Guid, IDisposable?)>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource lifetime = new();
    private SearchViewStore? store;
    private Exception? initializationFailure;
    private Task? worker;

    internal event Action<StoredSearchView>? ViewPublished;

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
        var changed = Channel.CreateBounded<StoredSearchView>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        long lastPublishedRevision = -1;
        void Published(StoredSearchView view)
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
        StoredSearchView? sourceView = null;
        void SourceChanged(Guid jobId)
        {
            if (jobId == Volatile.Read(ref sourceView)?.SourceJobId) Schedule(viewId, observation: true);
        }
        bool observing = false;
        ViewPublished += Published;
        persistence.SearchSourceChanged += SourceChanged;
        try
        {
            StoredSearchView latest = await ReadObservedAsync(viewId, cancellationToken).ConfigureAwait(false);
            if (NeedsAdvance(latest))
            {
                lock (observers) observers[viewId] = observers.GetValueOrDefault(viewId) + 1;
                observing = true;
                Volatile.Write(ref sourceView, latest);
                // Subscribe before the read, then schedule after registering
                // demand: a commit racing either step cannot be missed.
                Schedule(viewId, observation: true);
            }
            long lastSent = clock.GetTimestamp();
            bool hasSentResults = latest.Counters.TopLevelItemCount > 0;
            yield return ToDto(latest);
            while (NeedsAdvance(latest))
            {
                StoredSearchView next = await changed.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                // First usable results are immediate. Subsequent notifications
                // coalesce to the existing UI refresh cadence, independently of
                // how many batches the worker can process during that interval.
                // Completion bypasses this cadence, including for CLI printing.
                TimeSpan remaining = PublicationInterval - clock.GetElapsedTime(lastSent);
                Task immediate = hasSentResults ? completed.Task : firstResultsOrCompletion.Task;
                if (latest.Revision > 0 && remaining > TimeSpan.Zero && !immediate.IsCompleted)
                    await Task.WhenAny(Task.Delay(remaining, clock, cancellationToken), immediate)
                        .WaitAsync(cancellationToken).ConfigureAwait(false);
                while (changed.Reader.TryRead(out StoredSearchView? newer)) next = newer;
                if (await ReplayDirectoryObservationsAsync(viewId, cancellationToken).ConfigureAwait(false))
                    next = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false)
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

    private async Task<StoredSearchView> ReadObservedAsync(Guid viewId, CancellationToken cancellationToken)
    {
        StoredSearchView? view = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (view != null && await ReplayDirectoryObservationsAsync(viewId, cancellationToken).ConfigureAwait(false))
            view = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        return view ?? throw new KeyNotFoundException("The Search View was not found.");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SearchViewStore? candidate = persistence.SearchViews;
        if (candidate is null)
        {
            initializationFailure = new InvalidOperationException(
                "The shared persistence runtime or Search View schema is unavailable.");
            ServerLogMessages.SearchViewUnavailable(logger);
            return;
        }
        try
        {
            cursors.Initialize();
            store = candidate;
            await candidate.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
            worker = RunWorkerAsync(lifetime.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            initializationFailure = exception;
            ServerLogMessages.SearchViewInitializationFailed(logger, exception);
        }
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
        SearchViewStore repository = RequiredStore();
        ValidateRefinementSettings(request.RefinementSettings);
        // Expiry is enforced by reads; physical pruning belongs to startup and
        // scheduled retention, never to an interactive View creation/refinement.
        RuntimeSource? source;
        SearchDefinition definition;
        SearchViewProjectionDefinition projection;
        SearchSettings settings;
        IReadOnlyDictionary<string, int> workflowUserSuccessCounts;

        SearchViewSource? observation = supervisor.GetSearchViewSource(sourceJobId);
        if (observation != null)
        {
            definition = observation.Definition;
            projection = ResolveProjection(definition, request);
            settings = SettingsCloner.Clone(observation.Settings);
            source = new ObservedSource(sourceJobId, supervisor, persistence);
            // TODO [V4]: Preserve workflow downranking only; daemon-wide
            // reputation ownership and persistence remain undefined.
            workflowUserSuccessCounts = observation.UserSuccessCounts;
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
            workflowUserSuccessCounts = new Dictionary<string, int>(
                StringComparer.Ordinal);
        }

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, request.RefinementSettings);

        using var initialPublication = repository.PrioritizeInitialPublication();
        JsonSerializerOptions jsonOptions = SockseekApiJson.CreateSerializerOptions();
        var durableDefinitionNode = new JsonObject
        {
            ["searchDefinitionJson"] = SearchDefinitionCodec.Serialize(definition),
            ["projection"] = JsonSerializer.SerializeToNode(
                ToDto(projection) with { RefinementSettings = request.RefinementSettings },
                jsonOptions),
        };
        if (projection.Kind == SearchViewProjectionKind.GenericDirectories)
            durableDefinitionNode["genericDirectoryGroupingVersion"] = SearchViewKernel.GenericDirectoryGroupingVersion;
        string durableDefinition = durableDefinitionNode.ToJsonString();
        StoredSearchView stored = await repository.CreateAsync(
            sourceJobId,
            projection.Kind,
            durableDefinition,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var runtime = new RuntimeView(
            stored, source, projection, settings, workflowUserSuccessCounts, clock.GetUtcNow(),
            new SearchViewKernel(projection, settings, workflowUserSuccessCounts,
                retainProjectedRows: false, trackPeerIdentities: false));
        if (!runtimes.TryAdd(stored.Id, runtime))
            throw new InvalidOperationException("The search view already exists in runtime state.");
        Schedule(stored.Id);
        ServerLogMessages.SearchViewCreated(logger, stored.Id, sourceJobId);
        return ToDto(stored);
    }

    public async Task<SearchViewSummaryDto?> GetAsync(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        StoredSearchView? view = await RequiredStore().GetAsync(viewId, cancellationToken)
            .ConfigureAwait(false);
        if (view != null && await ReplayDirectoryObservationsAsync(viewId, cancellationToken).ConfigureAwait(false))
            view = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (view == null || view.ExpiresAtUtc <= DateTimeOffset.UtcNow) return null;
        RequestAdvance(view);
        return ToDto(view);
    }

    public async Task<SearchViewDefinitionDto?> GetDefinitionAsync(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        StoredSearchView? view = await RequiredStore().GetAsync(viewId, cancellationToken)
            .ConfigureAwait(false);
        if (view == null || view.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return null;
        (SearchDefinition source, SearchViewProjectionDefinition projection,
            SearchSettingsPatchDto? refinement) = ParseDurableDefinition(view.DefinitionJson);
        SearchSettings settings = source.ProjectionSettings.ToSettings();
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, refinement);
        return new SearchViewDefinitionDto(
            viewId,
            ToDto(projection) with { RefinementSettings = refinement },
            EffectiveSettingsMapper.ToSearchSettings(settings));
    }

    public async Task<SearchViewFilePageDto?> GetFilesAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        ServerSearchViewSortOrder? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        SearchViewFilePosition? after = cursor == null
            ? null
            : cursors.DecodeFiles(cursor, viewId, revision, sortOrder);
        StoredSearchViewFilePage? page = await RequiredStore().GetFilesAsync(
            viewId,
            revision,
            after,
            limit,
            sortOrder?.ToCore(),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeFiles(viewId, revision, page.NextPosition, sortOrder));
    }

    public async Task<SearchViewDirectoryPageDto?> GetDirectoriesAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        ServerSearchViewSortOrder? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        SearchViewDirectoryPosition? after = cursor == null
            ? null
            : cursors.DecodeDirectories(cursor, viewId, revision, sortOrder);
        StoredSearchViewDirectoryPage? page = await RequiredStore().GetDirectoriesAsync(
            viewId,
            revision,
            after,
            limit,
            InitialDirectoryFileLimit, sortOrder?.ToCore(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(item => ToDtoWithInitialFiles(viewId, revision, item, page.DirectoryFilePreviews)).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeDirectories(viewId, revision, page.NextPosition, sortOrder));
    }

    public async Task<SearchViewDirectoryFilePageDto?> GetDirectoryFilesAsync(
        Guid viewId,
        string directoryRef,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewDirectoryFilePosition? after = cursor == null
            ? null
            : cursors.DecodeDirectoryFiles(
                cursor,
                viewId,
                directoryRef,
                revision);
        StoredSearchViewDirectoryFilePage? page = await RequiredStore()
            .GetDirectoryFilesAsync(
                viewId,
                directoryRef,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
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

    public async Task<SearchViewAggregateTrackPageDto?> GetAggregateTracksAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewAggregateTrackPosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateTracks(cursor, viewId, revision);
        StoredSearchViewAggregateTrackPage? page = await RequiredStore()
            .GetAggregateTracksAsync(
                viewId,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateTracks(
                    viewId,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateTrackOptionPageDto?> GetAggregateTrackOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewFilePosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateTrackOptions(
                cursor,
                viewId,
                groupRef,
                revision);
        StoredSearchViewAggregateTrackOptionPage? page = await RequiredStore()
            .GetAggregateTrackOptionsAsync(
                viewId,
                groupRef,
                revision,
                after,
                limit,
                cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.GroupRef,
            page.Items.Select(ToDto).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateTrackOptions(
                    viewId,
                    groupRef,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateAlbumPageDto?> GetAggregateAlbumsAsync(
        Guid viewId,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewAggregateAlbumPosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateAlbums(cursor, viewId, revision);
        StoredSearchViewAggregateAlbumPage? page = await RequiredStore()
            .GetAggregateAlbumsAsync(
                viewId,
                revision,
                after,
                limit,
                InitialDirectoryFileLimit, cancellationToken).ConfigureAwait(false);
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
                : cursors.EncodeAggregateAlbums(
                    viewId,
                    revision,
                    page.NextPosition));
    }

    public async Task<SearchViewAggregateAlbumOptionPageDto?> GetAggregateAlbumOptionsAsync(
        Guid viewId,
        string groupRef,
        long revision,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        SearchViewDirectoryPosition? after = cursor == null
            ? null
            : cursors.DecodeAggregateAlbumOptions(
                cursor,
                viewId,
                groupRef,
                revision);
        StoredSearchViewAggregateAlbumOptionPage? page = await RequiredStore()
            .GetAggregateAlbumOptionsAsync(
                viewId,
                groupRef,
                revision,
                after,
                limit,
                InitialDirectoryFileLimit, cancellationToken).ConfigureAwait(false);
        if (page == null)
            return null;
        return new(
            ToDto(page.Revision),
            page.GroupRef,
            page.Items.Select(item => ToDtoWithInitialFiles(viewId, revision, item, page.DirectoryFilePreviews)).ToArray(),
            page.NextPosition == null
                ? null
                : cursors.EncodeAggregateAlbumOptions(
                    viewId,
                    groupRef,
                    revision,
                    page.NextPosition));
    }

    public async Task<ResolveSearchViewChosenOptionsResponseDto?> ResolveChosenOptionsAsync(
        Guid viewId,
        ResolveSearchViewChosenOptionsRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Revision < 0 || request.ChosenOptions == null
            || request.ChosenOptions.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)))
            throw new ArgumentException("A revision and non-empty group/option refs are required.", nameof(request));
        StoredSearchViewChosenOptions? resolved = await RequiredStore()
            .ResolveChosenOptionsAsync(viewId, request.Revision, request.ChosenOptions,
                InitialDirectoryFileLimit, cancellationToken)
            .ConfigureAwait(false);
        if (resolved == null)
            return null;
        return new(ToDto(resolved.Revision), request.ChosenOptions.Select(pair =>
            new SearchViewChosenOptionDto(
                pair.Key,
                pair.Value,
                resolved.Tracks.TryGetValue(pair.Key, out StoredSearchViewFile? track) ? ToDto(track) : null,
                resolved.Albums.TryGetValue(pair.Key, out StoredSearchViewDirectory? album)
                    ? ToDtoWithInitialFiles(viewId, request.Revision, album, resolved.DirectoryFilePreviews) : null)).ToArray());
    }

    public async Task<SearchViewUpdateDto?> GetUpdatesAsync(
        Guid viewId,
        long afterRevision,
        bool waitForChange = false,
        CancellationToken cancellationToken = default)
    {
        StoredSearchView? sourceView = null;
        void SourceChanged(Guid jobId)
        {
            if (jobId == Volatile.Read(ref sourceView)?.SourceJobId) Schedule(viewId);
        }
        if (afterRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(afterRevision));
        var publication = waitForChange
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        void Published(StoredSearchView view)
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
            StoredSearchView? latest = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
            if (latest == null) return null;
            if (afterRevision > latest.Revision)
                throw new ArgumentOutOfRangeException(nameof(afterRevision),
                    "The requested revision is newer than the Search View.");
            if (waitForChange && !latest.IsComplete)
            {
                Volatile.Write(ref sourceView, latest);
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
                    StoredSearchView? updated = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
                    if (updated == null) return null;
                    latest = updated;
                }
            }
            // A publication can create the first matching directory while this
            // read is waiting. Replay against that observed prefix before
            // returning it, especially when Complete will stop client polling.
            if (await ReplayDirectoryObservationsAsync(viewId, cancellationToken).ConfigureAwait(false))
            {
                latest = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
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
            }
        }
    }

    private async Task<bool> ReplayDirectoryObservationsAsync(
        Guid viewId,
        CancellationToken cancellationToken)
    {
        SearchViewStore repository = RequiredStore();
        await using IAsyncEnumerator<StoredSearchDirectoryObservationTarget> targets = repository
            .ReadUnappliedDirectoryObservationsAsync(viewId, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        if (!await targets.MoveNextAsync().ConfigureAwait(false))
            return false;
        StoredSearchView? view = await repository.GetAsync(viewId, cancellationToken)
            .ConfigureAwait(false);
        if (view == null || view.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return false;
        Guid sourceJobId = view.SourceJobId;
        bool changed = false;
        (SearchDefinition definition, SearchViewProjectionDefinition projection,
            SearchSettingsPatchDto? refinementSettings) =
            ParseDurableDefinition(view.DefinitionJson);
        do
        {
            StoredSearchDirectoryObservationTarget target = targets.Current;
            try
            {
                StoredSearchDirectoryObservation? observation = await repository
                    .GetDirectoryObservationAsync(
                        sourceJobId, target.Identity, cancellationToken)
                    .ConfigureAwait(false);
                if (observation == null || observation.Id != target.ObservationId)
                    continue;
                view = await repository.GetAsync(viewId, cancellationToken).ConfigureAwait(false);
                if (view == null)
                    return changed;
                bool newerSourceFile = await repository.HasNewerDirectorySourceFileAsync(
                    viewId,
                    target.DirectoryRef,
                    view.Revision,
                    observation.SourceRevision,
                    cancellationToken).ConfigureAwait(false);
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
                StoredSearchViewDirectoryPublishResult published = await repository
                    .PublishRetrievedDirectoryAsync(
                        viewId,
                        target.DirectoryRef,
                        snapshot,
                        projected,
                        observation.PeerObservation,
                        cancellationToken,
                        existingObservationId: observation.Id).ConfigureAwait(false);
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
        } while (await targets.MoveNextAsync().ConfigureAwait(false));
        return changed;
    }

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
        string fingerprint = SubmissionCommitCoordinator.FingerprintDirectoryRetrieval(
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
                SearchViewStore repository = RequiredStore();
                StoredSearchView sourceView = await repository.GetAsync(
                    viewId, operationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The search view was not found.");
                StoredSearchViewDirectoryFilePage? membership = await repository
                    .GetDirectoryFilesAsync(
                        viewId,
                        request.Directory.Ref,
                        request.Revision,
                        after: null,
                        limit: 1,
                        operationToken).ConfigureAwait(false);
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
                    ParseDurableDefinition(sourceView.DefinitionJson);
                SearchProjectionInput peerObservation = membership.Items[0].File.Input;
                JobSummaryDto? job = await supervisor.StartSearchViewDirectoryRetrievalAsync(
                    sourceView.SourceJobId,
                    exactIdentity,
                    async (snapshot, publishToken) =>
                    {
                        ProjectedFileCandidate[] projected = ProjectRetrievedDirectoryFiles(
                            snapshot,
                            peerObservation,
                            sourceView.ConsumedSequence,
                            definition,
                            projection,
                            refinementSettings);
                        StoredSearchViewDirectoryPublishResult published = await repository
                            .PublishRetrievedDirectoryAsync(
                                viewId,
                                request.Directory.Ref,
                                snapshot,
                                projected,
                                peerObservation,
                                publishToken).ConfigureAwait(false);
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

    public async Task<CommitSearchViewSelectionResponseDto?> CommitSelectionAsync(
        Guid viewId,
        CommitSearchViewSelectionRequestDto request,
        CancellationToken cancellationToken)
    {
        ValidateCommitRequest(request);
        string fingerprint = SubmissionCommitCoordinator.Fingerprint(
            "search-view",
            viewId,
            request.Revision,
            request.Selection,
            request.ExcludedFileRefs,
            request.IncludedFileRefs,
            request.ChosenOptions,
            request.AlbumFileSelections,
            request.Options,
            excludedFileIdentities: request.ExcludedFileIdentities,
            useRepresentativeWhenUnchosen: request.UseRepresentativeWhenUnchosen == true,
            directoryPathRules: request.DirectoryPathRules);
        return await commits.ExecuteAsync(
            request.IdempotencyKey,
            fingerprint,
            async operationToken =>
            {
                // Accepted intent outlives its temporary issuing View. Only new
                // work needs projection availability and revision membership.
                SearchViewStore repository = RequiredStore();
                StoredSearchView sourceView = await repository.GetAsync(viewId, operationToken)
                    .ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The search view was not found.");
                ValidateExcludedIdentities(sourceView.SourceJobId, request.ExcludedFileRefs,
                    request.ExcludedFileIdentities);
                if ((request.ExcludedFileRefs is { Count: > 0 } || request.IncludedFileRefs is { Count: > 0 })
                    && sourceView.ProjectionKind is not ("GenericDirectories" or "AlbumDirectories"))
                    throw new ArgumentException("File exclusions require a directory projection.");
                if (request.ChosenOptions is { Count: > 0 }
                    && sourceView.ProjectionKind is not ("AggregateTracks" or "AggregateAlbums"))
                    throw new ArgumentException("Chosen options require an aggregate projection.");
                if (request.UseRepresentativeWhenUnchosen == true
                    && sourceView.ProjectionKind is not ("AggregateTracks" or "AggregateAlbums"))
                    throw new ArgumentException("Representative-only selection requires an aggregate projection.");
                if (request.AlbumFileSelections is { Count: > 0 }
                    && sourceView.ProjectionKind != "AggregateAlbums")
                    throw new ArgumentException("Album file selections require an aggregate-album projection.");
                if (request.AlbumFileSelections?.Keys.Any(groupRef => request.ChosenOptions?.ContainsKey(groupRef) != true) == true)
                    throw new ArgumentException("Choosing album files requires an exact option for each group.");
                var directoryPathRules = GroupDirectoryPathRules(request.DirectoryPathRules);
                if (directoryPathRules.Count > 0 && sourceView.ProjectionKind != "GenericDirectories")
                    throw new ArgumentException("Directory path rules require a File Search projection.");
                CommitSearchViewSelectionResponseDto receipt = await CommitSelectionCoreAsync(
                    viewId,
                    sourceView,
                    request,
                    fingerprint,
                    operationToken).ConfigureAwait(false);
                return receipt;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResolveSearchViewSelectionSettingsResponseDto?> ResolveSelectionSettingsAsync(
        Guid viewId,
        ResolveSearchViewSelectionSettingsRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SelectionExpressionValidator.Validate(request.Selection);
        if (request.Revision < 0
            || request.IncludedFileRefs?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("The selection or revision is invalid.");

        SearchViewStore repository = RequiredStore();
        StoredSearchView? view = await repository.GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (view == null)
            return null;
        await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
        if (request.IncludedFileRefs is { Count: > 0 }
            && view.ProjectionKind is not ("GenericDirectories" or "AlbumDirectories"))
            throw new ArgumentException("Included file refs require a directory projection.");
        var directoryPathRules = GroupDirectoryPathRules(request.DirectoryPathRules);
        if (directoryPathRules.Count > 0 && view.ProjectionKind != "GenericDirectories")
            throw new ArgumentException("Directory path rules require a File Search projection.");

        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var included = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
        if (request.Selection.Mode == RefSelectionMode.Only)
            refs.UnionWith(included);
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        var variants = new Dictionary<ServerJobKind, SearchViewSelectionSettingsVariantDto>();
        var unresolvedOnly = request.Selection.Mode == RefSelectionMode.Only
            ? new HashSet<string>(refs, StringComparer.Ordinal) : null;

        void AddVariant(StoredSearchViewCommitItem item)
        {
            if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
                return;
            Job? job = item switch
            {
                { File: { } file } when file.Input.Visibility != SearchResultVisibility.Locked
                    => new RemoteFileJob(file.Input.ToFileCandidate().Target),
                { Directory: { } directory } when directory.PublicMatchingFileCount > 0
                    => new RemoteDirectoryJob(directory.FolderPath.Length == 0
                        ? new RemoteDirectorySource.Resolved(DirectoryTransferPlanner.FromObservedSelection(
                            new PeerDirectorySnapshot(new PeerDirectoryIdentity(directory.Username, ""),
                                [directory.BestChild.Input.ToFileCandidate().Target], isComplete: false)))
                        : new RemoteDirectorySource.PeerDirectory(
                            new PeerDirectoryIdentity(directory.Username, directory.FolderPath))),
                { AggregateTrackGroup: { } track } when track.SelectableOptionCount > 0
                    => new SongJob(JsonSerializer.Deserialize<SongQuery>(track.QueryJson)
                        ?? throw new InvalidDataException("Invalid aggregate-track query.")),
                { AggregateAlbumGroup: { } album } when album.SelectableOptionCount > 0
                    => new AlbumJob(JsonSerializer.Deserialize<AlbumQuery>(album.QueryJson)
                        ?? throw new InvalidDataException("Invalid aggregate-album query.")),
                _ => null,
            };
            if (job == null)
                return;
            // Auto-profile predicates currently use semantic mode/input type
            // and shared request context, not peer or filename. One composed
            // baseline per selected job kind is therefore exact; revisit this
            // grouping if profile conditions gain item-specific variables.
            ServerJobKind kind = job switch
            {
                RemoteFileJob => ServerJobKind.RemoteFile,
                RemoteDirectoryJob => ServerJobKind.RemoteDirectory,
                SongJob => ServerJobKind.Song,
                AlbumJob => ServerJobKind.Album,
                _ => throw new InvalidOperationException("Unsupported Search View selection job."),
            };
            if (!variants.ContainsKey(kind))
            {
                DownloadSettings settings = supervisor.PrepareSearchViewSelectionJob(
                    job,
                    options: null,
                    allowSearchViewAlbumFolderOption: view.ProjectionKind == "AlbumDirectories"
                        && job is RemoteDirectoryJob);
                variants.Add(kind, new SearchViewSelectionSettingsVariantDto(
                    kind, EffectiveSettingsMapper.ToSafeSettings(settings)));
            }
            if (item.Directory != null || item.AggregateTrackGroup != null || item.AggregateAlbumGroup != null)
                selectedContainers.Add(item.Ref);
        }

        await foreach (var item in repository.ReadCommitItemsAsync(
            viewId, request.Revision, view.ProjectionKind,
            request.Selection.Mode.ToString(), refs, cancellationToken).ConfigureAwait(false))
        {
            unresolvedOnly?.Remove(item.Ref);
            AddVariant(item);
            // AllExcept yields only top-level rows; once a public job kind is
            // known, inspecting every remaining row cannot change its defaults.
            if (unresolvedOnly?.Count == 0
                || request.Selection.Mode == RefSelectionMode.AllExcept && variants.Count > 0)
                break;
        }
        if (directoryPathRules.Values.Any(rules => rules.Any(rule => rule.Selected))
            && !variants.ContainsKey(ServerJobKind.RemoteDirectory))
        {
            var rootRef = directoryPathRules.First(pair => pair.Value.Any(rule => rule.Selected)).Key;
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, view.ProjectionKind,
                "Only", new HashSet<string>(StringComparer.Ordinal) { rootRef }, cancellationToken)
                .ConfigureAwait(false))
            {
                if (item.Directory != null) AddVariant(item);
            }
        }
        if (request.Selection.Mode == RefSelectionMode.AllExcept && included.Count > 0)
        {
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, view.ProjectionKind,
                "Only", included, cancellationToken).ConfigureAwait(false))
                if (item.File != null)
                    AddVariant(item);
        }
        await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
        return new ResolveSearchViewSelectionSettingsResponseDto(
            request.Revision,
            variants.Values.OrderBy(variant => variant.JobKind).ToArray());
    }

    public async Task<CountSearchViewSelectionResponseDto?> CountSelectionAsync(
        Guid viewId,
        CountSearchViewSelectionRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SelectionExpressionValidator.Validate(request.Selection);
        if (request.Revision < 0
            || request.IncludedFileRefs?.Any(string.IsNullOrWhiteSpace) == true
            || request.ExcludedFileRefs?.Any(string.IsNullOrWhiteSpace) == true
            || request.ChosenOptions?.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)) == true)
            throw new ArgumentException("The selection or revision is invalid.");
        ValidateAlbumFileSelections(request.AlbumFileSelections);
        if ((request.IncludedFileRefs ?? []).Intersect(request.ExcludedFileRefs ?? [], StringComparer.Ordinal).Any())
            throw new ArgumentException("A file cannot be included and excluded simultaneously.");
        if (request.AlbumFileSelections?.Keys.Any(groupRef => request.ChosenOptions?.ContainsKey(groupRef) != true) == true)
            throw new ArgumentException("Choosing album files requires an exact option for each group.");

        SearchViewStore repository = RequiredStore();
        StoredSearchView? view = await repository.GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (view == null)
            return null;
        ValidateExcludedIdentities(view.SourceJobId, request.ExcludedFileRefs,
            request.ExcludedFileIdentities);
        await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
        bool directories = view.ProjectionKind is "GenericDirectories" or "AlbumDirectories";
        if ((request.ExcludedFileRefs is { Count: > 0 } || request.IncludedFileRefs is { Count: > 0 })
            && !directories)
            throw new ArgumentException("File exceptions require a directory projection.");
        if (request.ChosenOptions is { Count: > 0 }
            && view.ProjectionKind is not ("AggregateTracks" or "AggregateAlbums"))
            throw new ArgumentException("Chosen options require an aggregate projection.");
        if (request.AlbumFileSelections is { Count: > 0 }
            && view.ProjectionKind != "AggregateAlbums")
            throw new ArgumentException("Album file selections require an aggregate-album projection.");
        var directoryPathRules = GroupDirectoryPathRules(request.DirectoryPathRules);
        if (directoryPathRules.Count > 0 && view.ProjectionKind != "GenericDirectories")
            throw new ArgumentException("Directory path rules require a File Search projection.");
        if (directoryPathRules.Count > 0)
        {
            // The ordinary selection provides the cheap indexed baseline. Only
            // roots with path overrides need their revision-bound memberships
            // streamed; no child refs are materialized in the client or here.
            CountSearchViewSelectionResponseDto baseline = await CountSelectionWithoutPathRulesAsync(
                repository, view, viewId, request, directories, cancellationToken)
                .ConfigureAwait(false);
            var directoryRefs = new HashSet<string>(directoryPathRules.Keys, StringComparer.Ordinal);
            var foundDirectories = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, view.ProjectionKind, "Only", directoryRefs, cancellationToken)
                .ConfigureAwait(false))
                if (item.Directory != null) foundDirectories.Add(item.Ref);
            var topRefs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
            var includedRefs = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
            var excludedRefs = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
            long adjustedPublic = baseline.PublicFileCount;
            long adjustedLocked = baseline.LockedFileCount;
            long adjustedItems = baseline.SelectedItemCount;
            foreach (var (directoryRef, rules) in directoryPathRules)
            {
                if (!foundDirectories.Contains(directoryRef)) continue;
                bool rootSelected = request.Selection.Mode == RefSelectionMode.Only
                    ? topRefs.Contains(directoryRef) : !topRefs.Contains(directoryRef);
                bool anyAfter = false;
                long individuallyIncludedBefore = 0;
                await foreach (var file in repository.ReadDirectoryFileMembershipsAsync(
                    viewId, directoryRef, request.Revision, cancellationToken).ConfigureAwait(false))
                {
                    bool before = (rootSelected && !excludedRefs.Contains(file.Ref))
                        || includedRefs.Contains(file.Ref);
                    bool after = (IsSelectedDirectoryFile(file.RelativePath, rootSelected, rules)
                        && !excludedRefs.Contains(file.Ref)) || includedRefs.Contains(file.Ref);
                    if (!rootSelected && before) individuallyIncludedBefore++;
                    anyAfter |= after;
                    if (before == after) continue;
                    long delta = after ? 1 : -1;
                    if (file.Visibility == SearchResultVisibility.Locked) adjustedLocked += delta;
                    else adjustedPublic += delta;
                }
                if (!rootSelected)
                    adjustedItems += (anyAfter ? 1 : 0) - individuallyIncludedBefore;
                else if (!anyAfter)
                    adjustedItems--;
            }
            if (adjustedPublic < 0 || adjustedLocked < 0 || adjustedItems < 0)
                throw new InvalidDataException("The selected file counts are inconsistent with this revision.");
            await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
            return new(request.Revision, adjustedPublic, adjustedLocked, adjustedItems);
        }
        var counts = await CountSelectionWithoutPathRulesAsync(
            repository, view, viewId, request, directories, cancellationToken).ConfigureAwait(false);
        await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
        return counts;
    }

    private static async Task RequireRevisionAsync(
        SearchViewStore repository, Guid viewId, long revision, CancellationToken cancellationToken)
    {
        if (await repository.GetRevisionAsync(viewId, revision, cancellationToken).ConfigureAwait(false) == null)
            throw new InvalidOperationException("The search-view revision is stale or unavailable.");
    }

    private async Task<CountSearchViewSelectionResponseDto> CountSelectionWithoutPathRulesAsync(
        SearchViewStore repository,
        StoredSearchView view,
        Guid viewId,
        CountSearchViewSelectionRequestDto request,
        bool directories,
        CancellationToken cancellationToken)
    {
        StoredSearchViewChosenOptions? resolvedChoices = request.ChosenOptions is { Count: > 0 }
            ? await repository.ResolveChosenOptionsAsync(
                viewId, request.Revision, request.ChosenOptions, cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;

        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var included = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
        var excluded = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedChoices = new HashSet<string>(request.ChosenOptions?.Keys ?? [], StringComparer.Ordinal);
        var unresolvedAlbumSelections = new HashSet<string>(request.AlbumFileSelections?.Keys ?? [], StringComparer.Ordinal);
        if (unresolvedChoices.Concat(unresolvedAlbumSelections).Any(groupRef =>
                request.Selection.Mode == RefSelectionMode.Only
                    ? !refs.Contains(groupRef) : refs.Contains(groupRef)))
            throw new ArgumentException("An aggregate choice must belong to a selected group.");
        long publicCount = 0;
        long lockedCount = 0;
        long selectedItemCount = 0;

        bool IsSelectedTopLevel(string itemRef)
            => request.Selection.Mode == RefSelectionMode.Only
                ? refs.Contains(itemRef) : !refs.Contains(itemRef);

        async Task<(long Public, long Locked)> CountDirectoryAsync(StoredSearchViewDirectory directory)
        {
            if (directory.RetrievedFileCount == null)
                return (directory.PublicMatchingFileCount, directory.LockedMatchingFileCount);
            StoredSearchViewSelectionCounts observed = await repository.CountObservedDirectoryFilesAsync(
                viewId, request.Revision, directory.Ref, cancellationToken).ConfigureAwait(false);
            return (observed.PublicFileCount, observed.LockedFileCount);
        }

        async Task<(long Public, long Locked)> CountItemAsync(StoredSearchViewCommitItem item)
        {
            if (item.File is { } file)
                return file.Input.Visibility == SearchResultVisibility.Locked ? (0, 1) : (1, 0);
            if (item.Directory is { } directory)
                return await CountDirectoryAsync(directory).ConfigureAwait(false);
            if (item.AggregateTrackGroup is { } track)
            {
                if (request.ChosenOptions?.ContainsKey(item.Ref) == true)
                {
                    return resolvedChoices?.Tracks.TryGetValue(item.Ref, out _) == true
                        ? (1, 0)
                        : throw new ArgumentException("A chosen track option is not public in this revision.");
                }
                return track.Representative.Input.Visibility == SearchResultVisibility.Locked ? (0, 1) : (1, 0);
            }
            if (item.AggregateAlbumGroup is not { } album)
                return (0, 0);
            StoredSearchViewDirectory selectedDirectory = album.Representative;
            if (request.ChosenOptions?.ContainsKey(item.Ref) == true)
            {
                selectedDirectory = resolvedChoices?.Albums.GetValueOrDefault(item.Ref)
                    ?? throw new ArgumentException("A chosen album option is not public in this revision.");
            }
            if (request.AlbumFileSelections?.GetValueOrDefault(item.Ref) is not { } files)
                return await CountDirectoryAsync(selectedDirectory).ConfigureAwait(false);
            var fileRefs = new HashSet<string>(files.Refs, StringComparer.Ordinal);
            var matching = (await repository.ReadDirectoryFileRefsAsync(
                viewId, request.Revision, fileRefs, cancellationToken).ConfigureAwait(false))
                .Where(info => StringComparer.Ordinal.Equals(info.DirectoryRef, selectedDirectory.Ref))
                .GroupBy(info => info.Ref, StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
            if (matching.Length != fileRefs.Count)
                throw new ArgumentException("An album file ref is not in its chosen directory and revision.");
            long selectedPublic = matching.LongCount(info => info.Visibility == SearchResultVisibility.Public);
            long selectedLocked = matching.LongLength - selectedPublic;
            (long directoryPublic, long directoryLocked) = await CountDirectoryAsync(selectedDirectory)
                .ConfigureAwait(false);
            return files.Mode switch
            {
                RefSelectionMode.Only => (selectedPublic, selectedLocked),
                RefSelectionMode.AllExcept => (
                    directoryPublic - selectedPublic,
                    directoryLocked - selectedLocked),
                _ => throw new ArgumentException("Album file selection mode is invalid."),
            };
        }

        if (request.Selection.Mode == RefSelectionMode.Only)
        {
            refs.UnionWith(included);
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, view.ProjectionKind, "Only", refs, cancellationToken)
                .ConfigureAwait(false))
            {
                if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                    || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
                    continue;
                unresolvedChoices.Remove(item.Ref);
                unresolvedAlbumSelections.Remove(item.Ref);
                if (request.ChosenOptions?.ContainsKey(item.Ref) == true
                    && item.AggregateTrackGroup == null && item.AggregateAlbumGroup == null)
                    throw new ArgumentException("A chosen option must refer to an aggregate group.");
                if (request.AlbumFileSelections?.ContainsKey(item.Ref) == true
                    && item.AggregateAlbumGroup == null)
                    throw new ArgumentException("An album file selection must refer to an aggregate-album group.");
                (long itemPublic, long itemLocked) = await CountItemAsync(item).ConfigureAwait(false);
                publicCount = checked(publicCount + itemPublic);
                lockedCount = checked(lockedCount + itemLocked);
                selectedItemCount = checked(selectedItemCount + 1);
                if (item.Directory != null || item.AggregateTrackGroup != null || item.AggregateAlbumGroup != null)
                    selectedContainers.Add(item.Ref);
            }
        }
        else
        {
            StoredSearchViewSelectionCounts baseline = await repository.CountTopLevelSelectionAsync(
                viewId, request.Revision, view.ProjectionKind, "AllExcept", refs, cancellationToken)
                .ConfigureAwait(false);
            publicCount = baseline.PublicFileCount;
            lockedCount = baseline.LockedFileCount;
            selectedItemCount = baseline.SelectedItemCount;
            var modifiedGroups = new HashSet<string>(unresolvedChoices, StringComparer.Ordinal);
            modifiedGroups.UnionWith(unresolvedAlbumSelections);
            if (modifiedGroups.Count > 0)
            {
                await foreach (var item in repository.ReadCommitItemsAsync(
                    viewId, request.Revision, view.ProjectionKind, "Only", modifiedGroups, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (!IsSelectedTopLevel(item.Ref)) continue;
                    unresolvedChoices.Remove(item.Ref);
                    unresolvedAlbumSelections.Remove(item.Ref);
                    if (request.ChosenOptions?.ContainsKey(item.Ref) == true
                        && item.AggregateTrackGroup == null && item.AggregateAlbumGroup == null)
                        throw new ArgumentException("A chosen option must refer to an aggregate group.");
                    if (request.AlbumFileSelections?.ContainsKey(item.Ref) == true
                        && item.AggregateAlbumGroup == null)
                        throw new ArgumentException("An album file selection must refer to an aggregate-album group.");
                    (long actualPublic, long actualLocked) = await CountItemAsync(item).ConfigureAwait(false);
                    var baselineItem = item.AggregateAlbumGroup is { } album
                        ? await CountDirectoryAsync(album.Representative).ConfigureAwait(false)
                        : item.AggregateTrackGroup is { } track
                            ? track.Representative.Input.Visibility == SearchResultVisibility.Locked ? (0L, 1L) : (1L, 0L)
                            : (0L, 0L);
                    publicCount = checked(publicCount + actualPublic - baselineItem.Item1);
                    lockedCount = checked(lockedCount + actualLocked - baselineItem.Item2);
                }
            }
        }
        await RejectPresentUnboundChoicesAsync(repository, viewId, request.Revision,
            view.ProjectionKind, unresolvedChoices, unresolvedAlbumSelections, cancellationToken)
            .ConfigureAwait(false);

        if (directories && (excluded.Count > 0 || included.Count > 0))
        {
            var exceptionRefs = new HashSet<string>(excluded, StringComparer.Ordinal);
            exceptionRefs.UnionWith(included);
            var details = await repository.ReadDirectoryFileRefsAsync(
                viewId, request.Revision, exceptionRefs, cancellationToken).ConfigureAwait(false);
            var found = new HashSet<string>(details.Select(info => info.Ref), StringComparer.Ordinal);
            if (exceptionRefs.Any(fileRef => !found.Contains(fileRef)
                && excluded.Contains(fileRef)
                && request.ExcludedFileIdentities?.ContainsKey(fileRef) != true))
                throw new ArgumentException("A file exception ref is not available in this revision.");
            foreach (var group in details.GroupBy(info => info.Ref, StringComparer.Ordinal))
            {
                var info = group.First();
                bool parentSelected = group.Any(mapping => IsSelectedTopLevel(mapping.DirectoryRef));
                if (excluded.Contains(info.Ref) && parentSelected)
                {
                    if (info.Visibility == SearchResultVisibility.Locked) lockedCount--;
                    else publicCount--;
                }
                else if (request.Selection.Mode == RefSelectionMode.AllExcept
                    && included.Contains(info.Ref) && !parentSelected)
                {
                    if (info.Visibility == SearchResultVisibility.Locked) lockedCount++;
                    else publicCount++;
                    selectedItemCount++;
                }
            }
        }
        if (publicCount < 0 || lockedCount < 0)
            throw new InvalidDataException("The selected file counts are inconsistent with this revision.");
        return new CountSearchViewSelectionResponseDto(request.Revision, publicCount, lockedCount,
            selectedItemCount);
    }

    private async Task<CommitSearchViewSelectionResponseDto> CommitSelectionCoreAsync(
        Guid viewId,
        StoredSearchView sourceView,
        CommitSearchViewSelectionRequestDto request,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        SearchViewStore repository = RequiredStore();
        var started = Stopwatch.StartNew();
        await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
        StoredSearchViewChosenOptions? resolvedChoices = request.ChosenOptions is { Count: > 0 }
            ? await repository.ResolveChosenOptionsAsync(
                viewId, request.Revision, request.ChosenOptions, cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;
        long choiceMs = started.ElapsedMilliseconds;

        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var directoryPathRules = GroupDirectoryPathRules(request.DirectoryPathRules);
        var directoryRuleRefs = new HashSet<string>(directoryPathRules.Keys, StringComparer.Ordinal);
        long missingDirectoryRuleCount = 0;
        if (directoryRuleRefs.Count > 0)
        {
            var foundDirectoryRuleRefs = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, sourceView.ProjectionKind,
                "Only", directoryRuleRefs, cancellationToken).ConfigureAwait(false))
                if (item.Directory != null) foundDirectoryRuleRefs.Add(item.Ref);
            missingDirectoryRuleCount = directoryPathRules.LongCount(pair =>
                !foundDirectoryRuleRefs.Contains(pair.Key)
                && pair.Value.Any(rule => rule.Selected)
                && (request.Selection.Mode != RefSelectionMode.Only
                    || !request.Selection.Refs.Contains(pair.Key)));
        }
        var includedRefs = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
        var includedItems = new List<StoredSearchViewCommitItem>();
        long missingIncludedCount = 0;
        if (includedRefs.Count > 0)
        {
            var unresolved = new HashSet<string>(includedRefs, StringComparer.Ordinal);
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, sourceView.ProjectionKind, "Only", includedRefs, cancellationToken))
            {
                if (item.File is null) continue;
                unresolved.Remove(item.Ref);
                includedItems.Add(item);
            }
            missingIncludedCount = unresolved.Count;
        }
        if (request.Selection.Mode == RefSelectionMode.Only)
            refs.UnionWith(includedRefs);
        var exclusionRefs = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
        if (request.Selection.Mode == RefSelectionMode.AllExcept)
            exclusionRefs.UnionWith(refs);
        var excludedFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string fileRef in request.ExcludedFileRefs ?? [])
        {
            if (request.ExcludedFileIdentities?.GetValueOrDefault(fileRef) is not { } identity)
                continue;
            if (!excludedFiles.TryGetValue(identity.Username, out var names))
                excludedFiles.Add(identity.Username, names = new(StringComparer.Ordinal));
            names.Add(identity.Filename);
        }
        var unresolvedExclusions = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
        if (exclusionRefs.Count > 0
            && sourceView.ProjectionKind is "GenericDirectories" or "AlbumDirectories")
        {
            await foreach (var excluded in repository.ReadCommitItemsAsync(
                viewId, request.Revision, sourceView.ProjectionKind, "Only", exclusionRefs, cancellationToken))
            {
                if (excluded.File is not { } file) continue;
                unresolvedExclusions.Remove(excluded.Ref);
                var target = file.Input.ToFileCandidate().Target;
                if (!excludedFiles.TryGetValue(target.Username, out var names))
                    excludedFiles.Add(target.Username, names = new(StringComparer.Ordinal));
                names.Add(target.Filename);
            }
        }
        if (unresolvedExclusions.Any(fileRef =>
                request.ExcludedFileIdentities?.ContainsKey(fileRef) != true))
            throw new ArgumentException("An excluded file ref is not available in this Search View revision.");
        var foundRefs = request.Selection.Mode == RefSelectionMode.Only
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        long requested = 0;
        long resolved = 0;
        long submitted = 0;
        long skipped = 0;
        long rejected = 0;
        long planMs = 0;
        var reasons = new Dictionary<(string Reason, string? Message), long>();
        var unboundChoices = new HashSet<string>(request.ChosenOptions?.Keys ?? [], StringComparer.Ordinal);
        var unboundAlbumSelections = new HashSet<string>(request.AlbumFileSelections?.Keys ?? [], StringComparer.Ordinal);
        // This list only owns the accepted batch. A name would also become an
        // output scope for music children, leaking an internal label into paths.
        var root = new JobList();
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        async IAsyncEnumerable<StoredSearchViewCommitItem> ReadSelectedItems()
        {
            var emittedRuleRoots = new HashSet<string>(StringComparer.Ordinal);
            if (directoryRuleRefs.Count > 0)
            {
                await foreach (var item in repository.ReadCommitItemsAsync(
                    viewId, request.Revision, sourceView.ProjectionKind,
                    "Only", directoryRuleRefs, cancellationToken))
                {
                    if (item.Directory == null) continue;
                    bool rootSelected = request.Selection.Mode == RefSelectionMode.Only
                        ? request.Selection.Refs.Contains(item.Ref) : !request.Selection.Refs.Contains(item.Ref);
                    if (rootSelected || directoryPathRules[item.Ref].Any(rule => rule.Selected))
                    {
                        emittedRuleRoots.Add(item.Ref);
                        yield return item;
                    }
                }
            }
            await foreach (var item in repository.ReadCommitItemsAsync(
                viewId, request.Revision, sourceView.ProjectionKind,
                request.Selection.Mode.ToString(), refs, cancellationToken))
                if (!emittedRuleRoots.Contains(item.Ref)) yield return item;
            if (request.Selection.Mode == RefSelectionMode.AllExcept)
                foreach (var item in includedItems)
                    yield return item;
        }
        await foreach (StoredSearchViewCommitItem item in ReadSelectedItems().ConfigureAwait(false))
        {
            requested = checked(requested + 1);
            resolved = checked(resolved + 1);
            foundRefs?.Add(item.Ref);
            if (unboundAlbumSelections.Remove(item.Ref) && item.AggregateAlbumGroup is null)
                throw new ArgumentException("An album file selection must refer to an aggregate-album group.");
            if (unboundChoices.Remove(item.Ref)
                && item.AggregateTrackGroup is null && item.AggregateAlbumGroup is null)
                throw new ArgumentException("A chosen option must refer to an aggregate group.");
            if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
            {
                skipped = checked(skipped + 1);
                continue;
            }
            try
            {
                long planStartMs = started.ElapsedMilliseconds;
                (Job? job, string? rejectionReason) = await ResolveSelectionJobAsync(
                    repository,
                    viewId,
                    request.Revision,
                    sourceView.ProjectionKind,
                    item,
                    excludedFiles,
                    request.IncludedFileRefs,
                    directoryPathRules.GetValueOrDefault(item.Ref),
                    request.Selection.Mode == RefSelectionMode.Only
                        ? request.Selection.Refs.Contains(item.Ref) : !request.Selection.Refs.Contains(item.Ref),
                    request.ChosenOptions?.GetValueOrDefault(item.Ref),
                    resolvedChoices?.Tracks.GetValueOrDefault(item.Ref),
                    resolvedChoices?.Albums.GetValueOrDefault(item.Ref),
                    request.AlbumFileSelections?.GetValueOrDefault(item.Ref),
                    request.UseRepresentativeWhenUnchosen == true,
                    cancellationToken).ConfigureAwait(false);
                planMs += started.ElapsedMilliseconds - planStartMs;
                if (job == null)
                {
                    rejected = checked(rejected + 1);
                    AddReason(reasons, rejectionReason ?? "unavailable-item");
                    continue;
                }
                DownloadSettings settings = supervisor.PrepareSearchViewSelectionJob(
                    job,
                    OptionsForSelectionJob(request.Options, sourceView.ProjectionKind, job),
                    allowSearchViewAlbumFolderOption: sourceView.ProjectionKind == "AlbumDirectories"
                        && job is RemoteDirectoryJob);
                if (sourceView.ProjectionKind == "AlbumDirectories"
                    && item.Directory is { } selectedDirectory
                    && job is RemoteDirectoryJob selectedJob
                    && settings.Search.NoBrowseFolder)
                {
                    var observed = new List<PeerFileTarget>();
                    await foreach (StoredSearchViewDirectoryFile child in repository
                        .ReadPublicDirectoryFilesAsync(
                            viewId,
                            selectedDirectory.Ref,
                            request.Revision,
                            cancellationToken).ConfigureAwait(false))
                        observed.Add(child.File.Input.ToFileCandidate().Target);
                    IReadOnlySet<string>? excluded = excludedFiles.GetValueOrDefault(
                        selectedDirectory.Username);
                    if (observed.All(target => excluded?.Contains(target.Filename) == true))
                    {
                        skipped = checked(skipped + 1);
                        AddReason(reasons, "empty-selection");
                        continue;
                    }
                    var identity = new PeerDirectoryIdentity(
                        selectedDirectory.Username,
                        selectedDirectory.FolderPath);
                    var snapshot = new PeerDirectorySnapshot(identity, observed, isComplete: false);
                    selectedJob.UseObservedSelection(DirectoryTransferPlanner.FromObservedSelection(
                        snapshot,
                        excluded));
                }
                root.Add(job);
                submitted = checked(submitted + 1);
                if (item.Directory != null
                    || item.AggregateTrackGroup != null
                    || item.AggregateAlbumGroup != null)
                    selectedContainers.Add(item.Ref);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                rejected = checked(rejected + 1);
                // Preserve the actionable error without failing independent
                // selections. Equal explanations share one counted reason.
                AddReason(reasons, "invalid-item", message: exception.Message);
            }
        }

        await RejectPresentUnboundChoicesAsync(repository, viewId, request.Revision,
            sourceView.ProjectionKind, unboundChoices, unboundAlbumSelections, cancellationToken)
            .ConfigureAwait(false);

        if (foundRefs != null)
        {
            long missing = refs.Count - foundRefs.Count;
            if (missing > 0)
            {
                requested = checked(requested + missing);
                rejected = checked(rejected + missing);
                AddReason(reasons, "missing-ref", missing);
            }
        }
        else if (missingIncludedCount > 0)
        {
            requested = checked(requested + missingIncludedCount);
            rejected = checked(rejected + missingIncludedCount);
            AddReason(reasons, "missing-ref", missingIncludedCount);
        }
        if (missingDirectoryRuleCount > 0)
        {
            requested = checked(requested + missingDirectoryRuleCount);
            rejected = checked(rejected + missingDirectoryRuleCount);
            AddReason(reasons, "missing-ref", missingDirectoryRuleCount);
        }

        // Every query above reads one immutable revision. If retention removed
        // it between queries, discard the assembled result before accepting work.
        // Once assembled, the jobs own their complete targets independently of it.
        await RequireRevisionAsync(repository, viewId, request.Revision, cancellationToken).ConfigureAwait(false);
        long resolutionMs = started.ElapsedMilliseconds;
        var receipt = new CommitSearchViewSelectionResponseDto(
            viewId,
            request.Revision,
            root.Count == 0 ? null : request.IdempotencyKey,
            root.Count == 0 ? null : root.WorkflowId,
            requested,
            resolved,
            submitted,
            skipped,
            rejected,
            reasons.OrderBy(pair => pair.Key.Reason, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Message, StringComparer.Ordinal)
                .Select(pair => new SubmissionReasonCountDto(pair.Key.Reason, pair.Value, pair.Key.Message))
                .ToArray());
        if (root.Count > 0)
        {
            await supervisor.QueueSearchViewSelectionAsync(
                root,
                sourceView.SourceJobId,
                request.IdempotencyKey,
                commitFingerprint,
                receipt,
                cancellationToken).ConfigureAwait(false);
        }
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SearchViewCommitted(
            logger,
            viewId,
            request.Revision,
            durationMs,
            requested,
            submitted,
            rejected);
        ServerLogMessages.SearchViewCommitTiming(
            logger, viewId, request.Revision,
            resolutionMs, choiceMs, planMs, durationMs - resolutionMs);
        return receipt;
    }

    private static SubmissionOptionsDto? OptionsForSelectionJob(
        SubmissionOptionsDto? options,
        string projectionKind,
        Job job)
    {
        if (projectionKind != "AlbumDirectories"
            || job is not RemoteFileJob
            || options?.DownloadSettings?.Search is not { NoBrowseFolder: not null } search)
            return options;

        // One selection may contain a whole album directory and an exact file.
        // The folder-only option composes for directory jobs, not for exact files.
        SearchSettingsPatchDto remaining = search with { NoBrowseFolder = null };
        return options with
        {
            DownloadSettings = options.DownloadSettings with
            {
                Search = remaining == new SearchSettingsPatchDto() ? null : remaining,
            },
        };
    }

    private static void ValidateExcludedIdentities(
        Guid sourceJobId,
        IReadOnlyList<string>? excludedRefs,
        IReadOnlyDictionary<string, FileCandidateRefDto>? identities)
    {
        if (identities is null)
            return;
        var refs = new HashSet<string>(excludedRefs ?? [], StringComparer.Ordinal);
        foreach ((string fileRef, FileCandidateRefDto identity) in identities)
        {
            if (identity is null || string.IsNullOrEmpty(identity.Username)
                || string.IsNullOrEmpty(identity.Filename) || !refs.Contains(fileRef)
                || !SearchViewStore.MatchesStableFileIdentity(
                    sourceJobId, fileRef, identity.Username, identity.Filename))
                throw new ArgumentException(
                    "An excluded file identity does not match a file ref from this search job.");
        }
    }

    private static async Task RejectPresentUnboundChoicesAsync(
        SearchViewStore repository,
        Guid viewId,
        long revision,
        string projectionKind,
        HashSet<string> choices,
        HashSet<string> albumSelections,
        CancellationToken cancellationToken)
    {
        if (choices.Count == 0 && albumSelections.Count == 0)
            return;
        var unbound = new HashSet<string>(choices, StringComparer.Ordinal);
        unbound.UnionWith(albumSelections);
        await foreach (StoredSearchViewCommitItem _ in repository.ReadCommitItemsAsync(
            viewId, revision, projectionKind, "Only", unbound, cancellationToken)
            .ConfigureAwait(false))
            throw new ArgumentException(
                "An option or album-file selection group is not selected in this revision.");
        // A browser can retain a choice while its group is hidden by a newer
        // projection. It is neither persisted nor applied to this revision.
    }

    private static async Task<(Job? Job, string? RejectionReason)> ResolveSelectionJobAsync(
        SearchViewStore repository,
        Guid viewId,
        long viewRevision,
        string projectionKind,
        StoredSearchViewCommitItem item,
        IReadOnlyDictionary<string, HashSet<string>> excludedFiles,
        IReadOnlyList<string>? includedFileRefs,
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? directoryPathRules,
        bool rootSelected,
        string? chosenOption,
        StoredSearchViewFile? chosenTrack,
        StoredSearchViewDirectory? chosenAlbum,
        RefSelectionExpressionDto? albumFileSelection,
        bool useRepresentativeWhenUnchosen,
        CancellationToken cancellationToken)
    {
        if (item.File is { } file)
        {
            if (file.Input.Visibility == SearchResultVisibility.Locked)
                return (null, "locked");
            return (new RemoteFileJob(file.Input.ToFileCandidate().Target), null);
        }
        if (item.Directory is { } directory)
        {
            if (directory.PublicMatchingFileCount == 0)
                return (null, "locked");
            if (projectionKind == "GenericDirectories")
            {
                // File Search selects the files projected into this exact view
                // revision. Browsing the peer's entire directory here would add
                // unobserved/nonmatching files to a reviewed selection.
                IReadOnlySet<string>? excluded = excludedFiles.GetValueOrDefault(directory.Username);
                var observed = new List<PeerFileTarget>();
                await foreach (StoredSearchViewDirectoryFile child in repository
                    .ReadPublicDirectoryFilesAsync(
                        viewId, directory.Ref, viewRevision, cancellationToken)
                    .ConfigureAwait(false))
                {
                    PeerFileTarget target = child.File.Input.ToFileCandidate().Target;
                    if ((IsSelectedDirectoryFile(child.RelativePath, rootSelected, directoryPathRules)
                            && excluded?.Contains(target.Filename) != true)
                        || includedFileRefs?.Contains(child.Ref) == true)
                        observed.Add(target);
                }
                if (observed.Count == 0)
                    return (null, "empty-selection");
                var identity = new PeerDirectoryIdentity(directory.Username, directory.FolderPath);
                var snapshot = new PeerDirectorySnapshot(identity, observed, isComplete: false);
                DirectoryTransferPlan plan = DirectoryTransferPlanner.FromObservedSelection(snapshot);
                return (new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan)), null);
            }
            return (new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                new PeerDirectoryIdentity(directory.Username, directory.FolderPath),
                excludedFiles.GetValueOrDefault(directory.Username))), null);
        }
        if (item.AggregateTrackGroup is { } aggregate)
        {
            var candidates = new List<FileCandidate>();
            if (chosenOption == null && useRepresentativeWhenUnchosen)
            {
                if (aggregate.Representative.Input.Visibility == SearchResultVisibility.Public)
                    candidates.Add(aggregate.Representative.Input.ToFileCandidate());
            }
            else if (chosenOption != null)
            {
                if (chosenTrack != null)
                    candidates.Add(chosenTrack.Input.ToFileCandidate());
            }
            else
            {
                await foreach (StoredSearchViewFile option in repository
                    .ReadPublicAggregateTrackOptionsAsync(
                        viewId,
                        aggregate.Ref,
                        viewRevision,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    candidates.Add(option.Input.ToFileCandidate());
                }
            }
            if (candidates.Count == 0)
                return (null, chosenOption == null ? "locked" : "unavailable-option");
            SongQuery query = JsonSerializer.Deserialize<SongQuery>(aggregate.QueryJson)
                ?? throw new InvalidDataException(
                    "A selected aggregate-track query is invalid.");
            return (new SongJob(query) { Candidates = candidates }, null);
        }
        if (item.AggregateAlbumGroup is { } aggregateAlbum)
        {
            if (albumFileSelection != null && chosenOption == null)
                throw new ArgumentException("Choosing album files requires an exact album option.");
            AlbumQuery query = JsonSerializer.Deserialize<AlbumQuery>(
                    aggregateAlbum.QueryJson)
                ?? throw new InvalidDataException(
                    "A selected aggregate-album query is invalid.");
            var defaultTrackQuery = new SongQuery
            {
                Artist = query.Artist,
                Album = query.Album,
                URI = query.URI,
                ArtistMaybeWrong = query.ArtistMaybeWrong,
            };
            var folders = new List<AlbumFolder>();
            async IAsyncEnumerable<StoredSearchViewDirectory> EligibleOptions()
            {
                if (chosenOption == null && useRepresentativeWhenUnchosen)
                {
                    yield return aggregateAlbum.Representative;
                    yield break;
                }
                if (chosenOption != null)
                {
                    if (chosenAlbum != null)
                        yield return chosenAlbum;
                    yield break;
                }
                await foreach (StoredSearchViewDirectory option in repository
                    .ReadPublicAggregateAlbumOptionsAsync(
                        viewId, aggregateAlbum.Ref, viewRevision, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return option;
                }
            }
            await foreach (StoredSearchViewDirectory option in EligibleOptions().ConfigureAwait(false))
            {
                var files = new List<AlbumFile>();
                var selectedFileRefs = albumFileSelection == null
                    ? null : new HashSet<string>(albumFileSelection.Refs, StringComparer.Ordinal);
                if (selectedFileRefs != null)
                {
                    // A locked child is still a valid selection in this revision;
                    // only public children may become download candidates.
                    var memberships = await repository.ReadDirectoryFileRefsAsync(
                        viewId, viewRevision, selectedFileRefs, cancellationToken).ConfigureAwait(false);
                    var matchedFileRefs = new HashSet<string>(memberships
                        .Where(member => StringComparer.Ordinal.Equals(member.DirectoryRef, option.Ref))
                        .Select(member => member.Ref), StringComparer.Ordinal);
                    if (!selectedFileRefs.SetEquals(matchedFileRefs))
                        throw new ArgumentException("An album file ref is not in the chosen directory and revision.");
                }
                await foreach (StoredSearchViewDirectoryFile child in repository
                    .ReadPublicDirectoryFilesAsync(
                        viewId,
                        option.Ref,
                        viewRevision,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (albumFileSelection != null &&
                        (albumFileSelection.Mode == RefSelectionMode.Only) != selectedFileRefs!.Contains(child.Ref))
                        continue;
                    FileCandidate candidate = child.File.Input.ToFileCandidate();
                    files.Add(AlbumFile.WithLazyQuery(
                        () => Searcher.InferSongQuery(
                            candidate.Filename,
                            defaultTrackQuery),
                        candidate));
                }
                if (files.Count == 0)
                    continue;
                folders.Add(new AlbumFolder(
                    option.Username,
                    option.FolderPath,
                    files)
                {
                    IsFullyRetrieved = option.IsFullyRetrieved,
                });
            }
            if (folders.Count == 0)
                return (null, chosenOption == null ? "locked" : "unavailable-option");
            var job = new AlbumJob(query) { Results = folders };
            if (albumFileSelection != null)
            {
                // Exact child selection must not be expanded by the album executor's
                // optional full-folder browse before transfers begin.
                job.ResolvedTarget = folders.Single();
                job.DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot;
            }
            return (job, null);
        }
        return (null, "unsupported-item");
    }

    private static void AddReason(
        IDictionary<(string Reason, string? Message), long> reasons,
        string reason,
        long amount = 1,
        string? message = null)
    {
        if (amount <= 0)
            return;
        var key = (reason, message);
        reasons[key] = reasons.TryGetValue(key, out long count)
            ? checked(count + amount)
            : amount;
    }

    private static IReadOnlyDictionary<string, SearchViewDirectoryPathRuleDto[]> GroupDirectoryPathRules(
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? rules)
    {
        if (rules is not { Count: > 0 })
            return new Dictionary<string, SearchViewDirectoryPathRuleDto[]>(StringComparer.Ordinal);
        var paths = new HashSet<(string DirectoryRef, string RelativePath)>();
        foreach (SearchViewDirectoryPathRuleDto rule in rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.DirectoryRef)
                || string.IsNullOrEmpty(rule.RelativePath) || rule.RelativePath.Contains('\\')
                || rule.RelativePath.Split('/').Any(part => part is "" or "." or "..")
                || !paths.Add((rule.DirectoryRef, rule.RelativePath)))
                throw new ArgumentException("Directory path rules must identify distinct relative subfolders.");
        }
        return rules.GroupBy(rule => rule.DirectoryRef, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(rule => rule.RelativePath.Length).ToArray(),
                StringComparer.Ordinal);
    }

    private static bool IsSelectedDirectoryFile(
        string relativePath,
        bool rootSelected,
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? rules)
    {
        if (rules == null) return rootSelected;
        string normalizedPath = relativePath.Replace('\\', '/');
        foreach (SearchViewDirectoryPathRuleDto rule in rules)
            if (normalizedPath.StartsWith(rule.RelativePath + "/", StringComparison.Ordinal))
                return rule.Selected;
        return rootSelected;
    }

    private static void ValidateCommitRequest(CommitSearchViewSelectionRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SelectionExpressionValidator.Validate(request.Selection);
        if (request.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.");
        if (request.Options?.WorkflowId != null)
            throw new ArgumentException("A Search View selection owns its workflow ID; download options cannot replace it.");
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Search View revision cannot be negative.");
        if (request.ExcludedFileRefs?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Excluded file refs cannot be empty.");
        if (request.IncludedFileRefs?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Included file refs cannot be empty.");
        if ((request.ExcludedFileRefs ?? []).Intersect(request.IncludedFileRefs ?? [], StringComparer.Ordinal).Any())
            throw new ArgumentException("A file cannot be explicitly included and excluded at the same time.");
        if (request.ChosenOptions?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) == true)
            throw new ArgumentException("Chosen group and option refs cannot be empty.");
        ValidateAlbumFileSelections(request.AlbumFileSelections);
        var selectedRefs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        if ((request.ChosenOptions?.Keys ?? []).Concat(request.AlbumFileSelections?.Keys ?? [])
            .Any(groupRef => request.Selection.Mode == RefSelectionMode.Only
                ? !selectedRefs.Contains(groupRef) : selectedRefs.Contains(groupRef)))
            throw new ArgumentException("An aggregate choice must belong to a selected group.");
    }

    private static void ValidateAlbumFileSelections(
        IReadOnlyDictionary<string, RefSelectionExpressionDto>? selections)
    {
        if (selections?.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
            || pair.Value == null
            || pair.Value.Refs == null
            || pair.Value.Mode is not (RefSelectionMode.Only or RefSelectionMode.AllExcept)
            || pair.Value.Refs.Any(string.IsNullOrWhiteSpace)
            || (pair.Value.Mode == RefSelectionMode.Only && pair.Value.Refs.Count == 0)) == true)
            throw new ArgumentException("Album file selection group, mode, or file refs are invalid.");
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid viewId = item.ViewId;
                using var preparation = item.Preparation;
                // Explicit reads request one batch. An observation requests catch-up
                // until idle; queued observation work is discarded after cancellation.
                pending.TryRemove(viewId, out _);
                bool explicitlyRequested = requested.TryRemove(viewId, out _);
                if (!explicitlyRequested && !IsObserved(viewId)) continue;
                ReleaseIdleKernels();
                RuntimeView? runtime = null;
                try
                {
                    runtime = runtimes.GetValueOrDefault(viewId)
                        ?? await RestoreRequestedAsync(viewId, cancellationToken).ConfigureAwait(false);
                    // A queued wake-up may outlive the first publication it protected.
                    if (runtime != null && Volatile.Read(ref runtime.PublishedRevision) > 0) preparation?.Dispose();
                    if (runtime != null) await ProcessAsync(runtime, explicitlyRequested, cancellationToken).ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    if (runtime != null) await FailAsync(runtime, exception, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        ServerLogMessages.SearchViewRecoveryFailed(logger, exception, viewId);
                        var stored = await RequiredStore().GetAsync(viewId, cancellationToken).ConfigureAwait(false);
                        if (stored != null) await CompleteUnrestorableAsync(RequiredStore(), stored, cancellationToken).ConfigureAwait(false);
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
            while (work.Reader.TryRead(out var item)) item.Preparation?.Dispose();
        }
    }

    private void ReleaseIdleKernels()
    {
        DateTimeOffset now = clock.GetUtcNow();
        // Only the projection worker uses these working kernels. Keep published
        // rows on disk and lightweight definitions here, not idle result graphs.
        foreach (RuntimeView runtime in runtimes.Values)
        {
            if (runtime.ExpiresAtUtc <= now)
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
    }

    private void RequestAdvance(StoredSearchView view)
    {
        if (NeedsAdvance(view)) Schedule(view.Id);
    }

    private static bool NeedsAdvance(StoredSearchView view)
        => !view.IsComplete;

    private async Task ProcessAsync(
        RuntimeView runtime,
        bool explicitlyRequested,
        CancellationToken cancellationToken)
    {
        runtime.LastUsedAt = clock.GetUtcNow();
        if (runtime.Kernel == null)
        {
            StoredSearchView? stored = await RequiredStore().GetAsync(runtime.ViewId, cancellationToken).ConfigureAwait(false);
            if (stored == null || stored.ExpiresAtUtc <= clock.GetUtcNow())
            {
                runtimes.TryRemove(runtime.ViewId, out _);
                runtime.PublicationTimer?.Dispose();
                return;
            }
            runtime.Kernel = await RebuildKernelAsync(runtime.Source, runtime.Projection,
                runtime.Settings, stored, cancellationToken, runtime.UserSuccessCounts).ConfigureAwait(false);
        }
        SearchViewKernel kernel = runtime.Kernel;
        // Catch-up never depends on a client poll. Sparse live arrivals, however,
        // should not create a transaction and rewrite group indexes per response.
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
        // Publish the first small page promptly, then amortize durable
        // commits when a burst has already arrived. Reads remain bounded
        // and a sparse live stream still publishes without waiting to fill.
        bool initialPublication = kernel.ConsumedSequence == 0;
        int batchSize = initialPublication
            ? Math.Min(InitialProjectionBatchSize, ProjectionBatchSize) : ProjectionBatchSize;
        using var preparation = ProjectionActivities.StartActivity("advance");
        preparation?.SetTag("view.id", runtime.ViewId);
        preparation?.SetTag("initial", initialPublication);
        SourceBatch batch;
        using (ProjectionActivities.StartActivity("source-read"))
            batch = await runtime.Source.ReadAsync(
                kernel.ConsumedSequence, batchSize, cancellationToken).ConfigureAwait(false);
        preparation?.SetTag("input.count", batch.Items.Count);
        bool complete = batch.IsComplete && batch.Items.Count < batchSize;
        if (batch.Items.Count == 0 && !complete)
        {
            runtime.CatchingUp = false;
            if (runtime.UnpublishedUsernames.Count == 0) return;
        }
        SearchViewKernelUpdate update;
        using (ProjectionActivities.StartActivity("kernel"))
            update = kernel.Apply(batch.Items, batch.SourceRevision, complete);
        // Projection time excludes the separately recorded queue/SQL publication.
        runtime.ProjectionDuration += Stopwatch.GetElapsedTime(projectionStarted);
        runtime.CatchingUp = batch.Items.Count == batchSize;
        // Filtered arrivals and not-yet-eligible groups change only counters.
        // Keep consuming them, but do not fsync a revision per source batch.
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
        StoredSearchView published = await RequiredStore().PublishAsync(
            runtime.ViewId, update, batch.RetentionState, cancellationToken,
            interactive: initialPublication).ConfigureAwait(false);
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

    private async Task FailAsync(
        RuntimeView runtime,
        Exception exception,
        CancellationToken cancellationToken)
    {
        runtimes.TryRemove(runtime.ViewId, out _);
        runtime.PublicationTimer?.Dispose();
        ServerLogMessages.SearchViewProjectionFailed(logger, exception, runtime.ViewId);
        try
        {
            // The failed batch was rolled back. Finish from the last durable
            // prefix, never retry its unavailable facts as a terminal revision.
            StoredSearchView current = await RequiredStore().GetAsync(
                runtime.ViewId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The search view was not found.");
            var terminal = new SearchViewKernelUpdate(
                current.SourceRevision, current.ConsumedSequence, true, current.Counters, []);
            StoredSearchView published = await RequiredStore().PublishAsync(
                runtime.ViewId,
                terminal,
                "Incomplete",
                cancellationToken).ConfigureAwait(false);
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

    private void NotifyPublished(StoredSearchView view)
    {
        foreach (Action<StoredSearchView> observer in Delegate.EnumerateInvocationList(ViewPublished))
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
        // Commits schedule work synchronously. Register preparation before the
        // worker runs, without taking the mutable kernel's lock on the writer.
        IDisposable? preparation = runtimes.TryGetValue(viewId, out var runtime)
            && Volatile.Read(ref runtime.PublishedRevision) == 0
                ? RequiredStore().PrioritizeInitialPublication() : null;
        if (!work.Writer.TryWrite((viewId, preparation)))
        {
            preparation?.Dispose();
            pending.TryRemove(viewId, out _);
        }
    }

    private async Task<RuntimeView?> RestoreRequestedAsync(Guid viewId, CancellationToken cancellationToken)
    {
        SearchViewStore repository = RequiredStore();
        StoredSearchView? stored = await repository.GetAsync(viewId, cancellationToken).ConfigureAwait(false);
        if (stored == null || stored.ExpiresAtUtc <= clock.GetUtcNow()) return null;
        if (stored.IsComplete) return null;
        (SearchDefinition definition, SearchViewProjectionDefinition projection,
            SearchSettingsPatchDto? refinementSettings) = ParseDurableDefinition(stored.DefinitionJson);
        RuntimeSource? source = await CreateStoredSourceAsync(stored.SourceJobId, definition, projection, cancellationToken)
            .ConfigureAwait(false);
        if (source == null)
        {
            await CompleteUnrestorableAsync(repository, stored, cancellationToken).ConfigureAwait(false);
            return null;
        }
        SearchSettings settings = definition.ProjectionSettings.ToSettings();
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, refinementSettings);
        IReadOnlyDictionary<string, int> reputation = supervisor.GetSearchViewSource(stored.SourceJobId)?.UserSuccessCounts
            ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var runtime = new RuntimeView(stored, source, projection, settings, reputation, clock.GetUtcNow());
        runtimes.TryAdd(stored.Id, runtime);
        return runtime;
    }

    private async Task CompleteUnrestorableAsync(
        SearchViewStore repository,
        StoredSearchView stored,
        CancellationToken cancellationToken)
    {
        try
        {
            var terminal = new SearchViewKernelUpdate(
                stored.SourceRevision,
                stored.ConsumedSequence,
                true,
                stored.Counters,
                []);
            StoredSearchView published = await repository.PublishAsync(
                stored.Id,
                terminal,
                "Incomplete",
                cancellationToken).ConfigureAwait(false);
            NotifyPublished(published);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServerLogMessages.SearchViewRecoveryOutcomeFailed(
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

    private static (SearchDefinition Definition, SearchViewProjectionDefinition Projection,
        SearchSettingsPatchDto? RefinementSettings)
        ParseDurableDefinition(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string definitionJson = root.GetProperty("searchDefinitionJson").GetString()
            ?? throw new InvalidDataException("The search-view definition is missing.");
        CreateSearchViewRequestDto projectionDto =
            root.GetProperty("projection").Deserialize<CreateSearchViewRequestDto>(
                SockseekApiJson.CreateSerializerOptions())
            ?? throw new InvalidDataException("The search-view projection is missing.");
        ValidateRefinementSettings(projectionDto.RefinementSettings);
        SearchDefinition definition = SearchDefinitionCodec.Deserialize(definitionJson);
        SearchViewProjectionDefinition projection = ResolveProjection(
            definition,
            projectionDto);
        return (definition, projection, projectionDto.RefinementSettings);
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
            || settings.IsAggregate != null)
        {
            throw new ArgumentException(
                "Search View refinements may only change result conditions, ranking, and aggregate grouping.",
                nameof(settings));
        }
    }

    private async Task<SearchViewKernel> RebuildKernelAsync(
        RuntimeSource source,
        SearchViewProjectionDefinition projection,
        SearchSettings settings,
        StoredSearchView stored,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, int>? reputation = null)
    {
        var kernel = new SearchViewKernel(
            projection,
            settings,
            reputation,
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
            SearchDefaultProjectionKind.Track => definition.AggregatePresentation
                ? SearchViewProjectionKind.AggregateTracks : SearchViewProjectionKind.Files,
            SearchDefaultProjectionKind.Album => definition.AggregatePresentation
                ? SearchViewProjectionKind.AggregateAlbums : SearchViewProjectionKind.AlbumDirectories,
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
            or SearchViewProjectionKind.AggregateTracks)
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
                or SearchViewProjectionKind.AggregateAlbums,
            requireFileSatisfies: projection.Kind == SearchViewProjectionKind.GenericDirectories
                && !projection.IncludeFullResults,
            ignoreStringSortConditions:
                projection.Kind == SearchViewProjectionKind.AggregateAlbums,
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

    private SearchViewStore RequiredStore()
        => store ?? throw new SearchViewUnavailableException(
            "Search Views are unavailable; search execution remains available.",
            initializationFailure);

    private static SearchViewSummaryDto ToDto(StoredSearchView view)
        => new(
            view.Id,
            view.SourceJobId,
            Enum.Parse<ServerSearchViewProjectionKind>(view.ProjectionKind),
            view.CreatedAtUtc,
            view.ExpiresAtUtc,
            view.Revision,
            view.SourceRevision,
            view.ConsumedSequence,
            view.IsComplete,
            ParseRetention(view.RetentionState),
            ToDto(view.Counters),
            view.SortOrder.ToServer());

    private static SearchViewRevisionDto ToDto(StoredSearchViewRevision revision)
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

    private static SearchViewFileDto ToDto(StoredSearchViewFile row)
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

    private static SearchViewDirectoryDto ToDto(StoredSearchViewDirectory row)
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
        StoredSearchViewDirectory row,
        IReadOnlyDictionary<string, StoredSearchViewDirectoryFilePreview> previews)
    {
        SearchViewDirectoryDto directory = ToDto(row);
        if (!previews.TryGetValue(row.Ref, out StoredSearchViewDirectoryFilePreview? preview))
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

    private static SearchViewAggregateTrackGroupDto ToDto(
        StoredSearchViewAggregateTrackGroup row)
    {
        SongQuery query = JsonSerializer.Deserialize<SongQuery>(row.QueryJson)
            ?? throw new InvalidDataException(
                "A retained aggregate-track query is invalid.");
        return new(
            row.Ref,
            ServerSnapshotMapper.ToSongQueryDto(query),
            row.ShareCount,
            row.SelectableOptionCount,
            ToDto(row.Representative));
    }

    private static SearchViewAggregateAlbumGroupDto ToDto(
        StoredSearchViewAggregateAlbumGroup row)
    {
        AlbumQuery query = JsonSerializer.Deserialize<AlbumQuery>(row.QueryJson)
            ?? throw new InvalidDataException(
                "A retained aggregate-album query is invalid.");
        return new(
            row.Ref,
            ServerSnapshotMapper.ToAlbumQueryDto(query),
            row.ShareCount,
            row.SelectableOptionCount,
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
        StoredSearchView stored, RuntimeSource source, SearchViewProjectionDefinition projection,
        SearchSettings settings, IReadOnlyDictionary<string, int> reputation,
        DateTimeOffset lastUsedAt, SearchViewKernel? kernel = null)
    {
        public Guid ViewId { get; } = stored.Id;
        public DateTimeOffset ExpiresAtUtc { get; } = stored.ExpiresAtUtc;
        public RuntimeSource Source { get; } = source;
        public SearchViewProjectionDefinition Projection { get; } = projection;
        public SearchSettings Settings { get; } = settings;
        public IReadOnlyDictionary<string, int> UserSuccessCounts { get; } = reputation;
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
        public override void ReleaseLiveInputs() => Interlocked.Exchange(ref liveInputs, null);

        public override bool IsSparseAndLive(long afterSequence, int batchSize)
        {
            SearchSession? session = ObservationSession(jobId, supervisor, persistence);
            return session != null && !session.IsComplete
                && (long)session.PublicFileCount + session.LockedFileCount - afterSequence < batchSize;
        }

        public override async Task<SourceBatch> ReadAsync(long afterSequence, int limit, CancellationToken cancellationToken)
        {
            ISearchHistoryReader reader = persistence.SearchHistory
                ?? throw new KeyNotFoundException("The search observation source is unavailable.");
            // Hold the current immutable source only for this batch. Validate
            // exact committed identities without rehydrating copies of its facts.
            // Capture ownership before SQL: retirement during the snapshot must
            // not make that older snapshot look terminal.
            SearchSession? session = Volatile.Read(ref liveInputs) ?? ObservationSession(jobId, supervisor, persistence);
            // Capture failure BEFORE the SQL snapshot. Reading it afterwards could
            // incorrectly finish an older snapshot while preceding rows commit.
            // The source queue orders completion after its admitted raw batches.
            bool completionLost = persistence.IsSearchCompletionLost(jobId);
            SourceBatch ReadBatch(PersistedSearchMetadata metadata, IReadOnlyList<SearchProjectionInput> inputs, long? next)
                => ToSourceBatch(metadata, inputs, next, canReceiveInputs: session != null,
                    completionLost: completionLost && !metadata.IsComplete);
            if (session != null)
            {
                Interlocked.CompareExchange(ref liveInputs, session, null);
                PersistedSearchObservationPage? keys = await reader.GetObservationKeysAsync(
                    jobId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
                if (keys == null) return new([], 0, completionLost, completionLost ? "Incomplete" : "Live");
                if (keys.Items.Count == 0)
                    return ReadBatch(keys.Metadata, [], keys.NextSequence);
                var inputs = session.RawSnapshot(afterSequence, keys.Items.Count)
                    .Select(row => row.ProjectionInput).ToArray();
                bool matches = inputs.Length == keys.Items.Count;
                for (int i = 0; matches && i < inputs.Length; i++)
                    matches = (inputs[i].Sequence, inputs[i].Revision) == keys.Items[i];
                if (matches)
                    return ReadBatch(keys.Metadata, inputs, keys.NextSequence);
                // A lost prefix or independent retention must preserve every
                // actually committed row, rather than guessing a numeric frontier.
            }
            PersistedSearchResultPage? page = await reader.GetRawResultsAsync(
                jobId, afterSequence, limit, cancellationToken).ConfigureAwait(false);
            if (page != null) return ReadBatch(page.Metadata, page.Items, page.NextSequence);
            throw new KeyNotFoundException("The retained search source was not found.");
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
        // Live and retained Views consume this same committed representation.
        PersistedSearchResultPage page = await reader.GetRawResultsAsync(
            metadata.JobId, afterSequence, limit, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The retained search source was not found.");
        return ToSourceBatch(page, canReceiveInputs: false);
    }

    private static SourceBatch ToSourceBatch(PersistedSearchResultPage page, bool canReceiveInputs)
        => ToSourceBatch(page.Metadata, page.Items,
            page.NextSequence, canReceiveInputs);

    private static SourceBatch ToSourceBatch(PersistedSearchMetadata metadata,
        IReadOnlyList<SearchProjectionInput> inputs, long? nextSequence, bool canReceiveInputs, bool completionLost = false)
        => new(inputs, checked((int)metadata.Revision),
            nextSequence == null && (completionLost || !canReceiveInputs || IsTerminalSource(metadata)),
            completionLost ? "Incomplete"
                : !canReceiveInputs || IsTerminalSource(metadata) ? metadata.ResultPersistenceState : "Live");

    private static bool IsTerminalSource(PersistedSearchMetadata metadata)
        => metadata.IsComplete || metadata.ResultPersistenceState is "Interrupted" or "Pruned";

    internal sealed record SourceBatch(
        IReadOnlyList<SearchProjectionInput> Items,
        int SourceRevision,
        bool IsComplete,
        string RetentionState);

    public ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        foreach (RuntimeView runtime in runtimes.Values) runtime.PublicationTimer?.Dispose();
        runtimes.Clear();
        store = null;
        return ValueTask.CompletedTask;
    }
}
