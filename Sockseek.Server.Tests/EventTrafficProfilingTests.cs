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
    public void CompactDeltas_MateriallyReduceLargeWorkflowAndTransferPayloads()
    {
        const int count = 1_000;
        var epoch = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var jobs = Enumerable.Range(1, count)
            .Select(index => JobStateDto.FromSummary(
                new JobSummaryDto(
                    Guid.NewGuid(),
                    index,
                    workflowId,
                    ServerJobKind.Song,
                    ServerJobLifecycleState.Running,
                    ServerJobActivityPhase.Downloading,
                    null,
                    ServerJobTerminalOutcome.None,
                    ServerJobSkipReason.None,
                    $"Track {index}",
                    $"Artist - Track {index}",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    ["profile-a"],
                    []),
                2))
            .ToList();
        var fullJobs = Batch(
            workflowId,
            epoch,
            jobs.Select(job => new JobDeltaDto(job.JobId, job.Revision, Added: job)).ToList(),
            []);
        var compactJobs = Batch(
            workflowId,
            epoch,
            jobs.Select(job => new JobDeltaDto(job.JobId, job.Revision, Lifecycle: job.Lifecycle)).ToList(),
            []);

        var transfers = jobs.Select(job => new TransferStateDto(
            Guid.NewGuid(),
            3,
            new TransferIdentityFieldsDto(
                job.JobId,
                workflowId,
                "Download",
                "SoulseekPeer",
                "peer",
                $"Artist\\Album\\{job.Display.QueryText}.flac",
                Guid.NewGuid().ToString("N")),
            new TransferStatusFieldsDto("InProgress", $"C:\\music\\{job.Display.QueryText}.flac", 1, false),
            new TransferProgressFieldsDto(500_000, 1_000_000))).ToList();
        var fullTransfers = Batch(
            workflowId,
            epoch,
            [],
            transfers.Select(transfer =>
                new TransferDeltaDto(transfer.TransferId, transfer.Revision, Added: transfer)).ToList());
        var compactTransfers = Batch(
            workflowId,
            epoch,
            [],
            transfers.Select(transfer =>
                new TransferDeltaDto(
                    transfer.TransferId,
                    transfer.Revision,
                    Progress: transfer.Progress)).ToList());
        var options = SockseekApiJson.CreateSerializerOptions();

        int fullJobBytes = JsonSerializer.SerializeToUtf8Bytes(fullJobs, options).Length;
        int compactJobBytes = JsonSerializer.SerializeToUtf8Bytes(compactJobs, options).Length;
        int fullTransferBytes = JsonSerializer.SerializeToUtf8Bytes(fullTransfers, options).Length;
        int compactTransferBytes = JsonSerializer.SerializeToUtf8Bytes(compactTransfers, options).Length;

        TestContext.WriteLine(
            $"jobs full={fullJobBytes:N0} compact={compactJobBytes:N0}; " +
            $"transfers full={fullTransferBytes:N0} compact={compactTransferBytes:N0}");
        Assert.IsTrue(
            compactJobBytes <= fullJobBytes * 0.65,
            "Lifecycle-only job deltas should be at least 35% smaller than full row replacements.");
        Assert.IsTrue(
            compactTransferBytes <= fullTransferBytes * 0.50,
            "Progress-only transfer deltas should be at least 50% smaller than full transfer replacements.");
    }

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
            broadcaster.BatchPublished += counter.Add;

            runTask = supervisor.RunAsync(cts.Token);

            string csvPath = Path.Combine(inputDir, "playlist.csv");
            File.WriteAllLines(csvPath, BuildCsvLines(jobCount));

            var root = await supervisor.Submissions.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    csvPath,
                    InputType: "CSV"),
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
            broadcaster.BatchPublished += counter.Add;

            runTask = supervisor.RunAsync(cts.Token);

            string csvPath = Path.Combine(inputDir, "playlist.csv");
            BuildMatchingTrackLibrary(musicRoot, csvPath, jobCount);

            var root = await supervisor.Submissions.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    csvPath,
                    InputType: "CSV"),
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
            broadcaster.BatchPublished += counter.Add;

            runTask = supervisor.RunAsync(cts.Token);

            string csvPath = Path.Combine(inputDir, "playlist.csv");
            File.WriteAllLines(csvPath, BuildCsvLines(jobCount));

            var root = await supervisor.Submissions.SubmitExtractJobAsync(
                new SubmitExtractJobRequestDto(
                    csvPath,
                    InputType: "CSV"),
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
            Persistence = new ServerPersistenceOptions
            {
                DataDirectory = Path.Combine(musicRoot, ".sockseek-test-data"),
            },
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
        using var signal = new TestSignal()
            .Subscribe<JobSummaryDto>(handler => supervisor.StateStore.JobUpserted += handler, handler => supervisor.StateStore.JobUpserted -= handler)
            .Subscribe<WorkflowSummaryDto>(handler => supervisor.StateStore.WorkflowUpserted += handler, handler => supervisor.StateStore.WorkflowUpserted -= handler)
            .Subscribe<SearchStateDto>(handler => supervisor.StateStore.SearchUpdated += handler, handler => supervisor.StateStore.SearchUpdated -= handler);
        await signal.WaitUntilAsync(() =>
        {
            var workflow = supervisor.StateStore.GetWorkflowSummary(workflowId);
            return (workflow?.ActiveJobCount ?? 0) + (workflow?.CompletedJobCount ?? 0) >= expectedCount;
        }, $"Workflow did not register {expectedCount} jobs.", 90_000);
    }

    private async Task WaitForWorkflowInactiveAsync(EngineSupervisor supervisor, Guid workflowId)
    {
        using var signal = new TestSignal()
            .Subscribe<JobSummaryDto>(handler => supervisor.StateStore.JobUpserted += handler, handler => supervisor.StateStore.JobUpserted -= handler)
            .Subscribe<WorkflowSummaryDto>(handler => supervisor.StateStore.WorkflowUpserted += handler, handler => supervisor.StateStore.WorkflowUpserted -= handler)
            .Subscribe<SearchStateDto>(handler => supervisor.StateStore.SearchUpdated += handler, handler => supervisor.StateStore.SearchUpdated -= handler);
        await signal.WaitUntilAsync(() => supervisor.StateStore.GetWorkflowSummary(workflowId)?.ActiveJobCount == 0,
            "Workflow did not become inactive.", 60_000);
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

    private static StateUpdateBatchDto Batch(
        Guid workflowId,
        Guid epoch,
        IReadOnlyList<JobDeltaDto> jobs,
        IReadOnlyList<TransferDeltaDto> transfers)
        => new(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            0,
            1,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                Jobs = jobs,
                Transfers = transfers,
            },
            []);

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
        private readonly List<(string Category, string Type)> logicalChanges = [];

        public void Add(StateUpdateBatchDto batch)
        {
            int serializedBytes = JsonSerializer.SerializeToUtf8Bytes(batch, JsonOptions).Length;
            Guid? workflowId = batch.Scope.WorkflowId;
            lock (gate)
            {
                networkMessages.Add(new EventTrafficSample("state.update-batch", workflowId, serializedBytes));
                if (batch.State.Daemon != null)
                    logicalChanges.Add(("state", "daemon.replaced"));
                logicalChanges.AddRange(batch.State.Workflows.Select(_ => ("state", "workflow.replaced")));
                logicalChanges.AddRange(batch.State.Jobs.Select(_ => ("state", "job.delta")));
                logicalChanges.AddRange(batch.State.Searches.Select(_ => ("state", "search.replaced")));
                logicalChanges.AddRange(batch.State.Transfers.Select(delta =>
                    ("state", delta.Added != null ? "transfer.added" : "transfer.delta")));
                logicalChanges.AddRange(batch.State.RemovedWorkflowIds.Select(_ => ("state", "workflow.removed")));
                logicalChanges.AddRange(batch.State.RemovedJobIds.Select(_ => ("state", "job.removed")));
                logicalChanges.AddRange(batch.State.RemovedSearchJobIds.Select(_ => ("state", "search.removed")));
                logicalChanges.AddRange(batch.State.RemovedTransferIds.Select(_ => ("state", "transfer.removed")));
                logicalChanges.AddRange(batch.Activity.Select(activity => ("activity", activity.Type)));
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
                    SnapshotInvalidations: 0,
                    WorkflowEvents: networkMessages.Count(e => e.WorkflowId == workflowId),
                    OtherWorkflowEvents: networkMessages.Count(e => e.WorkflowId.HasValue && e.WorkflowId != workflowId),
                    NoWorkflowEvents: networkMessages.Count(e => !e.WorkflowId.HasValue),
                    ByMessageType: networkMessages
                        .GroupBy(e => e.Type, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    SerializedBytesByMessageType: networkMessages
                        .GroupBy(e => e.Type, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Sum(e => (long)e.SerializedBytes), StringComparer.Ordinal),
                    ByCategory: logicalChanges
                        .GroupBy(e => e.Category, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                    ByType: logicalChanges
                        .GroupBy(e => e.Type, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
            }
        }
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

}
