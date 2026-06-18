using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server;

namespace Tests.Server;

[TestClass]
public class EventTrafficProfilingTests
{
    private const string RunProfileEnvVar = "SOCKSEEK_RUN_EVENT_PROFILE";
    private const string JobCountEnvVar = "SOCKSEEK_EVENT_PROFILE_JOBS";
    private const string CompletionJobCountEnvVar = "SOCKSEEK_EVENT_PROFILE_COMPLETION_JOBS";
    private const int DefaultJobCount = 3000;
    private const int DefaultCompletionJobCount = 30;
    private const int MaxDefaultProfileTotalEvents = 2500;
    private const int MaxDefaultProfileCancelDeltaEvents = 250;
    private const long MaxDefaultProfileTotalSerializedBytes = 3_000_000;
    private const long MaxDefaultProfileCancelDeltaSerializedBytes = 500_000;
    private const int MaxDefaultCompletionTotalEvents = 25_000;
    private const long MaxDefaultCompletionTotalSerializedBytes = 15_000_000;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Profiling")]
    [Timeout(120_000)]
    public async Task LargeWorkflowCancellation_EventTrafficProfile()
    {
        // This is intentionally env-gated instead of [Ignore] so the profiler can be run
        // without editing source, while plain `dotnet test` only executes this fast no-op.
        if (!ShouldRunProfile())
        {
            TestContext.WriteLine(
                $"Skipped opt-in event traffic profile. Set {RunProfileEnvVar}=1 to run it.");
            return;
        }

        int jobCount = ReadJobCount();
        string workDir = Path.Combine(Path.GetTempPath(), "Sockseek-event-profile-work-" + Guid.NewGuid());
        string musicRoot = Path.Combine(workDir, "music");
        string inputDir = Path.Combine(workDir, "input");
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-event-profile-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        ServerEventBroadcaster? broadcaster = null;

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir);
            var counter = new EventTrafficCounter();
            broadcaster = new ServerEventBroadcaster(
                supervisor.StateStore,
                supervisor,
                new NoOpHubContext<ServerEventHub>());
            broadcaster.EventPublished += counter.Add;
            broadcaster.BatchPublished += counter.Add;

            runTask = supervisor.RunAsync(cts.Token);

            string csvPath = Path.Combine(inputDir, "playlist.csv");
            File.WriteAllLines(csvPath, BuildCsvLines(jobCount));

            var root = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    csvPath,
                    InputType: "CSV",
                    AutoStartExtractedResult: true),
                CancellationToken.None);

            await WaitForWorkflowJobCountAsync(supervisor, root.WorkflowId, jobCount + 2);
            var beforeCancel = counter.Snapshot(root.WorkflowId);

            int cancelled = supervisor.CancelWorkflow(root.WorkflowId);
            await WaitForWorkflowInactiveAsync(supervisor, root.WorkflowId);

            broadcaster.Dispose();
            broadcaster = null;
            var afterCancel = counter.Snapshot(root.WorkflowId);

            WriteSnapshot("before cancel", beforeCancel);
            WriteSnapshot("after cancel", afterCancel);
            WriteDelta("cancel delta", beforeCancel, afterCancel);

            Assert.IsTrue(cancelled > 0, "The profiling workload did not cancel any jobs.");
            Assert.IsTrue(afterCancel.Total > beforeCancel.Total, "No events were published after cancellation.");
            AssertProfileBudget(jobCount, beforeCancel, afterCancel);
        }
        finally
        {
            broadcaster?.Dispose();
            cts.Cancel();
            await runTask;
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    [TestCategory("Profiling")]
    [Timeout(180_000)]
    public async Task LargeAggregateCompletion_EventTrafficProfile()
    {
        // This is intentionally env-gated instead of [Ignore] so the profiler can be run
        // without editing source, while plain `dotnet test` only executes this fast no-op.
        if (!ShouldRunProfile())
        {
            TestContext.WriteLine(
                $"Skipped opt-in event traffic profile. Set {RunProfileEnvVar}=1 to run it.");
            return;
        }

        int jobCount = ReadCompletionJobCount();
        string workDir = Path.Combine(Path.GetTempPath(), "Sockseek-event-profile-work-" + Guid.NewGuid());
        string musicRoot = Path.Combine(workDir, "music");
        string inputDir = Path.Combine(workDir, "input");
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-event-profile-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        ServerEventBroadcaster? broadcaster = null;

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, highSearchRate: true);
            var counter = new EventTrafficCounter();
            broadcaster = new ServerEventBroadcaster(
                supervisor.StateStore,
                supervisor,
                new NoOpHubContext<ServerEventHub>());
            broadcaster.EventPublished += counter.Add;
            broadcaster.BatchPublished += counter.Add;

            runTask = supervisor.RunAsync(cts.Token);

            string csvPath = Path.Combine(inputDir, "playlist.csv");
            BuildMatchingTrackLibrary(musicRoot, csvPath, jobCount);

            var root = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    csvPath,
                    InputType: "CSV",
                    AutoStartExtractedResult: true),
                CancellationToken.None);

            await WaitForWorkflowJobCountAsync(supervisor, root.WorkflowId, jobCount + 2);
            await WaitForWorkflowInactiveAsync(supervisor, root.WorkflowId);

            broadcaster.Dispose();
            broadcaster = null;
            var completed = counter.Snapshot(root.WorkflowId);

            WriteSnapshot("completed", completed);

            AssertProfileCompletionBudget(jobCount, completed);
        }
        finally
        {
            broadcaster?.Dispose();
            cts.Cancel();
            await runTask;
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [TestMethod]
    [TestCategory("Profiling")]
    [Timeout(180_000)]
    public async Task LargeNoResultAggregateCompletion_EventTrafficProfile()
    {
        // This is intentionally env-gated instead of [Ignore] so the profiler can be run
        // without editing source, while plain `dotnet test` only executes this fast no-op.
        if (!ShouldRunProfile())
        {
            TestContext.WriteLine(
                $"Skipped opt-in event traffic profile. Set {RunProfileEnvVar}=1 to run it.");
            return;
        }

        int jobCount = ReadJobCount();
        string workDir = Path.Combine(Path.GetTempPath(), "Sockseek-event-profile-work-" + Guid.NewGuid());
        string musicRoot = Path.Combine(workDir, "music");
        string inputDir = Path.Combine(workDir, "input");
        string outputDir = Path.Combine(Path.GetTempPath(), "Sockseek-event-profile-out-" + Guid.NewGuid());
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        using var cts = new CancellationTokenSource();
        Task runTask = Task.CompletedTask;
        ServerEventBroadcaster? broadcaster = null;

        try
        {
            var supervisor = CreateSupervisor(musicRoot, outputDir, highSearchRate: true);
            var counter = new EventTrafficCounter();
            broadcaster = new ServerEventBroadcaster(
                supervisor.StateStore,
                supervisor,
                new NoOpHubContext<ServerEventHub>());
            broadcaster.EventPublished += counter.Add;
            broadcaster.BatchPublished += counter.Add;

            runTask = supervisor.RunAsync(cts.Token);

            string csvPath = Path.Combine(inputDir, "playlist.csv");
            File.WriteAllLines(csvPath, BuildCsvLines(jobCount));

            var root = await supervisor.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    csvPath,
                    InputType: "CSV",
                    AutoStartExtractedResult: true),
                CancellationToken.None);

            await WaitForWorkflowJobCountAsync(supervisor, root.WorkflowId, jobCount + 2);
            await WaitForWorkflowInactiveAsync(supervisor, root.WorkflowId);

            broadcaster.Dispose();
            broadcaster = null;
            var completed = counter.Snapshot(root.WorkflowId);

            WriteSnapshot("completed no-result", completed);

            AssertProfileCompletionBudget(jobCount, completed);
        }
        finally
        {
            broadcaster?.Dispose();
            cts.Cancel();
            await runTask;
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, true);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static EngineSupervisor CreateSupervisor(string musicRoot, string outputDir, bool highSearchRate = false)
    {
        var options = Options.Create(new ServerOptions
        {
            Engine = new EngineSettings
            {
                MockFilesDir = musicRoot,
                MockFilesReadTags = false,
                ConcurrentJobs = 20,
                ConcurrentSearches = 2,
                SearchesPerTime = highSearchRate ? 10_000 : 34,
                SearchRenewTime = highSearchRate ? 1 : 220,
            },
            DefaultDownload = new DownloadSettings
            {
                Output =
                {
                    ParentDir = outputDir,
                    NameFormat = "{filename}",
                },
            },
            Profiles = ProfileCatalog.Empty,
        });

        return new EngineSupervisor(options);
    }

    private static IEnumerable<string> BuildCsvLines(int count)
    {
        yield return "artist,title";

        foreach (int i in Enumerable.Range(1, count))
            yield return $"Profile Artist,Profile Track {i:D5}";
    }

    private static void BuildMatchingTrackLibrary(string musicRoot, string csvPath, int count)
    {
        string artistDir = Path.Combine(musicRoot, "Profile Artist", "Profile Album");
        Directory.CreateDirectory(artistDir);

        using var csv = new StreamWriter(csvPath);
        csv.WriteLine("artist,title,album");

        foreach (int i in Enumerable.Range(1, count))
        {
            string title = $"Profile Track {i:D5}";
            string filename = $"{i:D5}. Profile Artist - {title}.flac";
            string path = Path.Combine(artistDir, filename);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                file.SetLength(4096);

            csv.WriteLine($"Profile Artist,{title},Profile Album");
        }
    }

    private async Task WaitForWorkflowJobCountAsync(EngineSupervisor supervisor, Guid workflowId, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int lastCount = 0;

        while (!timeout.IsCancellationRequested)
        {
            var workflow = supervisor.StateStore.GetWorkflow(workflowId, includeAll: true);
            lastCount = workflow?.Jobs.Count ?? 0;
            if (lastCount >= expectedCount)
                return;

            try
            {
                await Task.Delay(25, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Assert.Fail($"Timed out waiting for {expectedCount} registered workflow jobs. Last count: {lastCount}.");
    }

    private async Task WaitForWorkflowInactiveAsync(EngineSupervisor supervisor, Guid workflowId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        WorkflowSummaryDto? lastSummary = null;

        while (!timeout.IsCancellationRequested)
        {
            lastSummary = supervisor.StateStore.GetWorkflow(workflowId, includeAll: true)?.Summary;
            if (lastSummary?.ActiveJobCount == 0)
                return;

            try
            {
                await Task.Delay(25, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        string last = lastSummary == null
            ? "<missing>"
            : $"state={lastSummary.State} active={lastSummary.ActiveJobCount} completed={lastSummary.CompletedJobCount} failed={lastSummary.FailedJobCount}";
        Assert.Fail($"Timed out waiting for workflow to become inactive. Last summary: {last}.");
    }

    private void WriteSnapshot(string label, EventTrafficSnapshot snapshot)
    {
        TestContext.WriteLine($"{label}: networkMessages={snapshot.Total}, serializedBytes={FormatBytes(snapshot.SerializedBytes)}, maxMessageBytes={FormatBytes(snapshot.MaxEnvelopeBytes)}, snapshotInvalidations={snapshot.SnapshotInvalidations}, workflowMessages={snapshot.WorkflowEvents}, otherWorkflowMessages={snapshot.OtherWorkflowEvents}, noWorkflowMessages={snapshot.NoWorkflowEvents}");
        WriteCounts(label + " by network message", snapshot.ByMessageType);
        WriteByteCounts(label + " serialized bytes by network message", snapshot.SerializedBytesByMessageType);
        WriteCounts(label + " logical events by category", snapshot.ByCategory);
        WriteCounts(label + " logical events by type", snapshot.ByType);
    }

    private void WriteDelta(string label, EventTrafficSnapshot before, EventTrafficSnapshot after)
    {
        TestContext.WriteLine($"{label}: networkMessages={after.Total - before.Total}, serializedBytes={FormatBytes(after.SerializedBytes - before.SerializedBytes)}, snapshotInvalidations={after.SnapshotInvalidations - before.SnapshotInvalidations}");
        var messageKeys = before.ByMessageType.Keys.Concat(after.ByMessageType.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        foreach (string key in messageKeys)
        {
            int delta = after.ByMessageType.GetValueOrDefault(key) - before.ByMessageType.GetValueOrDefault(key);
            if (delta != 0)
            {
                long byteDelta = after.SerializedBytesByMessageType.GetValueOrDefault(key) - before.SerializedBytesByMessageType.GetValueOrDefault(key);
                TestContext.WriteLine($"  network {key}: {delta} ({FormatBytes(byteDelta)})");
            }
        }

        var keys = before.ByType.Keys.Concat(after.ByType.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        foreach (string key in keys)
        {
            int delta = after.ByType.GetValueOrDefault(key) - before.ByType.GetValueOrDefault(key);
            if (delta != 0)
                TestContext.WriteLine($"  logical {key}: {delta}");
        }
    }

    private static void AssertProfileBudget(int jobCount, EventTrafficSnapshot beforeCancel, EventTrafficSnapshot afterCancel)
    {
        int cancelDelta = afterCancel.Total - beforeCancel.Total;
        long cancelByteDelta = afterCancel.SerializedBytes - beforeCancel.SerializedBytes;
        int jobUpsertDelta = afterCancel.Count("job.upserted") - beforeCancel.Count("job.upserted");
        int songStateDelta = afterCancel.Count("song.state-changed") - beforeCancel.Count("song.state-changed");

        Assert.AreEqual(0, jobUpsertDelta, "Bulk workflow cancellation should not publish per-child job.upserted events.");
        Assert.AreEqual(0, songStateDelta, "Bulk workflow cancellation should not publish per-child song.state-changed events.");

        if (jobCount != DefaultJobCount)
            return;

        Assert.IsTrue(
            afterCancel.Total <= MaxDefaultProfileTotalEvents,
            $"Expected <= {MaxDefaultProfileTotalEvents} total events for the default profile, got {afterCancel.Total}.");
        Assert.IsTrue(
            cancelDelta <= MaxDefaultProfileCancelDeltaEvents,
            $"Expected <= {MaxDefaultProfileCancelDeltaEvents} cancel-delta events for the default profile, got {cancelDelta}.");
        Assert.IsTrue(
            afterCancel.SerializedBytes <= MaxDefaultProfileTotalSerializedBytes,
            $"Expected <= {FormatBytes(MaxDefaultProfileTotalSerializedBytes)} serialized event payload for the default profile, got {FormatBytes(afterCancel.SerializedBytes)}.");
        Assert.IsTrue(
            cancelByteDelta <= MaxDefaultProfileCancelDeltaSerializedBytes,
            $"Expected <= {FormatBytes(MaxDefaultProfileCancelDeltaSerializedBytes)} serialized cancel-delta payload for the default profile, got {FormatBytes(cancelByteDelta)}.");
    }

    private static void AssertProfileCompletionBudget(int jobCount, EventTrafficSnapshot completed)
    {
        if (jobCount != DefaultJobCount)
            return;

        Assert.IsTrue(
            completed.Total <= MaxDefaultCompletionTotalEvents,
            $"Expected <= {MaxDefaultCompletionTotalEvents} total events for the default completion profile, got {completed.Total}.");
        Assert.IsTrue(
            completed.SerializedBytes <= MaxDefaultCompletionTotalSerializedBytes,
            $"Expected <= {FormatBytes(MaxDefaultCompletionTotalSerializedBytes)} serialized event payload for the default completion profile, got {FormatBytes(completed.SerializedBytes)}.");
    }

    private void WriteCounts(string label, IReadOnlyDictionary<string, int> counts)
    {
        TestContext.WriteLine(label + ":");
        foreach (var pair in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            TestContext.WriteLine($"  {pair.Key}: {pair.Value}");
    }

    private void WriteByteCounts(string label, IReadOnlyDictionary<string, long> counts)
    {
        TestContext.WriteLine(label + ":");
        foreach (var pair in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            TestContext.WriteLine($"  {pair.Key}: {FormatBytes(pair.Value)}");
    }

    private static string FormatBytes(long bytes)
        => bytes < 1024
            ? $"{bytes} B"
            : bytes < 1024 * 1024
                ? $"{bytes / 1024.0:F1} KiB"
                : $"{bytes / (1024.0 * 1024.0):F2} MiB";

    private static bool ShouldRunProfile()
        => string.Equals(Environment.GetEnvironmentVariable(RunProfileEnvVar), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable(RunProfileEnvVar), "true", StringComparison.OrdinalIgnoreCase);

    private static int ReadJobCount()
        => ReadPositiveInt(JobCountEnvVar, DefaultJobCount);

    private static int ReadCompletionJobCount()
        => ReadPositiveInt(CompletionJobCountEnvVar, DefaultCompletionJobCount);

    private static int ReadPositiveInt(string envVar, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(envVar), out int parsed) && parsed > 0
            ? parsed
            : fallback;

    private sealed class EventTrafficCounter
    {
        private static readonly JsonSerializerOptions JsonOptions = SockseekApiJson.CreateSerializerOptions();
        private readonly Lock gate = new();
        private readonly List<EventTrafficSample> networkMessages = [];
        private readonly List<ServerEventEnvelopeDto> logicalEvents = [];

        public void Add(ServerEventEnvelopeDto envelope)
        {
            if (envelope.WorkflowId.HasValue)
                return;

            int serializedBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions).Length;
            lock (gate)
            {
                networkMessages.Add(new EventTrafficSample(envelope.Type, envelope.WorkflowId, serializedBytes));
                logicalEvents.Add(envelope);
            }
        }

        public void Add(WorkflowUpdateBatchDto batch)
        {
            int serializedBytes = JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions).Length;
            lock (gate)
            {
                networkMessages.Add(new EventTrafficSample("workflow.update-batch", batch.WorkflowId, serializedBytes));
                logicalEvents.AddRange(FlattenBatch(batch));
            }
        }

        public EventTrafficSnapshot Snapshot(Guid workflowId)
        {
            lock (gate)
            {
                return new EventTrafficSnapshot(
                    Total: networkMessages.Count,
                    SerializedBytes: networkMessages.Sum(e => (long)e.SerializedBytes),
                    MaxEnvelopeBytes: networkMessages.Count == 0 ? 0 : networkMessages.Max(e => e.SerializedBytes),
                    SnapshotInvalidations: logicalEvents.Count(e => e.SnapshotInvalidation),
                    WorkflowEvents: networkMessages.Count(e => e.WorkflowId == workflowId),
                    OtherWorkflowEvents: networkMessages.Count(e => e.WorkflowId.HasValue && e.WorkflowId != workflowId),
                    NoWorkflowEvents: networkMessages.Count(e => !e.WorkflowId.HasValue),
                    ByMessageType: networkMessages
                        .GroupBy(e => e.Type, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    SerializedBytesByMessageType: networkMessages
                        .GroupBy(e => e.Type, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Sum(e => (long)e.SerializedBytes), StringComparer.Ordinal),
                    ByCategory: logicalEvents
                        .GroupBy(e => e.Category, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    ByType: logicalEvents
                        .GroupBy(e => e.Type, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
            }
        }

        private static IEnumerable<ServerEventEnvelopeDto> FlattenBatch(WorkflowUpdateBatchDto batch)
        {
            foreach (var summary in batch.JobUpserts)
                yield return BatchEnvelope(batch, "job.upserted", "state", snapshotInvalidation: true, summary);
            if (batch.Workflow != null)
                yield return BatchEnvelope(batch, "workflow.upserted", "state", snapshotInvalidation: true, batch.Workflow);
            foreach (var update in batch.SearchUpdates)
                yield return BatchEnvelope(batch, "search.updated", "state", snapshotInvalidation: true, update);
            foreach (var progress in batch.Progress)
                yield return BatchEnvelope(batch, "download.progress", "progress", snapshotInvalidation: false, progress);
            foreach (var envelope in batch.Activity)
                yield return envelope;
        }

        private static ServerEventEnvelopeDto BatchEnvelope(
            WorkflowUpdateBatchDto batch,
            string type,
            string category,
            bool snapshotInvalidation,
            object payload)
            => new(
                batch.Sequence,
                type,
                batch.OccurredAtUtc,
                category,
                snapshotInvalidation,
                batch.WorkflowId,
                payload);
    }

    private sealed record EventTrafficSample(
        string Type,
        Guid? WorkflowId,
        int SerializedBytes);

    private sealed record EventTrafficSnapshot(
        int Total,
        long SerializedBytes,
        int MaxEnvelopeBytes,
        int SnapshotInvalidations,
        int WorkflowEvents,
        int OtherWorkflowEvents,
        int NoWorkflowEvents,
        IReadOnlyDictionary<string, int> ByMessageType,
        IReadOnlyDictionary<string, long> SerializedBytesByMessageType,
        IReadOnlyDictionary<string, int> ByCategory,
        IReadOnlyDictionary<string, int> ByType)
    {
        public int Count(string type) => ByType.GetValueOrDefault(type);
    }

    private sealed class NoOpHubContext<THub> : IHubContext<THub>
        where THub : Hub
    {
        public IHubClients Clients { get; } = new NoOpHubClients();
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class NoOpHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NoOpClientProxy();

        public IClientProxy All => Proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Client(string connectionId) => Proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

        public IClientProxy Group(string groupName) => Proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

        public IClientProxy User(string userId) => Proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
