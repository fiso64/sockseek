using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Sockseek.Server.Persistence;
using Sockseek.Server.Planning;
using Sockseek.Persistence.Read;
using Soulseek;

namespace Tests.Server;

[TestClass]
public sealed class SearchViewCoordinatorTests
{
    [TestMethod]
    public async Task LiveViewKeepsAvailableInputsAndRestartUsesOnlyRetainedFacts()
    {
        string directory = System.IO.Directory.CreateTempSubdirectory("sockseek-input-gap-").FullName;
        var client = ControllableSearchClientProxy.Create();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, ct) =>
        {
            receive(Response("Peer", Enumerable.Range(1, 3).Select(i => File($@"Music\Track-{i}.flac")).ToArray()));
            await finish.Task.WaitAsync(ct);
        };
        await using WebApplication app = global::Tests.TestServerHost.Build([], Options(directory, client.Client), "http://127.0.0.1:0");
        try
        {
            await app.StartAsync();
            var persistence = app.Services.GetRequiredService<PersistenceCoordinator>();
            var committed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Committed(Guid id) => committed.TrySetResult(id);
            persistence.SearchSourceChanged += Committed;
            JobSummaryDto source;
            try
            {
                source = await app.Services.GetRequiredService<JobSubmissions>()
                    .SubmitSearchJobAsync(new("Track"), CancellationToken.None);
                Assert.AreEqual(source.JobId, await committed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { persistence.SearchSourceChanged -= Committed; }
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { Pooling = false, DataSource = Path.Combine(directory, "sockseek.db") }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Model a lost durable middle row while Core still owns all three.
            command.CommandText = """
                DELETE FROM search_observations WHERE source_sequence=2 AND source_id=(
                    SELECT id FROM search_observation_sources WHERE search_job_id=$source);
                UPDATE search_jobs SET is_complete=1,result_persistence_state='Incomplete' WHERE job_id=$source;
                """;
            command.Parameters.AddWithValue("$source", source.JobId.ToString("D").ToUpperInvariant());
            await command.ExecuteNonQueryAsync();
            var views = app.Services.GetRequiredService<SearchViewCoordinator>();
            var created = (await views.CreateAsync(source.JobId, new(ServerSearchViewProjectionKind.Files,
                new SongQueryDto(Title: "Track"), IncludeFullResults: true), CancellationToken.None))!;
            var live = await WaitForConditionViewAsync(views, created.ViewId, value => value.Counters.PublicFileCount == 3, () => { });
            Assert.IsFalse(live.IsComplete, "Stored metadata cannot finish a still-running Core search.");
            Assert.AreEqual(3, (views.GetFiles(live.ViewId, live.Revision, null, 500, cancellationToken: CancellationToken.None))!.Items.Count);
            finish.TrySetResult();
            await app.StopAsync();
            // The lost row stays lost on restart; a temporary View is not a
            // second durable source or a promise that every observed row committed.
            command.CommandText = "UPDATE search_jobs SET is_complete=1,result_persistence_state='Incomplete' WHERE job_id=$source;";
            await command.ExecuteNonQueryAsync();
            await using var restarted = global::Tests.TestServerHost.Build([], Options(directory, ControllableSearchClientProxy.Create().Client), "http://127.0.0.1:0");
            await restarted.StartAsync();
            try
            {
                var historical = restarted.Services.GetRequiredService<SearchViewCoordinator>();
                var fresh = (await historical.CreateAsync(source.JobId, new(ServerSearchViewProjectionKind.Files,
                    new SongQueryDto(Title: "Track"), IncludeFullResults: true), CancellationToken.None))!;
                var retained = await WaitForConditionViewAsync(historical, fresh.ViewId, value => value.IsComplete, () => { });
                Assert.AreEqual(SearchViewRetentionState.Incomplete, retained.RetentionState);
                var files = (historical.GetFiles(retained.ViewId, retained.Revision, null, 500, cancellationToken: CancellationToken.None))!.Items;
                CollectionAssert.AreEquivalent(new[] { @"Music\Track-1.flac", @"Music\Track-3.flac" },
                    files.Select(file => file.RemoteFilename).ToArray());
            }
            finally { await restarted.StopAsync(); }
        }
        finally
        {
            finish.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyUpdatesCoalesceAndFirstMatchingResultsDoNotWaitForTheClock(bool explicitlyRequestProjection)
    {
        string directory = System.IO.Directory.CreateTempSubdirectory("sockseek-first-match-").FullName;
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, ct) => { started.TrySetResult(receive); await finish.Task.WaitAsync(ct); };
        var options = Microsoft.Extensions.Options.Options.Create(Options(directory, client.Client));
        options.Value.Persistence.SearchResultFlushCount = 2;
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options, persistence, NullLoggerFactory.Instance);
        var clock = new ManualViewClock();
        await using var views = new SearchViewCoordinator(supervisor, persistence,
            new SubmissionCommitCoordinator(persistence), new SearchViewCursorCodec(),
            NullLogger<SearchViewCoordinator>.Instance, clock);
        using var cancellation = new CancellationTokenSource();
        Task run = Task.CompletedTask;
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);
            var engineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.EngineCreated += _ => engineCreated.TrySetResult();
            run = supervisor.RunAsync(cancellation.Token);
            await engineCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var source = await supervisor.Submissions.SubmitSearchJobAsync(new("Track"), CancellationToken.None);
            var receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var view = (await views.CreateAsync(source.JobId, new(ServerSearchViewProjectionKind.Files,
                new SongQueryDto(Title: "Track"), IncludeFullResults: true, FilterText: "match"), CancellationToken.None))!;
            // The timeout is a watchdog; the test controls the publication clock.
            using var observationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var observation = views.ObserveAsync(view.ViewId, observationCancellation.Token).GetAsyncEnumerator();
            Assert.IsTrue(await observation.MoveNextAsync());
            receive(Response("Peer", [File(@"Music\Other.flac")]));
            Assert.IsTrue(await observation.MoveNextAsync());
            Assert.IsTrue(observation.Current.Revision > 0);
            Assert.AreEqual(0L, observation.Current.Counters.TopLevelItemCount);
            long initialRevision = observation.Current.Revision;
            var counterPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CountersPublished(Sockseek.Server.Planning.SearchViewInfo published)
            {
                if (published.Id == view.ViewId && published.Counters.PublicFileCount == 3)
                    counterPublication.TrySetResult();
            }
            // Arm is only a scheduling signal: the worker may have consumed
            // one arrival while the next source commit is still pending. Drain
            // both inputs before advancing virtual time, so this tests one
            // cadence rather than racing the database against the test clock.
            int consumedCounterInputs = 0;
            var counterInputsConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var consumption = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Sockseek.Server.SearchViews",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "advance"
                        && activity.GetTagItem("initial") is false
                        && activity.GetTagItem("view.id") is Guid id && id == view.ViewId
                        && activity.GetTagItem("input.count") is int count
                        && Interlocked.Add(ref consumedCounterInputs, count) >= 2)
                        counterInputsConsumed.TrySetResult();
                },
            };
            ActivitySource.AddActivityListener(consumption);
            views.ViewPublished += CountersPublished;
            try
            {
                receive(Response("CasePeer", [File(@"Music\Other-two.flac")]));
                receive(Response("casepeer", [File(@"Music\Other-three.flac")]));
                Task ready = await Task.WhenAny(clock.TimerArmed, counterPublication.Task)
                    .WaitAsync(observationCancellation.Token);
                Assert.AreSame(clock.TimerArmed, ready,
                    "Counter-only arrivals must wait for the existing cadence instead of committing each batch.");
                await counterInputsConsumed.Task.WaitAsync(observationCancellation.Token);
                var beforeTick = (views.Cache.Get(view.ViewId))!;
                Assert.AreEqual(initialRevision, beforeTick.Revision);
                Assert.AreEqual(1L, beforeTick.Counters.PublicFileCount);
                await clock.AdvanceAndFireAsync(TimeSpan.FromSeconds(1));
                await counterPublication.Task.WaitAsync(observationCancellation.Token);
                Assert.IsTrue(await observation.MoveNextAsync());
                Assert.AreEqual(3L, observation.Current.Counters.PublicFileCount);
                Assert.AreEqual(3, observation.Current.Counters.ObservedPeerCount,
                    "Deferred updates retain every exact peer spelling.");
                Assert.AreEqual(initialRevision + 1, observation.Current.Revision);
            }
            finally { views.ViewPublished -= CountersPublished; }
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Committed(Guid id) { if (id == source.JobId) committed.TrySetResult(); }
            persistence.SearchSourceChanged += Committed;
            try
            {
                Task<bool> firstResult = observation.MoveNextAsync().AsTask();
                receive(Response("Peer", [File(@"Music\Match.flac")]));
                if (explicitlyRequestProjection)
                {
                    await committed.Task.WaitAsync(observationCancellation.Token);
                    views.Get(view.ViewId, observationCancellation.Token);
                }
                Assert.IsTrue(await firstResult);
                Assert.AreEqual(1L, observation.Current.Counters.TopLevelItemCount);
                Assert.AreEqual(4L, observation.Current.Counters.PublicFileCount);
                Assert.IsFalse(observation.Current.IsComplete);
            }
            finally { persistence.SearchSourceChanged -= Committed; }
        }
        finally
        {
            finish.TrySetResult();
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ManualViewClock : TimeProvider
    {
        private long ticks;
        private readonly TaskCompletionSource<ManualTimer> armed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task TimerArmed => armed.Task;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public async Task AdvanceAndFireAsync(TimeSpan interval)
        {
            ManualTimer timer = await armed.Task;
            Interlocked.Add(ref ticks, interval.Ticks);
            timer.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // This clock controls the one-shot publication wakeup; housekeeping
            // uses ordinary periodic timers and must not satisfy TimerArmed.
            if (period != Timeout.InfiniteTimeSpan)
                return base.CreateTimer(callback, state, dueTime, period);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        private sealed class ManualTimer(ManualViewClock owner, TimerCallback callback, object? state) : ITimer
        {
            private volatile bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed) return false;
                if (dueTime != Timeout.InfiniteTimeSpan) owner.armed.TrySetResult(this);
                return true;
            }
            public void Fire() { if (!disposed) callback(state); }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [TestMethod]
    public async Task LostDurableCompletionDoesNotDiscardTheCompletedLiveProjection()
    {
        string directory = System.IO.Directory.CreateTempSubdirectory("sockseek-search-completion-failure-").FullName;
        var client = ControllableSearchClientProxy.Create();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, ct) =>
        {
            receive(Response("Peer", [File(@"Music\Track-one.flac"), File(@"Music\Track-two.flac")]));
            await finish.Task.WaitAsync(ct);
        };
        await using WebApplication app = global::Tests.TestServerHost.Build([], Options(directory, client.Client), "http://127.0.0.1:0");
        try
        {
            await app.StartAsync();
            var persistence = app.Services.GetRequiredService<PersistenceCoordinator>();
            var views = app.Services.GetRequiredService<SearchViewCoordinator>();
            var source = await app.Services.GetRequiredService<JobSubmissions>()
                .SubmitSearchJobAsync(new("Track"), CancellationToken.None);
            var created = (await views.CreateAsync(source.JobId, new(ServerSearchViewProjectionKind.Files,
                new SongQueryDto(Title: "Track"), IncludeFullResults: true), CancellationToken.None))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var observation = views.ObserveAsync(created.ViewId, timeout.Token).GetAsyncEnumerator();
            do { Assert.IsTrue(await observation.MoveNextAsync()); }
            while (observation.Current.Counters.ProjectedFileCount < 2);
            Assert.IsFalse(observation.Current.IsComplete);

            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { Pooling = false, DataSource = Path.Combine(directory, "sockseek.db") }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fail_search_completion BEFORE UPDATE OF is_complete ON search_jobs
                WHEN NEW.is_complete=1 BEGIN SELECT RAISE(ABORT, 'Injected search completion failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
            var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void CompletionLost(Guid id) { if (id == source.JobId && persistence.IsSearchCompletionLost(id)) failure.TrySetResult(); }
            persistence.SearchSourceChanged += CompletionLost;
            finish.TrySetResult();
            do { Assert.IsTrue(await observation.MoveNextAsync()); }
            while (!observation.Current.IsComplete);
            Assert.AreEqual(SearchViewRetentionState.Complete, observation.Current.RetentionState, "Live completeness does not wait for durable acceptance.");
            Assert.AreEqual(2L, observation.Current.Counters.ProjectedFileCount);
            var terminal = observation.Current;
            Assert.IsFalse(await observation.MoveNextAsync());
            var files = (views.GetFiles(created.ViewId, terminal.Revision, null, 500, cancellationToken: timeout.Token))!;
            CollectionAssert.AreEquivalent(new[] { @"Music\Track-one.flac", @"Music\Track-two.flac" },
                files.Items.Select(file => file.RemoteFilename).ToArray());
            try { await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { persistence.SearchSourceChanged -= CompletionLost; }
            var retained = (await persistence.SearchHistory!.GetRawResultsAsync(source.JobId, 0, 500))!;
            Assert.IsFalse(retained.Metadata.IsComplete, "Do not fabricate a durable completion on a failed write.");
            Assert.AreEqual(2, retained.Items.Count);
        }
        finally
        {
            finish.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ObservationCatchesUpWithoutPollingAndReleasesDemandWhenDisposed()
    {
        string directory = System.IO.Directory.CreateTempSubdirectory("sockseek-observation-").FullName;
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, token) => { started.TrySetResult(receive); await finish.Task.WaitAsync(token); };
        var options = Microsoft.Extensions.Options.Options.Create(Options(directory, client.Client));
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options, persistence, NullLoggerFactory.Instance);
        var clock = new ManualViewClock();
        await using var views = new SearchViewCoordinator(supervisor, persistence,
            new SubmissionCommitCoordinator(persistence), new SearchViewCursorCodec(),
            NullLogger<SearchViewCoordinator>.Instance, clock);
        using var cancellation = new CancellationTokenSource();
        var engineCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.EngineCreated += _ => engineCreated.TrySetResult();
        Task run = Task.CompletedTask;
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);
            run = supervisor.RunAsync(cancellation.Token);
            await engineCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var source = await supervisor.Submissions.SubmitSearchJobAsync(new("Track"), CancellationToken.None);
            var receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var definition = new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.Files, IncludeFullResults: true);
            var view = (await views.CreateAsync(source.JobId, definition, CancellationToken.None))!;
            var observation = views.ObserveAsync(view.ViewId, CancellationToken.None).GetAsyncEnumerator();
            await using (observation)
            {
                Assert.IsTrue(await observation.MoveNextAsync());
                Assert.AreEqual(0L, observation.Current.Revision);
                var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Published(Sockseek.Server.Planning.SearchViewInfo published) { if (published.Id == view.ViewId && published.Revision >= 2) caughtUp.TrySetResult(); }
                views.ViewPublished += Published;
                try
                {
                    // More than the initial 200-row batch, without another read
                    // or MoveNext: display cadence cannot throttle processing.
                    receive(Response("peer", Enumerable.Range(0, 301)
                        .Select(index => File($@"Music\Track-{index}.flac")).ToArray()));
                    await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var stored = (views.Cache.Get(view.ViewId))!;
                    Assert.AreEqual(301L, stored.Counters.PublicFileCount);
                    Assert.IsFalse(stored.IsComplete, "Exhausting the committed prefix must not finish a live search.");
                }
                finally { views.ViewPublished -= Published; }

                receive(Response("sparse-peer", [File(@"Music\Track-sparse.flac")]));
                await CreateAndObserveAsync(views, source.JobId, definition);
                var coalesced = (views.Cache.Get(view.ViewId))!;
                Assert.AreEqual(301L, coalesced.Counters.PublicFileCount,
                    "A sparse arrival must not create another tiny publication inside the refresh interval.");
                var nextBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void NextPublished(Sockseek.Server.Planning.SearchViewInfo published)
                {
                    if (published.Id == view.ViewId && published.Counters.PublicFileCount == 303) nextBatch.TrySetResult();
                }
                views.ViewPublished += NextPublished;
                try
                {
                    receive(Response("next-peer", [File(@"Music\Track-next.flac")]));
                    await CreateAndObserveAsync(views, source.JobId, definition);
                    await clock.AdvanceAndFireAsync(TimeSpan.FromSeconds(1));
                    await nextBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { views.ViewPublished -= NextPublished; }
            }

            receive(Response("other-peer", [File(@"Music\Track-later.flac")]));
            // A publication through the single worker is a causal barrier,
            // proving the inactive View did not process the later arrival.
            await CreateAndObserveAsync(views, source.JobId, definition);
            var stopped = (views.Cache.Get(view.ViewId))!;
            Assert.AreEqual(303L, stopped.Counters.PublicFileCount);
            finish.TrySetResult();
        }
        finally
        {
            finish.TrySetResult();
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("Pruned", 0)]
    [DataRow("Pruned", 500)]
    [DataRow("Incomplete", 500)]
    [DataRow("Interrupted", 500)]
    public async Task RetainedReplay_ReportsLossFromTheReadSnapshot(string retentionState, int retainedPrefix)
    {
        var original = new PersistedSearchMetadata(Guid.NewGuid(), "Track", 1, 1000, 0, 1,
            true, DateTimeOffset.UnixEpoch, "Complete", null);
        var current = original with { Revision = 2, ResultPersistenceState = retentionState,
            IsComplete = retentionState != "Incomplete" };
        var rows = Enumerable.Range(1, retainedPrefix).Select(index => new SearchProjectionInput(
            index, 1, "Peer", 1000, $@"Music\Track-{index}.flac", 128,
            320, 24, 44100, 180, ".flac", 1000, true, null,
            DateTimeOffset.UnixEpoch, 0, SearchResultVisibility.Public)).ToArray();
        var source = new SnapshotHistoryReader(current, rows);

        var batch = await SearchViewCoordinator.ReadHistoryBatchAsync(source, original, 0, 1000, CancellationToken.None);

        Assert.IsTrue(batch.IsComplete, "No further retained rows remain to project.");
        Assert.AreEqual(retainedPrefix, batch.Items.Count, "Keep the retained prefix when the rest of the source is unavailable.");
        Assert.AreEqual(retentionState, batch.RetentionState,
            "The rows and partial-retention state must belong to the same read snapshot.");
        Assert.AreEqual(2, batch.SourceRevision);
    }

    private sealed class SnapshotHistoryReader(PersistedSearchMetadata current,
        IReadOnlyList<SearchProjectionInput> rows) : ISearchHistoryReader
    {
        public Task<PersistedSearchResultPage?> GetRawResultsAsync(Guid jobId, long afterSequence,
            int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<PersistedSearchResultPage?>(new(current, rows, null));
        public Task<PersistedSearchMetadata?> GetMetadataAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<PersistedSearchMetadata?>(current);
        public Task<PersistedSearchResultLookup?> GetResultAsync(Guid id, string username, string filename,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SearchProjectionInput> ReadProjectionInputsAsync(Guid id,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SearchProjectionInput>> GetFolderResultsAsync(Guid id, PeerDirectoryIdentity directory,
            AlbumQuery query, SearchResultVisibility visibility, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadOrderingUsesOnePublishedViewAndBindsContinuationOrder(bool directories)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-read-order-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        int searches = 0;
        client.Search = (receive, _) =>
        {
            Interlocked.Increment(ref searches);
            receive(new SearchResponse("CasePeer", 1, true, 1000, 5,
                [File(@"Music\Z\Track one.flac"), File(@"Music\Z\Track two.flac")]));
            receive(new SearchResponse("casepeer", 2, true, 2000, 1,
                [File(@"Music\A\Track three.flac")]));
            return Task.CompletedTask;
        };
        await using WebApplication app = global::Tests.TestServerHost.Build([], Options(directory, client.Client), "http://127.0.0.1:0");
        try
        {
            await app.StartAsync();
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);
            var source = await api.SubmitSearchJobAsync(new SubmitSearchJobRequestDto("Track"));
            var views = app.Services.GetRequiredService<SearchViewCoordinator>();
            var created = await api.CreateSearchViewAsync(source.JobId, new CreateSearchViewRequestDto(
                directories ? ServerSearchViewProjectionKind.GenericDirectories : ServerSearchViewProjectionKind.Files,
                new SongQueryDto(Title: "Track"), IncludeFullResults: true));
            var before = await WaitForConditionViewAsync(views, created.ViewId, view => view.IsComplete, () => { });
            var order = directories ? ServerSearchViewSortOrder.DirectoryNameAscending : ServerSearchViewSortOrder.UploadSpeed;
            string? cursor = null;
            string? firstCursor = null;
            var peers = new List<string>();
            do
            {
                if (directories)
                {
                    var page = (await api.GetSearchViewDirectoriesAsync(before.ViewId, before.Revision, cursor, 1, order))!;
                    Assert.AreEqual(before.ViewId, page.Revision.ViewId);
                    peers.AddRange(page.Items.Select(item => item.Peer.Username));
                    cursor = page.NextCursor;
                }
                else
                {
                    var page = (await api.GetSearchViewFilesAsync(before.ViewId, before.Revision, cursor, 1, order))!;
                    Assert.AreEqual(before.ViewId, page.Revision.ViewId);
                    peers.AddRange(page.Items.Select(item => item.Peer.Username));
                    cursor = page.NextCursor;
                }
                firstCursor ??= cursor;
            }
            while (cursor != null);
            CollectionAssert.AreEqual(directories ? new[] { "casepeer", "CasePeer" }
                : new[] { "casepeer", "CasePeer", "CasePeer" }, peers);
            Assert.IsNotNull(firstCursor);
            string path = $"/api/search-views/{before.ViewId}/{(directories ? "directories" : "files")}?revision={before.Revision}&limit=1&cursor={Uri.EscapeDataString(firstCursor)}";
            using var wrongDefault = await http.GetAsync(path);
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, wrongDefault.StatusCode);
            using var wrongOrder = await http.GetAsync(path + "&sortOrder=" + (directories
                ? "DirectoryNameDescending" : "QueueDepth"));
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, wrongOrder.StatusCode);
            var after = (await api.GetSearchViewAsync(before.ViewId))!;
            Assert.IsTrue(after.ExpiresAtUtc >= before.ExpiresAtUtc);
            Assert.AreEqual(before with { ExpiresAtUtc = after.ExpiresAtUtc }, after);
            Assert.AreEqual(ServerSearchViewSortOrder.Relevance,
                (await api.GetSearchViewDefinitionAsync(before.ViewId))!.Projection.SortOrder);
            Assert.AreEqual(1, searches);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(ServerSearchViewProjectionKind.Files, false)]
    [DataRow(ServerSearchViewProjectionKind.TrackGroups, true)]
    [DataRow(ServerSearchViewProjectionKind.AlbumGroups, true)]
    public async Task UnreadViewsStayAtTheirPublishedPrefixAndResumeAfterSourceRetires(
        ServerSearchViewProjectionKind kind, bool evictIdleKernel)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-view-demand-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, ct) => { started.TrySetResult(receive); await finish.Task.WaitAsync(ct); };
        var options = Microsoft.Extensions.Options.Options.Create(Options(directory, client.Client));
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options, persistence, NullLoggerFactory.Instance);
        var clock = new ViewClock();
        await using var views = new SearchViewCoordinator(supervisor, persistence,
            new SubmissionCommitCoordinator(persistence), new SearchViewCursorCodec(),
            NullLogger<SearchViewCoordinator>.Instance, clock);
        using var cancellation = new CancellationTokenSource();
        var engineCreated = new TaskCompletionSource<DownloadEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.EngineCreated += engine => engineCreated.TrySetResult(engine);
        Task run = Task.CompletedTask;
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);
            run = supervisor.RunAsync(cancellation.Token);
            var engine = await engineCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var source = await supervisor.Submissions.SubmitSearchJobAsync(new("Artist"), CancellationToken.None);
            Action<SearchResponse> receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            receive(Response("Péér A", [File(@"Artist\Album\01 - Artist - Track.flac")]));
            engine.GetPeerFailureCounts(source.WorkflowId)["Péér B"] = 2;
            var request = new CreateSearchViewRequestDto(kind,
                SongQuery: new SongQueryDto("Artist", "", "", "", null, false),
                AlbumQuery: new AlbumQueryDto("Artist", "Album", "", "", false),
                IncludeFullResults: true,
                RefinementSettings: new SearchSettingsPatchDto(MinGroupShares: 1));
            var unread = await CreateAndObserveAsync(views, source.JobId, request);
            var first = (views.GetFiles(unread.ViewId, unread.Revision, null, 500, cancellationToken: CancellationToken.None))!.Items.Single();
            // This second View represents a refinement/another reader. Its
            // publication is also a barrier through the single projection worker.
            var active = await CreateAndObserveAsync(views, source.JobId, request);
            if (evictIdleKernel) clock.Advance(TimeSpan.FromMinutes(3));
            receive(Response("Péér B", [File(@"Artist\Album\01 - Artist - Track.flac")],
                [File(@"Artist\Album\02 - Artist - Locked.flac")]));
            active = await WaitForConditionViewAsync(views, active.ViewId,
                value => value.Counters.PublicFileCount == 2 && value.Counters.LockedFileCount == 1, () => { });
            // A fresh requested View runs after any work queued by that response.
            // This makes the unread-prefix assertion causal, without a delay.
            await CreateAndObserveAsync(views, source.JobId, request);
            var stillUnread = (views.Cache.Get(unread.ViewId))!;
            Assert.AreEqual(unread.Revision, stillUnread.Revision,
                "Reading a refinement must not advance the View the reader left behind.");
            Assert.AreEqual(1L, stillUnread.Counters.PublicFileCount);

            finish.TrySetResult();
            engine.CompleteEnqueue();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            await persistence.WaitForJobHandoffAsync(source.JobId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(supervisor.GetRuntimeJob<Job>(source.JobId));
            active = await WaitForConditionViewAsync(views, active.ViewId, value => value.IsComplete, () => { });
            stillUnread = (views.Cache.Get(unread.ViewId))!;
            Assert.AreEqual(unread.Revision, stillUnread.Revision,
                "Raw search completion must not trigger unused projection work.");
            var oldPage = (views.GetFiles(unread.ViewId, unread.Revision, null, 500, cancellationToken: CancellationToken.None))!;
            Assert.AreEqual(first.Ref, oldPage.Items.Single().Ref);
            var resumed = await WaitForConditionViewAsync(views, unread.ViewId, value => value.IsComplete, () => { });
            Assert.AreEqual(active.Counters, resumed.Counters);
            var resumedFiles = (views.GetFiles(unread.ViewId, resumed.Revision, null, 500, cancellationToken: CancellationToken.None))!.Items;
            Assert.AreEqual(3, resumedFiles.Count);
            Assert.AreEqual(first.Ref, resumedFiles.Single(file => file.Peer.Username == "Péér A").Ref);
            var activeFiles = (views.GetFiles(active.ViewId, active.Revision, null, 500, cancellationToken: CancellationToken.None))!.Items;
            CollectionAssert.AreEqual(activeFiles.Select(file => file.Ref).ToArray(), resumedFiles.Select(file => file.Ref).ToArray(),
                "Evicting a working kernel must preserve this View's workflow-local ranking facts.");
            clock.Advance(TimeSpan.FromMinutes(16));
            // A publication behind the timer's queued work is a deterministic
            // worker barrier. Creation/read requests themselves do no pruning.
            await CreateAndObserveAsync(views, source.JobId, request);
            Assert.AreEqual(0, views.Cache.PruneExpired(),
                "The maintenance tick must release expired Views without another read of them.");
        }
        finally
        {
            finish.TrySetResult();
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ViewClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        private TickTimer? maintenance;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan interval)
        {
            Interlocked.Add(ref ticks, interval.Ticks);
            maintenance?.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => period == Timeout.InfiniteTimeSpan ? base.CreateTimer(callback, state, dueTime, period)
                : maintenance = new TickTimer(callback, state);
        private sealed class TickTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [TestMethod]
    public async Task RetiredSearchCanOpenAndRefineWhileRawHistoryHandoffIsPending()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-search-read-handoff-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engineCreated = new TaskCompletionSource<DownloadEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, ct) =>
        {
            started.TrySetResult(receive);
            await finishSearch.Task.WaitAsync(ct);
        };
        var options = Microsoft.Extensions.Options.Options.Create(Options(directory, client.Client));
        var persistence = new PersistenceCoordinator(options);
        using var supervisor = new EngineSupervisor(options, persistence, NullLoggerFactory.Instance);
        await using var views = new SearchViewCoordinator(supervisor, persistence,
            new SubmissionCommitCoordinator(persistence), new SearchViewCursorCodec(),
            NullLogger<SearchViewCoordinator>.Instance);
        using var cancellation = new CancellationTokenSource();
        Task run = Task.CompletedTask;
        int holdNextCommit = 0;
        void HoldCommit()
        {
            if (Interlocked.Exchange(ref holdNextCommit, 0) == 0) return;
            commitHeld.TrySetResult();
            // Pause after a commit, outside its SQLite transaction. Reads must
            // still work; View writes must queue through this same owner.
            releaseWriter.Task.GetAwaiter().GetResult();
        }
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);
            supervisor.EngineCreated += engine => engineCreated.TrySetResult(engine);
            run = supervisor.RunAsync(cancellation.Token);
            DownloadEngine engine = await engineCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            JobSummaryDto source = await supervisor.Submissions.SubmitSearchJobAsync(new("Artist"), CancellationToken.None);
            Action<SearchResponse> receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            persistence.Health!.CommitCompleted += HoldCommit;
            Interlocked.Exchange(ref holdNextCommit, 1);
            receive(Response("Exact Péér", [File(@"Artist\Album\01 - Track.flac")],
                [File(@"Artist\Album\02 - Locked.flac")]));
            await commitHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.GetPeerFailureCounts(source.WorkflowId)["Exact Péér"] = 2;
            finishSearch.TrySetResult();
            engine.CompleteEnqueue();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(engine.GetJob(source.JobId), "Core must have released its execution indexes.");
            Assert.IsNull(supervisor.GetRuntimeJob<Job>(source.JobId));
            Assert.IsFalse(persistence.WaitForJobHandoffAsync(source.JobId, CancellationToken.None).IsCompleted);
            Assert.IsNotNull(persistence.GetRetiringSearchSource(source.JobId));
            Assert.AreEqual(2, persistence.GetRetiringSearchSource(source.JobId)!.PeerFailureCounts["Exact Péér"],
                "The read handoff preserves this workflow's last counters after engine disposal.");
            Assert.AreEqual(0, engine.GetPeerFailureCounts(source.WorkflowId).Count);

            var history = new JobReadService(supervisor.StateStore, supervisor, persistence);
            CombinedSearchRawPage firstRaw = (await history.GetRawSearchResultsAsync(source.JobId, 0, 1)
                .WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.AreEqual(1, firstRaw.Items.Count);
            Assert.AreEqual(1L, firstRaw.NextSequence);
            Assert.AreEqual(ServerSearchResultVisibility.Public, firstRaw.Items[0].Visibility);
            CombinedSearchRawPage lastRaw = (await history.GetRawSearchResultsAsync(source.JobId, firstRaw.NextSequence!.Value, 1)
                .WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.AreEqual(1, lastRaw.Items.Count);
            Assert.IsNull(lastRaw.NextSequence);
            Assert.AreEqual(ServerSearchResultVisibility.Locked, lastRaw.Items[0].Visibility);
            Assert.IsFalse(releaseWriter.Task.IsCompleted, "Raw paging must use the same read handoff as Search Views.");

            var pendingViews = new Dictionary<ServerSearchViewProjectionKind, Task<SearchViewSummaryDto?>>();
            foreach (ServerSearchViewProjectionKind kind in Enum.GetValues<ServerSearchViewProjectionKind>())
            {
                var request = new CreateSearchViewRequestDto(kind,
                    SongQuery: new SongQueryDto(Artist: "Artist", Title: "Track"),
                    AlbumQuery: new AlbumQueryDto(Artist: "Artist", Album: "Album"),
                    RefinementSettings: new SearchSettingsPatchDto(MinGroupShares: 1));
                pendingViews.Add(kind, views.CreateAsync(source.JobId, request, CancellationToken.None));
            }
            var beforeDrain = new Dictionary<ServerSearchViewProjectionKind, string[]>();
            foreach (var (kind, opening) in pendingViews)
            {
                SearchViewSummaryDto view = (await opening.WaitAsync(TimeSpan.FromSeconds(5)))!;
                Assert.IsNotNull(view);
                view = await WaitForConditionViewAsync(views, view.ViewId, summary => summary.IsComplete, () => { });
                Assert.AreEqual(1L, view.Counters.PublicFileCount);
                Assert.AreEqual(1L, view.Counters.LockedFileCount);
                Assert.IsTrue(view.Counters.TopLevelItemCount > 0, kind.ToString());
                beforeDrain[kind] = ConditionRowSnapshots(await ReadConditionFilesAsync(views, view));
            }

            Assert.IsFalse(releaseWriter.Task.IsCompleted, "Views must be readable while durable raw intake is held.");
            releaseWriter.TrySetResult();
            await persistence.WaitForJobHandoffAsync(source.JobId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(persistence.GetRetiringSearchSource(source.JobId), "Acknowledgement releases the in-memory read inputs.");
            CombinedSearchRawPage durableRaw = (await history.GetRawSearchResultsAsync(source.JobId, 0, 500))!;
            Assert.AreEqual(2, durableRaw.Items.Count);
            Assert.IsNull(durableRaw.NextSequence);
            foreach (var (before, after) in new[] { firstRaw.Items[0], lastRaw.Items[0] }.Zip(durableRaw.Items))
            {
                Assert.AreEqual(before with { ObservedAtUtc = null }, after with { ObservedAtUtc = null });
                Assert.AreEqual(before.ObservedAtUtc?.ToUnixTimeMilliseconds(), after.ObservedAtUtc?.ToUnixTimeMilliseconds(),
                    "Storage timestamps have millisecond precision.");
            }
            foreach (var (kind, rows) in beforeDrain)
            {
                SearchViewSummaryDto view = (await views.CreateAsync(source.JobId,
                    new(kind, SongQuery: new SongQueryDto(Artist: "Artist", Title: "Track"),
                        AlbumQuery: new AlbumQueryDto(Artist: "Artist", Album: "Album"),
                        RefinementSettings: new SearchSettingsPatchDto(MinGroupShares: 1)), CancellationToken.None))!;
                view = await WaitForConditionViewAsync(views, view.ViewId, summary => summary.IsComplete, () => { });
                CollectionAssert.AreEqual(rows, ConditionRowSnapshots(await ReadConditionFilesAsync(views, view)),
                    "The durable source must agree with the handoff source for " + kind);
            }
        }
        finally
        {
            releaseWriter.TrySetResult();
            finishSearch.TrySetResult();
            if (persistence.Health is { } health) health.CommitCompleted -= HoldCommit;
            cancellation.Cancel();
            await run;
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlbumGroupChildSelectionsKeepLockedCountsAndIsolateInvalidFiles(bool allExcept)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-album-child-selection-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, ct) =>
        {
            started.TrySetResult(receive);
            await release.Task.WaitAsync(ct);
        };
        try
        {
            await using WebApplication app = TestServerHost.Build([], Options(directory, client.Client), "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                var views = app.Services.GetRequiredService<SearchViewCoordinator>();
                JobSummaryDto source = await supervisor.Submissions.SubmitAlbumSearchJobAsync(
                    new(new AlbumQueryDto(Artist: "Artist")), CancellationToken.None);
                Action<SearchResponse> receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SearchViewSummaryDto view = (await views.CreateAsync(source.JobId,
                    new(ServerSearchViewProjectionKind.AlbumGroups,
                        RefinementSettings: new SearchSettingsPatchDto(MinGroupShares: 1)), CancellationToken.None))!;
                const string first = @"Artist\Album Alpha\01 - First.flac";
                const string second = @"Artist\Album Alpha\02 - Second.flac";
                const string locked = @"Artist\Album Alpha\03 - Locked.flac";
                const string sibling = @"Artist\Album Beta\01 - Sibling.flac";
                view = await WaitForConditionViewAsync(views, view.ViewId,
                    summary => summary.Counters.PublicFileCount == 3 && summary.Counters.LockedFileCount == 1,
                    () => receive(Response("ExactPeer", [File(first), File(second), File(sibling)], [File(locked)])));
                var groups = (views.GetAlbumGroups(view.ViewId, view.Revision, null, 10, CancellationToken.None))!.Items;
                Assert.AreEqual(2, groups.Count);
                SearchViewAlbumGroupDto alpha = groups.Single(group => group.Query.Album == "Album Alpha");
                SearchViewAlbumGroupDto beta = groups.Single(group => group.Query.Album == "Album Beta");
                Assert.AreEqual(SearchViewDirectoryVisibility.Mixed, alpha.Representative.Visibility);
                var children = (views.GetDirectoryFiles(view.ViewId, alpha.Representative.Ref.Ref, view.Revision, null, 10, CancellationToken.None))!.Items;
                Assert.AreEqual(3, children.Count);
                string Ref(string path) => children.Single(child => child.File.RemoteFilename == path).Ref;
                var chosen = new Dictionary<string, string> { [alpha.Ref] = alpha.Representative.Ref.Ref };
                if (!allExcept) chosen[beta.Ref] = beta.Representative.Ref.Ref;
                var files = new Dictionary<string, RefSelectionExpressionDto>
                {
                    [alpha.Ref] = allExcept
                        ? new(RefSelectionMode.AllExcept, [Ref(second)])
                        : new(RefSelectionMode.Only, [Ref(first), Ref(locked)]),
                };
                var selection = new RefSelectionExpressionDto(RefSelectionMode.Only, [alpha.Ref, beta.Ref]);
                var count = (views.Selections.CountSelection(view.ViewId, new(view.Revision, selection, ChosenOptions: chosen, AlbumFileSelections: files), CancellationToken.None))!;
                Assert.AreEqual(2L, count.PublicFileCount);
                Assert.AreEqual(1L, count.LockedFileCount);
                Assert.AreEqual(2L, count.SelectedItemCount);
                var request = new CommitSearchViewSelectionRequestDto(view.Revision, selection, Guid.NewGuid(),
                    ChosenOptions: chosen, AlbumFileSelections: files, UseRepresentativeWhenUnchosen: true);
                var receipt = (await views.Selections.CommitSelectionAsync(view.ViewId, request, CancellationToken.None))!;
                Assert.AreEqual(2L, receipt.SubmittedCount);
                Assert.AreEqual(0L, receipt.RejectedCount);
                var accepted = await app.Services.GetRequiredService<PersistenceCoordinator>().Submissions!
                    .GetSubmissionAsync(receipt.SubmissionId!.Value, CancellationToken.None);
                Assert.IsNotNull(accepted);
                var root = (JobList)Sockseek.Core.Planning.SubmissionSpecificationCodec.Deserialize(accepted.SpecificationJson).MaterializeJob();
                var pinned = root.Jobs.OfType<AlbumJob>().Single(album => album.Query.Album == "Album Alpha");
                Assert.AreEqual(AlbumDirectoryResolutionPolicy.UseSelectedSnapshot, pinned.DirectoryResolutionPolicy);
                Assert.AreEqual(AlbumValidationRequirement.UserAccepted, pinned.ValidationRequirement);
                Assert.IsFalse(pinned.CanBeSkipped);
                CollectionAssert.AreEqual(new[] { first }, pinned.ResolvedTarget!.Files.Select(file => file.Candidate.Filename).ToArray());
                var wholeFolder = root.Jobs.OfType<AlbumJob>().Single(album => album.Query.Album == "Album Beta");
                Assert.AreEqual(AlbumValidationRequirement.UserAccepted, wholeFolder.ValidationRequirement);
                Assert.IsFalse(wholeFolder.CanBeSkipped);
                Assert.AreEqual(AlbumDirectoryResolutionPolicy.CompleteIfNeeded, wholeFolder.DirectoryResolutionPolicy);
                CollectionAssert.AreEqual(new[] { sibling }, wholeFolder.ResolvedTarget!.Files.Select(file => file.Candidate.Filename).ToArray());

                foreach (string invalid in new[] { "missing-file", beta.Representative.BestChild.Ref })
                {
                    var invalidFiles = new Dictionary<string, RefSelectionExpressionDto>
                    {
                        [alpha.Ref] = new(allExcept ? RefSelectionMode.AllExcept : RefSelectionMode.Only, [invalid]),
                    };
                    Assert.ThrowsExactly<ArgumentException>(() => views.Selections.CountSelection(view.ViewId, new(view.Revision, selection, ChosenOptions: chosen, AlbumFileSelections: invalidFiles), CancellationToken.None));
                    var isolated = (await views.Selections.CommitSelectionAsync(view.ViewId,
                        request with { IdempotencyKey = Guid.NewGuid(), AlbumFileSelections = invalidFiles }, CancellationToken.None))!;
                    Assert.AreEqual(1L, isolated.SubmittedCount);
                    Assert.AreEqual(1L, isolated.RejectedCount);
                    Assert.AreEqual("invalid-item", isolated.RejectionReasons.Single().Reason);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(isolated.RejectionReasons.Single().Message),
                        "Explain the rejected selection while accepting the independent album.");
                }
                release.TrySetResult();
            }
            finally { release.TrySetResult(); await app.StopAsync(); }
        }
        finally
        {
            release.TrySetResult();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("count")]
    [DataRow("settings")]
    [DataRow("commit")]
    [DataRow("commit-late")]
    public async Task PruningDuringSelectionPlanningCannotTurnValidWorkIntoAnEmptyResult(string operation)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-planning-prune-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        var options = Microsoft.Extensions.Options.Options.Create(Options(directory, client.Client));
        var clock = new ViewClock();
        var persistence = new PersistenceCoordinator(options, timeProvider: clock);
        var supervisor = new EngineSupervisor(options, persistence, NullLoggerFactory.Instance);
        await using var views = new SearchViewCoordinator(
            supervisor, persistence, new SubmissionCommitCoordinator(persistence),
            new SearchViewCursorCodec(), NullLogger<SearchViewCoordinator>.Instance, clock);
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);
            // Admission and planning need no running engine or network search.
            JobSummaryDto source = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new(new SongQueryDto(Artist: "Artist", Title: "Track")), CancellationToken.None);
            var repository = views.Cache;
            bool commit = operation.StartsWith("commit", StringComparison.Ordinal);
            bool latePrune = operation == "commit-late";
            long expected = latePrune ? 2 : 1;
            SearchViewProjectionKind[] kinds = latePrune
                ? [SearchViewProjectionKind.GenericDirectories] : Enum.GetValues<SearchViewProjectionKind>();
            foreach (SearchViewProjectionKind kind in kinds)
            {
                var definition = kind is SearchViewProjectionKind.AlbumDirectories or SearchViewProjectionKind.AlbumGroups
                    ? new SearchViewProjectionDefinition(kind, AlbumQuery: new AlbumQuery { Artist = "Artist", Album = "Album" })
                    : new SearchViewProjectionDefinition(kind, new SongQuery { Artist = "Artist", Title = "Track" });
                var kernel = new SearchViewKernel(definition, new SearchSettings
                {
                    NecessaryCond = new FileConditions(), PreferredCond = new FileConditions(),
                    NecessaryFolderCond = new FolderConditions(), MinGroupShares = 1,
                });
                var stored = repository.Create(source.JobId, new SearchViewDefinition(SearchDefinition.Create(new SearchJob("test"), new SearchSettings()), definition));
                var inputs = new List<SearchProjectionInput>
                {
                    new(1, 1, "ExactPeer", (int)expected, @"Artist\Album\01 - Track.flac", 900,
                        320, null, 44100, 180, "flac", 1000, true, null, DateTimeOffset.UtcNow),
                };
                if (latePrune) inputs.Add(inputs[0] with { Sequence = 2, Filename = @"Artist\Album Two\02 - Track.flac" });
                await SeedOriginalsAsync(persistence, source.JobId, inputs);
                stored = repository.Publish(stored.Id, kernel.Apply(inputs, 1, true), "Complete");
                var selection = new RefSelectionExpressionDto(RefSelectionMode.AllExcept, []);
                var control = views.Selections.CountSelection(stored.Id, new(stored.Revision, selection), CancellationToken.None);
                Assert.AreEqual(expected, control!.PublicFileCount, kind.ToString());
                Assert.AreEqual(expected, control.SelectedItemCount, kind.ToString());
                if (commit)
                {
                    var accepted = await views.Selections.CommitSelectionAsync(stored.Id,
                        new(stored.Revision, selection, Guid.NewGuid()), CancellationToken.None);
                    Assert.AreEqual(expected, accepted!.SubmittedCount, kind.ToString());
                    Assert.AreEqual(0L, accepted.RejectedCount, kind.ToString());
                    Assert.IsNotNull(accepted.SubmissionId, kind.ToString());
                }
                if (operation == "settings")
                {
                    var settings = views.Selections.ResolveSelectionSettings(stored.Id, new(stored.Revision, selection), CancellationToken.None);
                    Assert.AreEqual(1, settings!.Variants.Count, kind.ToString());
                }
                int armed = 1;
                int pruned = 0;
                using var observer = new ActivityListener
                {
                    ShouldListenTo = activitySource => activitySource.Name == "Sockseek.Server.SearchViewCache",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                    ActivityStopped = activity =>
                    {
                        if (activity.OperationName != (latePrune ? "directory-files" : "snapshot")
                            || activity.GetTagItem("view.id") is not Guid id
                            || id != stored.Id || Interlocked.Exchange(ref armed, 0) == 0) return;
                        clock.Advance(TimeSpan.FromMinutes(16));
                        pruned = repository.PruneExpired();
                    },
                };
                ActivitySource.AddActivityListener(observer);
                var request = new CommitSearchViewSelectionRequestDto(stored.Revision, selection, Guid.NewGuid());
                if (commit)
                {
                    var receipt = await views.Selections.CommitSelectionAsync(stored.Id, request, CancellationToken.None);
                    Assert.IsNotNull(receipt);
                    Assert.AreEqual(expected, receipt.SubmittedCount, kind.ToString());
                    Assert.AreEqual(0L, receipt.RejectedCount, kind.ToString());
                    var retry = await views.Selections.CommitSelectionAsync(stored.Id, request, CancellationToken.None);
                    Assert.AreEqual(receipt.SubmissionId, retry!.SubmissionId,
                        "Accepted intent and its retry receipt outlive the issuing snapshot.");
                }
                else if (operation == "settings")
                {
                    var settings = views.Selections.ResolveSelectionSettings(stored.Id,
                        new(stored.Revision, selection), CancellationToken.None);
                    Assert.IsNotNull(settings);
                    Assert.AreEqual(1, settings.Variants.Count, kind.ToString());
                }
                else
                {
                    var count = views.Selections.CountSelection(stored.Id,
                        new(stored.Revision, selection), CancellationToken.None);
                    Assert.IsNotNull(count);
                    Assert.AreEqual(expected, count.PublicFileCount, kind.ToString());
                    Assert.AreEqual(expected, count.SelectedItemCount, kind.ToString());
                }
                Assert.IsNull(views.Selections.CountSelection(stored.Id,
                    new(stored.Revision, selection), CancellationToken.None));
                Assert.IsNull(views.Selections.ResolveSelectionSettings(stored.Id,
                    new(stored.Revision, selection), CancellationToken.None));
                await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => views.Selections.CommitSelectionAsync(
                    stored.Id, request with { IdempotencyKey = Guid.NewGuid() }, CancellationToken.None));
                Assert.AreEqual(1, pruned, kind.ToString());
                Assert.IsNull(views.Get(stored.Id, CancellationToken.None));
            }
        }
        finally
        {
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AcceptedSearchIsReadableAndCanCreateAViewBeforeEngineRegistration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-search-admission-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        var options = Microsoft.Extensions.Options.Options.Create(Options(directory, client.Client));
        var persistence = new PersistenceCoordinator(options);
        var supervisor = new EngineSupervisor(options, persistence, NullLoggerFactory.Instance);
        await using var views = new SearchViewCoordinator(
            supervisor, persistence, new SubmissionCommitCoordinator(persistence),
            new SearchViewCursorCodec(), NullLogger<SearchViewCoordinator>.Instance);
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);
            // Do not run the engine: keep admission -> registration deterministic.
            JobSummaryDto accepted = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                new SubmitTrackSearchJobRequestDto(new SongQueryDto(Artist: "Fixture"), GroupResults: true),
                CancellationToken.None);
            Assert.IsNotNull(supervisor.StateStore.GetJobSummary(accepted.JobId),
                "Durable acceptance must not leave the returned job absent from immediate reads.");
            var queries = new JobReadService(supervisor.StateStore, supervisor, persistence);
            var pendingPage = await queries.GetJobsAsync(
                new JobQuery(null, null, null, null, IncludeAll: false), null, 100);
            Assert.AreEqual(accepted.JobId, pendingPage.Items.Single().JobId);
            Assert.AreEqual(accepted.JobId, (await queries.GetJobAsync(accepted.JobId))!.Summary.JobId);
            Assert.AreEqual(accepted.JobId,
                (await queries.LookupJobsAsync(new JobLookupRequestDto([accepted.JobId]))).Single().JobId);
            Assert.IsNotNull(supervisor.StateStore.GetWorkflowSummary(accepted.WorkflowId));
            supervisor.StateStore.MarkActiveJobsInfrastructureFailed("previous engine stopped");
            Assert.AreEqual(ServerJobLifecycleState.Pending,
                supervisor.StateStore.GetJobSummary(accepted.JobId)!.LifecycleState,
                "A failed engine must not fail accepted work still owned by the submission queue.");
            SearchViewSummaryDto? view = await views.CreateAsync(
                accepted.JobId, new CreateSearchViewRequestDto(), CancellationToken.None);
            Assert.IsNotNull(view);
            Assert.AreEqual(accepted.JobId, view.SourceJobId);
            Assert.AreEqual(ServerSearchViewProjectionKind.TrackGroups, view.Kind);
            Assert.IsFalse(view.IsComplete);
            Assert.IsNull(await persistence.JobHistory!.GetJobAsync(accepted.JobId));
            JobSummaryDto? replay = await supervisor.Submissions.RerunSearchJobAsync(accepted.JobId, CancellationToken.None);
            Assert.IsNotNull(replay, "Accepted live/pending searches must not depend on a historical job row.");
            Assert.AreNotEqual(accepted.JobId, replay.JobId);
            Assert.AreNotEqual(accepted.SubmissionId, replay.SubmissionId);
            SearchViewSummaryDto? replayView = await views.CreateAsync(
                replay.JobId, new CreateSearchViewRequestDto(), CancellationToken.None);
            Assert.IsNotNull(replayView);
            Assert.AreEqual(ServerSearchViewProjectionKind.TrackGroups, replayView.Kind);
            DeleteSubmissionResponseDto archive = await queries.DeleteSubmissionAsync(
                accepted.SubmissionId!.Value, CancellationToken.None);
            Assert.AreEqual(1, archive.RejectedSubmissionCount);
            Assert.AreEqual("nonterminal-jobs", archive.RejectionReasons.Single().Reason);
        }
        finally
        {
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task WaitingUpdateReplaysCachedDirectoryWhenALateMatchFirstMakesItVisible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-search-replay-wait-" + Guid.NewGuid().ToString("N"));
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (receive, cancellationToken) =>
        {
            started.TrySetResult(receive);
            await release.Task.WaitAsync(cancellationToken);
        };
        int browseCount = 0;
        client.Browse = _ =>
        {
            Interlocked.Increment(ref browseCount);
            return new BrowseResponse([new Soulseek.Directory("Folder", [File("Root.txt"), File("NewCached.txt")])]);
        };
        try
        {
            await using WebApplication app = global::Tests.TestServerHost.Build([], Options(directory, client.Client), "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
                var views = app.Services.GetRequiredService<SearchViewCoordinator>();
                JobSummaryDto job = await supervisor.Submissions.SubmitSearchJobAsync(new("Folder"), CancellationToken.None);
                Action<SearchResponse> receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SearchViewSummaryDto original = (await views.CreateAsync(job.JobId, new(), CancellationToken.None))!;
                long revision = await PublishAsync(views, original.ViewId, () => receive(Response("peer", [File(@"Folder\Root.txt")])));
                var page = (views.GetDirectories(original.ViewId, revision, null, 10, cancellationToken: CancellationToken.None))!;
                var retrievalPublished = ObserveNext(views, original.ViewId);
                try
                {
                    await views.StartDirectoryRetrievalAsync(original.ViewId,
                        new(revision, page.Items.Single().Ref, Guid.NewGuid()), CancellationToken.None);
                    await retrievalPublished.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { retrievalPublished.Dispose(); }

                SearchViewSummaryDto refined = await CreateAndObserveAsync(views, job.JobId,
                    new CreateSearchViewRequestDto(FilterText: "New"));
                Assert.AreEqual(0L, refined.Counters.TopLevelItemCount);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Task<SearchViewUpdateDto?> waiting = views.GetUpdatesAsync(refined.ViewId, refined.Revision,
                    waitForChange: true, cancellationToken: timeout.Token);
                Assert.IsFalse(waiting.IsCompleted);
                receive(Response("peer", [File(@"Folder\NewLate.txt")]));
                SearchViewUpdateDto updated = (await waiting)!;
                Assert.IsTrue(updated.HasNewRevision);
                var directories = (views.GetDirectories(refined.ViewId, updated.Summary.Revision, null, 10, cancellationToken: CancellationToken.None))!;
                var files = (views.GetDirectoryFiles(refined.ViewId, directories.Items.Single().Ref.Ref, updated.Summary.Revision, null, 10, CancellationToken.None))!;
                CollectionAssert.AreEquivalent(new[] { @"Folder\NewCached.txt", @"Folder\NewLate.txt" },
                    files.Items.Select(item => item.File.RemoteFilename).ToArray());
                Assert.AreEqual(1, browseCount, "Replaying retained observations must not browse the peer again.");
            }
            finally { release.TrySetResult(); await app.StopAsync(); }
        }
        finally
        {
            release.TrySetResult();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RefinementConditionsProjectTheWholeLivePrefixBeforePaging()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-search-view-refinement-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var client = ControllableSearchClientProxy.Create();
        var searchStarted = new TaskCompletionSource<Action<SearchResponse>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid jobId;
        client.Search = async (handler, cancellationToken) =>
        {
            searchStarted.TrySetResult(handler);
            await releaseSearch.Task.WaitAsync(cancellationToken);
        };
        try
        {
            await using WebApplication app = global::Tests.TestServerHost.Build(
                [], Options(directory, client.Client), "http://127.0.0.1:0");
            await app.StartAsync();
            EngineSupervisor supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            JobSummaryDto job = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("Track"), CancellationToken.None);
            jobId = job.JobId;
            Action<SearchResponse> receive = await searchStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            SearchViewCoordinator views = app.Services.GetRequiredService<SearchViewCoordinator>();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.Files,
                    RefinementSettings: new SearchSettingsPatchDto(SearchTimeout: 1)),
                CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.Files,
                    SortOrder: ServerSearchViewSortOrder.DirectoryNameAscending),
                CancellationToken.None));
            SearchViewSummaryDto view = (await views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.Files,
                    RefinementSettings: new SearchSettingsPatchDto(
                        NecessaryCond: new FileConditionsPatchDto(
                            Formats: new CollectionPatchDto<string>(Replace: ["flac"])),
                        PreferredCond: new FileConditionsPatchDto(
                            Formats: new CollectionPatchDto<string>(Replace: ["flac"]))),
                    FilterText: "flac",
                    SortOrder: ServerSearchViewSortOrder.ItemSizeDescending),
                CancellationToken.None))!;
            Assert.AreEqual(ServerSearchViewSortOrder.ItemSizeDescending, view.SortOrder);
            SearchViewDefinitionDto liveDefinition = (views.GetDefinition(view.ViewId, CancellationToken.None))!;
            Assert.AreEqual("flac", liveDefinition.Projection.FilterText);
            CollectionAssert.AreEqual(
                new[] { "flac" },
                liveDefinition.EffectiveSearchSettings.NecessaryCond!.Formats!.Replace!.ToArray());
            long revision = await PublishAsync(views, view.ViewId, () => receive(Response(
                "peer",
                [File(@"Music\Track.flac"), File(@"Music\Track.mp3")])));
            var invalidMode = new RefSelectionExpressionDto((RefSelectionMode)123, []);
            Assert.ThrowsExactly<ArgumentException>(() =>
                views.Selections.ResolveSelectionSettings(view.ViewId, new ResolveSearchViewSelectionSettingsRequestDto(revision, invalidMode), CancellationToken.None));
            Assert.ThrowsExactly<ArgumentException>(() =>
                views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(revision, invalidMode), CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                views.Selections.CommitSelectionAsync(view.ViewId,
                    new CommitSearchViewSelectionRequestDto(revision, invalidMode, Guid.NewGuid()),
                    CancellationToken.None));
            SearchViewSummaryDto summary = (views.Get(view.ViewId, CancellationToken.None))!;
            Assert.AreEqual(2L, summary.Counters.PublicFileCount);
            Assert.AreEqual(1L, summary.Counters.ProjectedFileCount);
            Assert.AreEqual(1L, summary.Counters.PreferredFileCount);
            SearchViewFilePageDto page = (views.GetFiles(view.ViewId, revision, null, 10, cancellationToken: CancellationToken.None))!;
            Assert.AreEqual(1, page.Items.Count);
            Assert.AreEqual(@"Music\Track.flac", page.Items[0].RemoteFilename);
            Assert.AreEqual(ServerSearchPreferenceTier.Preferred, page.Items[0].PreferenceTier);
            var completed = ObserveNext(views, view.ViewId);
            try
            {
                releaseSearch.TrySetResult();
                await completed.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                completed.Dispose();
            }
            await app.StopAsync();

            await using WebApplication restarted = global::Tests.TestServerHost.Build(
                [], Options(directory, ControllableSearchClientProxy.Create().Client),
                "http://127.0.0.1:0");
            await restarted.StartAsync();
            SearchViewCoordinator historicalViews = restarted.Services
                .GetRequiredService<SearchViewCoordinator>();
            var historicalPublished = new TaskCompletionSource<(Guid Id, long Revision)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHistoricalPublished(Sockseek.Server.Planning.SearchViewInfo published)
                => historicalPublished.TrySetResult((published.Id, published.Revision));
            historicalViews.ViewPublished += OnHistoricalPublished;
            try
            {
                SearchViewSummaryDto historical = (await historicalViews.CreateAsync(
                    jobId,
                    new CreateSearchViewRequestDto(
                        ServerSearchViewProjectionKind.Files,
                        RefinementSettings: new SearchSettingsPatchDto(
                            NecessaryCond: new FileConditionsPatchDto(
                                Formats: new CollectionPatchDto<string>(Replace: ["mp3"]))),
                        FilterText: "mp3",
                        SortOrder: ServerSearchViewSortOrder.ItemSizeAscending),
                    CancellationToken.None))!;
                Assert.AreEqual(ServerSearchViewSortOrder.ItemSizeAscending,
                    historical.SortOrder);
                SearchViewDefinitionDto retainedDefinition = (historicalViews.GetDefinition(historical.ViewId, CancellationToken.None))!;
                Assert.AreEqual("mp3", retainedDefinition.Projection.FilterText);
                CollectionAssert.AreEqual(
                    new[] { "mp3" },
                    retainedDefinition.EffectiveSearchSettings.NecessaryCond!.Formats!.Replace!.ToArray());
                (Guid id, long historicalRevision) = await historicalPublished.Task
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(historical.ViewId, id);
                SearchViewFilePageDto historicalPage = (historicalViews.GetFiles(historical.ViewId, historicalRevision, null, 10, cancellationToken: CancellationToken.None))!;
                Assert.AreEqual(1, historicalPage.Items.Count);
                Assert.AreEqual(@"Music\Track.mp3", historicalPage.Items[0].RemoteFilename);
            }
            finally
            {
                historicalViews.ViewPublished -= OnHistoricalPublished;
            }
            await restarted.StopAsync();
        }
        finally
        {
            releaseSearch.TrySetResult();
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(ServerSearchViewProjectionKind.GenericDirectories)]
    [DataRow(ServerSearchViewProjectionKind.Files)]
    [DataRow(ServerSearchViewProjectionKind.AlbumDirectories)]
    [DataRow(ServerSearchViewProjectionKind.TrackGroups)]
    [DataRow(ServerSearchViewProjectionKind.AlbumGroups)]
    public async Task LiveConditionRefinementsKeepRowsTiersAndCountsConsistentThroughCompletion(
        ServerSearchViewProjectionKind kind)
    {
        bool albumMode = kind is ServerSearchViewProjectionKind.AlbumDirectories
            or ServerSearchViewProjectionKind.AlbumGroups;
        string directory = System.IO.Directory.CreateTempSubdirectory("sockseek-live-conditions-").FullName;
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int searchCalls = 0;
        client.Search = async (receive, token) =>
        {
            Interlocked.Increment(ref searchCalls);
            started.TrySetResult(receive);
            await finish.Task.WaitAsync(token);
        };
        try
        {
            string expectedMode = albumMode ? "album" : kind == ServerSearchViewProjectionKind.GenericDirectories ? "generic" : "song";
            var options = Options(directory, client.Client);
            options.Profiles = new ProfileCatalog { AutoProfiles = [new SettingsProfile
            {
                Name = "selection", Condition = $"input-type == \"manual-search\" && download-mode == \"{expectedMode}\" && !interactive",
                Download = new SettingsPatch([new("Search.MaxStaleTime", 4321)]),
            }], NamedProfiles = [new SettingsProfile
            {
                Name = "download", Download = new SettingsPatch([new("Search.MaxStaleTime", 5432)]),
            }] };
            await using WebApplication app = global::Tests.TestServerHost.Build(
                [], options, "http://127.0.0.1:0");
            await app.StartAsync();
            var supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            JobSummaryDto source = kind switch
            {
                ServerSearchViewProjectionKind.GenericDirectories => await supervisor.Submissions.SubmitSearchJobAsync(
                    new SubmitSearchJobRequestDto("Track"), CancellationToken.None),
                ServerSearchViewProjectionKind.Files or ServerSearchViewProjectionKind.TrackGroups =>
                    await supervisor.Submissions.SubmitTrackSearchJobAsync(new SubmitTrackSearchJobRequestDto(
                        new SongQueryDto(Artist: "Artist", Title: "Track")), CancellationToken.None),
                _ => await supervisor.Submissions.SubmitAlbumSearchJobAsync(new SubmitAlbumSearchJobRequestDto(
                    new AlbumQueryDto(Artist: "Artist", Album: "Album")), CancellationToken.None),
            };
            Action<SearchResponse> receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var views = app.Services.GetRequiredService<SearchViewCoordinator>();
            CreateSearchViewRequestDto Definition(string format,
                bool? acceptMissingPreferredProperties = null,
                bool? acceptMissingNecessaryProperties = null) => new(kind,
                RefinementSettings: new SearchSettingsPatchDto(
                    NecessaryCond: new FileConditionsPatchDto(
                        Formats: new CollectionPatchDto<string>(Replace: [format]),
                        MinBitrate: acceptMissingNecessaryProperties.HasValue ? 256 : null,
                        MinSampleRate: acceptMissingNecessaryProperties.HasValue ? 44_100 : null,
                        AcceptMissingProps: acceptMissingNecessaryProperties),
                    PreferredCond: new FileConditionsPatchDto(
                        Formats: new CollectionPatchDto<string>(Replace: [format]),
                        MinBitrate: 256, MinSampleRate: 44_100,
                        AcceptMissingProps: acceptMissingPreferredProperties),
                    MinGroupShares: 1));
            Soulseek.File[] Files(int bitrate) => [.. new[] { "flac", "mp3" }.Select(format =>
                new Soulseek.File(1, $@"Música\Artist\Album\01 - Track.{format}", 1_000, "." + format,
                [new(FileAttributeType.BitRate, bitrate), new(FileAttributeType.Length, 180),
                 new(FileAttributeType.SampleRate, 44_100)]))];

            SearchViewSummaryDto flac = (await views.CreateAsync(
                source.JobId, Definition("flac"), CancellationToken.None))!;
            using (var cancelled = new CancellationTokenSource())
            {
                var cancelledWait = views.GetUpdatesAsync(flac.ViewId, flac.Revision,
                    waitForChange: true, cancellationToken: cancelled.Token);
                cancelled.Cancel();
                try { await cancelledWait; Assert.Fail("A cancelled update wait must stop."); }
                catch (OperationCanceledException) { }
            }
            using var updateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firstPublication = views.GetUpdatesAsync(flac.ViewId, flac.Revision,
                waitForChange: true, cancellationToken: updateTimeout.Token);
            Assert.IsFalse(firstPublication.IsCompleted);
            receive(Response("first-peer", Files(320)));
            SearchViewUpdateDto firstUpdate = (await firstPublication)!;
            Assert.IsTrue(firstUpdate.HasNewRevision);
            flac = firstUpdate.Summary;
            Assert.AreEqual(2L, flac.Counters.PublicFileCount);
            Assert.IsFalse(flac.IsComplete);
            Assert.AreEqual(albumMode ? 2L : 1L, flac.Counters.ProjectedFileCount);
            Assert.AreEqual(1L, flac.Counters.PreferredFileCount);
            Assert.AreEqual(albumMode ? 1L : 0L, flac.Counters.OtherFileCount);
            SearchViewSummaryDto initialFlac = flac;

            // This is a new projection over the same still-running discovery,
            // not a second network search or a post-completion rebuild.
            SearchViewSummaryDto mp3 = (await views.CreateAsync(
                source.JobId, Definition("mp3"), CancellationToken.None))!;
            mp3 = await WaitForConditionViewAsync(views, mp3.ViewId,
                summary => summary.Counters.PublicFileCount == 2, () => { });
            Assert.IsFalse(mp3.IsComplete);
            Assert.AreEqual(albumMode ? 2L : 1L, mp3.Counters.ProjectedFileCount);
            Assert.AreEqual(1L, mp3.Counters.PreferredFileCount);

            flac = await WaitForConditionViewAsync(views, flac.ViewId,
                summary => summary.Counters.PublicFileCount == 4,
                () => receive(Response("FIRST-PEER", Files(128))));
            mp3 = await WaitForConditionViewAsync(views, mp3.ViewId,
                summary => summary.Counters.PublicFileCount == 4, () => { });
            var liveRows = new Dictionary<Guid, string[]>();
            foreach ((SearchViewSummaryDto view, string format) in new[] { (flac, "flac"), (mp3, "mp3") })
            {
                Assert.IsFalse(view.IsComplete);
                Assert.AreEqual(albumMode ? 4L : 2L, view.Counters.ProjectedFileCount);
                Assert.AreEqual(1L, view.Counters.PreferredFileCount);
                Assert.AreEqual(albumMode ? 3L : 1L, view.Counters.OtherFileCount);
                SearchViewFileDto[] files = await ReadConditionFilesAsync(views, view);
                Assert.AreEqual(albumMode ? 4 : 2, files.Length);
                // Soulseek identities stay exact through grouping/refinement:
                // these peers differ only by case, and the path contains Unicode.
                CollectionAssert.AreEquivalent(new[] { "first-peer", "FIRST-PEER" },
                    files.Select(file => file.Peer.Username).Distinct(StringComparer.Ordinal).ToArray());
                Assert.IsTrue(files.All(file => file.RemoteFilename is
                    @"Música\Artist\Album\01 - Track.flac" or @"Música\Artist\Album\01 - Track.mp3"));
                // Album quality conditions admit the whole mixed folder; the
                // other projections filter individual files. Preference tiers
                // still describe each file's public preferred-condition facts.
                if (!albumMode)
                    Assert.IsTrue(files.All(file => file.RemoteFilename.EndsWith("." + format, StringComparison.Ordinal)));
                foreach (SearchViewFileDto file in files)
                    Assert.AreEqual(file.Peer.Username == "first-peer"
                        && file.RemoteFilename.EndsWith("." + format, StringComparison.Ordinal)
                            ? ServerSearchPreferenceTier.Preferred : ServerSearchPreferenceTier.Other,
                        file.PreferenceTier);
                liveRows[view.ViewId] = ConditionRowSnapshots(files);
                var count = (views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(view.Revision,
                        new RefSelectionExpressionDto(RefSelectionMode.AllExcept, [])), CancellationToken.None))!;
                long selectedFiles = kind switch
                {
                    ServerSearchViewProjectionKind.TrackGroups => 1,
                    ServerSearchViewProjectionKind.AlbumGroups => 2,
                    ServerSearchViewProjectionKind.AlbumDirectories => 4,
                    _ => 2,
                };
                Assert.AreEqual(selectedFiles, count.PublicFileCount);
            }
            // Paging an earlier revision must not pick up the late peer.
            Assert.AreEqual(albumMode ? 2 : 1, (await ReadConditionFilesAsync(views, initialFlac)).Length);

            // Some peers omit all music attributes. Unknown stays unknown on
            // the wire; accepting it is an explicit condition policy, not zero.
            flac = await WaitForConditionViewAsync(views, flac.ViewId,
                summary => summary.Counters.PublicFileCount == 6,
                () => receive(Response("missing-metadata", [.. new[] { "flac", "mp3" }.Select(format =>
                    new Soulseek.File(1, $@"Música\Artist\Album\01 - Track.{format}", 1_000, "." + format, []))])));
            mp3 = await WaitForConditionViewAsync(views, mp3.ViewId,
                summary => summary.Counters.PublicFileCount == 6, () => { });
            foreach (SearchViewSummaryDto view in new[] { flac, mp3 })
            {
                Assert.AreEqual(albumMode ? 6L : 3L, view.Counters.ProjectedFileCount);
                Assert.AreEqual(2L, view.Counters.PreferredFileCount);
                SearchViewFileDto[] rows = await ReadConditionFilesAsync(views, view);
                SearchViewFileDto[] unknown = rows.Where(file => file.Peer.Username == "missing-metadata").ToArray();
                Assert.AreEqual(albumMode ? 2 : 1, unknown.Length);
                foreach (SearchViewFileDto file in unknown)
                {
                    Assert.IsNull(file.File.BitRate);
                    Assert.IsNull(file.File.SampleRate);
                    Assert.IsNull(file.File.Length);
                }
                liveRows[view.ViewId] = ConditionRowSnapshots(rows);
            }

            var additionalViews = new List<SearchViewSummaryDto>();
            foreach ((bool? preferred, bool? necessary) in new (bool?, bool?)[]
                { (false, null), (null, false), (null, true) })
            {
                SearchViewSummaryDto view = (await views.CreateAsync(source.JobId,
                    Definition("flac", preferred, necessary), CancellationToken.None))!;
                view = await WaitForConditionViewAsync(views, view.ViewId,
                    summary => summary.Counters.PublicFileCount == 6, () => { });
                SearchViewFileDto[] rows = await ReadConditionFilesAsync(views, view);
                string[] expectedPeers = necessary switch
                {
                    false => ["first-peer"],
                    true => ["first-peer", "missing-metadata"],
                    null => ["first-peer", "FIRST-PEER", "missing-metadata"],
                };
                CollectionAssert.AreEquivalent(expectedPeers,
                    rows.Select(file => file.Peer.Username).Distinct(StringComparer.Ordinal).ToArray());
                Assert.AreEqual((long)expectedPeers.Length * (albumMode ? 2 : 1), view.Counters.ProjectedFileCount);
                Assert.AreEqual(preferred is false || necessary is false ? 1L : 2L, view.Counters.PreferredFileCount);
                var selection = (views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(view.Revision,
                        new RefSelectionExpressionDto(RefSelectionMode.AllExcept, [])), CancellationToken.None))!;
                long expectedSelectedFiles = kind switch
                {
                    // Unknown duration is a separate group from 180-second
                    // tracks at the default three-second grouping tolerance.
                    ServerSearchViewProjectionKind.TrackGroups => necessary is false ? 1 : 2,
                    ServerSearchViewProjectionKind.AlbumGroups => necessary is false ? 2 : 4,
                    ServerSearchViewProjectionKind.AlbumDirectories => expectedPeers.Length * 2,
                    _ => expectedPeers.Length,
                };
                Assert.AreEqual(expectedSelectedFiles, selection.PublicFileCount);
                Assert.AreEqual(0L, selection.LockedFileCount);
                if (preferred is false)
                {
                    SearchViewFileDto unknown = rows.First(file => file.Peer.Username == "missing-metadata");
                    Assert.AreEqual(ServerSearchPreferenceTier.Other, unknown.PreferenceTier);
                    CollectionAssert.Contains(unknown.UnsatisfiedPreferredConditions.ToArray(), ServerSearchPreferenceCondition.Bitrate);
                    CollectionAssert.Contains(unknown.UnsatisfiedPreferredConditions.ToArray(), ServerSearchPreferenceCondition.SampleRate);
                }
                liveRows[view.ViewId] = ConditionRowSnapshots(rows);
                additionalViews.Add(view);
            }
            foreach (SearchViewSummaryDto before in new[] { flac, mp3 }.Concat(additionalViews))
            {
                SearchViewSummaryDto completed = await WaitForConditionViewAsync(views, before.ViewId,
                    summary => summary.IsComplete, () => finish.TrySetResult());
                Assert.AreEqual(before.Counters, completed.Counters);
                CollectionAssert.AreEqual(liveRows[before.ViewId],
                    ConditionRowSnapshots(await ReadConditionFilesAsync(views, completed)));
            }
            Assert.AreEqual(1, searchCalls, "Refining a live View must not repeat the network search.");
            var completedView = views.Get(flac.ViewId, CancellationToken.None)!;
            string selectedRef = kind switch
            {
                ServerSearchViewProjectionKind.Files => views.GetFiles(flac.ViewId, completedView.Revision, null, 1, cancellationToken: CancellationToken.None)!.Items[0].Ref,
                ServerSearchViewProjectionKind.TrackGroups => views.GetTrackGroups(flac.ViewId, completedView.Revision, null, 1, CancellationToken.None)!.Items[0].Ref,
                ServerSearchViewProjectionKind.AlbumGroups => views.GetAlbumGroups(flac.ViewId, completedView.Revision, null, 1, CancellationToken.None)!.Items[0].Ref,
                _ => views.GetDirectories(flac.ViewId, completedView.Revision, null, 1, cancellationToken: CancellationToken.None)!.Items[0].Ref.Ref,
            };
            var committedSelection = new RefSelectionExpressionDto(RefSelectionMode.Only, [selectedRef]);
            var selectedOptions = new SubmissionOptionsDto(ProfileNames: ["download"]);
            var defaults = views.Selections.ResolveSelectionSettings(flac.ViewId,
                new(completedView.Revision, committedSelection, Options: selectedOptions), CancellationToken.None)!.Variants.Single();
            Assert.AreEqual(albumMode ? ServerJobKind.Album : expectedMode == "song" ? ServerJobKind.Song : ServerJobKind.RemoteDirectory, defaults.JobKind);
            Assert.AreEqual(5432, defaults.Settings.Values.Search!.MaxStaleTime);
            CollectionAssert.AreEqual(new[] { new AppliedProfileDto("selection", "automatic"), new AppliedProfileDto("download", "selected") }, defaults.AppliedProfiles.ToArray());
            var receipt = (await views.Selections.CommitSelectionAsync(flac.ViewId,
                new(completedView.Revision, committedSelection, Guid.NewGuid(), Options: selectedOptions,
                    UseRepresentativeWhenUnchosen: kind is ServerSearchViewProjectionKind.TrackGroups or ServerSearchViewProjectionKind.AlbumGroups), CancellationToken.None))!;
            Assert.AreEqual(1L, receipt.SubmittedCount);
            var accepted = (await app.Services.GetRequiredService<PersistenceCoordinator>().Submissions!
                .GetSubmissionAsync(receipt.SubmissionId!.Value, CancellationToken.None))!;
            var (job, settings) = Sockseek.Core.Planning.SubmissionSpecificationCodec.Deserialize(accepted.SpecificationJson).Materialize();
            Assert.AreEqual(JobSelectionSource.ManualSearch, job.SelectionSource);
            Assert.AreEqual(defaults.Settings.Values.Search.MaxStaleTime, settings.Search.MaxStaleTime);
            CollectionAssert.AreEqual(defaults.AppliedProfiles.Select(profile => new AppliedProfile(profile.Name, profile.Source)).ToArray(), settings.AppliedProfiles);
            Assert.IsTrue(ProfileConditionEvaluator.Satisfied($"download-mode == \"{expectedMode}\"", settings, job));
            if (job is AlbumJob album) Assert.AreEqual(AlbumValidationRequirement.UserAccepted, album.ValidationRequirement);
            if (job is SongJob song) Assert.IsNotNull(song.ExactTarget);
            if (kind == ServerSearchViewProjectionKind.AlbumDirectories)
            {
                var childRefs = views.GetDirectoryFiles(flac.ViewId, selectedRef, completedView.Revision, null, 100, CancellationToken.None)!
                    .Items.Select(child => child.Ref).ToArray();
                var excludeObserved = new CommitSearchViewSelectionRequestDto(completedView.Revision, committedSelection,
                    Guid.NewGuid(), ExcludedFileRefs: childRefs);
                var fullFolder = (await views.Selections.CommitSelectionAsync(flac.ViewId, excludeObserved, CancellationToken.None))!;
                Assert.AreEqual(1L, fullFolder.SubmittedCount, "A whole folder can contain files not observed by search.");
                var observedOnly = (await views.Selections.CommitSelectionAsync(flac.ViewId, excludeObserved with
                {
                    IdempotencyKey = Guid.NewGuid(),
                    Options = new SubmissionOptionsDto(DownloadSettings: new DownloadSettingsPatchDto(
                        Search: new SearchSettingsPatchDto(NoBrowseFolder: true))),
                }, CancellationToken.None))!;
                Assert.AreEqual(0L, observedOnly.SubmittedCount);
                Assert.AreEqual(1L, observedOnly.SkippedCount);
            }
            await app.StopAsync();
        }
        finally
        {
            finish.TrySetResult();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static string[] ConditionRowSnapshots(IEnumerable<SearchViewFileDto> files)
        => files.OrderBy(file => file.Ref, StringComparer.Ordinal)
            .Select(file => JsonSerializer.Serialize(file)).ToArray();

    private static async Task<SearchViewSummaryDto> WaitForConditionViewAsync(
        SearchViewCoordinator views, Guid viewId, Func<SearchViewSummaryDto, bool> ready, Action action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        action();
        while (true)
        {
            SearchViewSummaryDto summary = (views.Get(viewId, timeout.Token))!;
            if (ready(summary)) return summary;
            await views.GetUpdatesAsync(viewId, summary.Revision, waitForChange: true, timeout.Token);
        }
    }

    private static async Task<SearchViewFileDto[]> ReadConditionFilesAsync(
        SearchViewCoordinator views, SearchViewSummaryDto view)
    {
        async Task<List<T>> Pages<T>(Func<string?, Task<(IReadOnlyList<T> Items, string? Next)>> read)
        {
            var rows = new List<T>();
            string? cursor = null;
            do { var page = await read(cursor); rows.AddRange(page.Items); cursor = page.Next; }
            while (cursor != null);
            return rows;
        }
        async Task<List<SearchViewDirectoryDto>> Directories()
            => await Pages<SearchViewDirectoryDto>(async cursor =>
            {
                var page = (views.GetDirectories(view.ViewId, view.Revision, cursor, 1, cancellationToken: CancellationToken.None))!;
                return (page.Items, page.NextCursor);
            });
        async Task<IEnumerable<SearchViewFileDto>> FolderFiles(SearchViewDirectoryDto folder)
            => (await Pages<SearchViewDirectoryFileDto>(async cursor =>
            {
                var page = (views.GetDirectoryFiles(view.ViewId, folder.Ref.Ref, view.Revision, cursor, 1, CancellationToken.None))!;
                return (page.Items, page.NextCursor);
            })).Select(row => row.File);
        if (view.Kind == ServerSearchViewProjectionKind.Files)
            return [.. await Pages<SearchViewFileDto>(async cursor =>
            {
                var page = (views.GetFiles(view.ViewId, view.Revision, cursor, 1, cancellationToken: CancellationToken.None))!;
                return (page.Items, page.NextCursor);
            })];
        if (view.Kind is ServerSearchViewProjectionKind.GenericDirectories or ServerSearchViewProjectionKind.AlbumDirectories)
        {
            var children = new List<SearchViewFileDto>();
            foreach (var folder in await Directories()) children.AddRange(await FolderFiles(folder));
            return children.ToArray();
        }
        var files = new List<SearchViewFileDto>();
        if (view.Kind == ServerSearchViewProjectionKind.TrackGroups)
        {
            var groups = await Pages<SearchViewTrackGroupDto>(async cursor =>
            {
                var page = (views.GetTrackGroups(view.ViewId, view.Revision, cursor, 1, CancellationToken.None))!;
                return (page.Items, page.NextCursor);
            });
            foreach (var group in groups)
                files.AddRange(await Pages<SearchViewFileDto>(async cursor =>
                {
                    var page = (views.GetTrackGroupOptions(view.ViewId, group.Ref, view.Revision, cursor, 1, CancellationToken.None))!;
                    return (page.Items, page.NextCursor);
                }));
        }
        else
        {
            var groups = await Pages<SearchViewAlbumGroupDto>(async cursor =>
            {
                var page = (views.GetAlbumGroups(view.ViewId, view.Revision, cursor, 1, CancellationToken.None))!;
                return (page.Items, page.NextCursor);
            });
            foreach (var group in groups)
            {
                var folders = await Pages<SearchViewDirectoryDto>(async cursor =>
                {
                    var page = (views.GetAlbumGroupOptions(view.ViewId, group.Ref, view.Revision, cursor, 1, CancellationToken.None))!;
                    return (page.Items, page.NextCursor);
                });
                foreach (var folder in folders) files.AddRange(await FolderFiles(folder));
            }
        }
        return files.ToArray();
    }

    [TestMethod]
    public async Task SubmittedGroupResultsIsTheDefaultSearchViewProjection()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-group-view-default-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var client = ControllableSearchClientProxy.Create();
            var searchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Search = async (_, cancellationToken) =>
            {
                searchStarted.TrySetResult();
                await releaseSearch.Task.WaitAsync(cancellationToken);
            };
            Guid trackId;
            await using (WebApplication app = global::Tests.TestServerHost.Build(
                [], Options(directory, client.Client), "http://127.0.0.1:0"))
            {
                await app.StartAsync();
                EngineSupervisor supervisor = app.Services.GetRequiredService<EngineSupervisor>();

                JobSummaryDto track = await supervisor.Submissions.SubmitTrackSearchJobAsync(
                    new SubmitTrackSearchJobRequestDto(
                        new SongQueryDto(Title: "Track"), GroupResults: true),
                    CancellationToken.None);
                trackId = track.JobId;
                await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SearchViewCoordinator views = app.Services.GetRequiredService<SearchViewCoordinator>();
                Assert.AreEqual(ServerSearchViewProjectionKind.TrackGroups,
                    (await views.CreateAsync(trackId, new CreateSearchViewRequestDto(), CancellationToken.None))!.Kind);
                var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnJobUpserted(JobSummaryDto summary)
                {
                    if (summary.JobId == trackId && summary.LifecycleState == ServerJobLifecycleState.Terminal)
                        terminal.TrySetResult();
                }
                supervisor.StateStore.JobUpserted += OnJobUpserted;
                try
                {
                    releaseSearch.TrySetResult();
                    await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    supervisor.StateStore.JobUpserted -= OnJobUpserted;
                }
                await app.StopAsync();
            }

            await using (WebApplication restarted = global::Tests.TestServerHost.Build(
                [], Options(directory, ControllableSearchClientProxy.Create().Client), "http://127.0.0.1:0"))
            {
                await restarted.StartAsync();
                JobDetailDto retained = (await restarted.Services.GetRequiredService<JobReadService>()
                    .GetJobAsync(trackId))!;
                if (retained.Payload is not SearchJobPayloadDto payload)
                    throw new AssertFailedException("Restarted search has no retained search payload.");
                Assert.IsTrue(payload.GroupResults);
                SearchViewCoordinator views = restarted.Services.GetRequiredService<SearchViewCoordinator>();
                Assert.AreEqual(ServerSearchViewProjectionKind.TrackGroups,
                    (await views.CreateAsync(trackId, new CreateSearchViewRequestDto(), CancellationToken.None))!.Kind);
                await restarted.StopAsync();
            }
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LiveSearchViewsDoNotRequirePersistence()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-search-view-unavailable-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions
        {
            Engine = new EngineSettings { MockFilesDir = directory },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = false,
                DataDirectory = Path.Combine(directory, "data"),
            },
        });
        var supervisor = new EngineSupervisor(options);
        var persistence = new PersistenceCoordinator(options);
        var commits = new SubmissionCommitCoordinator(persistence);
        await using var views = new SearchViewCoordinator(
            supervisor,
            persistence,
            commits,
            new SearchViewCursorCodec(),
            NullLogger<SearchViewCoordinator>.Instance);
        try
        {
            await persistence.StartAsync(CancellationToken.None);
            await views.StartAsync(CancellationToken.None);

            JobSummaryDto direct = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("still accepted"),
                CancellationToken.None);
            var view = await views.CreateAsync(direct.JobId,
                new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.Files), CancellationToken.None);
            Assert.IsNotNull(view, "Live observations and temporary display state need no daemon database.");
            Assert.AreEqual(ServerJobKind.Search, direct.Kind);
            Assert.IsNotNull(direct.SubmissionId);
        }
        finally
        {
            await views.StopAsync(CancellationToken.None);
            await persistence.StopAsync(CancellationToken.None);
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task RestartRequiresANewViewAndRebuildsFromRetainedRawResults(bool captureCompletion, bool upgradeFailureThresholds)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-search-view-restart-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        Guid viewId;
        Guid sourceJobId;
        long prefixRevision;
        try
        {
            var firstClient = ControllableSearchClientProxy.Create();
            var searchStarted = new TaskCompletionSource<Action<SearchResponse>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var holdSearch = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            firstClient.Search = async (handler, cancellationToken) =>
            {
                searchStarted.TrySetResult(handler);
                await holdSearch.Task.WaitAsync(cancellationToken);
            };
            await using (WebApplication first = global::Tests.TestServerHost.Build(
                [],
                Options(directory, firstClient.Client),
                "http://127.0.0.1:0"))
            {
                EngineSupervisor supervisor = first.Services.GetRequiredService<EngineSupervisor>();
                await first.StartAsync();
                JobSummaryDto job = await supervisor.Submissions.SubmitSearchJobAsync(
                    new SubmitSearchJobRequestDto("Restart Track"),
                    CancellationToken.None);
                Action<SearchResponse> receive = await searchStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                SearchViewCoordinator views = first.Services.GetRequiredService<SearchViewCoordinator>();
                SearchViewSummaryDto created = (await views.CreateAsync(
                    job.JobId,
                    new CreateSearchViewRequestDto(
                        ServerSearchViewProjectionKind.GenericDirectories,
                        new SongQueryDto(Title: "Restart Track"),
                        IncludeFullResults: true),
                    CancellationToken.None))!;
                sourceJobId = job.JobId;
                viewId = created.ViewId;
                prefixRevision = await PublishAsync(views, viewId, () => receive(Response(
                    "restart-peer",
                    [File(@"Music\Restart Track.flac")])));
                if (!captureCompletion)
                {
                    // Model losing the completion tail after a durable prefix.
                    // A graceful host stop now joins roots and captures it.
                    await views.StopAsync(CancellationToken.None);
                    supervisor.StateStore.SetJobPersistence(null);
                }
                await first.StopAsync();
            }

            if (upgradeFailureThresholds)
            {
                await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    { Pooling = false, DataSource = Path.Combine(directory, "sockseek.db") }.ToString());
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                // Reproduce a search saved by the score-based implementation,
                // before the failure-count migration existed.
                command.CommandText = """
                    UPDATE jobs SET payload_json=json_set(payload_json,
                        '$.Definition.ProjectionSettings.DownrankOn', -1,
                        '$.Definition.ProjectionSettings.IgnoreOn', -2) WHERE id=$job;
                    UPDATE submission_specifications SET specification_json=json_set(specification_json,
                        '$.search.projectionSettings.downrankOn', -1,
                        '$.search.projectionSettings.ignoreOn', -2);
                    DELETE FROM __EFMigrationsHistory WHERE MigrationId='20261113220000_PositivePeerFailureThresholds';
                    """;
                command.Parameters.AddWithValue("$job", sourceJobId);
                await command.ExecuteNonQueryAsync();
            }

            var secondClient = ControllableSearchClientProxy.Create();
            await using (WebApplication second = global::Tests.TestServerHost.Build(
                [],
                Options(directory, secondClient.Client),
                "http://127.0.0.1:0"))
            {
                SearchViewCoordinator views = second.Services.GetRequiredService<SearchViewCoordinator>();
                try
                {
                    await second.StartAsync();
                    var untouched = views.Cache.Get(viewId, CancellationToken.None);
                    Assert.IsNull(untouched, "Restart discards temporary Views and performs no eager projection.");
                    Assert.IsNull(views.GetDirectories(viewId, prefixRevision, null, 10, cancellationToken: CancellationToken.None));
                    var fresh = (await views.CreateAsync(sourceJobId,
                        new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.GenericDirectories,
                            new SongQueryDto(Title: "Restart Track"), IncludeFullResults: true), CancellationToken.None))!;
                    SearchViewSummaryDto summary = await WaitForConditionViewAsync(views, fresh.ViewId,
                        value => value.IsComplete, () => { });
                    Assert.IsTrue(summary.IsComplete);
                    Assert.AreEqual(captureCompletion
                        ? SearchViewRetentionState.Complete
                        : SearchViewRetentionState.Incomplete, summary.RetentionState);
                    Assert.AreEqual(1L, summary.Counters.PublicFileCount);
                    Assert.AreEqual(1L, summary.Counters.ProjectedFileCount);
                    SearchViewDirectoryPageDto recoveredDirectories = (views.GetDirectories(fresh.ViewId, summary.Revision, null, 10, cancellationToken: CancellationToken.None))!;
                    Assert.AreEqual(1, recoveredDirectories.Items.Count);
                    Assert.AreEqual(@"Music", recoveredDirectories.Items[0].Ref.FolderPath);
                }
                finally
                {
                    await second.StopAsync();
                }
            }
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LiveRevisionsExposeExactPrefixAndCompletionUsesTheSameProjection()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "sockseek-live-search-view-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var client = ControllableSearchClientProxy.Create();
        var searchStarted = new TaskCompletionSource<Action<SearchResponse>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSearch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (handler, cancellationToken) =>
        {
            searchStarted.TrySetResult(handler);
            await releaseSearch.Task.WaitAsync(cancellationToken);
        };
        client.Browse = _ => new BrowseResponse(
        [
            new Soulseek.Directory(
                "Music",
                [
                    File("Track one.flac"),
                    File("Cover.jpg"),
                ]),
        ]);
        await using WebApplication app = global::Tests.TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings
            {
                Username = "local",
                Password = "password",
                ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
            ClientFactory = _ => client.Client,
        }, "http://127.0.0.1:0");

        try
        {
            await app.StartAsync();
            EngineSupervisor supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            JobSummaryDto job = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("Track"),
                CancellationToken.None);
            Action<SearchResponse> receive = await searchStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            SearchViewCoordinator views = app.Services.GetRequiredService<SearchViewCoordinator>();
            SearchViewSummaryDto view = (await views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.Files),
                CancellationToken.None))!;
            SearchViewSummaryDto directoryView = (await views.CreateAsync(
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.GenericDirectories,
                    new SongQueryDto(Title: "Track"),
                    IncludeFullResults: true),
                CancellationToken.None))!;

            var firstDirectoryPublished = ObserveNext(views, directoryView.ViewId);
            long firstRevision;
            try
            {
                firstRevision = await PublishAsync(views, view.ViewId, () => receive(Response(
                    "first",
                    [File(@"Music\Track one.flac")])));
                _ = await firstDirectoryPublished.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                firstDirectoryPublished.Dispose();
            }
            SearchViewSummaryDto first = (views.Get(view.ViewId, CancellationToken.None))!;
            Assert.IsFalse(first.IsComplete);
            Assert.AreEqual(1L, first.Counters.PublicFileCount);
            Assert.AreEqual(1L, first.Counters.ProjectedFileCount);
            SearchViewFilePageDto firstPage = (views.GetFiles(view.ViewId, firstRevision, cursor: null, limit: 10, cancellationToken: CancellationToken.None))!;
            Assert.AreEqual(1, firstPage.Items.Count);
            Assert.AreEqual("first", firstPage.Items[0].Peer.Username);
            Assert.IsTrue(firstPage.Items[0].NecessaryConditionsSatisfied);
            SearchViewSummaryDto firstDirectorySummary = (views.Get(directoryView.ViewId, CancellationToken.None))!;
            Assert.AreEqual(1L, firstDirectorySummary.Counters.TopLevelItemCount);
            SearchViewDirectoryPageDto firstDirectories = (views.GetDirectories(directoryView.ViewId, firstDirectorySummary.Revision, null, 10, cancellationToken: CancellationToken.None))!;
            Assert.AreEqual(1, firstDirectories.Items.Count);
            Assert.AreEqual(@"Music", firstDirectories.Items[0].Ref.FolderPath);
            Assert.AreEqual(1, firstDirectories.Items[0].InitialFiles?.Items.Count);
            Assert.IsNull(firstDirectories.Items[0].InitialFiles?.NextCursor);
            SearchViewDirectoryFilePageDto firstChildren = (views.GetDirectoryFiles(directoryView.ViewId, firstDirectories.Items[0].Ref.Ref, firstDirectorySummary.Revision, null, 10, CancellationToken.None))!;
            Assert.AreEqual("Track one.flac", firstChildren.Items.Single().RelativePath);
            Assert.AreEqual(firstDirectories.Items[0].Ref, firstChildren.DirectorySummary.Ref);
            Assert.AreEqual(firstDirectories.Items[0].PublicMatchingFileCount,
                firstChildren.DirectorySummary.PublicMatchingFileCount);
            SearchViewSummaryDto preexistingSibling = await CreateAndObserveAsync(
                views,
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.GenericDirectories,
                    new SongQueryDto(Title: "Track"),
                    IncludeFullResults: true,
                    FilterText: "Music"));

            var retrievalPublished = ObserveNext(views, directoryView.ViewId);
            var retrievalRequest = new RetrieveSearchViewDirectoryRequestDto(
                firstDirectorySummary.Revision,
                firstDirectories.Items[0].Ref,
                Guid.NewGuid());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                views.StartDirectoryRetrievalAsync(directoryView.ViewId,
                    retrievalRequest with
                    {
                        IdempotencyKey = Guid.NewGuid(),
                        Directory = retrievalRequest.Directory with { Username = "different-peer" },
                    }, CancellationToken.None));
            JobSummaryDto? retrieval;
            try
            {
                retrieval = await views.StartDirectoryRetrievalAsync(
                    directoryView.ViewId,
                    retrievalRequest,
                    CancellationToken.None);
                _ = await retrievalPublished.Completion.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
            finally
            {
                retrievalPublished.Dispose();
            }
            Assert.IsNotNull(retrieval);
            Assert.AreEqual(retrievalRequest.IdempotencyKey, retrieval.SubmissionId);
            JobSummaryDto? replayedRetrieval = await views.StartDirectoryRetrievalAsync(
                directoryView.ViewId,
                retrievalRequest,
                CancellationToken.None);
            Assert.AreEqual(retrieval.JobId, replayedRetrieval?.JobId,
                "Retrying an accepted retrieval must return its original job.");
            SearchViewSummaryDto retrievedSummary = (views.Get(directoryView.ViewId, CancellationToken.None))!;
            Assert.IsTrue(retrievedSummary.Revision > firstDirectorySummary.Revision);
            SearchViewDirectoryDto retrieved = (views.GetDirectories(directoryView.ViewId, retrievedSummary.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items.Single();
            Assert.AreEqual(SearchViewDirectoryRetrievalState.Complete, retrieved.RetrievalState);
            Assert.AreEqual(2L, retrieved.RetrievedFileCount);
            SearchViewDirectoryFilePageDto retrievedChildren = (views.GetDirectoryFiles(directoryView.ViewId, retrieved.Ref.Ref, retrievedSummary.Revision, null, 10, CancellationToken.None))!;
            Assert.AreEqual(2, retrievedChildren.Items.Count);
            Assert.AreEqual(retrieved.Ref, retrievedChildren.DirectorySummary.Ref);
            Assert.AreEqual(retrieved.RetrievedFileCount,
                retrievedChildren.DirectorySummary.RetrievedFileCount);
            SearchViewDirectoryFilePageDto originalChildren = (views.GetDirectoryFiles(directoryView.ViewId, retrieved.Ref.Ref, firstDirectorySummary.Revision, null, 10, CancellationToken.None))!;
            Assert.AreEqual(1, originalChildren.Items.Count,
                "Retrieval must not mutate the issuing revision.");
            SearchViewUpdateDto preexistingUpdate = (await views.GetUpdatesAsync(
                preexistingSibling.ViewId,
                preexistingSibling.Revision,
                cancellationToken: CancellationToken.None))!;
            Assert.IsTrue(preexistingUpdate.HasNewRevision);
            SearchViewDirectoryDto preexistingDirectory = (views.GetDirectories(preexistingSibling.ViewId, preexistingUpdate.Summary.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items.Single();
            Assert.AreEqual(SearchViewDirectoryRetrievalState.Complete,
                preexistingDirectory.RetrievalState);
            Assert.AreEqual(2L, preexistingDirectory.RetrievedFileCount);
            SearchViewSummaryDto filteredSibling = await CreateAndObserveAsync(
                views,
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.GenericDirectories,
                    new SongQueryDto(Title: "Track"),
                    IncludeFullResults: true,
                    FilterText: "Track"));
            SearchViewDirectoryDto filteredDirectory = (views.GetDirectories(filteredSibling.ViewId, filteredSibling.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items.Single();
            Assert.AreEqual(SearchViewDirectoryRetrievalState.Complete,
                filteredDirectory.RetrievalState);
            Assert.AreEqual(1L, filteredDirectory.RetrievedFileCount);
            SearchViewDirectoryFilePageDto filteredFiles = (views.GetDirectoryFiles(filteredSibling.ViewId, filteredDirectory.Ref.Ref, filteredSibling.Revision, null, 10, CancellationToken.None))!;
            Assert.AreEqual("Track one.flac", filteredFiles.Items.Single().RelativePath);

            SearchViewSummaryDto unfilteredSibling = await CreateAndObserveAsync(
                views,
                job.JobId,
                new CreateSearchViewRequestDto(
                    ServerSearchViewProjectionKind.GenericDirectories,
                    new SongQueryDto(Title: "Track"),
                    IncludeFullResults: true));
            SearchViewDirectoryDto unfilteredDirectory = (views.GetDirectories(unfilteredSibling.ViewId, unfilteredSibling.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items.Single();
            Assert.AreEqual(SearchViewDirectoryRetrievalState.Complete,
                unfilteredDirectory.RetrievalState);
            Assert.AreEqual(2L, unfilteredDirectory.RetrievedFileCount);
            Assert.AreEqual(2, (views.GetDirectoryFiles(unfilteredSibling.ViewId, unfilteredDirectory.Ref.Ref, unfilteredSibling.Revision, null, 10, CancellationToken.None))!.Items.Count);
            var firstDirectoryCount = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    firstDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.Only,
                        [firstDirectories.Items.Single().Ref.Ref])), CancellationToken.None))!;
            Assert.AreEqual(1L, firstDirectoryCount.PublicFileCount);
            Assert.AreEqual(0L, firstDirectoryCount.LockedFileCount);
            Assert.AreEqual(1L, firstDirectoryCount.SelectedItemCount);
            var firstDirectoryReceipt = (await views.Selections.CommitSelectionAsync(
                directoryView.ViewId,
                new CommitSearchViewSelectionRequestDto(
                    firstDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.Only,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    Guid.NewGuid()),
                CancellationToken.None))!;
            var firstDirectorySubmission = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(firstDirectoryReceipt.SubmissionId!.Value, CancellationToken.None);
            var firstDirectoryRoot = (Sockseek.Core.Jobs.RemoteDirectoryJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(firstDirectorySubmission!.SpecificationJson).MaterializeJob();
            var firstDirectoryPlan = ((Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)
                firstDirectoryRoot.Source).Plan;
            CollectionAssert.AreEqual(new[] { @"Music\Track one.flac" },
                firstDirectoryPlan.Entries.Select(entry => entry.Target.Filename).ToArray(),
                "File Search root selection must not browse unprojected Cover.jpg.");
            var hiddenIncluded = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    firstDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    IncludedFileRefs: ["not-in-this-view"]), CancellationToken.None))!;
            Assert.AreEqual(0L, hiddenIncluded.SelectedItemCount);
            var hiddenIncludedReceipt = (await views.Selections.CommitSelectionAsync(
                directoryView.ViewId,
                new CommitSearchViewSelectionRequestDto(
                    firstDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    Guid.NewGuid(), IncludedFileRefs: ["not-in-this-view"]),
                CancellationToken.None))!;
            Assert.AreEqual(0L, hiddenIncludedReceipt.SubmittedCount);
            Assert.AreEqual("missing-ref", hiddenIncludedReceipt.RejectionReasons.Single().Reason);
            Assert.ThrowsExactly<ArgumentException>(() => views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    firstDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.Only,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    ExcludedFileRefs: [firstChildren.Items.Single().File.Ref],
                    ExcludedFileIdentities: new Dictionary<string, FileCandidateRefDto>
                    {
                        [firstChildren.Items.Single().File.Ref] = new("different-peer", "Different\\File.txt"),
                    }), CancellationToken.None));
            var retrievedDirectoryCount = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    retrievedSummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept, [])), CancellationToken.None))!;
            Assert.AreEqual(2L, retrievedDirectoryCount.PublicFileCount,
                "A retrieved directory counts the children visible at its new revision, not only search matches.");
            var explicitlySelectedRetrievedCount = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    retrievedSummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.Only, [retrieved.Ref.Ref])), CancellationToken.None))!;
            Assert.AreEqual(2L, explicitlySelectedRetrievedCount.PublicFileCount);
            Assert.AreEqual(firstDirectories.Items[0].Ref, originalChildren.DirectorySummary.Ref);
            Assert.AreEqual(firstDirectories.Items[0].PublicMatchingFileCount,
                originalChildren.DirectorySummary.PublicMatchingFileCount);

            var secondDirectoryPublished = ObserveNext(views, directoryView.ViewId);
            long secondRevision;
            try
            {
                secondRevision = await PublishAsync(views, view.ViewId, () => receive(Response(
                    "second",
                    [File(@"Music\Track two.flac")],
                    [File(@"Private\Track locked.flac")])));
                _ = await secondDirectoryPublished.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                secondDirectoryPublished.Dispose();
            }
            SearchViewSummaryDto second = (views.Get(view.ViewId, CancellationToken.None))!;
            Assert.IsFalse(second.IsComplete);
            Assert.AreEqual(2L, second.Counters.PublicFileCount);
            Assert.AreEqual(1L, second.Counters.LockedFileCount);
            Assert.AreEqual(3L, second.Counters.ProjectedFileCount);
            Assert.AreEqual(2, second.Counters.ObservedPeerCount);
            Assert.AreEqual(1, (views.GetFiles(view.ViewId, firstRevision, null, 10, cancellationToken: CancellationToken.None))!.Items.Count,
                "An older revision must remain an exact prefix after new rows arrive.");

            SearchViewUpdateDto update = (await views.GetUpdatesAsync(
                view.ViewId,
                afterRevision: firstRevision,
                cancellationToken: CancellationToken.None))!;
            Assert.AreEqual(secondRevision, update.Summary.Revision);
            Assert.IsTrue(update.HasNewRevision);

            SearchViewSummaryDto secondDirectorySummary = (views.Get(directoryView.ViewId, CancellationToken.None))!;
            Assert.AreEqual(3L, secondDirectorySummary.Counters.TopLevelItemCount);
            Assert.AreEqual(2L, secondDirectorySummary.Counters.SelectableOptionCount);
            var allDirectoryCount = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept, [])), CancellationToken.None))!;
            Assert.AreEqual(3L, allDirectoryCount.PublicFileCount);
            Assert.AreEqual(1L, allDirectoryCount.LockedFileCount);
            Assert.AreEqual(3L, allDirectoryCount.SelectedItemCount);
            var excludedDirectoryFileCount = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept, []),
                    ExcludedFileRefs: [firstChildren.Items.Single().File.Ref]), CancellationToken.None))!;
            Assert.AreEqual(2L, excludedDirectoryFileCount.PublicFileCount);
            Assert.AreEqual(1L, excludedDirectoryFileCount.LockedFileCount);
            Assert.AreEqual(3L, excludedDirectoryFileCount.SelectedItemCount);
            var reIncludedDirectoryFileCount = (views.Selections.CountSelection(directoryView.ViewId, new CountSearchViewSelectionRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    IncludedFileRefs: [firstChildren.Items.Single().File.Ref]), CancellationToken.None))!;
            Assert.AreEqual(2L, reIncludedDirectoryFileCount.PublicFileCount);
            Assert.AreEqual(1L, reIncludedDirectoryFileCount.LockedFileCount);
            Assert.AreEqual(3L, reIncludedDirectoryFileCount.SelectedItemCount);
            var directoryDefaults = (views.Selections.ResolveSelectionSettings(directoryView.ViewId, new ResolveSearchViewSelectionSettingsRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.Only,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    [firstChildren.Items.Single().File.Ref]), CancellationToken.None))!;
            CollectionAssert.AreEqual(new[] { ServerJobKind.RemoteDirectory },
                directoryDefaults.Variants.Select(variant => variant.JobKind).ToArray(),
                "An explicitly included child under a selected directory must not change its effective job kind.");
            var mixedDefaults = (views.Selections.ResolveSelectionSettings(directoryView.ViewId, new ResolveSearchViewSelectionSettingsRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept,
                        [firstDirectories.Items.Single().Ref.Ref]),
                    [firstChildren.Items.Single().File.Ref]), CancellationToken.None))!;
            CollectionAssert.AreEqual(new[] { ServerJobKind.RemoteFile, ServerJobKind.RemoteDirectory },
                mixedDefaults.Variants.Select(variant => variant.JobKind).ToArray());
            var excludedChildReceipt = (await views.Selections.CommitSelectionAsync(
                directoryView.ViewId,
                new CommitSearchViewSelectionRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.AllExcept,
                        [firstChildren.Items.Single().File.Ref]),
                    Guid.NewGuid()),
                CancellationToken.None))!;
            Assert.AreEqual(2L, excludedChildReceipt.SubmittedCount);
            var retainedSelection = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(excludedChildReceipt.SubmissionId!.Value, CancellationToken.None);
            Assert.IsNotNull(retainedSelection);
            var retainedRoot = (Sockseek.Core.Jobs.JobList)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(retainedSelection.SpecificationJson).MaterializeJob();
            var selectedDirectories = retainedRoot.Jobs
                .Cast<Sockseek.Core.Jobs.RemoteDirectoryJob>()
                .Select(directoryJob => (Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)directoryJob.Source)
                .ToArray();
            CollectionAssert.AreEqual(new[] { @"Music\Cover.jpg" },
                selectedDirectories.Single(source => source.Plan.Username == "first").Plan.Entries
                    .Select(entry => entry.Target.Filename).ToArray());
            CollectionAssert.AreEqual(new[] { @"Music\Track two.flac" },
                selectedDirectories.Single(source => source.Plan.Username == "second").Plan.Entries
                    .Select(entry => entry.Target.Filename).ToArray(),
                "An exception for one peer must not affect another peer's directory.");
            var onlyDirectoryRequest = new CommitSearchViewSelectionRequestDto(
                secondDirectorySummary.Revision,
                new RefSelectionExpressionDto(RefSelectionMode.Only, [firstDirectories.Items.Single().Ref.Ref]),
                Guid.NewGuid(),
                [firstChildren.Items.Single().File.Ref]);
            var onlyDirectoryReceipt = (await views.Selections.CommitSelectionAsync(
                directoryView.ViewId, onlyDirectoryRequest, CancellationToken.None))!;
            Assert.AreEqual(1L, onlyDirectoryReceipt.SubmittedCount);
            var publicDirectoryRefs = (views.GetDirectories(directoryView.ViewId, secondDirectorySummary.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items
                .Where(item => item.Visibility != SearchViewDirectoryVisibility.Locked)
                .Select(item => item.Ref.Ref).ToArray();
            Assert.AreEqual(2, publicDirectoryRefs.Length);
            var incompatibleOptions = (await views.Selections.CommitSelectionAsync(
                directoryView.ViewId,
                new CommitSearchViewSelectionRequestDto(
                    secondDirectorySummary.Revision,
                    new RefSelectionExpressionDto(RefSelectionMode.Only, publicDirectoryRefs),
                    Guid.NewGuid(),
                    Options: new SubmissionOptionsDto(
                        DownloadSettings: new DownloadSettingsPatchDto(
                            Search: new SearchSettingsPatchDto(NoBrowseFolder: true)))),
                CancellationToken.None))!;
            Assert.AreEqual(0L, incompatibleOptions.SubmittedCount);
            Assert.AreEqual(2L, incompatibleOptions.RejectedCount,
                "An ordinary directory transfer must not silently ignore a music-only download option.");
            Assert.AreEqual(2L, incompatibleOptions.RejectionReasons.Single().Count,
                "Repeated errors share one counted explanation.");
            StringAssert.Contains(incompatibleOptions.RejectionReasons.Single().Message!, "Search",
                "The caller needs the incompatible setting, not just an invalid-item code.");
            var retainedOnly = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(onlyDirectoryReceipt.SubmissionId!.Value, CancellationToken.None);
            var onlyRoot = (Sockseek.Core.Jobs.RemoteDirectoryJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(retainedOnly!.SpecificationJson).MaterializeJob();
            var onlySource = (Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)
                onlyRoot.Source;
            CollectionAssert.AreEqual(new[] { @"Music\Cover.jpg" },
                onlySource.Plan.Entries.Select(entry => entry.Target.Filename).ToArray());
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() => views.Selections.CommitSelectionAsync(
                directoryView.ViewId, onlyDirectoryRequest with { ExcludedFileRefs = [] }, CancellationToken.None));
            var reinclusionRequest = new CommitSearchViewSelectionRequestDto(
                secondDirectorySummary.Revision,
                new RefSelectionExpressionDto(RefSelectionMode.AllExcept, [firstDirectories.Items.Single().Ref.Ref]),
                Guid.NewGuid(), IncludedFileRefs: [firstChildren.Items.Single().File.Ref]);
            var reinclusionReceipt = (await views.Selections.CommitSelectionAsync(
                directoryView.ViewId, reinclusionRequest, CancellationToken.None))!;
            Assert.AreEqual(2L, reinclusionReceipt.SubmittedCount);
            var retainedReinclusion = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(reinclusionReceipt.SubmissionId!.Value, CancellationToken.None);
            var reinclusionRoot = (Sockseek.Core.Jobs.JobList)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(retainedReinclusion!.SpecificationJson).MaterializeJob();
            Assert.AreEqual(@"Music\Track one.flac", reinclusionRoot.Jobs
                .OfType<Sockseek.Core.Jobs.RemoteFileJob>().Single().Target.Filename);
            Assert.AreEqual("second", ((Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)reinclusionRoot.Jobs
                .OfType<Sockseek.Core.Jobs.RemoteDirectoryJob>().Single().Source).Plan.Username);
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() => views.Selections.CommitSelectionAsync(
                directoryView.ViewId, reinclusionRequest with { IncludedFileRefs = [] }, CancellationToken.None));
            Assert.AreEqual(1, (views.GetDirectories(directoryView.ViewId, firstDirectorySummary.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items.Count,
                "Older directory pages remain bound to their exact observed prefix.");

            Guid idempotencyKey = Guid.NewGuid();
            var commitRequest = new CommitSearchViewSelectionRequestDto(
                secondRevision,
                new RefSelectionExpressionDto(RefSelectionMode.AllExcept, []),
                idempotencyKey,
                Options: new SubmissionOptionsDto(
                    DownloadSettings: new DownloadSettingsPatchDto(
                        Skip: new SkipSettingsPatchDto(SkipExisting: false))));
            CommitSearchViewSelectionResponseDto receipt = (await views.Selections.CommitSelectionAsync(
                view.ViewId,
                commitRequest,
                CancellationToken.None))!;
            Assert.AreEqual(secondRevision, receipt.ViewRevision);
            Assert.AreEqual(3L, receipt.RequestedCount);
            Assert.AreEqual(3L, receipt.ResolvedCount);
            Assert.AreEqual(2L, receipt.SubmittedCount);
            Assert.AreEqual(1L, receipt.RejectedCount);
            Assert.AreEqual("locked", receipt.RejectionReasons.Single().Reason);
            Assert.AreEqual(1L, receipt.RejectionReasons.Single().Count);
            Assert.IsNotNull(receipt.SubmissionId);
            var retainedWithOptions = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(receipt.SubmissionId.Value, CancellationToken.None);
            var optionRoot = (Sockseek.Core.Jobs.JobList)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(retainedWithOptions!.SpecificationJson).MaterializeJob();
            Assert.IsTrue(optionRoot.Jobs.All(job => job.PlannedSettings?.ReadSettings().Skip.SkipExisting == false),
                "The same download-time override must be composed for each accepted child.");
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var api = new SockseekApiClient(http);
            var replayTimer = System.Diagnostics.Stopwatch.StartNew();
            CommitSearchViewSelectionResponseDto repeated = await api.CommitSearchViewSelectionAsync(view.ViewId, commitRequest);
            long firstHttpMs = replayTimer.ElapsedMilliseconds;
            Assert.AreEqual(receipt.SubmissionId, repeated.SubmissionId);
            Assert.AreEqual(receipt.RequestedCount, repeated.RequestedCount);
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                views.Selections.CommitSelectionAsync(
                    view.ViewId,
                    commitRequest with
                    {
                        Options = new SubmissionOptionsDto(
                            DownloadSettings: new DownloadSettingsPatchDto(
                                Skip: new SkipSettingsPatchDto(SkipExisting: true))),
                    },
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                views.Selections.CommitSelectionAsync(
                    view.ViewId,
                    commitRequest with
                    {
                        Selection = new RefSelectionExpressionDto(
                            RefSelectionMode.Only,
                            []),
                    },
                    CancellationToken.None));

            CommitSearchViewSelectionResponseDto onlyReceipt = (await views.Selections.CommitSelectionAsync(
                view.ViewId,
                new CommitSearchViewSelectionRequestDto(
                    secondRevision,
                    new RefSelectionExpressionDto(
                        RefSelectionMode.Only,
                        [firstPage.Items[0].Ref, "missing-ref"]),
                    Guid.NewGuid()),
                CancellationToken.None))!;
            Assert.AreEqual(2L, onlyReceipt.RequestedCount);
            Assert.AreEqual(1L, onlyReceipt.ResolvedCount);
            Assert.AreEqual(1L, onlyReceipt.SubmittedCount);
            Assert.AreEqual(1L, onlyReceipt.RejectedCount);
            Assert.AreEqual("missing-ref", onlyReceipt.RejectionReasons.Single().Reason);

            var completedDirectoryPublished = ObserveNext(views, directoryView.ViewId);
            long completedRevision = await PublishAsync(
                views,
                view.ViewId,
                () => releaseSearch.TrySetResult());
            try
            {
                _ = await completedDirectoryPublished.Completion.WaitAsync(
                    TimeSpan.FromSeconds(5));
            }
            finally
            {
                completedDirectoryPublished.Dispose();
            }
            SearchViewSummaryDto completed = (views.Get(view.ViewId, CancellationToken.None))!;
            Assert.AreEqual(completedRevision, completed.Revision);
            Assert.IsTrue(completed.IsComplete);
            Assert.AreEqual(SearchViewRetentionState.Complete, completed.RetentionState);
            Assert.AreEqual(second.Counters, completed.Counters);
            SearchViewUpdateDto completionUpdate = (await views.GetUpdatesAsync(
                view.ViewId,
                secondRevision,
                cancellationToken: CancellationToken.None))!;
            Assert.IsTrue(completionUpdate.HasNewRevision);
            Assert.AreEqual(completedRevision, completionUpdate.Summary.Revision);
            Assert.IsFalse((await views.GetUpdatesAsync(
                view.ViewId,
                completedRevision,
                cancellationToken: CancellationToken.None))!.HasNewRevision);

            // Lose all temporary read state. Accepted intent and its receipt
            // must remain recoverable independently of the issuing Views.
            app.Services.GetRequiredService<SearchViewCoordinator>().Cache.Clear();
            Assert.IsNull(views.Get(view.ViewId, CancellationToken.None));
            var persistence = app.Services.GetRequiredService<PersistenceCoordinator>();
            int acceptedBeforeReplay = (await persistence.Submissions!.GetSubmissionsAsync(new(Limit: 200))).Items.Count;
            replayTimer.Restart();
            CommitSearchViewSelectionResponseDto replayedCommit = await api.CommitSearchViewSelectionAsync(view.ViewId, commitRequest);
            long httpReplayMs = replayTimer.ElapsedMilliseconds;
            string ExpectedReceipt() => JsonSerializer.Serialize(receipt, SockseekApiJson.CreateSerializerOptions());
            Assert.AreEqual(ExpectedReceipt(), JsonSerializer.Serialize(replayedCommit, SockseekApiJson.CreateSerializerOptions()),
                "Pruning a View must not turn an accepted commit retry into a missing resource.");
            var replayConflict = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                api.CommitSearchViewSelectionAsync(view.ViewId, commitRequest with { Revision = commitRequest.Revision + 1 }));
            Assert.AreEqual(System.Net.HttpStatusCode.Conflict, replayConflict.StatusCode);
            var missingView = await Assert.ThrowsExactlyAsync<SockseekApiRequestException>(() =>
                api.CommitSearchViewSelectionAsync(view.ViewId, commitRequest with { IdempotencyKey = Guid.NewGuid() }));
            Assert.AreEqual(System.Net.HttpStatusCode.NotFound, missingView.StatusCode);

            await using var recoveredViews = new SearchViewCoordinator(supervisor, persistence,
                new SubmissionCommitCoordinator(persistence), app.Services.GetRequiredService<SearchViewCursorCodec>(),
                NullLogger<SearchViewCoordinator>.Instance);
            // Do not start this optional projection service or restore its memory
            // cache. Accepted intent must resolve solely from durable acceptance.
            replayTimer.Restart();
            CommitSearchViewSelectionResponseDto durableReplay = (await recoveredViews.Selections.CommitSelectionAsync(
                view.ViewId, commitRequest, CancellationToken.None))!;
            long durableReplayMs = replayTimer.ElapsedMilliseconds;
            Assert.AreEqual(ExpectedReceipt(), JsonSerializer.Serialize(durableReplay, SockseekApiJson.CreateSerializerOptions()));
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() => recoveredViews.Selections.CommitSelectionAsync(
                view.ViewId, commitRequest with { Revision = commitRequest.Revision + 1 }, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => recoveredViews.Selections.CommitSelectionAsync(
                view.ViewId, commitRequest with { IdempotencyKey = Guid.NewGuid() }, CancellationToken.None));
            Assert.IsNull(views.Get(directoryView.ViewId, CancellationToken.None));
            JobSummaryDto? recoveredRetrieval = await views.StartDirectoryRetrievalAsync(
                directoryView.ViewId, retrievalRequest, CancellationToken.None);
            Assert.AreEqual(retrieval?.JobId, recoveredRetrieval?.JobId);
            await Assert.ThrowsExactlyAsync<IdempotencyConflictException>(() =>
                views.StartDirectoryRetrievalAsync(directoryView.ViewId,
                    retrievalRequest with { Revision = retrievalRequest.Revision + 1 },
                    CancellationToken.None));
            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() =>
                views.StartDirectoryRetrievalAsync(directoryView.ViewId,
                    retrievalRequest with { IdempotencyKey = Guid.NewGuid() },
                    CancellationToken.None));
            Assert.AreEqual(acceptedBeforeReplay,
                (await persistence.Submissions.GetSubmissionsAsync(new(Limit: 200))).Items.Count,
                "Replaying or rejecting requests must not submit work again.");
            Console.WriteLine($"Pruned Search View replay: cold first HTTP={firstHttpMs} ms; retained HTTP={httpReplayMs} ms; durable metadata={durableReplayMs} ms.");
        }
        finally
        {
            releaseSearch.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GenericSubfolderRulesCountAndCommitUnloadedRevisionFilesExactly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sockseek-subfolder-selection-tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var client = ControllableSearchClientProxy.Create();
        var started = new TaskCompletionSource<Action<SearchResponse>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Search = async (handler, cancellationToken) =>
        {
            started.TrySetResult(handler);
            await release.Task.WaitAsync(cancellationToken);
        };
        client.Browse = _ => new BrowseResponse([
            new Soulseek.Directory("Folder", [File("Root.txt")]),
            new Soulseek.Directory(@"Folder\Nested", [File("One.txt"), File("Two.txt")]),
            new Soulseek.Directory(@"Folder\Nested\Deep", [File("Three.txt")]),
        ]);
        try
        {
            await using WebApplication app = global::Tests.TestServerHost.Build([], Options(directory, client.Client),
                "http://127.0.0.1:0");
            await app.StartAsync();
            EngineSupervisor supervisor = app.Services.GetRequiredService<EngineSupervisor>();
            JobSummaryDto job = await supervisor.Submissions.SubmitSearchJobAsync(
                new SubmitSearchJobRequestDto("Folder"), CancellationToken.None);
            Action<SearchResponse> receive = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SearchViewCoordinator views = app.Services.GetRequiredService<SearchViewCoordinator>();
            SearchViewSummaryDto view = (await views.CreateAsync(job.JobId,
                new CreateSearchViewRequestDto(ServerSearchViewProjectionKind.GenericDirectories,
                    new SongQueryDto(Title: "Folder"), IncludeFullResults: true),
                CancellationToken.None))!;
            await PublishAsync(views, view.ViewId, () => receive(Response("peer", [File(@"Folder\Root.txt")])));
            SearchViewSummaryDto before = (views.Get(view.ViewId, CancellationToken.None))!;
            SearchViewDirectoryDto root = (views.GetDirectories(view.ViewId, before.Revision, null, 10, cancellationToken: CancellationToken.None))!.Items.Single();
            var published = ObserveNext(views, view.ViewId);
            try
            {
                await views.StartDirectoryRetrievalAsync(view.ViewId,
                    new RetrieveSearchViewDirectoryRequestDto(before.Revision, root.Ref, Guid.NewGuid()),
                    CancellationToken.None);
                await published.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { published.Dispose(); }
            SearchViewSummaryDto after = (views.Get(view.ViewId, CancellationToken.None))!;
            SearchViewDirectoryFilePageDto firstChildPage = (views.GetDirectoryFiles(view.ViewId, root.Ref.Ref, after.Revision, null, 1, CancellationToken.None))!;
            Assert.IsNotNull(firstChildPage.NextCursor, "The client need not load later child pages.");

            var nested = new SearchViewDirectoryPathRuleDto(root.Ref.Ref, "Nested", true);
            var nestedSelection = new RefSelectionExpressionDto(RefSelectionMode.Only, []);
            CountSearchViewSelectionResponseDto count = (views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    DirectoryPathRules: [nested]), CancellationToken.None))!;
            Assert.AreEqual(3L, count.PublicFileCount);
            Assert.AreEqual(1L, count.SelectedItemCount);
            var defaults = (views.Selections.ResolveSelectionSettings(view.ViewId, new ResolveSearchViewSelectionSettingsRequestDto(after.Revision, nestedSelection,
                    DirectoryPathRules: [nested]), CancellationToken.None))!;
            Assert.IsTrue(defaults.Variants.Any(variant => variant.JobKind == ServerJobKind.RemoteDirectory));
            CommitSearchViewSelectionResponseDto receipt = (await views.Selections.CommitSelectionAsync(view.ViewId,
                new CommitSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    Guid.NewGuid(), DirectoryPathRules: [nested]), CancellationToken.None))!;
            Assert.AreEqual(1L, receipt.SubmittedCount);
            var retained = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(receipt.SubmissionId!.Value, CancellationToken.None);
            var jobList = (Sockseek.Core.Jobs.RemoteDirectoryJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(retained!.SpecificationJson).MaterializeJob();
            var plan = ((Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)
                jobList.Source).Plan;
            CollectionAssert.AreEquivalent(new[] { @"Folder\Nested\One.txt", @"Folder\Nested\Two.txt",
                @"Folder\Nested\Deep\Three.txt" },
                plan.Entries.Select(entry => entry.Target.Filename).ToArray());

            var rootSelection = new RefSelectionExpressionDto(RefSelectionMode.Only, [root.Ref.Ref]);
            var nestedOff = new SearchViewDirectoryPathRuleDto(root.Ref.Ref, "Nested", false);
            var deepOn = new SearchViewDirectoryPathRuleDto(root.Ref.Ref, "Nested/Deep", true);
            CountSearchViewSelectionResponseDto nestedCount = (views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(after.Revision, rootSelection,
                    DirectoryPathRules: [nestedOff, deepOn]), CancellationToken.None))!;
            Assert.AreEqual(2L, nestedCount.PublicFileCount);
            CommitSearchViewSelectionResponseDto nestedReceipt = (await views.Selections.CommitSelectionAsync(view.ViewId,
                new CommitSearchViewSelectionRequestDto(after.Revision, rootSelection,
                    Guid.NewGuid(), DirectoryPathRules: [nestedOff, deepOn]), CancellationToken.None))!;
            var nestedRetained = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(nestedReceipt.SubmissionId!.Value, CancellationToken.None);
            var nestedJobList = (Sockseek.Core.Jobs.RemoteDirectoryJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(nestedRetained!.SpecificationJson).MaterializeJob();
            var nestedPlan = ((Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)
                nestedJobList.Source).Plan;
            CollectionAssert.AreEquivalent(new[] { @"Folder\Root.txt", @"Folder\Nested\Deep\Three.txt" },
                nestedPlan.Entries.Select(entry => entry.Target.Filename).ToArray());

            SearchViewDirectoryFilePageDto allChildren = (views.GetDirectoryFiles(view.ViewId, root.Ref.Ref, after.Revision, null, 10, CancellationToken.None))!;
            string deepRef = allChildren.Items.Single(item => item.RelativePath.EndsWith("Three.txt",
                StringComparison.Ordinal)).File.Ref;
            CountSearchViewSelectionResponseDto exceptionCount = (views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    ExcludedFileRefs: [deepRef], DirectoryPathRules: [nested]), CancellationToken.None))!;
            Assert.AreEqual(2L, exceptionCount.PublicFileCount);
            CommitSearchViewSelectionResponseDto exceptionReceipt = (await views.Selections.CommitSelectionAsync(view.ViewId,
                new CommitSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    Guid.NewGuid(), ExcludedFileRefs: [deepRef], DirectoryPathRules: [nested]),
                CancellationToken.None))!;
            var exceptionRetained = await app.Services.GetRequiredService<PersistenceCoordinator>()
                .Submissions!.GetSubmissionAsync(exceptionReceipt.SubmissionId!.Value, CancellationToken.None);
            var exceptionJobList = (Sockseek.Core.Jobs.RemoteDirectoryJob)Sockseek.Core.Planning.SubmissionSpecificationCodec
                .Deserialize(exceptionRetained!.SpecificationJson).MaterializeJob();
            var exceptionPlan = ((Sockseek.Core.Jobs.RemoteDirectorySource.Resolved)
                exceptionJobList.Source).Plan;
            CollectionAssert.AreEquivalent(new[] { @"Folder\Nested\One.txt", @"Folder\Nested\Two.txt" },
                exceptionPlan.Entries.Select(entry => entry.Target.Filename).ToArray());
            var vanishedRule = nested with { DirectoryRef = "directory-from-an-older-view" };
            CountSearchViewSelectionResponseDto vanishedCount = (views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    DirectoryPathRules: [vanishedRule]), CancellationToken.None))!;
            Assert.AreEqual(0L, vanishedCount.PublicFileCount);
            CommitSearchViewSelectionResponseDto vanishedReceipt = (await views.Selections.CommitSelectionAsync(view.ViewId,
                new CommitSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    Guid.NewGuid(), DirectoryPathRules: [vanishedRule]), CancellationToken.None))!;
            Assert.AreEqual(0L, vanishedReceipt.SubmittedCount);
            Assert.AreEqual("missing-ref", vanishedReceipt.RejectionReasons.Single().Reason);
            Assert.ThrowsExactly<ArgumentException>(() => views.Selections.CountSelection(view.ViewId, new CountSearchViewSelectionRequestDto(after.Revision, nestedSelection,
                    DirectoryPathRules: [nested with { RelativePath = "../Nested" }]), CancellationToken.None));
            await app.StopAsync();
        }
        finally
        {
            release.TrySetResult();
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SeedOriginalsAsync(PersistenceCoordinator persistence, Guid source,
        IReadOnlyList<SearchProjectionInput> inputs)
    {
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Committed(Guid id) { if (id == source) committed.TrySetResult(); }
        persistence.SearchSourceChanged += Committed;
        try
        {
            // This fixture intentionally has no running engine; persist its
            // parent before admitting raw inputs, just as engine registration does.
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new Sockseek.Persistence.Write.JobPersistenceMutation(
                persistence.Runtime!.RuntimeId, 1, DateTimeOffset.UtcNow, source, 1,
                Sockseek.Persistence.Write.PersistenceMutationPriority.Structural,
                source, null, null, null, 1, "Search", "Queued", "None", null,
                "None", "None", "None", "None", null, null, null, "Track", 1, null)));
            Assert.IsTrue(persistence.MutationSink!.TryEnqueue(new Sockseek.Persistence.Write.SearchResultsPersistenceMutation(
                persistence.Runtime!.RuntimeId, 1, DateTimeOffset.UtcNow, source, 1, inputs)));
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { persistence.SearchSourceChanged -= Committed; }
    }

    private static async Task<SearchViewSummaryDto> CreateAndObserveAsync(
        SearchViewCoordinator views, Guid sourceJobId, CreateSearchViewRequestDto request)
    {
        SearchViewSummaryDto created = (await views.CreateAsync(sourceJobId, request, CancellationToken.None))!;
        return await WaitForConditionViewAsync(views, created.ViewId, summary => summary.Revision > 0, () => { });
    }

    private static async Task<long> PublishAsync(SearchViewCoordinator views, Guid viewId, Action publish)
    {
        using var read = ObserveNext(views, viewId);
        publish();
        return await read.Completion;
    }

    // Exercise the same read demand as an API client, not a test-only background
    // subscription. The server's long poll holds interest until a change arrives.
    private static ViewRead ObserveNext(SearchViewCoordinator views, Guid viewId) => new(views, viewId);

    private sealed class ViewRead : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        public Task<long> Completion { get; }
        public ViewRead(SearchViewCoordinator views, Guid viewId) => Completion = ReadAsync(views, viewId);
        private async Task<long> ReadAsync(SearchViewCoordinator views, Guid viewId)
        {
            SearchViewSummaryDto current = (views.Get(viewId, cancellation.Token))!;
            while (true)
            {
                var update = (await views.GetUpdatesAsync(viewId, current.Revision, waitForChange: true, cancellation.Token))!;
                if (update.HasNewRevision) return update.Summary.Revision;
            }
        }
        public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
    }

    private static SearchResponse Response(
        string username,
        IReadOnlyCollection<Soulseek.File> files,
        IReadOnlyCollection<Soulseek.File>? locked = null)
        => new(
            username,
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100_000,
            queueLength: 2,
            fileList: files,
            lockedFileList: locked ?? []);

    private static Soulseek.File File(string path)
        => new(1, path, 1_000, Path.GetExtension(path), []);

    private static ServerOptions Options(string directory, ISoulseekClient client)
    {
        string database = SockseekDataPaths.ResolveDatabasePath(directory);
        if (!System.IO.File.Exists(database)) global::Tests.TestDatabase.Create(database);
        return new()
        {
            Engine = new EngineSettings
            {
                Username = "local",
                Password = "password",
                ListenPort = null,
                LogLevel = Microsoft.Extensions.Logging.LogLevel.None,
            },
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true,
                DataDirectory = directory,
            },
            ClientFactory = _ => client,
        };
    }
}

internal class ControllableSearchClientProxy : DispatchProxy
{
    private readonly Dictionary<string, Delegate?> handlers = new(StringComparer.Ordinal);

    public ISoulseekClient Client { get; private set; } = null!;
    public Func<Action<SearchResponse>, CancellationToken, Task>? Search { get; set; }
    public Func<string, BrowseResponse>? Browse { get; set; }

    public static ControllableSearchClientProxy Create()
    {
        ISoulseekClient client = DispatchProxy.Create<ISoulseekClient, ControllableSearchClientProxy>();
        var proxy = (ControllableSearchClientProxy)(object)client;
        proxy.Client = client;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (targetMethod.Name.StartsWith("add_", StringComparison.Ordinal))
        {
            string name = targetMethod.Name[4..];
            handlers[name] = Delegate.Combine(handlers.GetValueOrDefault(name), (Delegate)args[0]!);
            return null;
        }
        if (targetMethod.Name.StartsWith("remove_", StringComparison.Ordinal))
        {
            string name = targetMethod.Name[7..];
            handlers[name] = Delegate.Remove(handlers.GetValueOrDefault(name), (Delegate)args[0]!);
            return null;
        }
        return targetMethod.Name switch
        {
            "get_State" => SoulseekClientStates.Connected | SoulseekClientStates.LoggedIn,
            "get_MajorVersion" => 170,
            "get_MinorVersion" => 800850000,
            "SearchAsync" when targetMethod.ReturnType == typeof(Task<Search>)
                => SearchAsync(args),
            "BrowseAsync" when targetMethod.ReturnType == typeof(Task<BrowseResponse>)
                => Task.FromResult(Browse?.Invoke((string)args[0]!) ?? new BrowseResponse()),
            "ConnectAsync" => Task.CompletedTask,
            "Dispose" => null,
            _ => DefaultReturn(targetMethod.ReturnType),
        };
    }

    private async Task<Search> SearchAsync(object?[] args)
    {
        Action<SearchResponse> handler = args.OfType<Action<SearchResponse>>().Single();
        CancellationToken cancellationToken = args.OfType<CancellationToken>().LastOrDefault();
        if (Search != null)
            await Search(handler, cancellationToken);
        return null!;
    }

    private static object? DefaultReturn(Type type)
    {
        if (type == typeof(void))
            return null;
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type resultType = type.GetGenericArguments()[0];
            object? value = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType)
                .Invoke(null, [value]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
