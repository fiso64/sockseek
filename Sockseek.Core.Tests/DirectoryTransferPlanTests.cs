using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Core;

[TestClass]
public sealed class DirectoryTransferPlanTests
{
    [TestMethod]
    [TestCategory("Load")]
    [DataRow(false)]
    [DataRow(true)]
    public void ObservedSelectionPlanning_HandlesTenThousandFilesAndCompactExclusions(bool bare)
    {
        var files = Enumerable.Range(0, 10_000)
            .Select(index => Target("Peer", bare ? $"Track-{index:D5}.flac" : $@"Root\Disc-{index / 100:D2}\Track-{index:D5}.flac", 1))
            .Append(Target("Peer", @"Root\..\Escape.flac", 1))
            .Append(Target("Peer", @"root\Case-distinct.flac", 1))
            .ToArray();
        var excluded = Enumerable.Range(0, 100)
            .Select(index => bare ? $"Track-{index * 100:D5}.flac" : $@"Root\Disc-{index:D2}\Track-{index * 100:D5}.flac")
            .ToHashSet(StringComparer.Ordinal);
        var observation = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", bare ? "" : "Root"), files, isComplete: false);

        var timer = Stopwatch.StartNew();
        DirectoryTransferPlan plan = DirectoryTransferPlanner.FromObservedSelection(
            observation, excluded);
        timer.Stop();

        Assert.AreEqual(9_900, plan.Entries.Count);
        Assert.AreEqual(9_900L, plan.TotalKnownBytes);
        Assert.IsFalse(plan.Entries.Any(entry => excluded.Contains(entry.Target.Filename)));
        Assert.IsFalse(plan.Entries.Any(entry => entry.Target.Filename.Contains("Escape", StringComparison.Ordinal)));
        Assert.IsFalse(plan.Entries.Any(entry => entry.Target.Filename.Contains("Case-distinct", StringComparison.Ordinal)));
        Console.WriteLine($"PERF_PLAN bare={bare} observed-10k-files-ms={timer.ElapsedMilliseconds}");
    }

    [TestMethod]
    public void Plan_CopiesAndDeterministicallyOrdersEntries()
    {
        var components = new List<string> { "Disc 2" };
        var entries = new List<DirectoryTransferEntry>
        {
            Entry("Peer", @"Root\Disc 2\02.flac", 2, components),
            Entry("Peer", @"Root\Disc 1\01.flac", 1, ["Disc 1"]),
        };

        var plan = new DirectoryTransferPlan("Selection", entries);
        components[0] = "Changed";
        entries.Clear();

        Assert.AreEqual(2, plan.Entries.Count);
        Assert.AreEqual("Disc 1", plan.Entries[0].RelativeDirectoryComponents[0]);
        Assert.AreEqual("Disc 2", plan.Entries[1].RelativeDirectoryComponents[0]);
        Assert.AreEqual(3L, plan.TotalKnownBytes);
    }

    [TestMethod]
    public void Plan_RejectsMixedPeersAndDeduplicatesExactTargets()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 1),
            Entry("Other", @"Root\B.flac", 1),
        ]));

        var deduplicated = new DirectoryTransferPlan("Root", [
            Entry("Peer", @"Root\A.flac", 1),
            Entry("Peer", @"Root\A.flac", 2),
        ]);

        Assert.AreEqual(1, deduplicated.Entries.Count);
        Assert.AreEqual(1L, deduplicated.TotalKnownBytes);
    }

    [TestMethod]
    public void Plan_DistinguishesUnknownSizesFromRealZeroByteFilesAfterDeduplication()
    {
        var unknown = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Unknown.bin"), null, ".bin");
        var empty = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Empty.bin"), 0, ".bin");
        var plan = new DirectoryTransferPlan("Root", [
            new DirectoryTransferEntry(unknown, []),
            new DirectoryTransferEntry(unknown, []),
            new DirectoryTransferEntry(empty, []),
        ]);

        Assert.AreEqual(2, plan.Entries.Count);
        Assert.AreEqual(0, plan.TotalKnownBytes);
        Assert.AreEqual(1, plan.UnknownFileSizeCount);
    }

    [TestMethod]
    public void Entry_RejectsRootedEmptyTraversalAndSeparatorComponents()
    {
        var target = Target("Peer", @"Root\A.flac", 1);

        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, [""]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, [".."]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, ["A/B"]));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferEntry(target, ["A\\B"]));
    }

    [TestMethod]
    public void Plan_PreservesControlBearingRemoteTreeComponents()
    {
        var entry = new DirectoryTransferEntry(
            Target("Peer", "Root\\A\nB\\file\u001B.bin", 1),
            ["A\nB"]);

        var plan = new DirectoryTransferPlan("Root\t", [entry]);

        Assert.AreEqual("Root\t", plan.DisplayRoot);
        Assert.AreEqual("A\nB", plan.Entries.Single().RelativeDirectoryComponents.Single());
    }

    [TestMethod]
    public void PeerDirectorySnapshot_CopiesTargetsAndRejectsMixedPeers()
    {
        var files = new List<PeerFileTarget> { Target("Peer", @"Root\A.flac", 1) };
        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", "Root"),
            files,
            isComplete: true);
        files.Clear();

        Assert.AreEqual(1, snapshot.Files.Count);
        Assert.IsTrue(snapshot.IsComplete);
        Assert.ThrowsExactly<ArgumentException>(() => new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", "Root"),
            [Target("Other", @"Root\B.flac", 1)],
            isComplete: true));
    }

    [TestMethod]
    public void SnapshotPlanning_UsesExactCaseForRemoteDirectoryContainment()
    {
        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Root\Selected"),
            [Target("Peer", @"Root\selected\A.flac", 1)],
            isComplete: true);

        Assert.ThrowsExactly<ArgumentException>(() => DirectoryTransferPlanner.FromSnapshot(snapshot));
    }

    [TestMethod]
    public void SnapshotPlanning_SkipsInvalidEntryWithoutRejectingValidSibling()
    {
        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Root\Selected"),
            [
                Target("Peer", @"Root\Selected\Good.flac", 1),
                Target("Peer", @"Root\Other\Outside.flac", 2),
            ],
            isComplete: true);

        var plan = DirectoryTransferPlanner.FromSnapshot(snapshot);

        Assert.AreEqual(1, plan.Entries.Count);
        Assert.AreEqual(@"Root\Selected\Good.flac", plan.Entries[0].Target.Filename);
        Assert.AreEqual("Peer", plan.SourceDirectory?.Username);
        Assert.AreEqual(@"Root\Selected", plan.SourceDirectory?.FolderPath);
    }

    [TestMethod]
    [DataRow("Root")]
    [DataRow("")]
    public void ObservedSelectionPlanning_UsesIncompleteObservationWithoutInventingFullDirectory(string root)
    {
        string prefix = root.Length == 0 ? "" : root + "\\";
        var observation = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", root),
            [Target("Peer", prefix + "Observed.flac", 1),
             Target("Peer", prefix + "Excluded.flac", 2)],
            isComplete: false);

        Assert.ThrowsExactly<ArgumentException>(() => DirectoryTransferPlanner.FromSnapshot(observation));
        var plan = DirectoryTransferPlanner.FromObservedSelection(
            observation,
            new HashSet<string>(StringComparer.Ordinal) { prefix + "Excluded.flac" });

        Assert.AreEqual(1, plan.Entries.Count);
        Assert.AreEqual(prefix + "Observed.flac", plan.Entries[0].Target.Filename);
        Assert.IsFalse(observation.IsComplete);
        Assert.AreEqual(root.Length == 0 ? null : root, plan.SourceDirectory?.FolderPath);
        Assert.AreEqual(root.Length != 0, plan.RepresentsDirectorySelection);
    }

    [TestMethod]
    public void Plan_RejectsFalseSourceDirectoryForLogicalOrUnrelatedFiles()
    {
        var source = new PeerDirectoryIdentity("Peer", @"Root\Selected");
        var inside = Entry("Peer", @"Root\Selected\A.flac", 1);
        var outside = Entry("Peer", @"Root\Other\A.flac", 1);

        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferPlan(
            "Selected", [inside], representsDirectorySelection: false,
            sourceDirectory: source));
        Assert.ThrowsExactly<ArgumentException>(() => new DirectoryTransferPlan(
            "Selected", [outside], sourceDirectory: source));
        Assert.IsNull(new DirectoryTransferPlan("Selection", [outside]).SourceDirectory);
    }

    [TestMethod]
    public void SnapshotPlanning_ExcludesOnlyExactWireFilenames()
    {
        var excluded = new List<string> { @"Root\A.flac" };
        var source = new Sockseek.Core.Jobs.RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("Peer", "Root"), excluded);
        excluded.Clear();
        var snapshot = new PeerDirectorySnapshot(source.Directory,
            [Target("Peer", @"Root\A.flac", 1),
             Target("Peer", @"Root\a.flac", 2),
             Target("Peer", "Root/A.flac", 3),
             Target("Peer", @"Root\Nested\B.flac", 4)], isComplete: true);

        var plan = DirectoryTransferPlanner.FromSnapshot(snapshot, source.ExcludedFilenames);

        CollectionAssert.AreEquivalent(
            new[] { @"Root\a.flac", "Root/A.flac", @"Root\Nested\B.flac" },
            plan.Entries.Select(entry => entry.Target.Filename).ToArray());
        Assert.AreEqual(9L, plan.TotalKnownBytes);
    }

    private static DirectoryTransferEntry Entry(
        string username,
        string filename,
        long size,
        IReadOnlyList<string>? components = null)
        => new(Target(username, filename, size), components ?? Array.Empty<string>());

    private static PeerFileTarget Target(string username, string filename, long size)
        => new(new PeerFileIdentity(username, filename), size, Path.GetExtension(filename));
}
