using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class JobPreviewStoreTests
{
    [TestMethod]
    public async Task FailedNodeBatchRollsBackSettingsOrdinalsAndCountsAndCanBeRetried()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        await store.AppendNodesAsync(preview.Id, [Node(preview.Id, "0", null, selectable: false)]);
        StoredJobPreview before = (await store.GetPreviewAsync(preview.Id))!;
        var first = Node(preview.Id, "0/0", "0", selectable: true) with
        {
            EffectiveSettingsRef = "batch-settings", EffectiveSettingsJson = "{\"version\":1}",
        };
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.AppendNodesAsync(
            preview.Id, [first, first with { Ref = "0" }]));
        Assert.AreEqual(before, await store.GetPreviewAsync(preview.Id));
        Assert.AreEqual(0, (await store.GetNodesAsync(preview.Id, "0", -1, 100)).Count);

        // Reusing that settings ref with different content proves the failed
        // batch did not leave settings behind after its node insert failed.
        first = first with
        {
            EffectiveSettingsJson = "{\"version\":2}",
            ItemName = "曲 é ' \"\nnull", QueryText = "null", DirectChildCount = int.MaxValue,
            AppliedAutoProfilesJson = "[\"Exact Profile\"]", SpecificationJson = "{\"text\":\"曲\"}",
            CredentialBindingsJson = "[{\"name\":\"local\"}]",
        };
        var failed = Node(preview.Id, "0/1", "0", selectable: false, state: "Failed");
        await store.AppendNodesAsync(preview.Id, [first, failed]);
        var nodes = await store.GetNodesAsync(preview.Id, "0", -1, 100);
        Assert.AreEqual(first with { Ordinal = 1, SelectableSubtreeCount = 1 }, nodes[0]);
        Assert.AreEqual(failed with { Ordinal = 2 }, nodes[1]);
        preview = await store.CompleteAsync(preview.Id);
        Assert.AreEqual(3, preview.NodeCount);
        Assert.AreEqual(2, preview.ReadyNodeCount);
        Assert.AreEqual(1, preview.FailedNodeCount);
        Assert.AreEqual(1, preview.SelectableNodeCount);
        Assert.AreEqual("PartiallyReady", preview.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConflictingSettingsCannotChangeReviewedContent(bool previouslyStored)
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        var first = Node(preview.Id, "0", null, selectable: true);
        if (previouslyStored) await store.AppendNodesAsync(preview.Id, [first]);
        var before = await store.GetPreviewAsync(preview.Id);
        var conflict = first with { Ref = "1", EffectiveSettingsJson = "{\"different\":true}" };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.AppendNodesAsync(preview.Id,
            previouslyStored ? [conflict] : [first, conflict]));
        Assert.AreEqual(before, await store.GetPreviewAsync(preview.Id));
        var nodes = await store.GetNodesAsync(preview.Id, null, -1, 100);
        Assert.AreEqual(previouslyStored ? 1 : 0, nodes.Count);
        if (previouslyStored) Assert.AreEqual(first.EffectiveSettingsJson, nodes[0].EffectiveSettingsJson);
        await store.AppendNodesAsync(preview.Id, [first with { Ref = "next" }]);
    }

    [TestMethod]
    public async Task PreviewPagesAndRevisionBoundCommitSurviveStoreReopenWithinSession()
    {
        await using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "job-previews.db");
        Guid previewId;
        long previewRevision;

        await using (var store = new JobPreviewStore(path))
        {
            await store.InitializeAsync();
            StoredJobPreview preview = await store.CreateAsync("{\"kind\":\"test\"}");
            previewId = preview.Id;
            await store.AppendNodesAsync(preview.Id,
            [
                Node(preview.Id, "0", null, selectable: false),
                Node(preview.Id, "0/0", "0", selectable: true),
                Node(preview.Id, "0/1", "0", selectable: true),
                Node(preview.Id, "0/2", "0", selectable: false, state: "Failed"),
            ]);
            preview = await store.CompleteAsync(preview.Id);
            previewRevision = preview.Revision;
            Assert.AreEqual("PartiallyReady", preview.State);
            Assert.AreEqual(4, preview.NodeCount);
            Assert.AreEqual(2, preview.SelectableNodeCount);

            IReadOnlyList<StoredJobPreviewNode> firstPage = await store.GetNodesAsync(
                preview.Id, "0", afterOrdinal: 0, limit: 1);
            Assert.AreEqual(1, firstPage.Count);
            IReadOnlyList<StoredJobPreviewNode> secondPage = await store.GetNodesAsync(
                preview.Id, "0", firstPage[0].Ordinal, limit: 2);
            Assert.AreEqual(2, secondPage.Count);
            Assert.AreEqual(
                firstPage[0].EffectiveSettingsRef,
                secondPage[0].EffectiveSettingsRef,
                "Nodes with identical settings should reference one immutable settings record.");

        }

        await using (var reopened = new JobPreviewStore(path))
        {
            await reopened.InitializeAsync();
            StoredPreviewCommit commit = (await reopened.ResolveCommitAsync(
                previewId,
                previewRevision,
                "AllExcept",
                new HashSet<string>(["0/1"], StringComparer.Ordinal)))!;
            Assert.AreEqual(1, commit.SelectedNodes.Count);
            Assert.AreEqual("0/0", commit.SelectedNodes[0].Ref);

            Guid submissionId = Guid.NewGuid();
            Assert.IsTrue(await reopened.TryBeginCommitAsync(previewId, submissionId));
            Assert.IsFalse(await reopened.TryBeginCommitAsync(previewId, Guid.NewGuid()));
            Assert.AreEqual(0, (await reopened.PruneTombstonesAsync(TimeSpan.FromDays(1), TimeSpan.Zero)).Count,
                "A claimed plan must not be pruned while acceptance is in flight.");
            Assert.IsFalse(await reopened.MarkCommittedAsync(previewId, Guid.NewGuid()),
                "A different submission cannot mark this reviewed plan committed.");
            Assert.IsTrue(await reopened.MarkCommittedAsync(previewId, submissionId));
            StoredJobPreview committed = (await reopened.GetPreviewAsync(previewId))!;
            Assert.AreEqual("Committed", committed.State);
            Assert.AreEqual(submissionId, committed.CommittedSubmissionId);
            StoredJobPreview sibling = await reopened.CreateAsync("{}");
            CollectionAssert.AreEqual(new[] { previewId },
                (await reopened.PruneTombstonesAsync(TimeSpan.FromDays(1), TimeSpan.Zero)).ToArray());
            Assert.IsNull(await reopened.GetPreviewAsync(previewId));
            Assert.AreEqual(0, (await reopened.GetNodesAsync(previewId, null, -1, 1)).Count);
            Assert.IsNotNull(await reopened.GetPreviewAsync(sibling.Id), "Uncommitted work must remain available.");
            Assert.AreEqual(0, (await reopened.PruneTombstonesAsync(TimeSpan.FromDays(1), TimeSpan.Zero)).Count);
        }
    }

    [TestMethod]
    public async Task FailedCommitClaimCanBeReleasedForRetry()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        await store.AppendNodesAsync(preview.Id, [Node(preview.Id, "0", null, selectable: true)]);
        await store.CompleteAsync(preview.Id);

        Guid firstSubmission = Guid.NewGuid();
        Assert.IsTrue(await store.TryBeginCommitAsync(preview.Id, firstSubmission));
        Assert.IsTrue(await store.ReleaseCommitAsync(preview.Id, firstSubmission));
        Assert.IsTrue(await store.TryBeginCommitAsync(preview.Id, Guid.NewGuid()));
    }

    [TestMethod]
    public async Task ExpiryPublishesTombstoneBeforePruningSummary()
    {
        await using var directory = new TemporaryDirectory();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        await using var store = new JobPreviewStore(
            Path.Combine(directory.Path, "job-previews.db"),
            clock);
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}", TimeSpan.FromMinutes(1));
        await store.AppendNodesAsync(preview.Id, [Node(preview.Id, "0", null, selectable: true)]);
        await store.CompleteAsync(preview.Id);

        clock.Advance(TimeSpan.FromMinutes(2));
        IReadOnlyList<Guid> expired = await store.ExpireDueAsync();
        CollectionAssert.AreEqual(new[] { preview.Id }, expired.ToArray());
        StoredJobPreview tombstone = (await store.GetPreviewAsync(preview.Id))!;
        Assert.AreEqual("Expired", tombstone.State);
        Assert.AreEqual(0, (await store.GetNodesAsync(preview.Id, null, -1, 10)).Count);

        clock.Advance(TimeSpan.FromDays(2));
        IReadOnlyList<Guid> pruned = await store.PruneTombstonesAsync(
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(30));
        Assert.AreEqual(1, pruned.Count);
        Assert.IsNull(await store.GetPreviewAsync(preview.Id));
    }

    [TestMethod]
    public async Task StreamedPlanPagesAndDirectOnlySelectionStayBounded()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        await store.AppendNodesAsync(preview.Id, [Node(preview.Id, "0", null, selectable: false)]);

        const int batchSize = 73;
        const int nodeCount = 501;
        for (int offset = 0; offset < nodeCount; offset += batchSize)
        {
            await store.AppendNodesAsync(
                preview.Id,
                Enumerable.Range(offset, Math.Min(batchSize, nodeCount - offset))
                    .Select(index => Node(preview.Id, $"0/{index}", "0", selectable: true))
                    .ToArray());
        }
        preview = await store.CompleteAsync(preview.Id);
        Assert.AreEqual(nodeCount + 1, preview.NodeCount);

        long after = -1;
        int traversed = 0;
        while (true)
        {
            IReadOnlyList<StoredJobPreviewNode> page = await store.GetNodesAsync(
                preview.Id,
                "0",
                after,
                limit: 500);
            Assert.IsTrue(page.Count <= 500);
            if (page.Count == 0)
                break;
            traversed += page.Count;
            after = page[^1].Ordinal;
        }
        Assert.AreEqual(nodeCount, traversed);
        Assert.AreEqual(nodeCount, (await store.GetNodesAsync(preview.Id, "0", -1, int.MaxValue)).Count);

        var selectedRefs = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < nodeCount; index += 2)
            selectedRefs.Add($"0/{index}");
        StoredPreviewCommit resolved = (await store.ResolveCommitAsync(
            preview.Id,
            preview.Revision,
            "Only",
            selectedRefs))!;
        Assert.AreEqual((nodeCount + 1) / 2, resolved.SelectedNodes.Count);
        Assert.AreEqual(0, resolved.MissingRequestedRefCount);
    }

    [TestMethod]
    [TestCategory("Load")]
    public async Task TenThousandNodePreviewMeasuresPagedReadsAndExactAndSubtreeSelection()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        var settings = new DownloadSettings();
        SubmissionSpecification song = SubmissionSpecification.CreateShallow(
            new SongJob(new SongQuery { Artist = "Fixture", Title = "Track" }), settings);
        SubmissionSpecification container = SubmissionSpecification.CreateShallow(new JobList("Fixture"), settings);
        string Compact(SubmissionSpecification specification) => SubmissionSpecificationCodec.Serialize(specification with
        {
            Command = specification.Command with { PlannedEffectiveSettingsJson = null, PlannedCredentialBindings = null },
            EffectiveSettingsJson = "{}", CredentialBindings = [],
        });
        string songJson = Compact(song);
        string containerJson = Compact(container);
        string[] branchSettings = Enumerable.Range(0, 100).Select(branch =>
        {
            var configured = SettingsCloner.Clone(settings);
            configured.Output.ParentDir = $"branch-{branch:D3}";
            return SubmissionSpecification.CreateShallow(
                new SongJob(new SongQuery { Artist = "Fixture", Title = "Track" }), configured).EffectiveSettingsJson;
        }).ToArray();
        StoredJobPreviewNode PlannedNode(string nodeRef, string? parent, bool selectable,
            int children = 0, int? settingsBranch = null)
            => Node(preview.Id, nodeRef, parent, selectable) with
            {
                Kind = selectable ? "Song" : "JobList",
                DirectChildCount = children,
                SpecificationJson = selectable ? songJson : containerJson,
                EffectiveSettingsRef = settingsBranch is int group ? $"settings-{group}" : "shared-settings",
                EffectiveSettingsJson = settingsBranch is int index ? branchSettings[index] : song.EffectiveSettingsJson,
                CredentialBindingsJson = "[]",
            };
        var watch = Stopwatch.StartNew();
        await store.AppendNodesAsync(preview.Id, [PlannedNode("0", null, false, 100)]);
        for (int branch = 0; branch < 100; branch++)
        {
            var nodes = new List<StoredJobPreviewNode> { PlannedNode($"0/{branch}", "0", false, 100, branch) };
            nodes.AddRange(Enumerable.Range(0, 100)
                .Select(leaf => PlannedNode($"0/{branch}/{leaf}", $"0/{branch}", true, settingsBranch: branch)));
            await store.AppendNodesAsync(preview.Id, nodes);
        }
        preview = await store.CompleteAsync(preview.Id);
        TimeSpan publication = watch.Elapsed;
        Assert.AreEqual(10_000, preview.SelectableNodeCount);
        watch.Restart();
        StoredJobPreviewNode root = (await store.GetNodesAsync(preview.Id, null, -1, 1)).Single();
        IReadOnlyList<StoredJobPreviewNode> branches = await store.GetNodesAsync(preview.Id, "0", -1, 20);
        IReadOnlyList<StoredJobPreviewNode> leaves = await store.GetNodesAsync(preview.Id, "0/99", -1, 20);
        TimeSpan pages = watch.Elapsed;
        Assert.AreEqual(10_000, root.SelectableSubtreeCount);
        Assert.AreEqual(20, branches.Count);
        Assert.IsTrue(branches.All(node => node.SelectableSubtreeCount == 100));
        Assert.AreEqual(20, branches.Select(node => node.EffectiveSettingsRef).Distinct().Count());
        Assert.AreEqual(20, leaves.Count);
        Assert.IsTrue(leaves.All(node => node.SelectableSubtreeCount == 1));
        Assert.AreEqual(1, leaves.Select(node => node.EffectiveSettingsRef).Distinct().Count());

        watch.Restart();
        StoredPreviewCommit exact = (await store.ResolveCommitAsync(preview.Id, preview.Revision, "Only",
            new HashSet<string>(["0/99/99", "absent"], StringComparer.Ordinal)))!;
        TimeSpan exactElapsed = watch.Elapsed;
        CollectionAssert.AreEqual(new[] { "0/99/99" }, exact.SelectedNodes.Select(node => node.Ref).ToArray());
        CollectionAssert.AreEquivalent(new[] { "0", "0/99" }, exact.AncestorNodes.Select(node => node.Ref).ToArray());
        Assert.AreEqual(1, exact.MissingRequestedRefCount);
        Assert.AreEqual(branchSettings[99], exact.SelectedNodes.Single().EffectiveSettingsJson);

        watch.Restart();
        StoredPreviewCommit rules = (await store.ResolveCommitAsync(preview.Id, preview.Revision, "Only",
            new HashSet<string>(), [new("0/99", true), new("0/99/50", false)]))!;
        TimeSpan rulesElapsed = watch.Elapsed;
        Assert.AreEqual(99, rules.SelectedNodes.Count);
        Assert.IsTrue(rules.SelectedNodes.All(node => node.ParentRef == "0/99" && node.Ref != "0/99/50"));

        watch.Restart();
        StoredPreviewCommit allExcept = (await store.ResolveCommitAsync(preview.Id, preview.Revision, "AllExcept",
            new HashSet<string>(), [new("0/0", false), new("0/0/1", true)]))!;
        TimeSpan allElapsed = watch.Elapsed;
        Assert.AreEqual(9_901, allExcept.SelectedNodes.Count);
        Assert.AreEqual(0, allExcept.MissingRequestedRefCount);
        Assert.AreEqual(1, allExcept.SelectedNodes.Count(node => node.ParentRef == "0/0"));
        Guid submissionId = Guid.NewGuid();
        Assert.IsTrue(await store.TryBeginCommitAsync(preview.Id, submissionId));
        Assert.IsTrue(await store.MarkCommittedAsync(preview.Id, submissionId));
        watch.Restart();
        IReadOnlyList<Guid> cleanup = await store.PruneTombstonesAsync(TimeSpan.FromDays(1), TimeSpan.Zero);
        TimeSpan cleanupElapsed = watch.Elapsed;
        CollectionAssert.AreEqual(new[] { preview.Id }, cleanup.ToArray());
        Assert.IsNull(await store.GetPreviewAsync(preview.Id));
        Assert.AreEqual(0, (await store.GetNodesAsync(preview.Id, null, -1, 1)).Count);
        Assert.IsTrue(cleanupElapsed < TimeSpan.FromSeconds(1),
            $"Deleting a 10k-node plan with varied settings took {cleanupElapsed.TotalMilliseconds:F1} ms; it must not take seconds.");
        Console.WriteLine($"Preview 10k: publish={publication.TotalMilliseconds:F1} ms; "
            + $"three pages={pages.TotalMilliseconds:F1} ms; exact={exactElapsed.TotalMilliseconds:F1} ms; "
            + $"subtree={rulesElapsed.TotalMilliseconds:F1} ms; all-except={allElapsed.TotalMilliseconds:F1} ms; "
            + $"delete={cleanupElapsed.TotalMilliseconds:F1} ms.");
    }

    [TestMethod]
    public async Task PagedParentCountsAndSubtreeRulesSelectUnloadedDescendants()
    {
        await using var directory = new TemporaryDirectory();
        await using var store = new JobPreviewStore(Path.Combine(directory.Path, "job-previews.db"));
        await store.InitializeAsync();
        StoredJobPreview preview = await store.CreateAsync("{}");
        await store.AppendNodesAsync(preview.Id,
        [
            Node(preview.Id, "0", null, selectable: false),
            Node(preview.Id, "0/0", "0", selectable: false),
            Node(preview.Id, "0/0/0", "0/0", selectable: true),
            Node(preview.Id, "0/0/1", "0/0", selectable: true),
            Node(preview.Id, "0/1", "0", selectable: true),
            Node(preview.Id, "0/2", "0", selectable: false, state: "Failed"),
        ]);
        preview = await store.CompleteAsync(preview.Id);

        StoredJobPreviewNode root = (await store.GetNodesAsync(preview.Id, null, -1, 1)).Single();
        StoredJobPreviewNode branch = (await store.GetNodesAsync(preview.Id, "0", -1, 1)).Single();
        Assert.AreEqual(3, root.SelectableSubtreeCount);
        Assert.AreEqual(2, branch.SelectableSubtreeCount);

        StoredPreviewCommit selected = (await store.ResolveCommitAsync(
            preview.Id, preview.Revision, "Only", new HashSet<string>(),
            [new("0", true), new("0/0", false), new("0/0/1", true)]))!;
        CollectionAssert.AreEquivalent(
            new[] { "0/0/1", "0/1" },
            selected.SelectedNodes.Select(node => node.Ref).ToArray());
        Assert.AreEqual(0, selected.MissingRequestedRefCount);

        StoredPreviewCommit excluded = (await store.ResolveCommitAsync(
            preview.Id, preview.Revision, "AllExcept", new HashSet<string>(),
            [new("0/0", false), new("0/0/1", true)]))!;
        CollectionAssert.AreEquivalent(
            new[] { "0/0/1", "0/1" },
            excluded.SelectedNodes.Select(node => node.Ref).ToArray());

        StoredPreviewCommit missing = (await store.ResolveCommitAsync(
            preview.Id, preview.Revision, "Only", new HashSet<string>(),
            [new("not-in-this-preview", true)]))!;
        Assert.AreEqual(1, missing.MissingRequestedRefCount);
        Assert.AreEqual(0, missing.SelectedNodes.Count);
    }

    private static StoredJobPreviewNode Node(
        Guid previewId,
        string nodeRef,
        string? parentRef,
        bool selectable,
        string state = "Ready")
        => new(
            previewId,
            0,
            nodeRef,
            parentRef,
            "ExecutionChild",
            state,
            selectable,
            "Song",
            nodeRef,
            null,
            0,
            "[]",
            selectable ? "{}" : null,
            selectable ? "same-settings" : null,
            selectable ? "{\"downloadMode\":\"Normal\"}" : null,
            selectable ? "[]" : null,
            state == "Failed" ? "test" : null,
            state == "Failed" ? "failed row" : null);

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sockseek-preview-tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan elapsed) => current += elapsed;
    }
}
