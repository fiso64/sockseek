using Sockseek.Core.Extractors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;
using Tests.ClientTests;

namespace Tests;

[TestClass]
public sealed class JobPlannerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResolvedRootKeepsItsSettingsWhileChildrenResolveAndNormalizeIndependently(bool review)
    {
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            ExtractorCond = new FileConditionPatch { Formats = ["flac"] },
        };
        var remote = new RemoteFileJob(new PeerFileTarget(
            new PeerFileIdentity("ExactPeer", @"Share\notes.txt"), 4, ".txt"));
        var root = new JobList("mixed", [song, remote]);
        var resolved = new DownloadSettings();
        resolved.Search.MaxStaleTime = 3333;
        resolved.Output.NameFormat = "{artist}/{filename}";
        resolved.Output.WriteIndex = false;
        resolved.Skip.SkipExisting = false;
        resolved.Skip.SkipNotFound = false;
        var resolver = new ChildSettingsResolver();
        var planner = new JobPlanner(resolver);
        List<PlannedJobNode>? nodes = review ? await CollectAsync(planner.PlanResolvedAsync(root, resolved)) : null;
        if (!review) await planner.PrepareResolvedAsync(root, resolved);

        Assert.AreEqual(3333, root.PlannedSettings!.ReadSettings().Search.MaxStaleTime,
            "Validation's resolved root must not be composed again during preparation.");
        Assert.AreEqual(2222, song.PlannedSettings!.ReadSettings().Search.MaxStaleTime);
        Assert.AreEqual(2222, remote.PlannedSettings!.ReadSettings().Search.MaxStaleTime);
        Assert.AreEqual("{artist}/{filename}", song.Config.Output.NameFormat);
        Assert.AreEqual("", remote.Config.Output.NameFormat);
        CollectionAssert.AreEqual(new[] { "flac" }, song.Config.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(resolved.Search.NecessaryCond.Formats,
            remote.Config.Search.NecessaryCond.Formats,
            "One sibling's extractor constraints must not change the next sibling.");

        var retained = SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(nodes is null
                ? SubmissionSpecification.Create(root) : CaptureSpecification(nodes)));
        foreach (JobList prepared in new[] { root, (JobList)retained.MaterializeJob() })
        {
            JobPreparer.PrepareSubtree(prepared, new DownloadSettings(), resolver);
            Assert.AreEqual(3333, prepared.Config.Search.MaxStaleTime);
            Assert.AreEqual("{artist}/{filename}", prepared[0].PlannedSettings!.ReadSettings().Output.NameFormat);
            Assert.AreEqual("", prepared[1].PlannedSettings!.ReadSettings().Output.NameFormat);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task EditingAStreamedParentsConfigCannotChangeItsPendingChildren(bool resolved, bool extract)
    {
        var child = new SongJob(new SongQuery { Title = "Track" });
        Job root = extract ? new ExtractJob("Artist - Track", InputType.String)
            { RequestedModeOverride = ExtractionMode.Song } : new JobList("parent", [child]);
        var settings = new DownloadSettings();
        settings.Search.MaxStaleTime = 1234;
        string[] originalFormats = [.. settings.Search.NecessaryCond.Formats];
        var planner = new JobPlanner(DefaultJobSettingsResolver.Instance);
        await using var nodes = (resolved ? planner.PlanResolvedAsync(root, settings)
            : planner.PlanAsync(root, settings)).GetAsyncEnumerator();
        Assert.IsTrue(await nodes.MoveNextAsync());
        PlannedJobNode parent = nodes.Current;
        if (root is ExtractJob source) child = (SongJob)source.Result!;
        settings.Search.MaxStaleTime = 9000;
        root.Config.Search.MaxStaleTime = 8000;
        root.Config.Search.NecessaryCond.Formats = ["mp3"];

        Assert.IsTrue(await nodes.MoveNextAsync());
        PlannedJobNode leaf = nodes.Current;
        Assert.AreEqual(1234, parent.ReadSettings()!.Search.MaxStaleTime);
        Assert.AreEqual(1234, leaf.ReadSettings()!.Search.MaxStaleTime);
        Assert.AreEqual(1234, child.Config.Search.MaxStaleTime);
        CollectionAssert.AreEqual(originalFormats, leaf.ReadSettings()!.Search.NecessaryCond.Formats);
        child.Config.Search.MaxStaleTime = 7000;
        Assert.AreEqual(1234, leaf.ReadSettings()!.Search.MaxStaleTime);
        Assert.AreEqual(8000, root.Config.Search.MaxStaleTime);
        Assert.IsFalse(await nodes.MoveNextAsync());
    }

    [TestMethod]
    public void SnapshotMetadataUsesFrozenSettingsAndReflectsLaterLiveEdits()
    {
        var settings = new DownloadSettings { PrintOption = PrintOption.Results | PrintOption.Full };
        settings.AppliedAutoProfiles.Add("frozen-profile");
        var job = new SongJob(new SongQuery { Title = "Track" })
        {
            PlannedSettings = JobSettingsSnapshot.Capture(settings),
        };
        var frozen = CoreSnapshotFactory.CreateJob(job, 1);
        var reference = CoreSnapshotFactory.CreateJobReference(job, 1);
        Assert.AreEqual(settings.PrintOption, frozen.PrintOption);
        Assert.AreEqual(settings.PrintOption, reference.PrintOption);
        CollectionAssert.AreEqual(new[] { "frozen-profile" }, frozen.AppliedAutoProfiles.ToArray());

        job.Config.PrintOption = PrintOption.Jobs;
        job.Config.AppliedAutoProfiles.Clear();
        job.Config.AppliedAutoProfiles.Add("live-profile");
        var live = CoreSnapshotFactory.CreateJob(job, 2);
        Assert.AreEqual(PrintOption.Jobs, live.PrintOption);
        Assert.AreEqual(PrintOption.Jobs, CoreSnapshotFactory.CreateJobReference(job, 2).PrintOption);
        CollectionAssert.AreEqual(new[] { "live-profile" }, live.AppliedAutoProfiles.ToArray());
        Assert.AreEqual(settings.PrintOption, frozen.PrintOption);
        CollectionAssert.AreEqual(new[] { "frozen-profile" }, frozen.AppliedAutoProfiles.ToArray());
    }

    [TestMethod]
    [TestCategory("Load")]
    [DoNotParallelize]
    public void PendingJobSnapshotsStayCompactAndPreserveSettingsMetadata()
    {
        var settings = new DownloadSettings { PrintOption = PrintOption.Results | PrintOption.Full };
        settings.AppliedAutoProfiles.Add("fixture-profile");
        var shared = JobSettingsSnapshot.Capture(settings);
        var jobs = Enumerable.Range(0, 10_000).Select(index => (Job)new SongJob(
            new SongQuery { Title = $"Track {index:D5}" }) { PlannedSettings = shared }).ToList();
        var root = new JobList("pending", jobs) { PlannedSettings = shared };

        long before = GC.GetTotalAllocatedBytes(true);
        JobSnapshot snapshot = CoreSnapshotFactory.CreateJob(root, 1);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        // Leave room for the rows themselves, while catching settings copies
        // created merely to render pending-job metadata. This is opt-in Load.
        Assert.IsTrue(allocated < 20_000_000, $"Pending snapshots allocated {allocated:N0} bytes.");
        var rows = ((JobListSnapshotPayload)snapshot.Payload).Jobs;
        Assert.AreEqual(10_000, rows.Count);
        Assert.IsTrue(rows.All(row => row.PrintOption == settings.PrintOption
            && row.AppliedAutoProfiles.SequenceEqual(new[] { "fixture-profile" })));
        CollectionAssert.AreEqual(jobs.Select(job => job.Id).ToArray(), rows.Select(row => row.Id).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectCaptureRetainsTheSameRefinedRootSettingsAsReview(bool extract)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sockseek-prepared-{Guid.NewGuid():N}.csv");
        try
        {
            if (extract) await File.WriteAllTextAsync(path, "artist,title\nFixture,Track\n");
            Job root = extract ? new ExtractJob(path, InputType.None)
                : new SongJob(new SongQuery { Title = "Track" })
                { ExtractorCond = new FileConditionPatch { Formats = ["flac"] } };
            var baseline = new DownloadSettings();
            string original = SubmissionSpecification.Create(new JobList("baseline"), baseline).EffectiveSettingsJson;
            var nodes = await CollectAsync(new JobPlanner(DefaultJobSettingsResolver.Instance)
                .PlanResolvedAsync(root, baseline));
            SubmissionSpecification reviewed = CaptureSpecification(nodes);
            SubmissionSpecification direct = SubmissionSpecification.Create(root);
            Assert.ThrowsExactly<InvalidOperationException>(() => SubmissionSpecification.Create(root, baseline));
            Assert.AreEqual(reviewed.EffectiveSettingsJson, direct.EffectiveSettingsJson);
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(reviewed.Search),
                System.Text.Json.JsonSerializer.Serialize(direct.Search));
            Assert.AreEqual(original,
                SubmissionSpecification.Create(new JobList("baseline"), baseline).EffectiveSettingsJson,
                "Planning must not mutate the caller's baseline to communicate refinements.");
            if (extract) Assert.AreEqual(InputType.CSV, direct.MaterializeSettings().Extraction.InputType);
            else CollectionAssert.AreEqual(new[] { "flac" }, direct.MaterializeSettings().Search.NecessaryCond.Formats);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class ChildSettingsResolver : IJobSettingsResolver
    {
        public DownloadSettings Resolve(DownloadSettings inherited, JobSettingsContext job,
            JobSettingsInheritance inheritance = JobSettingsInheritance.None)
        {
            var settings = SettingsCloner.Clone(inherited);
            settings.Search.MaxStaleTime = 2222;
            return settings;
        }
    }

    [TestMethod]
    public async Task ReviewedJobsExposeTheSameSearchDefinitionWhenSearchingAfterReplay()
    {
        Job[] jobs =
        [
            new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }),
            new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }),
            new SongGroupsJob(new SongQuery { Artist = "Artist", Title = "Track" }),
            new AlbumGroupsJob(new AlbumQuery { Artist = "Artist", Album = "Album" }),
            new SearchJob("generic files"),
            new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" }),
            new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }),
        ];
        foreach (Job job in jobs)
        {
            var settings = new DownloadSettings();
            settings.Skip.SkipExisting = false;
            settings.Skip.SkipNotFound = false;
            settings.Output.WriteIndex = false;
            settings.Search.NecessaryCond.Formats = ["flac"];
            List<PlannedJobNode> nodes = await CollectAsync(
                new JobPlanner(DefaultJobSettingsResolver.Instance).PlanAsync(job, settings));
            var retained = SubmissionSpecificationCodec.Deserialize(
                SubmissionSpecificationCodec.Serialize(CaptureSpecification(nodes)));
            foreach (Job prepared in new[] { job, retained.MaterializeJob() })
            {
                JobPreparer.PrepareSubtree(prepared, settings);
                Assert.IsNull(prepared.ExecutedSearchDefinition,
                    "Queued jobs have not searched and must not repeat search descriptions in history.");
                using var searcher = new Searcher(new MockSoulseekClient([]),
                    TestHelpers.CreatePeerFailureTracker(), new DownloadEvents(), 10, 10);
                var response = new ResponseData();
                switch (prepared)
                {
                    case SongJob song:
                        await searcher.SearchSong(song, prepared.Config.Search, response, CancellationToken.None);
                        break;
                    case AlbumJob album:
                        await searcher.SearchAlbum(album, prepared.Config.Search, response, CancellationToken.None);
                        break;
                    case SongGroupsJob group:
                        await searcher.SearchGroup(group, prepared.Config.Search, response, CancellationToken.None);
                        break;
                    case AlbumGroupsJob group:
                        await searcher.SearchAlbumGroup(group, prepared.Config.Search, response, CancellationToken.None);
                        break;
                    case SearchJob search:
                        await searcher.Search(search, prepared.Config.Search, response, CancellationToken.None);
                        break;
                }
                Assert.IsNotNull(prepared.ExecutedSearchDefinition, prepared.GetType().Name);
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(retained.Search),
                    System.Text.Json.JsonSerializer.Serialize(prepared.ExecutedSearchDefinition));
            }
        }
    }

    [TestMethod]
    public async Task SourceOwnedMetadataSurvivesPreparationAndReplayWithoutAProviderRule()
    {
        var info = new JobSourceInfo(Detail: "Catalog item 42", PreviewDetail: "Item 42");
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }) { SourceInfo = info };
        IExtractor extractor = new CatalogExtractor(song);
        var source = new ExtractJob("catalog:42") { Config = new DownloadSettings(), ItemNumber = 42 };
        Job prepared = JobExtraction.Normalize(source,
            await extractor.GetTracks(source.Input, source.Config.Extraction), false, extractor);
        Assert.AreSame(song, prepared, "Preparation must reuse the extractor's Job.");
        Assert.AreEqual(info.Detail, CoreSnapshotFactory.CreateJob(prepared, 1).SourceDetail);
        Job restored = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(prepared, source.Config))).MaterializeJob();
        Assert.AreEqual(info, restored.SourceInfo);
        Assert.AreEqual(42, restored.ItemNumber);
    }

    private sealed class CatalogExtractor(Job result) : IExtractor
    {
        public Task<Job> GetTracks(string input, ExtractionSettings extraction, ExtractorContext? context = null)
            => Task.FromResult(result);
    }

    [TestMethod]
    public async Task Planner_StreamsIndependentFailuresWithoutDroppingLaterSiblings()
    {
        var root = new JobList("mixed",
        [
            new ExtractJob("Artist - Track", InputType.String),
            new ExtractJob("", InputType.String),
            new SongJob(new SongQuery { Artist = "Later", Title = "Sibling" }),
        ]);
        var planner = new JobPlanner(new JobSettingsComposerResolver(
            new JobSettingsComposer(null, ProfileCatalog.Empty)));

        List<PlannedJobNode> nodes = await CollectAsync(
            planner.PlanAsync(root, new DownloadSettings()));

        Assert.AreEqual("0", nodes[0].Ref);
        PlannedJobNode failed = nodes.Single(node => node.Ref == "0/1");
        Assert.AreEqual(PlannedJobState.Failed, failed.State);
        Assert.AreEqual("extraction", failed.FailureCode);
        PlannedJobNode later = nodes.Single(node => node.Ref == "0/2");
        Assert.AreEqual(PlannedJobState.Ready, later.State);
        Assert.AreEqual(JobSnapshotKind.Song, later.Kind);
        Assert.IsNotNull(later.ReadSettings());
        var restored = (JobList)SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(SubmissionSpecification.Create(root))).MaterializeJob();
        var failedExtract = (ExtractJob)restored.Jobs[1];
        Assert.IsTrue(failedExtract.HasPlannedExtraction);
        Assert.IsNotNull(failedExtract.PlannedExtractionFailure);
        Assert.IsNotNull(failedExtract.PlannedSettings);
    }

    [TestMethod]
    public async Task SourceRevisionReadFailureDoesNotDiscardHealthySiblings()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"sockseek-planner-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "artist,title\nArtist,Track\n");
        FileStream? blocker = null;
        try
        {
            var source = new ExtractJob(csv, InputType.CSV);
            // Extraction has finished reading before publishing Result. Hold
            // the source exclusively so retaining its revision fails afterwards.
            source.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(ExtractJob.Result))
                    blocker = new FileStream(csv, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            };
            var root = new JobList("mixed",
                [source, new SongJob(new SongQuery { Artist = "Later", Title = "Sibling" })]);
            List<PlannedJobNode> nodes = await CollectAsync(
                new JobPlanner(DefaultJobSettingsResolver.Instance).PlanAsync(root, new DownloadSettings()));

            Assert.IsNotNull(blocker);
            PlannedJobNode failed = nodes.Single(node => node.Ref == "0/0");
            Assert.AreEqual(PlannedJobState.Failed, failed.State);
            Assert.AreEqual("extraction", failed.FailureCode);
            Assert.IsTrue(source.HasPlannedExtraction);
            Assert.IsNotNull(source.PlannedExtractionFailure);
            Assert.IsFalse(nodes.Any(node => node.ParentRef == failed.Ref));
            Assert.AreEqual(PlannedJobState.Ready, nodes.Single(node => node.Ref == "0/1").State);
            var restored = (JobList)SubmissionSpecificationCodec.Deserialize(
                SubmissionSpecificationCodec.Serialize(SubmissionSpecification.Create(root))).MaterializeJob();
            Assert.IsNotNull(((ExtractJob)restored.Jobs[0]).PlannedExtractionFailure);
            Assert.IsInstanceOfType<SongJob>(restored.Jobs[1]);
        }
        finally
        {
            blocker?.Dispose();
            File.Delete(csv);
        }
    }

    [TestMethod]
    public async Task Planner_CarriesResolvedExtractorIdentityIntoChildAutoProfiles()
    {
        string csv = Path.Combine(Path.GetTempPath(), $"sockseek-planner-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "artist,title\nTest Artist,Test Track\n");
        try
        {
            var patch = new SettingsPatch([new("Search.MaxStaleTime", 4242)]);
            var profile = new SettingsProfile
            {
                Name = "csv-song",
                Condition = "input-type == \"csv\" && download-mode == \"song\"",
                Download = patch,
            };
            var resolver = new ProfileJobSettingsResolver(
                baseDefaults: null,
                new ProfileCatalog { AutoProfiles = [profile], NamedProfiles = [profile] },
                namedProfiles: [],
                cliProfile: null);
            var planner = new JobPlanner(resolver);

            List<PlannedJobNode> nodes = await CollectAsync(planner.PlanAsync(
                new ExtractJob(csv, InputType.None),
                new DownloadSettings()));

            PlannedJobNode song = nodes.Single(node => node.Kind == JobSnapshotKind.Song);
            Assert.AreEqual(InputType.CSV, song.SourceInputType);
            Assert.AreEqual(4242, song.ReadSettings()?.Search.MaxStaleTime);
            CollectionAssert.Contains(
                song.AppliedAutoProfiles.ToList(),
                "csv-song");
        }
        finally
        {
            if (File.Exists(csv))
                File.Delete(csv);
        }
    }

    [TestMethod]
    public async Task Planner_ProjectsExactSoulseekLinkKindIntoSharedSourceDetail()
    {
        var root = new JobList("links",
        [
            new ExtractJob("slsk://Peer/Share/notes.txt", InputType.SoulseekLink),
            new ExtractJob("slsk://Peer/Share/Album/", InputType.SoulseekLink),
        ]);
        var planner = new JobPlanner(new JobSettingsComposerResolver(
            new JobSettingsComposer(null, ProfileCatalog.Empty)));

        List<PlannedJobNode> nodes = await CollectAsync(
            planner.PlanAsync(root, new DownloadSettings()));

        PlannedJobNode file = nodes.Single(node => node.Kind == JobSnapshotKind.RemoteFile);
        PlannedJobNode directory = nodes.Single(node => node.Kind == JobSnapshotKind.RemoteDirectory);
        Assert.AreEqual(PlannedJobState.Ready, file.State);
        Assert.AreEqual(PlannedJobState.Ready, directory.State);
        Assert.AreEqual("Soulseek link",
            CoreSnapshotFactory.CreateJob(file.CreateSpecification()!.MaterializeJob(), 1).SourceDetail);
        Assert.AreEqual("Soulseek directory",
            CoreSnapshotFactory.CreateJob(directory.CreateSpecification()!.MaterializeJob(), 1).SourceDetail);
    }

    [TestMethod]
    public async Task CapturedPlan_IsUnaffectedByRuntimeSettingsOrExportEdits()
    {
        var song = new SongJob(new SongQuery { Artist = "Original", Title = "Track" })
        {
            ItemName = "Original name",
            ExtractorCond = new FileConditionPatch { Formats = ["flac"] },
            ExtractorFolderCond = new FolderConditionPatch { RequiredTrackTitles = ["Track"] },
        };
        var inherited = new DownloadSettings();
        inherited.Search.MaxStaleTime = 1234;
        var planner = new JobPlanner(DefaultJobSettingsResolver.Instance);
        var nodes = await CollectAsync(planner.PlanAsync(song, inherited));

        song.ItemName = "Changed";
        song.ExtractorCond.Formats![0] = "mp3";
        song.ExtractorFolderCond.RequiredTrackTitles![0] = "Changed";
        var laterSettings = song.PlannedSettings!.ReadSettings();
        laterSettings.Search.MaxStaleTime = 9876;
        Assert.AreEqual(1234, song.PlannedSettings.ReadSettings().Search.MaxStaleTime,
            "Editing an execution copy must not change its frozen binding.");
        song.PlannedSettings = JobSettingsSnapshot.Capture(laterSettings);
        inherited.Search.MaxStaleTime = 9876;
        nodes[0].ReadSettings()!.Search.MaxStaleTime = 9876;
        SubmissionSpecification exported = CaptureSpecification(nodes);
        exported.Command.ExtractorConditions!.Formats![0] = "ogg";
        exported.Command.ExtractorFolderConditions!.RequiredTrackTitles![0] = "Export edit";

        SubmissionSpecification retained = CaptureSpecification(nodes);
        var restored = (SongJob)retained.MaterializeJob();
        Assert.AreEqual("Original", restored.Query.Artist);
        Assert.AreEqual("Original name", restored.ItemName);
        CollectionAssert.AreEqual(new[] { "flac" }, restored.ExtractorCond!.Formats);
        CollectionAssert.AreEqual(new[] { "Track" }, restored.ExtractorFolderCond!.RequiredTrackTitles);
        Assert.AreEqual(1234, retained.MaterializeSettings().Search.MaxStaleTime);
    }

    [TestMethod]
    public async Task CapturedSelectionsStayIndependentOfRuntimeAndExportedCollections()
    {
        var candidate = new SearchProjectionInput(1, 1, "ExactPeer", 3, @"Share\Album\track.flac", 123,
            800, 24, 48_000, 180, "flac", 1000, true,
            [new FileAttributeSnapshot("BitRate", 800)], DateTimeOffset.UnixEpoch).ToFileCandidate();
        var song = new SongJob(new SongQuery { Title = "track" })
        {
            Candidates = [candidate], ResolvedTarget = candidate,
            ExtractorCond = new FileConditionPatch { Formats = ["flac"] },
        };
        var folder = new AlbumFolder("ExactPeer", @"Share\Album",
            [new AlbumFile(new SongQuery { Title = "track" }, candidate)]);
        var album = new AlbumJob(new AlbumQuery { Album = "Album" })
        {
            Results = [folder], ResolvedTarget = folder,
        };
        var search = new SearchJob("query");
        var settings = new DownloadSettings();
        settings.Search.NecessaryCond.Formats = ["flac"];
        search.Definition = SearchDefinition.Create(search, settings.Search);
        var directory = new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
            new DirectoryTransferPlan("Album", [new(candidate.Target, ["Disc 1"])])));
        var root = new JobList("selections", [song, album, search, directory]);
        var nodes = await CollectAsync(
            new JobPlanner(DefaultJobSettingsResolver.Instance).PlanAsync(root, settings));
        SubmissionSpecification direct = SubmissionSpecification.Create(root);
        string reviewed = SubmissionSpecificationCodec.Serialize(CaptureSpecification(nodes));
        string submitted = SubmissionSpecificationCodec.Serialize(direct);

        song.Candidates.Clear();
        song.ExtractorCond!.Formats![0] = "mp3";
        folder.Files.Clear();
        album.Results.Clear();
        ((IList<string>)search.Definition.ProjectionSettings.NecessaryFile.Formats)[0] = "mp3";
        root.Clear();

        SubmissionSpecification exported = CaptureSpecification(nodes);
        exported.Command.Children![0].ExtractorConditions!.Formats![0] = "ogg";
        // Callers can replace records in a new specification, but cannot edit
        // shared captured lists to change what a subsequent commit will restore.
        static void EditIfMutable<T>(IReadOnlyList<T> values, T replacement)
        {
            if (values is IList<T> { IsReadOnly: false } list) list[0] = replacement;
        }
        var commands = exported.Command.Children!;
        EditIfMutable(commands[0].SongCandidates!, commands[0].SelectedSong! with { Filename = "other.flac" });
        EditIfMutable(commands[1].AlbumOptions!, commands[1].AlbumOptions![0] with { FolderPath = "other" });
        EditIfMutable(commands[2].SearchDefinition!.ProjectionSettings.NecessaryFile.Formats, "ogg");
        EditIfMutable(commands[3].DirectoryPlan!.Entries[0].RelativeDirectoryComponents, "other");
        EditIfMutable(commands, commands[3]);

        Assert.AreEqual(reviewed, SubmissionSpecificationCodec.Serialize(CaptureSpecification(nodes)));
        Assert.AreEqual(submitted, SubmissionSpecificationCodec.Serialize(direct));
        var restored = (JobList)CaptureSpecification(nodes).MaterializeJob();
        Assert.AreEqual(candidate.Target.Identity, ((SongJob)restored[0]).Candidates!.Single().Target.Identity);
        Assert.AreEqual(candidate.Target.Identity, ((AlbumJob)restored[1]).ResolvedTarget!.Files.Single().Candidate.Target.Identity);
        CollectionAssert.AreEqual(new[] { "flac" },
            ((SearchJob)restored[2]).Definition!.ProjectionSettings.NecessaryFile.Formats.ToArray());
        Assert.AreEqual("Disc 1", ((RemoteDirectorySource.Resolved)((RemoteDirectoryJob)restored[3]).Source)
            .Plan.Entries.Single().RelativeDirectoryComponents.Single());
    }

    [TestMethod]
    public void FlatPlanAssembly_ResolvesEachNodesCompactSettingsBeforeCombiningThem()
    {
        SubmissionSpecification Leaf(string query, string format)
        {
            var settings = new DownloadSettings();
            settings.Search.NecessaryCond.Formats = [format];
            var original = SubmissionSpecification.CreateShallow(new SearchJob(query), settings);
            var compact = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(original with
            {
                Command = original.Command with
                {
                    PlannedEffectiveSettingsJson = original.EffectiveSettingsJson,
                    PlannedCredentialBindings = original.CredentialBindings,
                },
            }));
            Assert.AreEqual(0, compact.Command.PlannedSettingsRef);
            return compact;
        }
        var root = SubmissionSpecification.CreateShallow(new JobList("selected"), new DownloadSettings());
        SubmissionSpecification selected = JobPlan.AssembleSpecifications([
            ("0", null, root), ("0/1", "0", Leaf("first", "txt")), ("0/2", "0", Leaf("second", "flac")),
        ]);
        var retained = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(selected));
        var restored = (JobList)retained.MaterializeJob();
        CollectionAssert.AreEqual(new[] { "txt" }, restored[0].PlannedSettings!.ReadSettings().Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(new[] { "flac" }, restored[1].PlannedSettings!.ReadSettings().Search.NecessaryCond.Formats);
        Assert.AreEqual("first", ((SearchJob)restored[0]).QueryText);
        Assert.AreEqual("second", ((SearchJob)restored[1]).QueryText);
    }

    [TestMethod]
    public async Task FlatPlanAssembly_PreservesTypedSearchDefinitionsAndSelectedOrder()
    {
        var root = new JobList("mixed",
        [
            new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }),
            new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }),
            new SongGroupsJob(new SongQuery { Artist = "Artist", Title = "SongGroups" }),
            new AlbumGroupsJob(new AlbumQuery { Artist = "Artist", Album = "Grouped album" }),
            new SearchJob("plain query"),
        ]);
        var planner = new JobPlanner(DefaultJobSettingsResolver.Instance);
        List<PlannedJobNode> nodes = await CollectAsync(planner.PlanAsync(root, new DownloadSettings()));
        SubmissionSpecification direct = CaptureSpecification(nodes);
        Assert.AreEqual(SubmissionSpecificationCodec.Serialize(SubmissionSpecification.Create(root)), SubmissionSpecificationCodec.Serialize(direct));

        var selected = JobPlan.AssembleSpecifications(nodes.Where(node => node.Ref is "0" or "0/1" or "0/4")
            .Select(node => (node.Ref, node.ParentRef, node.CreateSpecification()!)));
        var restored = (JobList)selected.MaterializeJob();
        Assert.AreEqual(2, restored.Count);
        Assert.IsInstanceOfType<AlbumJob>(restored[0]);
        Assert.AreEqual("plain query", ((SearchJob)restored[1]).QueryText);
    }

    [TestMethod]
    [DataRow(1, false, false, 8)]
    [DataRow(2, false, false, 8)]
    [DataRow(1, true, false, 8)]
    [DataRow(2, true, false, 8)]
    [DataRow(1, false, true, 8)]
    [DataRow(2, false, true, 8)]
    [DataRow(1, true, true, 8)]
    [DataRow(2, true, true, 8)]
    [DataRow(1, false, false, 1)]
    [DataRow(1, true, false, 1)]
    [DataRow(1, false, true, 1)]
    [DataRow(1, true, true, 1)]
    public void ExtractedCollectionKeepsPositionsWhenSingleOrUpgraded(
        int count, bool album, bool group, int firstItem)
    {
        var settings = new DownloadSettings();
        settings.Search.GroupResults = group;
        var extract = new ExtractJob("https://open.spotify.com/playlist/source", InputType.Spotify)
        {
            Config = settings,
            ItemNumber = 4,
            LineNumber = 6,
        };
        var collection = new JobList("Playlist", Enumerable.Range(firstItem, count).Select(position =>
            new SongJob(new SongQuery
            {
                Artist = "Artist", Title = $"Track {position}", Album = $"Album {position}",
            })
            {
                ItemNumber = position,
                SourceMutation = SourceMutation.RemoveSpotifyPlaylistTrack(
                    "spotify:playlist:source", $"spotify:track:{position}", position),
            })) { EnablesIndexByDefault = true };

        Job normalized = JobExtraction.Normalize(extract, collection, album, new SpotifyExtractor(settings.Spotify));
        var result = normalized is JobList list ? list.Jobs.ToArray() : [normalized];
        CollectionAssert.AreEqual(Enumerable.Range(firstItem, count).ToArray(),
            result.Select(job => job.ItemNumber).ToArray(),
            "Collection positions belong to their extractor, including a single remaining item.");
        Assert.IsTrue(result.All(job => job.SourceInputType == InputType.Spotify));
        Assert.AreEqual("Spotify playlist", extract.SourceInfo?.CollectionLabel);
        Assert.IsTrue(normalized.EnablesIndexByDefault,
            "Collapsing a collection must retain its default index policy.");
        CollectionAssert.AreEqual(Enumerable.Range(firstItem, count).ToArray(),
            result.Select(job => job.SourceMutation!.ItemNumber).ToArray());

        Job restored = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(normalized, settings))).MaterializeJob();
        var replayed = restored is JobList replayedList ? replayedList.Jobs.ToArray() : [restored];
        CollectionAssert.AreEqual(result.Select(job => job.SourceInfo).ToArray(),
            replayed.Select(job => job.SourceInfo).ToArray());
        CollectionAssert.AreEqual(result.Select(job => job.ItemNumber).ToArray(),
            replayed.Select(job => job.ItemNumber).ToArray());
        CollectionAssert.AreEqual(result.Select(job => job.SourceMutation).ToArray(),
            replayed.Select(job => job.SourceMutation).ToArray());
        Assert.AreEqual(normalized.EnablesIndexByDefault, restored.EnablesIndexByDefault);
        CollectionAssert.AreEqual(result.Select(job => CoreSnapshotFactory.CreateJob(job, 1).SourceDetail).ToArray(),
            replayed.Select(job => CoreSnapshotFactory.CreateJob(job, 1).SourceDetail).ToArray());
        foreach (SongJob song in replayed.OfType<SongJob>())
        {
            Assert.AreEqual(song.ItemNumber.ToString(), FileManager.ReplaceVariables(
                "{snum}", FileManagerContext.FromSongJob(song, restored), tagFile: null));
        }
    }

    private static SubmissionSpecification CaptureSpecification(IEnumerable<PlannedJobNode> nodes)
        => JobPlan.AssembleSpecifications(nodes.Select(node => (node.Ref, node.ParentRef, node.CreateSpecification()!)));

    private static async Task<List<PlannedJobNode>> CollectAsync(
        IAsyncEnumerable<PlannedJobNode> source)
    {
        var result = new List<PlannedJobNode>();
        await foreach (PlannedJobNode node in source)
            result.Add(node);
        return result;
    }

    private sealed class JobSettingsComposerResolver(JobSettingsComposer composer)
        : IJobSettingsResolver
    {
        public DownloadSettings Resolve(
            DownloadSettings inherited,
            JobSettingsContext job,
            JobSettingsInheritance inheritance = JobSettingsInheritance.None)
            => composer.Compose(inherited, job, inheritance);
    }
}
