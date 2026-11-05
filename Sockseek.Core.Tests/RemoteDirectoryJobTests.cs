using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Events;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Tests.ClientTests;

namespace Tests.Core;

[TestClass]
public sealed class RemoteDirectoryJobTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DirectoryProgress_CreditsExistingFilesWithoutInflatingTransferredBytes(bool successful)
    {
        var existingTarget = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Existing.bin"), 64, ".bin");
        var pendingTarget = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Pending.bin"), 192, ".bin");
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(new DirectoryTransferPlan("Root",
            [new DirectoryTransferEntry(existingTarget, []), new DirectoryTransferEntry(pendingTarget, [])])));
        var existing = new RemoteFileJob(existingTarget);
        var pending = new RemoteFileJob(pendingTarget);
        job.MaterializeDirectoryChildren([existing, pending]);
        existing.SetAlreadyExists();
        pending.BytesTransferred = 48;

        Assert.AreEqual(48L, job.BytesTransferred);
        Assert.AreEqual(256L, job.TotalKnownBytes);
        Assert.AreEqual(0.4375, job.Progress, 0.000001);
        var snapshot = (RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 1).Payload;
        if (successful) pending.SetDone();
        else pending.SetCancelled(JobCancellationSource.UserRequestedJob);
        Assert.AreEqual(successful ? 1 : 0.4375, job.Progress, 0.000001,
            "A terminal failed member must not count its remaining bytes as complete.");
        Assert.AreEqual(48L, job.BytesTransferred);
        Assert.AreEqual(0.4375, snapshot.Directory.Progress);
        Assert.AreEqual(48L, snapshot.Directory.BytesTransferred);
    }

    [TestMethod]
    public void TrackedDirectory_PublishesPlanAndMaterializationBeforeLifecycleChanges_AndStopsAfterRetirement()
    {
        var events = new DownloadEvents();
        var tracker = new DownloadJobTracker(events);
        var changes = new List<JobStructureChangedChange>();
        events.JobStructureChanged += changes.Add;
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(new PeerDirectoryIdentity("Peer", "Root")));
        tracker.Register(job, null);
        tracker.Register(job, null);
        var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\A.bin"), 10, ".bin");
        var plan = new DirectoryTransferPlan("Root", [new DirectoryTransferEntry(target, [])]);

        job.BeginDirectoryAttempt(plan);
        var planned = changes.Single();
        Assert.AreEqual(JobLifecycleState.Pending, planned.Job.LifecycleState);
        var plannedPayload = (RemoteDirectoryJobSnapshotPayload)planned.Job.Payload;
        Assert.IsNotNull(plannedPayload.Manifest);
        Assert.IsFalse(plannedPayload.Manifest.ChildrenMaterialized);
        Assert.AreEqual(@"Root\A.bin", plannedPayload.Manifest.Plan.Entries.Single().Target.Identity.Filename);

        var child = new RemoteFileJob(target);
        job.MaterializeDirectoryChildren([child]);
        var materialized = (RemoteDirectoryJobSnapshotPayload)changes.Last().Job.Payload;
        Assert.IsNotNull(materialized.Manifest);
        Assert.IsTrue(materialized.Manifest.ChildrenMaterialized);
        Assert.AreEqual(child.Id, materialized.Manifest.EntryJobIds.Single());
        Assert.IsTrue(changes.Last().Job.Revision > planned.Job.Revision);
        Assert.IsFalse(plannedPayload.Manifest.ChildrenMaterialized);

        job.ResetDirectoryResolution();
        Assert.IsNull(((RemoteDirectoryJobSnapshotPayload)changes.Last().Job.Payload).Manifest);
        int beforeRetirement = changes.Count;
        tracker.Retire([job.Id]);
        job.BeginDirectoryAttempt(plan);
        Assert.AreEqual(beforeRetirement, changes.Count);
    }

    [TestMethod]
    public void TrackedAlbum_PublishesSupplementalWorkWithoutChangingTheAcceptedPlan()
    {
        var events = new DownloadEvents();
        var tracker = new DownloadJobTracker(events);
        var changes = new List<JobStructureChangedChange>();
        events.JobStructureChanged += changes.Add;
        var album = new AlbumJob(new AlbumQuery { Album = "Root" });
        tracker.Register(album, null);
        var folder = new AlbumFolder("Peer", "Root", [TestHelpers.CreateAlbumFile("Peer", @"Root\Track.flac", size: 10, extension: ".flac")]);
        album.BeginAlbumTransferAttempt(folder);
        var accepted = ((AlbumJobSnapshotPayload)changes.Last().Job.Payload).Manifest;
        Assert.IsNotNull(accepted);
        int beforeAdmission = changes.Count;
        var cover = TestHelpers.CreateAlbumFile("Peer", @"Artwork\Cover.jpg", size: 20, extension: ".jpg");
        var differentSpelling = TestHelpers.CreateAlbumFile("Peer", @"Artwork\cover.jpg", size: 30, extension: ".jpg");
        var added = album.EnsureSupplementalTrackJobs([cover, differentSpelling, cover, folder.Files[0]]);
        var art = added[0];
        Assert.AreEqual(beforeAdmission + 1, changes.Count, "Publish the full admitted batch, not each growing prefix.");
        Assert.AreSame(art, added[2]);
        Assert.AreSame(album.ActiveAttempt!.FileJobs[0], added[3]);
        Assert.AreNotEqual(art.Id, added[1].Id, "Exact wire spelling identifies distinct files.");
        var latest = ((AlbumJobSnapshotPayload)changes.Last().Job.Payload).Manifest;
        Assert.IsNotNull(latest);
        Assert.AreEqual(1, latest.Plan.Entries.Count);
        CollectionAssert.AreEqual(new[] { art.Id, added[1].Id }, latest.SupplementalJobIds.ToArray());
        Assert.AreEqual(@"Artwork\Cover.jpg", latest.SupplementalTargets?[0]?.Identity.Filename);
        Assert.AreEqual(20, art.FileSize);
        Assert.AreEqual(60, ((AlbumJobSnapshotPayload)changes.Last().Job.Payload).Directory.TotalKnownBytes);
        Assert.AreEqual(0, accepted.SupplementalJobIds.Count);
        Assert.AreEqual(0, accepted.SupplementalTargets?.Count);
        album.EnsureSupplementalTrackJobs([cover, differentSpelling]);
        Assert.AreEqual(beforeAdmission + 1, changes.Count, "Reusing owned files must not publish another admission.");
        Assert.AreEqual(JobLifecycleState.Pending, album.LifecycleState);
    }

    [TestMethod]
    public void Manifest_RetainsAcceptedEntriesBeforeAndAfterChildMaterialization()
    {
        var first = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\A.bin"), 10, ".bin");
        var second = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\B.bin"), 20, ".bin");
        var plan = new DirectoryTransferPlan("Root", [
            new DirectoryTransferEntry(first, []),
            new DirectoryTransferEntry(second, []),
        ]);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));

        var before = (RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 1).Payload;
        Assert.IsNotNull(before.Manifest);
        Assert.AreEqual(1, before.Manifest.AttemptNumber);
        Assert.AreEqual(2, before.Manifest.Plan.Entries.Count);
        Assert.IsFalse(before.Manifest.ChildrenMaterialized);
        Assert.AreEqual(0, before.Manifest.EntryJobIds.Count);

        var children = new[] { new RemoteFileJob(first), new RemoteFileJob(second) };
        job.MaterializeDirectoryChildren(children);
        var after = (RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 2).Payload;
        Assert.IsNotNull(after.Manifest);
        Assert.IsTrue(after.Manifest.ChildrenMaterialized);
        CollectionAssert.AreEqual(children.Select(child => child.Id).ToArray(),
            after.Manifest.EntryJobIds.ToArray());
        Assert.AreEqual(0, after.Manifest.SupplementalJobIds.Count);

        var replacement = new PeerFileTarget(new PeerFileIdentity("OtherPeer", @"Other\C.bin"), 30, ".bin");
        job.BeginDirectoryAttempt(new DirectoryTransferPlan("Other", [new DirectoryTransferEntry(replacement, [])]));
        var next = (RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 3).Payload;
        Assert.IsNotNull(next.Manifest);
        Assert.AreEqual(2, next.Manifest.AttemptNumber);
        Assert.AreEqual("Other", next.Manifest.Plan.DisplayRoot);
        Assert.AreEqual(@"Other\C.bin", next.Manifest.Plan.Entries.Single().Target.Identity.Filename);
        Assert.AreEqual(2, after.Manifest.Plan.Entries.Count);
    }

    [TestMethod]
    public void DirectorySnapshot_PreservesUnknownPlanSizeCountAcrossAttemptReplacement()
    {
        var unknown = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Unknown.bin"), null, ".bin");
        var empty = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Empty.bin"), 0, ".bin");
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [new DirectoryTransferEntry(unknown, [])])));

        var first = (RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 1).Payload;
        Assert.AreEqual(1, first.Directory.UnknownFileSizeCount);
        Assert.AreEqual(0, first.Directory.TotalKnownBytes);
        Assert.AreEqual(0, job.Progress);

        job.BeginDirectoryAttempt(new DirectoryTransferPlan("Root", [new DirectoryTransferEntry(empty, [])]));
        var second = (RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 2).Payload;
        Assert.AreEqual(0, second.Directory.UnknownFileSizeCount);
        Assert.AreEqual(0, second.Directory.TotalKnownBytes);
        Assert.AreEqual(1, first.Directory.UnknownFileSizeCount);
        var existing = new RemoteFileJob(empty);
        job.MaterializeDirectoryChildren([existing]);
        existing.SetAlreadyExists();
        Assert.AreEqual(1, job.Progress);
        Assert.AreEqual(0, job.BytesTransferred);
    }

    [TestMethod]
    public void AlbumManifest_RetainsSelectedFolderPlanAndTrackIdentity()
    {
        var folder = new AlbumFolder("Peer", "Root", [
            TestHelpers.CreateAlbumFile("Peer", @"Root\Track.flac", size: 10, extension: ".flac"),
        ]);
        var job = new AlbumJob(new AlbumQuery { Album = "Root" });
        job.BeginAlbumTransferAttempt(folder);

        var payload = (AlbumJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 1).Payload;
        Assert.IsNotNull(payload.Manifest);
        Assert.IsTrue(payload.Manifest.ChildrenMaterialized);
        Assert.AreEqual(1, payload.Manifest.Plan.Entries.Count);
        Assert.AreEqual(@"Root\Track.flac", payload.Manifest.Plan.Entries[0].Target.Identity.Filename);
        CollectionAssert.AreEqual(job.TrackJobs.Select(track => track.Id).ToArray(),
            payload.Manifest.EntryJobIds.ToArray());
    }

    [TestMethod]
    public async Task PeerDirectorySource_RetrievesExactlyOnceThenTransfersItsSnapshot()
    {
        string sourceRoot = CreateTempDirectory("source");
        string selected = Path.Combine(sourceRoot, "Selected");
        string nested = Path.Combine(selected, "Nested");
        string output = CreateTempDirectory("output");
        Directory.CreateDirectory(nested);
        string sourceFile = Path.Combine(nested, "File.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4]);
        var client = MockSoulseekClient.FromLocalPaths(useTags: false, sourceRoot);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("local", selected.Replace('/', '\\'))));

        try
        {
            await Run(job, client, output);

            Assert.AreEqual(1, client.BrowseCallCount);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.IsNotNull(job.ResolvedDirectory);
            Assert.AreEqual(1, job.ActiveAttempt!.AttemptNumber);
            Assert.AreEqual(1, job.FileJobs.Count);
            Assert.IsTrue(File.Exists(Path.Combine(output, "Selected", "Nested", "File.bin")));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PeerDirectorySource_TransfersOnlyNonExcludedFiles(bool excludeAll)
    {
        string sourceRoot = CreateTempDirectory("source");
        string output = CreateTempDirectory("output");
        try
        {
            string selected = Path.Combine(sourceRoot, "Selected");
            Directory.CreateDirectory(selected);
            string excluded = Path.Combine(selected, "Excluded.bin");
            string retained = Path.Combine(selected, "Retained.bin");
            await File.WriteAllBytesAsync(excluded, [1, 2]);
            await File.WriteAllBytesAsync(retained, [3, 4]);
            var client = MockSoulseekClient.FromLocalPaths(useTags: false, sourceRoot);
            var exclusions = new List<string> { excluded.Replace('/', '\\') };
            if (excludeAll) exclusions.Add(retained.Replace('/', '\\'));
            var job = new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
                new PeerDirectoryIdentity("local", selected.Replace('/', '\\')), exclusions));

            await Run(job, client, output);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.AreEqual(1, client.BrowseCallCount);
            Assert.AreEqual(excludeAll ? 0 : 1, job.FileJobs.Count);
            Assert.IsFalse(File.Exists(Path.Combine(output, "Selected", "Excluded.bin")));
            Assert.AreEqual(!excludeAll, File.Exists(Path.Combine(output, "Selected", "Retained.bin")));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public async Task ResolvedSource_NeverBrowsesAndUsesTheOwnedPlanAsAttemptOne()
    {
        string sourceRoot = CreateTempDirectory("source");
        string output = CreateTempDirectory("output");
        string sourceFile = Path.Combine(sourceRoot, "Exact.bin");
        await File.WriteAllBytesAsync(sourceFile, [1, 2, 3, 4]);
        string queuedFile = Path.Combine(sourceRoot, "Queued.bin");
        await File.WriteAllBytesAsync(queuedFile, [5, 6]);
        var target = new PeerFileTarget(
            new PeerFileIdentity("local", sourceFile.Replace('/', '\\')),
            4,
            ".bin");
        var plan = new DirectoryTransferPlan("Selection", [
            new DirectoryTransferEntry(target, ["Nested"]),
            new DirectoryTransferEntry(new PeerFileTarget(
                new PeerFileIdentity("local", queuedFile.Replace('/', '\\')), 2, ".bin"), ["Nested"]),
        ]);
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));
        var client = MockSoulseekClient.FromLocalPaths(useTags: false, sourceRoot);
        var downloadGate = new TestHelpers.DownloadGate();
        client.BeforeDownloadCompletesAsync = downloadGate.BlockAsync;
        var engineSettings = new EngineSettings { Username = "test", Password = "test", ConcurrentJobs = 1 };
        var settings = new DownloadSettings { Output = { ParentDir = output, NameFormat = "" } };
        await using var engine = new DownloadEngine(engineSettings, TestHelpers.CreateMockClientManager(client, engineSettings));
        Task? running = null;
        DirectoryManifestSnapshot? transferringManifest = null;
        job.StateChanged += (_, transition) =>
        {
            if (transition.After.ActivityPhase == JobActivityPhase.Downloading)
                transferringManifest = ((RemoteDirectoryJobSnapshotPayload)CoreSnapshotFactory.CreateJob(job, 1).Payload).Manifest;
        };

        try
        {
            engine.Enqueue(job, settings);
            engine.CompleteEnqueue();
            running = engine.RunAsync(CancellationToken.None);
            await downloadGate.WaitForStartedCountAsync(1);
            Assert.AreEqual(2, job.FileJobs.Count);
            foreach (var child in job.FileJobs)
            {
                Assert.AreSame(child, engine.GetJob(child.Id), "Accepted directory members must be addressable before their transfer starts.");
                Assert.IsNotNull(child.Cts);
                Assert.AreEqual(job.WorkflowId, child.WorkflowId);
            }
            Assert.IsTrue(job.FileJobs[1].IsPending);
            Assert.AreEqual(1, downloadGate.StartedCount);
            downloadGate.Open();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(0, client.BrowseCallCount);
            Assert.AreSame(plan, job.ActiveAttempt!.Plan);
            Assert.AreEqual(1, job.ActiveAttempt.AttemptNumber);
            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            Assert.IsNotNull(transferringManifest);
            Assert.IsTrue(transferringManifest.ChildrenMaterialized);
            CollectionAssert.AreEqual(job.FileJobs.Select(child => child.Id).ToArray(), transferringManifest.EntryJobIds.ToArray());
            Assert.IsTrue(File.Exists(Path.Combine(output, "Selection", "Nested", "Exact.bin")));
            CollectionAssert.AreEqual(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(output, "Selection", "Nested", "Queued.bin")));
        }
        finally
        {
            engine.Cancel();
            downloadGate.Open();
            if (running != null) await running.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public void InheritedMusicNameFormat_FallsBackToOrdinaryTreePlacement()
    {
        var target = new PeerFileTarget(
            new PeerFileIdentity("Peer", @"Root\Nested\File.bin"),
            4,
            ".bin");
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [
                new DirectoryTransferEntry(target, ["Nested"]),
            ])));
        var settings = new DownloadSettings
        {
            Output = { NameFormat = "{albumartist}/{album}/{filename}" },
        };

        JobPreparer.PrepareSubtree(job, settings);

        Assert.AreEqual("", job.Config.Output.NameFormat);
    }

    private static async Task Run(
        RemoteDirectoryJob job,
        MockSoulseekClient client,
        string output)
    {
        var engineSettings = new EngineSettings { Username = "test", Password = "test" };
        var settings = new DownloadSettings
        {
            Output =
            {
                ParentDir = output,
                NameFormat = "",
            },
        };
        var engine = new DownloadEngine(
            engineSettings,
            TestHelpers.CreateMockClientManager(client, engineSettings));
        engine.Enqueue(job, settings);
        engine.CompleteEnqueue();
        await engine.RunAsync(CancellationToken.None);
    }

    private static string CreateTempDirectory(string suffix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sockseek-remote-directory-{suffix}-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        return path;
    }
}
