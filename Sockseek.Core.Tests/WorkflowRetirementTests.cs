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
