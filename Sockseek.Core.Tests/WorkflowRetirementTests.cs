using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Tests;
using MockSoulseekClient = Tests.ClientTests.MockSoulseekClient;

namespace Sockseek.Core.Tests;

[TestClass]
public sealed class WorkflowRetirementTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CompletedDownloadsReleaseActiveCacheOwnershipWhenTheirWorkflowRetires(bool retire)
    {
        string output = Directory.CreateTempSubdirectory("sockseek-download-retirement-").FullName;
        var targets = Enumerable.Range(0, 8).Select(i =>
            new PeerFileTarget(new("Peer", $@"Share\File-{i}.bin"), 16, ".bin")).ToArray();
        var client = new MockSoulseekClient([new Soulseek.SearchResponse("Peer", 1, true, 100, 0,
            targets.Select(target => new Soulseek.File(1, target.Filename, 16, ".bin")))]);
        var settings = new EngineSettings { Username = "test", Password = "test", ConcurrentJobs = 2 };
        await using var engine = new DownloadEngine(settings,
            TestHelpers.CreateMockClientManager(client, settings), retireTerminalWorkflows: retire);
        try
        {
            string source = Path.Combine(output, "pending.txt");
            File.WriteAllLines(source, targets.Select(target => target.Filename));
            var jobs = targets.Select((target, index) => new RemoteFileJob(target)
            {
                SourceMutation = SourceMutation.ClearTextLine(source, index + 1),
            }).ToArray();
            foreach (var job in jobs)
                engine.Enqueue(job, new DownloadSettings
                {
                    Output = { ParentDir = output }, Transfer = { NoIncompleteExt = true },
                    Extraction = { RemoveTracksFromSource = true },
                });
            engine.CompleteEnqueue();
            await engine.RunAsync(CancellationToken.None);
            Assert.AreEqual(8, client.DownloadCallCount);
            Assert.IsTrue(jobs.All(job => job.TerminalOutcome == JobTerminalOutcome.Succeeded));
            Assert.AreEqual(retire ? 0 : 8, engine.RetainedStateCounts.ActiveCachedDownloads);
            Assert.AreEqual(retire ? 0 : 8, engine.RetainedStateCounts.SourceMutationWorkflows);
            Assert.IsTrue(File.ReadAllLines(source).All(string.IsNullOrEmpty));
            Assert.AreEqual(8, Directory.EnumerateFiles(output, "*.bin", SearchOption.AllDirectories).Count());
        }
        finally { Directory.Delete(output, true); }
    }

    [TestMethod]
    public async Task DownloadReuseBelongsToItsWorkflowAndRetirementLeavesOutputIntact()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-cache-lifetime-").FullName;
        using var files = new Sockseek.Core.Services.DownloadFileOperations(1);
        try
        {
            string path = Path.Combine(directory, "track.flac");
            string copy = Path.Combine(directory, "copy.flac");
            File.WriteAllBytes(path, [1, 2, 3]);
            var target = new PeerFileTarget(new("Peer", @"Album\track.flac"), 3, ".flac");
            Guid workflow = Guid.NewGuid(), other = Guid.NewGuid();
            await files.WithCacheAsync(cache => { cache.Publish(path, target, workflow); return true; });
            Assert.IsNull(await files.TryReuseAsync(target, copy, Guid.NewGuid(), other,
                publish: true, allowOverwrite: true));
            Assert.IsNull(await files.TryReuseAsync(target, copy, Guid.NewGuid(), workflow,
                publish: true, allowOverwrite: true, allowReuse: false));
            Assert.IsFalse(File.Exists(copy));
            Assert.IsNotNull(await files.TryReuseAsync(target, copy, Guid.NewGuid(), workflow,
                publish: true, allowOverwrite: true));
            files.RetireWorkflow(other);
            Assert.IsTrue(files.MayHaveReusable(target, workflow));
            files.RetireWorkflow(workflow);
            Assert.AreEqual(0, files.CachedDownloadCount);
            Assert.IsNull(await files.TryReuseAsync(target, path + ".later", Guid.NewGuid(), other,
                publish: true, allowOverwrite: true));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(copy));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DaemonModePublishesFinalStateThenReleasesEveryWorkflowOwner()
    {
        var engineSettings = new EngineSettings
        {
            Username = "test_user",
            Password = "test_pass",
            ConcurrentJobs = 1,
        };
        var client = new MockSoulseekClient([]);
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            retireTerminalWorkflows: true);
        var settings = new DownloadSettings { PrintOption = PrintOption.Jobs };
        var workflow = new JobList("prepared workflow");
        for (int i = 0; i < 64; i++)
        {
            workflow.Add(new SearchJob($"missing {i}")
            {
                WorkflowId = workflow.WorkflowId,
            });
        }

        var eventOrder = new List<string>();
        var registeredIds = new List<Guid>();
        engine.Events.JobRegistered += change =>
        {
            registeredIds.Add(change.Job.Id);
            eventOrder.Add($"registered:{change.Job.Id}");
        };
        engine.Events.JobExecutionCompleted += change =>
        {
            if (change.Job.Id == workflow.Id)
                eventOrder.Add("root-terminal");
        };
        engine.Events.WorkflowRetired += _ => eventOrder.Add("retired");

        engine.Enqueue(workflow, settings);
        engine.CompleteEnqueue();
        await engine.RunAsync(CancellationToken.None);

        Assert.AreEqual(65, registeredIds.Count);
        Assert.IsTrue(eventOrder.IndexOf("root-terminal") < eventOrder.IndexOf("retired"));
        Assert.IsTrue(registeredIds.All(id => engine.GetJob(id) == null));
        Assert.IsNull(workflow.Cts);
        Assert.AreEqual(
            new DownloadEngineRetainedStateCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            engine.RetainedStateCounts);
    }

    [TestMethod]
    public async Task RepeatedDaemonWorkflowsDoNotGrowRetainedOwnerCounts()
    {
        var engineSettings = new EngineSettings
        {
            Username = "test_user",
            Password = "test_pass",
            ConcurrentJobs = 1,
        };
        var client = new MockSoulseekClient([]);
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            retireTerminalWorkflows: true);
        var retired = new Dictionary<Guid, TaskCompletionSource>();
        engine.Events.WorkflowRetired += change => retired[change.WorkflowId].TrySetResult();
        Task run = engine.RunAsync(CancellationToken.None);

        for (int generation = 0; generation < 5; generation++)
        {
            var job = new SearchJob($"missing generation {generation}");
            retired[job.WorkflowId] = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Enqueue(job, new DownloadSettings { PrintOption = PrintOption.Jobs });
            await retired[job.WorkflowId].Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => engine.RetainedStateCounts ==
                    new DownloadEngineRetainedStateCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        }

        engine.CompleteEnqueue();
        await run;
    }

    [TestMethod]
    public async Task OneShotModeRetainsTerminalQueueForRenderingAndExitStatus()
    {
        var engineSettings = new EngineSettings
        {
            Username = "test_user",
            Password = "test_pass",
        };
        var client = new MockSoulseekClient([]);
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));
        var job = new SearchJob("missing");

        engine.Enqueue(job, new DownloadSettings { PrintOption = PrintOption.Jobs });
        engine.CompleteEnqueue();
        await engine.RunAsync(CancellationToken.None);

        Assert.AreSame(job, engine.GetJob(job.Id));
        CollectionAssert.Contains(engine.Queue.Jobs.ToArray(), job);
        Assert.IsTrue(job.IsTerminal);
    }

    [TestMethod]
    public async Task PreparationFailureFailsAndRetiresOnlyItsWorkflow()
    {
        var broken = new SearchJob("broken");
        var healthy = new SearchJob("healthy");
        var engineSettings = new EngineSettings
        {
            Username = "test_user",
            Password = "test_pass",
            ConcurrentJobs = 1,
        };
        var client = new MockSoulseekClient([]);
        await using var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings),
            new SelectiveFailureResolver(broken.Id),
            retireTerminalWorkflows: true);
        var retired = new HashSet<Guid>();
        var failures = new List<JobStateChangedChange>();
        engine.Events.JobStateChanged += change =>
        {
            if (change.Job.TerminalOutcome == JobTerminalOutcome.Failed)
                failures.Add(change);
        };
        engine.Events.WorkflowRetired += change => retired.Add(change.WorkflowId);

        engine.Enqueue(broken, new DownloadSettings { PrintOption = PrintOption.Jobs });
        engine.Enqueue(healthy, new DownloadSettings { PrintOption = PrintOption.Jobs });
        engine.CompleteEnqueue();
        await engine.RunAsync(CancellationToken.None);

        Assert.IsTrue(failures.Any(change => change.Job.Id == broken.Id));
        Assert.IsTrue(healthy.IsTerminal, "A preparation failure must not abort an unrelated queued root.");
        CollectionAssert.AreEquivalent(
            new[] { broken.WorkflowId, healthy.WorkflowId },
            retired.ToArray());
        Assert.AreEqual(
            new DownloadEngineRetainedStateCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            engine.RetainedStateCounts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class SelectiveFailureResolver(Guid failedJobId) : IJobSettingsResolver
    {
        public DownloadSettings Resolve(
            DownloadSettings inherited,
            JobSettingsContext job,
            JobSettingsInheritance inheritance = JobSettingsInheritance.None)
        {
            if (job.Id == failedJobId)
                throw new InvalidOperationException("synthetic preparation failure");
            return SettingsCloner.Clone(inherited);
        }
    }
}
