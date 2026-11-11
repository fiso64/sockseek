using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Events;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Persistence.Write;
using Sockseek.Persistence.Read;
using Sockseek.Api;
using Sockseek.Server.Persistence;
using Soulseek;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class EnginePersistenceTests
{
    [TestMethod]
    [DataRow(16, true)]
    [DataRow(2, true)]
    [DataRow(32, true)]
    [DataRow(6, false)]
    [DataRow(32, false)]
    public async Task DownloadRetriesPreserveOrRestartPartialFilesAndAccountForActualBytes(int remoteSize, bool interrupted)
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-resume-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        byte[] content = Enumerable.Range(0, remoteSize).Select(value => (byte)value).ToArray();
        bool changedSize = remoteSize != 16;
        var offsets = new List<long>();
        var fake = global::Tests.Server.SoulseekClientProxy.Create();
        fake.DownloadHandler = async args =>
        {
            long offset = (long)args[4]!;
            offsets.Add(offset);
            bool firstPartial = interrupted && offsets.Count == 1;
            long announcedSize = firstPartial ? 16 : content.Length;
            long expectedSize = (long)args[3]!;
            if (expectedSize != announcedSize)
                throw new TransferSizeMismatchException(expectedSize, announcedSize);
            var options = (TransferOptions)args[6]!;
            var ct = (CancellationToken)args[7]!;
            Stream output = await ((Func<Task<Stream>>)args[2]!)();
            Assert.AreEqual(offset, output.Position);
            long end = firstPartial ? 4 : content.Length;
            Transfer Snapshot(TransferStates state, long bytes) => new(TransferDirection.Download,
                (string)args[0]!, (string)args[1]!, 1, state, announcedSize, offset, bytes);
            options.StateChanged?.Invoke((TransferStates.Queued, Snapshot(TransferStates.InProgress, offset)));
            options.ProgressUpdated?.Invoke((offset, Snapshot(TransferStates.InProgress, offset)));
            // A changed file's old prefix must not survive the restart.
            await output.WriteAsync(firstPartial && changedSize ? new byte[] { 255, 255, 255, 255 }
                : content.AsMemory((int)offset, (int)(end - offset)), ct);
            options.ProgressUpdated?.Invoke((offset, Snapshot(TransferStates.InProgress, end)));
            if (firstPartial)
            {
                options.StateChanged?.Invoke((TransferStates.InProgress, Snapshot(TransferStates.Completed | TransferStates.Errored, end)));
                throw new ConnectionException("Peer connection interrupted after four bytes.");
            }
            var completed = Snapshot(TransferStates.Completed | TransferStates.Succeeded, end);
            options.StateChanged?.Invoke((TransferStates.InProgress, completed));
            return completed;
        };
        var settings = new Sockseek.Core.Settings.EngineSettings { Username = "test", Password = "test" };
        await using var manager = new SoulseekClientManager(settings, fake.Client);
        await using var engine = new DownloadEngine(settings, manager);
        var sink = new CapturingSink();
        AttachPersistence(engine.Events, Guid.NewGuid(), sink);
        var job = new RemoteFileJob(new PeerFileTarget(new PeerFileIdentity("Peer", @"Share\Track.bin"), 16, ".bin"));
        var download = new Sockseek.Core.Settings.DownloadSettings
        {
            Output = { ParentDir = root, NameFormat = "" },
            Transfer = { UnknownErrorRetries = interrupted ? 1 : 0 },
        };
        try
        {
            engine.Enqueue(job, download);
            engine.CompleteEnqueue();
            await engine.RunAsync(CancellationToken.None);

            Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome);
            CollectionAssert.AreEqual(interrupted ? changedSize ? new long[] { 0, 4, 0 } : [0, 4] : [0, 0], offsets);
            CollectionAssert.AreEqual(content, await System.IO.File.ReadAllBytesAsync(job.DownloadPath!));
            Assert.IsFalse(System.IO.File.Exists(job.DownloadPath + ".incomplete"));
            var terminal = sink.Mutations.OfType<TransferTerminalPersistenceMutation>().Single();
            Assert.AreEqual(remoteSize, job.FileSize);
            Assert.AreEqual(remoteSize, job.BytesTransferred);
            Assert.AreEqual(remoteSize, terminal.Transfer.TransferredBytes);
            Assert.AreEqual(remoteSize, terminal.Transfer.TotalBytes);
            Assert.AreEqual(offsets.Count, terminal.Transfer.AttemptCount);
            var observations = sink.Mutations.SelectMany(mutation => mutation switch
            {
                TransferPersistenceMutation transfer => transfer.AccountingObservations ?? [],
                TransferAttemptPersistenceMutation attempt => attempt.AccountingObservations ?? [],
                TransferTerminalPersistenceMutation end => end.Transfer.AccountingObservations ?? [],
                _ => Array.Empty<TransferAccountingObservation>(),
            }).GroupBy(observation => observation.AttemptId)
                .Select(attempt => attempt.Max(observation => observation.CumulativeBytes)).Order().ToArray();
            long[] expectedBytes = interrupted ? [4, changedSize ? remoteSize : remoteSize - 4] : [remoteSize];
            if (changedSize) expectedBytes = [.. expectedBytes, 0]; // Rejected negotiation sends no file bytes.
            CollectionAssert.AreEqual(expectedBytes.Order().ToArray(), observations);
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize]
    public void RepeatedJobStateCaptureDoesNotEncodeRetainedDetail()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var job = new GenericTestJob(new string('x', 16 * 1024));
        events.RaiseJobStateChanged(job);
        sink.Mutations.Clear();
        // Long input/detail can be shared by many immutable state captures.
        // Measure capture, not SQL or the eventual encoding of the chosen state.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int index = 0; index < 512; index++) events.RaiseJobStateChanged(job);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.HasCount(512, sink.Mutations);
        Assert.IsLessThan(4 * 1024 * 1024, allocated,
            $"Replaceable state capture must not repeatedly encode retained detail: {allocated:N0} bytes.");
        Assert.IsTrue(sink.Mutations.OfType<JobPersistenceMutation>().All(mutation => !mutation.PayloadJson!.IsValueCreated));
    }

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FreshDownloadCompletionKeepsDurableCommandsResponsive(bool cachedCopy)
    {
        string root = Path.Combine(Path.GetTempPath(), "sockseek-output-commit-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        using var files = new DownloadFileOperations(2);
        var outputs = new List<Stream>();
        byte[] chunk = new byte[64 * 1024];
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "probe.db"), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=0; CREATE TABLE entries(value BLOB NOT NULL);";
            command.ExecuteNonQuery();
            var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Share\File.bin"), 8 * 1024 * 1024, ".bin");
            if (cachedCopy)
            {
                string source = Path.Combine(root, "source.bin");
                using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
                {
                    for (int part = 0; part < 128; part++) file.Write(chunk);
                    file.Flush(flushToDisk: true); // Prepare a stable existing input before measurement.
                }
                await files.WithCacheAsync(cache => { cache.Publish(source, target, Guid.NewGuid()); return true; });
            }
            else
            {
                // Fast peers can leave this payload buffered just before a user
                // submits another job. Keep writes outside the measured boundary.
                for (int file = 0; file < 8; file++)
                {
                    Stream output = await files.OpenAsync(Path.Combine(root, file + ".bin"));
                    outputs.Add(output);
                    for (int part = 0; part < 128; part++) await output.WriteAsync(chunk.AsMemory());
                }
            }
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (cachedCopy)
            {
                for (int file = 0; file < 8; file++)
                    Assert.IsNotNull(await files.TryReuseAsync(target, Path.Combine(root, file + ".bin"), Guid.NewGuid(), Guid.NewGuid(),
                        publish: false, allowOverwrite: true, protectPublishedOutput: false));
            }
            else
            {
                await Task.WhenAll(outputs.Select(output => files.CloseAsync(output).AsTask()));
                outputs.Clear();
            }
            command.CommandText = "INSERT INTO entries VALUES(zeroblob(4096));";
            command.ExecuteNonQuery();
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Console.WriteLine($"Complete eight 8 MiB outputs (cachedCopy={cachedCopy}) and commit 4 KiB: {elapsed:F1} ms.");
            Assert.IsLessThan(500, elapsed,
                "Completing fresh downloads must not hold a small durable command behind their buffered payloads.");
            for (int file = 0; file < 8; file++)
                Assert.AreEqual(8L * 1024 * 1024, new FileInfo(Path.Combine(root, file + ".bin")).Length);
        }
        finally
        {
            foreach (Stream output in outputs) await files.CloseAsync(output);
            System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("Load")]
    public void LargeSearchAdmissionDoesNotAllocateStorageCopiesForEveryResult()
    {
        SearchResultsAddedChange Capture(int count)
        {
            SearchResultsAddedChange? captured = null;
            var session = new SearchSession(Guid.NewGuid());
            session.ResultsAdded += change => captured = change;
            session.AddResponse(new SearchResponse("Peer", 1, true, 1000, 0,
                Enumerable.Range(1, count).Select(i => new Soulseek.File(1,
                    $@"Music\Track-{i}.flac", 8_000_000, ".flac")).ToArray()));
            return captured!;
        }
        var change = Capture(100_000);
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        events.RaiseSearchChange(Capture(1)); // Exclude one-time dispatch/JIT setup.
        sink.Mutations.Clear();

        long before = GC.GetAllocatedBytesForCurrentThread();
        events.RaiseSearchChange(change);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsLessThan(2_000_000L, allocated,
            "Admission must queue immutable observations before per-row storage encoding; batch preparation belongs to the writer.");
        var admitted = (SearchResultsPersistenceMutation)sink.Mutations.Single();
        Assert.AreEqual(100_000, admitted.Results.Count);
        Assert.AreEqual(@"Music\Track-100000.flac", admitted.Results[^1].Filename);
    }

    [TestMethod]
    public void ActivityNotifications_PreserveTransitionsWithoutDuplicateJobWrites()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var tracker = new DownloadJobTracker(events);
        var job = new SongJob(new SongQuery { Title = "Track" });
        var activities = new List<JobActivityChangedChange>();
        events.JobActivityChanged += activities.Add;
        tracker.Register(job, null);
        job.UpdateActivity(JobActivityPhase.Searching);
        job.UpdateActivity(JobActivityPhase.Downloading);
        job.SetDone();

        var mutations = sink.Mutations.OfType<JobPersistenceMutation>().ToArray();
        Assert.AreEqual(4, mutations.Length);
        CollectionAssert.AreEqual(new[] { "None", "Searching", "Downloading", "None" },
            mutations.Select(mutation => mutation.ActivityPhase).ToArray());
        Assert.AreEqual("Succeeded", mutations[^1].TerminalOutcome);
        Assert.AreEqual(2, activities.Count);
        Assert.AreEqual(mutations[1].Revision, activities[0].Revision);
        Assert.AreEqual(mutations[2].Revision, activities[1].Revision);
    }

    [TestMethod]
    public void DirectoryPlanAndMaterialization_AreStructuralBeforeLifecycleChanges()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var tracker = new DownloadJobTracker(events);
        var directory = new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("Peer", "Root")));
        tracker.Register(directory, null);
        var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\A.bin"), 10, ".bin");
        directory.BeginDirectoryAttempt(new DirectoryTransferPlan("Root", [new(target, [])]));
        directory.MaterializeDirectoryChildren([new RemoteFileJob(target)]);

        var plans = sink.Mutations.OfType<JobPersistenceMutation>()
            .Where(mutation => mutation.DirectoryManifest != null).ToArray();
        Assert.AreEqual(2, plans.Length);
        Assert.IsTrue(plans.All(mutation => mutation.Priority == PersistenceMutationPriority.Structural));
        Assert.IsTrue(plans.All(mutation => mutation.LifecycleState == "Pending"));
        Assert.IsFalse(plans[0].DirectoryManifest!.ChildrenMaterialized);
        Assert.IsTrue(plans[1].DirectoryManifest!.ChildrenMaterialized);
        Assert.AreEqual(1, plans[1].DirectoryManifest!.EntryJobIds.Count);

        directory.SetDone();
        events.RaiseJobStructureChanged(directory);
        var terminalStructure = sink.Mutations.OfType<JobPersistenceMutation>().Last();
        Assert.AreEqual(PersistenceMutationPriority.Terminal, terminalStructure.Priority);
        Assert.IsNotNull(terminalStructure.DirectoryManifest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PersistenceMapsRelationshipsWithoutRuntimeLookups(bool registrationArrivesLate)
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var state = new EngineStateStore();
        var parent = new JobList();
        var source = new SearchJob("source");
        var child = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            WorkflowId = parent.WorkflowId,
        };

        if (registrationArrivesLate)
        {
            JobRegisteredChange? registration = null;
            events.JobRegistered += change => registration = change;
            events.RaiseJobRegistered(child, parent, source.Id);
            AttachPersistence(events, Guid.NewGuid(), sink, state: state);
            child.SetDone();
            events.RaiseJobStateChanged(child);
            sink.Mutations.Clear();
            typeof(EngineStateStore).GetMethod("OnJobRegistered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(state, [registration]);
        }
        else
        {
            AttachPersistence(events, Guid.NewGuid(), sink, state: state);
            events.RaiseJobRegistered(child, parent, source.Id);
        }

        var mutation = sink.Mutations.OfType<JobPersistenceMutation>().Single();
        Assert.AreEqual(child.Id, mutation.JobId);
        Assert.AreEqual(parent.Id, mutation.ParentJobId);
        Assert.AreEqual(source.Id, mutation.SourceJobId);
        Assert.AreEqual(registrationArrivesLate ? PersistenceMutationPriority.Terminal : PersistenceMutationPriority.Structural,
            mutation.Priority);
        Assert.AreEqual(registrationArrivesLate ? "Terminal" : "Pending", mutation.LifecycleState);
        Assert.AreEqual(parent.Id, state.GetJobSummary(child.Id)!.ParentJobId);
        Assert.AreEqual(source.Id, state.GetJobSummary(child.Id)!.SourceJobId);
        Assert.IsFalse(mutation.PayloadJson!.IsValueCreated,
            "Event callbacks capture facts without serializing replaceable detail.");
        child.BytesTransferred = 77;
        using var payload = System.Text.Json.JsonDocument.Parse(mutation.PayloadJson.Value!);
        Assert.AreEqual(0, payload.RootElement.GetProperty("File").GetProperty("BytesTransferred").GetInt64(),
            "Deferred encoding retains capture-time values, not the later runtime job.");
        Assert.IsFalse(mutation.PayloadJson?.Value?.Contains("CancellationToken", StringComparison.Ordinal) == true);
    }

    [TestMethod]
    public void ExtractResult_IsAdmittedBeforeItsForwardReference()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var extract = new ExtractJob("s:\"Artist - Song\"");
        var result = new SongJob(new SongQuery { Artist = "Artist", Title = "Song" })
        {
            WorkflowId = extract.WorkflowId,
        };

        Invoke(events, "RaiseJobResultCreated", extract, result);

        var mutations = sink.Mutations.OfType<JobPersistenceMutation>().ToArray();
        Assert.AreEqual(2, mutations.Length);
        Assert.AreEqual(result.Id, mutations[0].JobId);
        Assert.AreEqual(extract.Id, mutations[1].JobId);
        Assert.AreEqual(result.Id, mutations[1].ResultJobId);
        var retained = (ExtractJobPayloadDto)HistoricalJobDtoMapper.ToPayload(ToPersistedJob(mutations[1]));
        Assert.AreEqual(result.Id, retained.ResultJobId,
            "The accepted relationship remains authoritative when the earlier payload had no result reference.");
    }

    [TestMethod]
    public void HistoricalSummary_MapsRawSearchDiscoveryCounts()
    {
        var persisted = new PersistedJob(
            Guid.NewGuid(),
            1,
            Guid.NewGuid(),
            null,
            null,
            null,
            "Search",
            "Terminal",
            "None",
            null,
            "Succeeded",
            "None",
            "None",
            "None",
            null,
            null,
            null,
            "query",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            1,
            1,
            null,
            DiscoveryPublicFileCount: 7,
            DiscoveryLockedFileCount: 3,
            DiscoveryObservedPeerCount: 2);

        JobSummaryDto summary = HistoricalJobDtoMapper.ToSummary(persisted);

        Assert.AreEqual(7, summary.DiscoveryRawResultCount);
        Assert.AreEqual(7, summary.DiscoveryPublicFileCount);
        Assert.AreEqual(3, summary.DiscoveryLockedFileCount);
        Assert.AreEqual(2, summary.DiscoveryObservedPeerCount);
    }

    [TestMethod]
    public void PersistenceAndHistoricalMapper_RoundTripEveryCurrentJobPayloadKind()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var retrieve = new RetrieveFolderJob(new PeerDirectoryIdentity("peer", @"Music\Artist\Album"))
        {
            NewFilesFoundCount = 4,
            RetrievalOutcome = FolderRetrievalOutcome.Completed,
        };
        var exactTarget = new PeerFileTarget(
            new PeerFileIdentity("Exact Peer", @"Share\Folder\File.bin"),
            42,
            ".bin");
        var exactSong = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            ExactTarget = exactTarget,
        };
        var cancelledSong = new SongJob(new SongQuery { Title = "Flexible track" })
        {
            LastAttemptedCandidate = new FileCandidate(
                new PeerFileTarget(new PeerFileIdentity(" Fallback CASE ", @"Music\Mix CASE.flac"), 128, ".flac"),
                new SearchPeerSnapshot(" Fallback CASE ", 7, 1234, false)),
            BytesTransferred = 64,
            FileSize = 128,
        };
        cancelledSong.SetCancelled(JobCancellationSource.UserRequestedJob);
        var remoteFile = new RemoteFileJob(exactTarget, new RelativeOutputPath(["Folder", "File.bin"]));
        var resolvedPlan = new DirectoryTransferPlan("Selection", [
            new DirectoryTransferEntry(exactTarget, ["Folder"]),
        ]);
        var remoteDirectory = new RemoteDirectoryJob(
            new RemoteDirectorySource.Resolved(resolvedPlan));
        var albumAggregate = new AlbumAggregateJob(new AlbumQuery { Artist = "Artist", Album = "Album" });
        var completedAlbum = new AlbumJob(new AlbumQuery { Album = "Complete" });
        var failedAlbum = new AlbumJob(new AlbumQuery { Album = "Failed" });
        completedAlbum.SetDone();
        failedAlbum.Fail(JobFailureReason.Other);
        albumAggregate.Albums = [completedAlbum, failedAlbum];
        var jobs = new (Job Job, Type PayloadType)[]
        {
            (new GenericTestJob("generic text"), typeof(GenericJobPayloadDto)),
            (new ExtractJob("https://example.test/list"), typeof(ExtractJobPayloadDto)),
            (new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" }), typeof(SearchJobPayloadDto)),
            (new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }), typeof(SearchJobPayloadDto)),
            (exactSong, typeof(SongJobPayloadDto)),
            (cancelledSong, typeof(SongJobPayloadDto)),
            (new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }), typeof(AlbumJobPayloadDto)),
            (new AggregateJob(new SongQuery { Artist = "Artist", Title = "Track" }), typeof(AggregateJobPayloadDto)),
            (albumAggregate, typeof(AlbumAggregateJobPayloadDto)),
            (new JobList("list", [new SongJob(new SongQuery { Title = "child" })]), typeof(JobListPayloadDto)),
            (retrieve, typeof(RetrieveFolderJobPayloadDto)),
            (remoteFile, typeof(RemoteFileJobPayloadDto)),
            (remoteDirectory, typeof(RemoteDirectoryJobPayloadDto)),
        };

        foreach (var (job, _) in jobs)
            events.RaiseJobRegistered(job, null, null);

        var mutations = sink.Mutations.OfType<JobPersistenceMutation>().ToDictionary(mutation => mutation.JobId);
        foreach (var (job, expectedPayloadType) in jobs)
        {
            var mutation = mutations[job.Id];
            var payload = HistoricalJobDtoMapper.ToPayload(ToPersistedJob(mutation));
            Assert.AreEqual(expectedPayloadType, payload.GetType(), $"Failed payload kind {mutation.Kind}.");
        }

        var retrievePayload = (RetrieveFolderJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[retrieve.Id]));
        Assert.AreEqual("peer", retrievePayload.Username);
        Assert.AreEqual(@"Music\Artist\Album", retrievePayload.FolderPath);
        Assert.AreEqual(4, retrievePayload.NewFilesFoundCount);
        Assert.AreEqual(ServerFolderRetrievalOutcome.Completed, retrievePayload.RetrievalOutcome);

        var trackSearch = jobs.Select(pair => pair.Job).OfType<SearchJob>()
            .Single(job => job.DefaultFileProjection != null);
        var trackPayload = (SearchJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[trackSearch.Id]));
        Assert.IsNotNull(trackPayload.SongQuery);
        Assert.AreEqual("Track", trackPayload.SongQuery.Title);

        var albumSearch = jobs.Select(pair => pair.Job).OfType<SearchJob>()
            .Single(job => job.DefaultFolderProjection != null);
        var albumPayload = (SearchJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[albumSearch.Id]));
        Assert.IsNotNull(albumPayload.AlbumQuery);
        Assert.AreEqual("Album", albumPayload.AlbumQuery.Album);

        var songPayload = (SongJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[exactSong.Id]));
        Assert.IsNotNull(songPayload.ExactTarget);
        Assert.AreEqual("Exact Peer", songPayload.ExactTarget.Username);
        Assert.AreEqual(@"Share\Folder\File.bin", songPayload.ExactTarget.Filename);
        Assert.IsNull(songPayload.ResolvedUsername);

        var cancelledPayload = (SongJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[cancelledSong.Id]));
        Assert.AreEqual(ServerJobTerminalOutcome.Cancelled, cancelledPayload.TerminalOutcome);
        Assert.IsNull(cancelledPayload.ResolvedUsername);
        Assert.IsNull(cancelledPayload.ExactTarget);
        Assert.IsNotNull(cancelledPayload.LastAttemptedFile);
        Assert.AreEqual(" Fallback CASE ", cancelledPayload.LastAttemptedFile.Target.Username);
        Assert.AreEqual(@"Music\Mix CASE.flac", cancelledPayload.LastAttemptedFile.Target.Filename);
        Assert.AreEqual(1234, cancelledPayload.LastAttemptedFile.Peer.UploadSpeed);
        Assert.AreEqual(false, cancelledPayload.LastAttemptedFile.Peer.HasFreeUploadSlot);
        Assert.AreEqual(50d, cancelledPayload.File.ProgressPercent);

        var filePayload = (RemoteFileJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[remoteFile.Id]));
        Assert.AreEqual("Exact Peer", filePayload.Target.Username);
        CollectionAssert.AreEqual(new[] { "Folder", "File.bin" }, filePayload.OutputPathComponents.ToArray());

        var directoryPayload = (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[remoteDirectory.Id]));
        Assert.AreEqual(RemoteDirectorySourceKindDto.Resolved, directoryPayload.SourceKind);
        Assert.AreEqual("Exact Peer", directoryPayload.SourceUsername);
        Assert.IsNull(directoryPayload.SourceFolderPath);
        Assert.AreEqual("planned", directoryPayload.Directory.Phase);

        var aggregatePayload = (AlbumAggregateJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutations[albumAggregate.Id]));
        Assert.AreEqual(2, aggregatePayload.ResultCount);
        Assert.AreEqual(2, aggregatePayload.CompletedAlbumCount);
        Assert.AreEqual(1, aggregatePayload.SucceededAlbumCount);
        Assert.AreEqual(1, aggregatePayload.FailedAlbumCount);

    }

    [TestMethod]
    public void RetainedResolvedDirectoryKeepsExactKnownOriginWithoutStoringWholePlan()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var target = new PeerFileTarget(
            new PeerFileIdentity("Exact Peer", @"Music\Album\Track.flac"), 42, ".flac");
        var origin = new PeerDirectoryIdentity("Exact Peer", @"Music\Album");
        var plan = DirectoryTransferPlanner.FromObservedSelection(
            new PeerDirectorySnapshot(origin, [target], isComplete: false));
        var job = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan));

        events.RaiseJobRegistered(job, null, null);

        var mutation = sink.Mutations.OfType<JobPersistenceMutation>()
            .Single(change => change.JobId == job.Id);
        Assert.IsFalse(mutation.PayloadJson!.Value!.Contains("ResolvedPlanSource", StringComparison.Ordinal));
        var payload = (RemoteDirectoryJobPayloadDto)HistoricalJobDtoMapper.ToPayload(
            ToPersistedJob(mutation));
        Assert.AreEqual("Exact Peer", payload.SourceUsername);
        Assert.AreEqual(@"Music\Album", payload.SourceFolderPath);
        Assert.AreEqual("Album", payload.DisplayRoot);
    }

    [TestMethod]
    public async Task MovedCompletedOutputUpdatesLiveAndDurableMetadataWithoutCompletingAgain()
    {
        var clock = new OutputClock { Now = DateTimeOffset.Parse("2026-10-31T12:00:00Z") };
        var settings = new Sockseek.Core.Settings.EngineSettings { Username = "test", Password = "test" };
        await using var manager = new SoulseekClientManager(settings, global::Tests.Server.SoulseekClientProxy.Create().Client);
        await using var engine = new DownloadEngine(settings, manager, timeProvider: clock);
        var sink = new CapturingSink();
        var store = new Sockseek.Server.EngineStateStore();
        AttachPersistence(engine.Events, Guid.NewGuid(), sink, state: store);
        var song = new SongJob(new SongQuery { Title = "Track" }) { WorkflowId = Guid.NewGuid() };
        var target = new PeerFileTarget(new PeerFileIdentity("Exact Peer", @"Share\Track.bin"), 100, ".bin");
        var completions = new List<TransferCompletedChange>();
        var moves = new List<TransferOutputChangedChange>();
        engine.Events.TransferCompleted += completions.Add;
        engine.Events.TransferOutputChanged += moves.Add;
        Guid transferId = Guid.NewGuid(), attemptId = Guid.NewGuid();
        engine.Events.RaiseJobRegistered(song, null, null);
        engine.Events.RaiseDownloadStarted(transferId, song, target, "/output/Track.bin");
        engine.Events.RaiseTransferAttemptStarted(transferId, attemptId, 1, song, target,
            "/output/Track.bin", "/output/Track.bin.incomplete");
        clock.Now = clock.Now.AddSeconds(5);
        engine.Events.RaiseTransferAttemptCompleted(transferId, attemptId, 1, song, target, "/output/Track.bin");
        engine.Events.RaiseTransferCompleted(transferId, song, target, "/output/Track.bin", 100L, 1);
        DateTimeOffset completedAt = clock.Now;
        var summary = store.GetDaemonSnapshot().TransferSummary;
        clock.Now = clock.Now.AddMinutes(1);
        engine.Events.RaiseTransferOutputChanged(song, "/output/failed/Track.bin");
        engine.Events.RaiseTransferOutputChanged(song, "/output/failed/Track.bin");
        Assert.AreEqual(1, completions.Count);
        Assert.AreEqual(1, moves.Count);
        Assert.AreEqual("/output/Track.bin", completions[0].FinalLocalPath, "Original completion remains immutable.");
        var live = store.GetJobTransferSnapshot(new HashSet<Guid> { song.Id }).Single().ToDto();
        Assert.AreEqual("/output/failed/Track.bin", live.Status.LocalPath);
        Assert.AreEqual(completedAt, live.Status.CompletedAtUtc);
        Assert.AreEqual(summary, store.GetDaemonSnapshot().TransferSummary, "Moving output cannot add completions or bytes.");
        var terminal = sink.Mutations.OfType<TransferTerminalPersistenceMutation>().Single();
        Assert.AreEqual(attemptId, terminal.FinalAttempt!.AttemptId);
        var moved = sink.Mutations.OfType<TransferPersistenceMutation>()
            .Single(mutation => mutation.LocalPath == "/output/failed/Track.bin");
        Assert.AreEqual("Succeeded", moved.TerminalOutcome);
        Assert.AreEqual(completedAt, moved.CompletedAtUtc);
        Assert.IsNull(moved.AccountingObservations, "Moving output records no transport bytes.");
        store.DetachEngine(engine);
    }

    private sealed class OutputClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public void PersistenceCombinesFinalAttemptWithTransferTerminalBarrier()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var adapter = AttachPersistence(events, Guid.NewGuid(), sink);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var file = new Soulseek.File(1, @"Music\Artist\Track.mp3", 100, ".mp3");
        var response = new SearchResponse("user", 1, true, 1_000, 0, [file]);
        var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
        Guid transferId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();

        Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");
        Invoke(events, "RaiseTransferAttemptStarted", transferId, attemptId, 1, song, candidate.Target, "C:/downloads/Track.mp3", "C:/downloads/Track.mp3.incomplete");
        song.BytesTransferred = 40;
        Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 40L, 100L);
        song.BytesTransferred = 100;
        Invoke(events, "RaiseTransferAttemptCompleted", transferId, attemptId, 1, song, candidate.Target, "C:/downloads/Track.mp3");
        Invoke(events, "RaiseTransferCompleted", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 100L, 1);

        var terminal = sink.Mutations.OfType<TransferTerminalPersistenceMutation>().Single();
        Assert.AreEqual(transferId, terminal.Transfer.TransferId);
        Assert.AreEqual("Succeeded", terminal.Transfer.TerminalOutcome);
        Assert.IsNotNull(terminal.FinalAttempt);
        Assert.AreEqual(attemptId, terminal.FinalAttempt.AttemptId);
        Assert.AreEqual("Completed", terminal.FinalAttempt.State);
        Assert.AreEqual("user", terminal.FinalAttempt.SourceUsername);
        Assert.AreEqual(@"Music\Artist\Track.mp3", terminal.FinalAttempt.SourcePath);
        Assert.AreEqual("C:/downloads/Track.mp3", terminal.FinalAttempt.OutputPath);
        Assert.AreEqual(2L, terminal.FinalAttempt.Revision);
        var finalBytes = (terminal.Transfer.AccountingObservations ?? [])
            .Concat(terminal.FinalAttempt.AccountingObservations ?? []).Single();
        Assert.AreEqual(100L, finalBytes.CumulativeBytes,
            "The terminal unit carries its final byte observation once, alongside the attempt.");
        Assert.AreEqual(
            40L,
            sink.Mutations.OfType<TransferPersistenceMutation>()
                .Single(mutation => mutation.Priority == PersistenceMutationPriority.Progress)
                .AccountingObservations?.Single().CumulativeBytes);
        Assert.AreEqual(0, sink.Mutations.OfType<TransferAttemptPersistenceMutation>().Count(mutation => mutation.State == "Completed"));

        Guid cancelledTransferId = Guid.NewGuid();
        Invoke(events, "RaiseDownloadStarted", cancelledTransferId, song, candidate.Target, "C:/downloads/Cancelled.mp3");
        Invoke(events, "RaiseTransferCancelled", cancelledTransferId, song, candidate.Target,
            "C:/downloads/Cancelled.mp3", 40L, 100L, 1, TransferCancellationReason.Requested);
        var cancelled = sink.Mutations.OfType<TransferTerminalPersistenceMutation>()
            .Single(mutation => mutation.Transfer.TransferId == cancelledTransferId).Transfer;
        Assert.AreEqual("Cancelled", cancelled.TerminalOutcome);
        Assert.AreEqual("None", cancelled.FailureReason);
        Assert.AreEqual("Requested", cancelled.CancellationSource);
    }

    [TestMethod]
    public void DirectoryDownloadsKeepOneGroupIdentityThroughLiveAndTerminalMutations()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var tracker = new DownloadJobTracker(events);
        var firstTarget = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\First.bin"), 10, ".bin");
        var secondTarget = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Second.bin"), 20, ".bin");
        var directory = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [new(firstTarget, []), new(secondTarget, [])])));
        var first = new RemoteFileJob(firstTarget) { WorkflowId = directory.WorkflowId };
        var second = new RemoteFileJob(secondTarget) { WorkflowId = directory.WorkflowId };
        directory.MaterializeDirectoryChildren([first, second]);
        tracker.Register(directory, null);
        tracker.Register(first, directory, directoryEntry: directory.ActiveAttempt!.Plan.Entries[0]);
        tracker.Register(second, directory, directoryEntry: directory.ActiveAttempt!.Plan.Entries[1]);

        Guid firstTransferId = Guid.NewGuid();
        Guid secondTransferId = Guid.NewGuid();
        Invoke(events, "RaiseDownloadStarted", firstTransferId, first, firstTarget, "C:/downloads/First.bin");
        Invoke(events, "RaiseDownloadStarted", secondTransferId, second, secondTarget, "C:/downloads/Second.bin");
        Invoke(events, "RaiseTransferCompleted", firstTransferId, first, firstTarget, "C:/downloads/First.bin", 10L, 1);
        Invoke(events, "RaiseTransferCompleted", secondTransferId, second, secondTarget, "C:/downloads/Second.bin", 20L, 1);

        var started = sink.Mutations.OfType<TransferPersistenceMutation>()
            .Where(mutation => mutation.State == "Started").ToArray();
        var completed = sink.Mutations.OfType<TransferTerminalPersistenceMutation>()
            .Select(mutation => mutation.Transfer).ToArray();
        Assert.AreEqual(2, started.Length);
        Assert.AreEqual(2, completed.Length);
        Assert.IsTrue(started.Concat(completed).All(mutation =>
            mutation.GroupRef == directory.Id.ToString("D") && mutation.GroupDisplayPath == "Root"
            && mutation.GroupRelativePath == (mutation.TransferId == firstTransferId ? "First.bin" : "Second.bin")));

        Invoke(events, "RaiseWorkflowRetired", directory.WorkflowId, 3);
        Assert.AreEqual((0, 0, 0, 0, 0, 0), events.RetainedStateCounts);
    }

    [TestMethod]
    public void IndependentlySelectedFilePlanPersistsFileGroupWithoutDirectoryOwnership()
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        AttachPersistence(events, Guid.NewGuid(), sink);
        var tracker = new DownloadJobTracker(events);
        var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Selected.bin"), 12, ".bin");
        var directory = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Root", [new(target, [])], representsDirectorySelection: false)));
        var file = new RemoteFileJob(target) { WorkflowId = directory.WorkflowId };
        directory.MaterializeDirectoryChildren([file]);
        tracker.Register(directory, null);
        tracker.Register(file, directory);

        Guid transferId = Guid.NewGuid();
        Invoke(events, "RaiseDownloadStarted", transferId, file, target, "C:/downloads/Root/Selected.bin");
        Invoke(events, "RaiseTransferCompleted", transferId, file, target, "C:/downloads/Root/Selected.bin", 12L, 1);

        var started = sink.Mutations.OfType<TransferPersistenceMutation>()
            .Single(mutation => mutation.State == "Started");
        var completed = sink.Mutations.OfType<TransferTerminalPersistenceMutation>().Single().Transfer;
        Assert.AreEqual($"files:{directory.WorkflowId:D}:Root", started.GroupRef);
        Assert.AreEqual(started.GroupRef, completed.GroupRef);
        Assert.AreEqual(Sockseek.Core.Snapshots.TransferGroupKind.Files, started.GroupKind);
        Assert.AreEqual(started.GroupKind, completed.GroupKind);
        Assert.AreEqual("Root", completed.GroupDisplayPath);
        Assert.AreEqual("Selected.bin", completed.GroupRelativePath);
        Assert.AreEqual("C:/downloads/Root/Selected.bin", completed.LocalPath);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadTerminalMutation_OwnsExactHandoffUntilItsRevisionCommits(bool fail)
    {
        var events = new DownloadEvents();
        var state = new EngineStateStore();
        var client = new DaemonClientStore();
        void Observe(string handler, CoreChange change)
            => typeof(EngineStateStore).GetMethod(handler,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(state, [change]);
        DownloadStartedChange? first = null;
        events.DownloadStarted += change => first = change;
        var sink = new CapturingSink();
        var handoffs = new PersistenceHandoffTracker();
        AttachPersistence(events, Guid.NewGuid(), sink, handoffs, state);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var file = new Soulseek.File(1, @"Music\Artist\Track.mp3", 100, ".mp3");
        var candidate = SoulseekSearchAdapter.ToFileCandidate(
            new SearchResponse("user", 1, true, 1_000, 0, [file]),
            file);
        Guid transferId = Guid.NewGuid();

        Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");
        client.ApplySnapshot(state.GetWorkflowSnapshot(song.WorkflowId));
        state.StateBatchPublished += batch => { if (batch.Scope == StateStreamScopeDto.Workflow(song.WorkflowId)) client.Apply(batch.ToDto()); };
        if (fail)
            Invoke(events, "RaiseTransferFailed", transferId, song, candidate.Target, "C:/downloads/Track.mp3",
                50L, 100L, 1, Sockseek.Core.Events.TransferFailureReason.PeerFailure,
                new IOException("Peer connection closed."));
        else
            Invoke(events, "RaiseTransferCompleted", transferId, song, candidate.Target, "C:/downloads/Track.mp3", 100L, 1);
        var terminal = sink.Mutations.OfType<TransferTerminalPersistenceMutation>().Single();

        var live = state.GetLiveTransfer(transferId)!;
        Assert.AreEqual(terminal.Transfer.Revision, live.Revision);
        Assert.AreEqual(terminal.Transfer.OccurredAtUtc, live.Status.CompletedAtUtc);
        Assert.AreEqual(terminal.Transfer.FailureMessage, live.Status.FailureMessage);
        Assert.AreEqual(fail ? TransferTerminalOutcome.Failed : TransferTerminalOutcome.Succeeded, live.Status.TerminalOutcome);
        state.FlushStateUpdates();
        Assert.AreEqual(live, client.GetTransfers().Single());
        // A delayed/repeated predecessor cannot resurrect a terminal transfer,
        // change its revision, or make archive wait for an unpersisted version.
        int writesBeforePredecessor = sink.Mutations.Count;
        Observe("OnNestedSongDownloadStarted", first!);
        Assert.AreSame(live, state.GetLiveTransfer(transferId));
        Assert.AreEqual(writesBeforePredecessor, sink.Mutations.Count,
            "A rejected stale fact must not enter persistence through a parallel subscriber.");
        var timeline = live;
        Assert.AreEqual(terminal.Transfer.Revision, timeline.Revision);
        Assert.AreEqual(terminal.Transfer.OccurredAtUtc.ToUnixTimeMilliseconds(), timeline.Status.CompletedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.AreEqual(terminal.Transfer.FailureMessage, timeline.Status.FailureMessage);

        Task handoff = handoffs.WaitForTransferAsync(
            transferId,
            terminal.Transfer.Revision,
            CancellationToken.None);
        Assert.IsFalse(handoff.IsCompleted);

        handoffs.Committed([terminal]);
        await handoff;
    }

    [TestMethod]
    public void BlockedWriter_DoesNotBlockProgressCallbacks_AndProgressMemoryStaysBounded()
    {
        var options = new PersistenceWriterOptions
        {
            CommandQueueCapacity = 2,
            BackgroundCommandQueueCapacity = 2,
            ProgressEntityCapacity = 2,
            MaximumBatchSize = 2,
        };
        var health = new PersistenceHealth();
        var inbox = new PersistenceInbox(options, health);
        var events = new DownloadEvents();
        AttachPersistence(events, Guid.NewGuid(), inbox);
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var file = new Soulseek.File(1, @"Music\Artist\Track.mp3", 10_000, ".mp3");
        var response = new SearchResponse("user", 1, true, 1_000, 0, [file]);
        var candidate = SoulseekSearchAdapter.ToFileCandidate(response, file);
        Guid transferId = Guid.NewGuid();
        Invoke(events, "RaiseDownloadStarted", transferId, song, candidate.Target, "C:/downloads/Track.mp3");

        for (int i = 1; i <= 10_000; i++)
        {
            song.BytesTransferred = i;
            Invoke(events, "RaiseDownloadProgress", transferId, song, candidate.Target, "C:/downloads/Track.mp3", (long)i, 10_000L);
        }

        Assert.AreEqual(10_000L, song.BytesTransferred);
        Assert.AreEqual(1, inbox.ProgressCount);
        Assert.AreEqual(1, inbox.CriticalDepth, "Progress replaces current state without duplicating structural admission.");
        Assert.AreEqual(0L, health.Snapshot(inbox).DroppedProgressCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaturatedSearchAdmission_AllowsAcknowledgementsAndUnrelatedState(bool completing)
    {
        var inbox = new PersistenceInbox(new PersistenceWriterOptions { SearchMutationQueueCapacity = 1 }, new PersistenceHealth());
        var sink = new AdmissionSignalSink(inbox);
        var events = new DownloadEvents();
        var state = new EngineStateStore(persistenceOwnsJobRetirement: true);
        var handoffs = new PersistenceHandoffTracker();
        AttachPersistence(events, Guid.NewGuid(), sink, handoffs, state);
        handoffs.JobProjectionHandoffCompleted += state.RetireJobProjections;
        handoffs.TerminalTransfersCommitted += committed => state.RetireCommittedTransfers(committed);

        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var target = new PeerFileTarget(new PeerFileIdentity("peer", @"Music\Track.flac"), 100, ".flac");
        Guid transferId = Guid.NewGuid();
        events.RaiseJobRegistered(song, null, null);
        Invoke(events, "RaiseDownloadStarted", transferId, song, target, "/downloads/Track.flac");
        Invoke(events, "RaiseTransferCompleted", transferId, song, target, "/downloads/Track.flac", 100L, 1);
        song.SetDone();
        events.RaiseJobStateChanged(song);
        Invoke(events, "RaiseWorkflowRetired", song.WorkflowId, 1);
        var committed = Drain();
        Assert.IsNotNull(state.GetJobDetail(song.Id));
        Assert.IsNotNull(state.GetLiveTransfer(transferId));

        var session = new SearchSession(Guid.NewGuid());
        session.ChangePublished += events.RaiseSearchChange;
        SearchResponse Response(string path) => new("peer", 1, true, 1000, 0, [new Soulseek.File(1, path, 100, ".flac")]);
        session.AddResponse(Response("first.flac")); // Saturate real raw admission.
        var producer = Task.Run(() =>
        {
            if (completing) session.Complete();
            else session.AddResponse(Response("second.flac"));
        });
        try
        {
            await sink.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(producer.IsCompleted);
            // The writer acknowledges its previous batch before its next drain.
            // Neither that handoff nor unrelated state access may wait for the
            // search producer, which cannot finish until this drain occurs.
            var writer = Task.Run(() =>
            {
                Assert.IsNotNull(state.GetJobSummary(song.Id));
                var other = new SongJob(new SongQuery { Title = "Other" });
                Guid otherTransfer = Guid.NewGuid();
                Invoke(events, "RaiseDownloadStarted", otherTransfer, other, target, "/downloads/Other.flac");
                Invoke(events, "RaiseDownloadProgress", otherTransfer, other, target, "/downloads/Other.flac", 10L, 100L);
                handoffs.Committed(committed);
                Assert.IsNull(state.GetJobDetail(song.Id));
                Assert.IsNull(state.GetLiveTransfer(transferId));
                return Drain();
            });
            var first = await writer.WaitAsync(TimeSpan.FromSeconds(5));
            await producer.WaitAsync(TimeSpan.FromSeconds(5));
            var admitted = first.Concat(Drain()).Where(m => m is SearchResultsPersistenceMutation or SearchCompletionPersistenceMutation).ToArray();
            Assert.AreEqual("first.flac", ((SearchResultsPersistenceMutation)admitted[0]).Results.Single().Filename);
            Assert.HasCount(2, admitted);
            if (completing)
                Assert.AreEqual(1L, ((SearchCompletionPersistenceMutation)admitted[1]).ResultCount);
            else
            {
                Assert.AreEqual("second.flac", ((SearchResultsPersistenceMutation)admitted[1]).Results.Single().Filename);
                session.Complete();
                Assert.AreEqual(2L, Drain().OfType<SearchCompletionPersistenceMutation>().Single().ResultCount);
            }
        }
        finally
        {
            // Also release a regressed producer so a failing check cannot hang
            // the test host. This is cleanup, never the successful writer drain.
            inbox.Complete();
            await producer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        IReadOnlyList<PersistenceMutation> Drain() => (IReadOnlyList<PersistenceMutation>)typeof(PersistenceInbox)
            .GetMethod("DrainBatch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                [typeof(bool)])!.Invoke(inbox, [true])!;
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkflowRetainsCurrentFactsUntilSuccessfulPersistence(bool permanentlyLost)
    {
        var events = new DownloadEvents();
        var sink = new CapturingSink();
        var handoffs = new PersistenceHandoffTracker();
        var store = new EngineStateStore(persistenceOwnsJobRetirement: true);
        AttachPersistence(events, Guid.NewGuid(), sink, handoffs, store);
        handoffs.JobProjectionHandoffCompleted += store.RetireJobProjections;
        var job = new SearchJob("query");
        var stale = new List<(string Handler, CoreChange Change)>();
        events.JobRegistered += change => stale.Add(("OnJobRegistered", change));
        events.JobStateChanged += change => { if (!change.IsTerminal) stale.Add(("OnJobStateChanged", change)); };
        events.JobStructureChanged += change => stale.Add(("OnJobStructureChanged", change));
        events.JobDiscoveryChanged += change => stale.Add(("OnJobDiscoveryChanged", change));
        void Project(string method, object change)
            => typeof(EngineStateStore).GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(store, [change]);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StateBatchPublished += source =>
        {
            var batch = source.ToDto();
            if (batch.Scope == StateStreamScopeDto.Workflow(job.WorkflowId)
                && batch.State.RemovedJobIds.Contains(job.Id)) removed.TrySetResult();
        };
        bool readableDuringRetirement = false;
        events.WorkflowRetired += _ => readableDuringRetirement = store.GetJobDetail(job.Id) is not null;

        events.RaiseJobRegistered(job, null, null);
        events.RaiseJobStateChanged(job);
        events.RaiseJobStructureChanged(job);
        events.RaiseJobDiscoveryChanged(job);
        job.SetDone();
        events.RaiseJobStateChanged(job);
        int acceptedWrites = sink.Mutations.Count;
        foreach (var (handler, change) in stale) Project(handler, change);
        Assert.AreEqual(acceptedWrites, sink.Mutations.Count,
            "Rejected older headers cannot mutate relationships or enter persistence.");
        Assert.AreEqual(ServerJobLifecycleState.Terminal, store.GetJobSummary(job.Id)!.LifecycleState);
        Invoke(events, "RaiseWorkflowRetired", job.WorkflowId, 1);

        Assert.IsTrue(readableDuringRetirement,
            "The read owner must not discard facts when Core releases its execution objects.");
        Task handoffObservedDuringRetirement = handoffs.WaitForJobAsync(job.Id, CancellationToken.None);
        Assert.IsFalse(handoffObservedDuringRetirement.IsCompleted);
        Assert.IsNotNull(store.GetJobDetail(job.Id));
        if (permanentlyLost)
        {
            handoffs.PermanentlyFailed(sink.Mutations, new IOException("required metadata lost"));
            await Assert.ThrowsExactlyAsync<PersistenceHandoffException>(() => handoffObservedDuringRetirement);
            Assert.IsFalse(removed.Task.IsCompleted);
            Assert.IsNotNull(store.GetJobDetail(job.Id), "Failed persistence must preserve still-available current facts.");
            Assert.AreEqual(1, store.RetainedWorkflowStateCounts.Jobs);
            return;
        }
        else
        {
            handoffs.Committed(sink.Mutations);
            await handoffObservedDuringRetirement;
        }
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(store.GetJobDetail(job.Id), "Only successful persistence releases its read projection.");
        Assert.AreEqual(0, store.RetainedWorkflowStateCounts.Jobs);
    }

    [TestMethod]
    public void RejectingPersistenceSinkCannotDelayOrPreventWorkflowRetirement()
    {
        var events = new DownloadEvents();
        var sink = new RejectingSink();
        var adapter = AttachPersistence(events, Guid.NewGuid(), sink);
        var job = new SearchJob("query");
        bool retired = false;
        events.WorkflowRetired += _ => retired = true;

        events.RaiseJobRegistered(job, null, null);
        Invoke(events, "RaiseWorkflowRetired", job.WorkflowId, 1);

        Assert.IsTrue(retired);
        Assert.IsTrue(sink.RejectedCount > 0);
        Assert.AreEqual(0, adapter.RetainedJobCount);
        Assert.AreEqual((0, 0, 0, 0, 0, 0), events.RetainedStateCounts);
    }

    private static JobPersistence AttachPersistence(DownloadEvents events, Guid runtimeId,
        IPersistenceMutationSink sink, PersistenceHandoffTracker? handoffs = null, EngineStateStore? state = null)
    {
        state ??= new EngineStateStore();
        state.SetTransferPersistence(new TransferPersistence(runtimeId, sink, handoffs));
        var writer = new JobPersistence(runtimeId, sink, handoffs);
        state.SetJobPersistence(writer);
        state.AttachEvents(events);
        return writer;
    }

    private static void Invoke(DownloadEvents events, string methodName, params object?[] args)
        => typeof(DownloadEvents)
            .GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(events, args);

    private static PersistedJob ToPersistedJob(JobPersistenceMutation mutation)
        => new(
            mutation.JobId,
            mutation.DisplayId,
            mutation.WorkflowId,
            mutation.ParentJobId,
            mutation.SourceJobId,
            mutation.ResultJobId,
            mutation.Kind,
            mutation.LifecycleState,
            mutation.ActivityPhase,
            mutation.ActivityUntilUtc,
            mutation.TerminalOutcome,
            mutation.SkipReason,
            mutation.CancellationSource,
            mutation.FailureReason,
            mutation.FailureMessage,
            mutation.FailureDetail,
            mutation.ItemName,
            mutation.QueryText,
            mutation.OccurredAtUtc,
            mutation.OccurredAtUtc,
            null,
            mutation.Revision,
            mutation.PayloadSchemaVersion,
            mutation.PayloadJson?.Value);

    private sealed class GenericTestJob(string text) : Job
    {
        protected override bool DefaultCanBeSkipped => false;
        public override SongQuery? QueryTrack => null;
        public override string ToString(bool noInfo) => text;
    }

    private sealed class AdmissionSignalSink(PersistenceInbox inbox) : IPersistenceMutationSink
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            var admission = inbox.EnqueueAsync(mutation);
            if (!admission.IsCompleted) Blocked.TrySetResult();
            return admission.AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class CapturingSink : IPersistenceMutationSink
    {
        public List<PersistenceMutation> Mutations { get; } = [];

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            Mutations.Add(mutation);
            return true;
        }
    }

    private sealed class RejectingSink : IPersistenceMutationSink
    {
        public int RejectedCount { get; private set; }

        public bool TryEnqueue(PersistenceMutation mutation)
        {
            RejectedCount++;
            return false;
        }
    }
}
