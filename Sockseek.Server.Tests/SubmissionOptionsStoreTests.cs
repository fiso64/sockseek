using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SubmissionOptionsStoreTests
{
    [TestMethod]
    public void ItemPatchComposesOverWorkflowProfilesAndPatch()
    {
        var store = new SubmissionOptionsStore();
        Guid workflowId = Guid.NewGuid();
        var job = new SearchJob("query") { WorkflowId = workflowId };
        store.SetWorkflowOptions(workflowId, new SubmissionOptionsDto(
            OutputParentDir: "workflow-output",
            ProfileNames: ["music"],
            ProfileContext: new Dictionary<string, bool> { ["interactive"] = true },
            DownloadSettings: new DownloadSettingsPatchDto(
                Search: new SearchSettingsPatchDto(NoBrowseFolder: true))));
        store.SetJobOptions(job.Id, new SubmissionOptionsDto(
            ProfileContext: new Dictionary<string, bool> { ["interactive"] = false },
            DownloadSettings: new DownloadSettingsPatchDto(
                Search: new SearchSettingsPatchDto(MaxStaleTime: 17))));

        SubmissionOptionsDto? effective = store.GetOptions(job);

        Assert.IsNotNull(effective);
        CollectionAssert.AreEqual(new[] { "music" }, effective.ProfileNames?.ToArray());
        Assert.AreEqual("workflow-output", effective.OutputParentDir);
        Assert.AreEqual(false, effective.ProfileContext?["interactive"]);
        Assert.AreEqual(true, effective.DownloadSettings?.Search?.NoBrowseFolder);
        Assert.AreEqual(17, effective.DownloadSettings?.Search?.MaxStaleTime);
    }

    [TestMethod]
    public void RetirementClearsExactGenerationStateWithoutDeletingLaterSameIdOptions()
    {
        var store = new SubmissionOptionsStore();
        Guid workflowId = Guid.NewGuid();
        Guid firstJobId = Guid.NewGuid();
        var firstOptions = new SubmissionOptionsDto(ProfileNames: ["first"]);
        store.SetWorkflowOptions(workflowId, firstOptions);
        store.SetJobOptions(firstJobId, firstOptions);
        store.SetJobOutputParentDir(firstJobId, "first-output");
        long firstVersion = store.CaptureWorkflowVersion(workflowId);

        var laterOptions = new SubmissionOptionsDto(ProfileNames: ["later"]);
        store.SetWorkflowOptions(workflowId, laterOptions);
        store.RetireWorkflow(workflowId, [firstJobId], firstVersion);

        var laterJob = new SearchJob("query") { WorkflowId = workflowId };
        Assert.AreSame(laterOptions, store.GetOptions(laterJob));
        Assert.IsNull(store.GetJobOutputParentDir(firstJobId));
        Assert.AreEqual((1, 0, 0), store.RetainedStateCounts);

        store.RetireWorkflow(
            workflowId,
            [laterJob.Id],
            store.CaptureWorkflowVersion(workflowId));
        Assert.AreEqual((0, 0, 0), store.RetainedStateCounts);
    }

    [TestMethod]
    public void PlanningScopeKeepsItsSettingsWhenWorkflowOptionsAreReplaced()
    {
        var resolver = new SubmissionOptionsJobSettingsResolver(new ProfileJobSettingsResolver(
            null, ProfileCatalog.Empty, [], null));
        Guid workflowId = Guid.NewGuid();
        var job = new SearchJob("query") { WorkflowId = workflowId };
        resolver.SetWorkflowOptions(workflowId, new SubmissionOptionsDto(
            OutputParentDir: "reviewed-output",
            DownloadSettings: new DownloadSettingsPatchDto(
                Search: new SearchSettingsPatchDto(MaxStaleTime: 17))));
        var scope = resolver.CreateScope(workflowId);
        var inherited = new DownloadSettings();

        DownloadSettings first = scope.Resolve(inherited, job);
        first.Output.ParentDir = "edited-copy";
        first.Search.MaxStaleTime = 99;
        resolver.SetWorkflowOptions(workflowId, new SubmissionOptionsDto(
            OutputParentDir: "later-output",
            DownloadSettings: new DownloadSettingsPatchDto(
                Search: new SearchSettingsPatchDto(MaxStaleTime: 23))));

        DownloadSettings reviewed = scope.Resolve(inherited, job);
        DownloadSettings later = resolver.Resolve(inherited, job);
        Assert.AreEqual("reviewed-output", reviewed.Output.ParentDir);
        Assert.AreEqual(17, reviewed.Search.MaxStaleTime);
        Assert.AreEqual("later-output", later.Output.ParentDir);
        Assert.AreEqual(23, later.Search.MaxStaleTime);
    }

    [TestMethod]
    public void ReusedWorkflowPatchPreservesItemIsolationOutputPrecedenceAndProvenance()
    {
        var inner = new ProfileJobSettingsResolver(null, ProfileCatalog.Empty, [], null);
        var resolver = new SubmissionOptionsJobSettingsResolver(inner);
        Guid workflowId = Guid.NewGuid();
        var ordinary = new SearchJob("ordinary") { WorkflowId = workflowId };
        var patched = new SearchJob("patched") { WorkflowId = workflowId };
        var isolated = new SearchJob("isolated") { WorkflowId = workflowId };
        var options = new SubmissionOptionsDto(OutputParentDir: "workflow-output",
            DownloadSettings: new DownloadSettingsPatchDto(
                Search: new SearchSettingsPatchDto(MaxStaleTime: 17)));
        resolver.SetWorkflowOptions(workflowId, options);
        resolver.SetJobOptions(patched.Id, new SubmissionOptionsDto(OutputParentDir: "item-output"));
        resolver.SetIsolatedJobOptions(isolated.Id, new SubmissionOptionsDto(OutputParentDir: "isolated-output"));
        resolver.SetJobOutputParentDir(patched.Id, "source-output");
        var inherited = new DownloadSettings();

        Assert.AreEqual(17, resolver.Resolve(inherited, ordinary).Search.MaxStaleTime);
        DownloadSettings item = resolver.Resolve(inherited, patched);
        Assert.AreEqual("source-output", item.Output.ParentDir);
        Assert.AreEqual(17, item.Search.MaxStaleTime);
        DownloadSettings separate = resolver.Resolve(inherited, isolated);
        Assert.AreEqual("isolated-output", separate.Output.ParentDir);
        Assert.AreEqual(new DownloadSettings().Search.MaxStaleTime, separate.Search.MaxStaleTime);
        Assert.AreEqual("workflow-output", resolver.Resolve(inherited, ordinary).Output.ParentDir);
        var detailed = inner.ResolveDetailed(inherited, ordinary,
            request: resolver.Options.GetRequestLayers(ordinary));
        Assert.AreEqual("request", detailed.Provenance["Output.ParentDir"]);
        Assert.AreEqual("request", detailed.Provenance["Search.MaxStaleTime"]);
    }
}
