using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Planning;

namespace Tests;

[TestClass]
public sealed class SubmissionSpecificationTests
{
    [TestMethod]
    public void ReusedSettingsValidationStillChecksEachCommandAndSettingsValue()
    {
        var settings = new DownloadSettings();
        settings.Search.MaxStaleTime = 123;
        var specification = SubmissionSpecification.CreateShallow(
            new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }), settings);
        var parsed = new Dictionary<string, DownloadSettings>(StringComparer.Ordinal);
        string valid = SubmissionSpecificationCodec.Serialize(specification, parsed);
        Assert.ThrowsExactly<InvalidDataException>(() => SubmissionSpecificationCodec.Serialize(
            specification with { Command = specification.Command with { SongQuery = null } }, parsed));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => SubmissionSpecificationCodec.Serialize(
            specification with { EffectiveSettingsJson = "{\"unknownSetting\":true}" }, parsed));
        settings.Search.MaxStaleTime = 456;
        var other = SubmissionSpecification.CreateShallow(
            new SongJob(new SongQuery { Title = "Other" }), settings);
        Assert.AreEqual(456, SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(other, parsed)).MaterializeSettings().Search.MaxStaleTime);
        Assert.AreEqual(123, SubmissionSpecificationCodec.Deserialize(valid).MaterializeSettings().Search.MaxStaleTime);
        Assert.AreEqual(valid, SubmissionSpecificationCodec.Serialize(specification, parsed));
    }

    [TestMethod]
    public async Task ExtractedDefinitionsRetainSourcePresentationAndPinnedAlbumPolicyOnReplay()
    {
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" })
        {
            ExtractorInfo = "Original title — source author",
        };
        var album = await new SoulseekExtractor().GetTracks("slsk://ExactPeer/Share/Album/",
            new ExtractionSettings { RequestedMode = ExtractionMode.Album });
        album.ExtractorInfo = "Catalog entry: A12";
        var settings = SubmissionSpecification.CreateShallow(new SearchJob("settings"), new DownloadSettings());
        var specification = settings with { Command = SubmissionSpecification.CommandFrom(new JobList(null, [song, album])), Search = null };
        var restored = (JobList)SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(specification)).MaterializeJob();

        Assert.AreEqual(song.ExtractorInfo, restored[0].ExtractorInfo);
        var pinned = (AlbumJob)restored[1];
        Assert.AreEqual("Catalog entry: A12", pinned.ExtractorInfo);
        Assert.IsFalse(pinned.CanBeSkipped);
        Assert.AreEqual(AlbumDirectoryResolutionPolicy.RetrieveBeforeSelection, pinned.DirectoryResolutionPolicy);
        Assert.AreEqual("ExactPeer", pinned.ResolvedTarget!.Username);
        Assert.AreEqual(@"Share\Album", pinned.ResolvedTarget.FolderPath);
        Assert.AreEqual(0, pinned.Results.Count);
    }

    [TestMethod]
    public void DirectorySourceDefinitionRoundTripsAndUsesReplacedTargetsRatherThanItsOriginalSource()
    {
        var target = new PeerFileTarget(new("ExactPeer", @"Share\Part\original.bin"), 128, "bin");
        var plan = new DirectoryTransferPlan("Folder", [new(target, ["Part"])]);
        var command = new NormalizedJobCommand { Kind = Sockseek.Core.Snapshots.JobSnapshotKind.RemoteDirectory }
            .WithDirectoryPlan(plan);
        var settings = SubmissionSpecification.CreateShallow(new SearchJob("settings"), new DownloadSettings());
        var specification = settings with { Command = command, Search = null };
        foreach (SubmissionSpecification source in new[] { specification,
            SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(specification)) })
        {
            var restored = (RemoteDirectoryJob)source.MaterializeJob();
            var selected = ((RemoteDirectorySource.Resolved)restored.Source).Plan;
            Assert.AreEqual(128, selected.TotalKnownBytes);
            Assert.AreEqual(target.Identity, selected.Entries.Single().Target.Identity);
        }

        var snapshot = command.DirectoryPlan!;
        var original = snapshot.Entries.Single();
        var replacement = command with
        {
            DirectoryPlan = snapshot with
            {
                Entries = [original with
                {
                    Target = new PeerFileTarget(new PeerFileIdentity("ExactPeer", @"Share\Part\replacement.bin"),
                        original.Target.Size, original.Target.Extension),
                }],
            },
        };
        var changed = (RemoteDirectoryJob)SubmissionSpecification.MaterializeCommand(replacement);
        Assert.AreEqual(@"Share\Part\replacement.bin",
            ((RemoteDirectorySource.Resolved)changed.Source).Plan.Entries.Single().Target.Filename);
        Assert.ThrowsExactly<ArgumentException>(() => SubmissionSpecification.MaterializeCommand(command with
        {
            DirectoryPlan = snapshot with
            {
                Entries = [original with { RelativeDirectoryComponents = [".."] }],
            },
        }));
    }

    [TestMethod]
    public void SuppliedSongCandidatesSurviveReplayWithoutBecomingANewSearch()
    {
        var first = new SearchProjectionInput(1, 1, "ExactPeer", 12, @"Music\Exact.flac", 123,
            800, 24, 48_000, 180, ".flac", 1000, true, null, DateTimeOffset.UnixEpoch, 2).ToFileCandidate();
        var fallback = (first.ToProjectionInput() with { Username = "exactpeer", Filename = @"Other\Exact.flac" }).ToFileCandidate();
        var root = new JobList("selected", [
            new SongJob(new SongQuery { Title = "Exact" }) { Candidates = [first, fallback], LastAttemptedCandidate = fallback },
            new SongJob(new SongQuery { Title = "Pinned" }) { ResolvedTarget = first },
            new SongJob(new SongQuery { Title = "Empty" }) { Candidates = [] },
            new SongJob(new SongQuery { Title = "Discover" }),
            new SongJob(new SongQuery { Title = "Exact target" }) { ExactTarget = first.Target },
        ]);
        var retained = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(root, new DownloadSettings())));
        var replay = (JobList)retained.MaterializeJob();
        var flexible = (SongJob)replay.Jobs[0];
        Assert.IsNotNull(flexible.Candidates);
        CollectionAssert.AreEqual(new[] { "ExactPeer", "exactpeer" }, flexible.Candidates.Select(file => file.Username).ToArray());
        Assert.AreEqual(12, flexible.Candidates[0].ResponseFileCount);
        Assert.AreEqual(24, flexible.Candidates[0].BitDepth);
        Assert.IsNull(flexible.ResolvedTarget, "Fallback order is not an exact pin.");
        Assert.IsNull(flexible.LastAttemptedCandidate, "Observed attempts must not become replay inputs.");
        Assert.AreEqual(first.Target.Identity, ((SongJob)replay.Jobs[1]).ResolvedTarget!.Target.Identity);
        Assert.AreEqual(0, ((SongJob)replay.Jobs[2]).Candidates!.Count);
        Assert.IsNull(((SongJob)replay.Jobs[3]).Candidates);
        Assert.AreEqual(first.Target.Identity, ((SongJob)replay.Jobs[4]).ExactTarget!.Identity);
        flexible.Candidates.Clear();
        Assert.AreEqual(2, ((SongJob)((JobList)retained.MaterializeJob()).Jobs[0]).Candidates!.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SuppliedAlbumPlanRetainsExactFilesPoliciesAndFallbackOrder(bool selectedInOptions)
    {
        var query = new SongQuery { Artist = "Exact Artist", Title = "Exact track", URI = "exact-uri", Length = 180 };
        var first = new SearchProjectionInput(1, 1, "Peer", 42, @"Music\Album\Nested\Exact.flac", 123,
            800, 24, 48_000, 180, ".flac", 1000, true, null, DateTimeOffset.UnixEpoch, 2).ToFileCandidate();
        var second = (first.ToProjectionInput() with { Username = "peer" }).ToFileCandidate();
        var chosen = new AlbumFolder("Peer", @"Music\Album", [new(query, first)]) { IsFullyRetrieved = true };
        var fallback = new AlbumFolder("peer", @"Music\Album", [new(query, second)]);
        var pinned = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" })
        {
            Results = selectedInOptions ? [fallback, chosen] : [],
            ResolvedTarget = chosen,
            DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot,
            ValidationRequirement = AlbumValidationRequirement.UserAccepted,
        };
        var flexible = new AlbumJob(pinned.Query) { Results = [fallback, chosen] };
        var root = new JobList("selected", [pinned, flexible]);
        var retained = SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(
            SubmissionSpecification.Create(root, new DownloadSettings())));
        var replay = (JobList)retained.MaterializeJob();
        var restored = (AlbumJob)replay.Jobs[0];
        Assert.IsNotNull(restored.ResolvedTarget);
        if (selectedInOptions) Assert.AreSame(restored.Results[1], restored.ResolvedTarget);
        else Assert.AreEqual(0, restored.Results.Count, "A pinned target must not invent a search alternative.");
        Assert.AreEqual(AlbumDirectoryResolutionPolicy.UseSelectedSnapshot, restored.DirectoryResolutionPolicy);
        Assert.AreEqual(AlbumValidationRequirement.UserAccepted, restored.ValidationRequirement);
        Assert.IsTrue(restored.ResolvedTarget.IsFullyRetrieved);
        AlbumFile file = restored.ResolvedTarget.Files.Single();
        Assert.AreEqual(query.URI, file.Query.URI);
        Assert.AreEqual(query.Title, file.Query.Title);
        Assert.AreEqual(first.Target.Identity, file.Candidate.Target.Identity);
        Assert.AreEqual(42, file.Candidate.ResponseFileCount);
        Assert.AreEqual(24, file.Candidate.BitDepth);
        var plan = AlbumTransferPlanner.FromSelectedDirectory(restored.ResolvedTarget);
        CollectionAssert.AreEqual(new[] { "Nested" }, plan.Entries.Single().RelativeDirectoryComponents.ToArray());
        var unpinned = (AlbumJob)replay.Jobs[1];
        Assert.IsNull(unpinned.ResolvedTarget);
        CollectionAssert.AreEqual(new[] { "peer", "Peer" }, unpinned.Results.Select(folder => folder.Username).ToArray());
        restored.ResolvedTarget.Files.Clear();
        Assert.AreEqual(1, ((AlbumJob)((JobList)retained.MaterializeJob()).Jobs[0]).ResolvedTarget!.Files.Count);
    }

    [TestMethod]
    public void QueryCommandsReplayAndInvalidAcceptedAlbumIndicesAreRejected()
    {
        var specification = SubmissionSpecification.Create(
            new AlbumJob(new AlbumQuery { Album = "Discovery" }), new DownloadSettings());
        var replay = (AlbumJob)SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(specification)).MaterializeJob();
        Assert.AreEqual("Discovery", replay.Query.Album);
        Assert.IsNull(replay.ResolvedTarget);
        Assert.AreEqual(0, replay.Results.Count);
        Assert.AreEqual(AlbumDirectoryResolutionPolicy.CompleteIfNeeded, replay.DirectoryResolutionPolicy);
        foreach (int invalid in new[] { -1, 0, 99 })
            Assert.ThrowsExactly<InvalidDataException>(() =>
                (specification with { Command = specification.Command with { SelectedAlbumOption = invalid } }).Validate());
    }

    [TestMethod]
    public void GroupSearchPresentationSurvivesSubmissionReplay()
    {
        var job = new SearchJob(new SongQuery { Artist = "Artist", Title = "Title" })
        {
            GroupResults = true,
        };
        var settings = new DownloadSettings();
        SubmissionSpecification submitted = SubmissionSpecification.Create(job, settings);
        SubmissionSpecification retained = SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(submitted));
        var replayed = (SearchJob)retained.MaterializeJob();

        Assert.IsTrue(retained.Search!.GroupResults);
        Assert.IsTrue(SearchDefinition.Create(replayed, settings.Search).GroupResults);


    }

    [TestMethod]
    public async Task PlannedExtractionRoundTripRetainsResultAfterSourceChanges()
    {
        string csv = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-retained-plan-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "artist,title\nOriginal Artist,Original Title\n");
        try
        {
            var root = new ExtractJob(csv, InputType.CSV);
            var planner = new JobPlanner(DefaultJobSettingsResolver.Instance);
            await foreach (PlannedJobNode _ in planner.PlanAsync(
                root,
                new DownloadSettings()))
            {
            }
            SubmissionSpecification specification = SubmissionSpecification.Create(root);
            await File.WriteAllTextAsync(csv, "artist,title\nChanged Artist,Changed Title\n");

            var restored = (ExtractJob)SubmissionSpecificationCodec
                .Deserialize(SubmissionSpecificationCodec.Serialize(specification))
                .MaterializeJob();
            Assert.IsTrue(restored.HasPlannedExtraction);
            SongJob song = restored.Result switch
            {
                SongJob direct => direct,
                JobList list => list.Jobs.OfType<SongJob>().Single(),
                _ => throw new AssertFailedException("The retained extraction did not contain a song."),
            };
            Assert.AreEqual("Original Artist", song.Query.Artist);
            Assert.AreEqual("Original Title", song.Query.Title);
        }
        finally
        {
            if (File.Exists(csv))
                File.Delete(csv);
        }
    }

    [TestMethod]
    public async Task PlannedChildSearchReplayKeepsItsSettingsWithoutItsSibling()
    {
        var first = new SearchJob("first");
        var sibling = new SearchJob("sibling");
        var root = new JobList("batch", [first, sibling]);
        var settings = new DownloadSettings();
        var planner = new JobPlanner(DefaultJobSettingsResolver.Instance);
        await foreach (PlannedJobNode _ in planner.PlanAsync(root, settings)) { }
        var changedSettings = first.PlannedSettings!.ReadSettings();
        changedSettings.Search.NecessaryCond.Formats = ["txt"];
        first.PlannedSettings = JobSettingsSnapshot.Capture(changedSettings);
        SubmissionSpecification accepted = SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(SubmissionSpecification.Create(root)));

        SubmissionSpecification replay = accepted.ForSearchJob(first.Id)!;
        var materialized = (SearchJob)replay.MaterializeJob();
        Assert.AreEqual("first", materialized.QueryText);
        CollectionAssert.AreEqual(
            new[] { "txt" }, replay.MaterializeSettings().Search.NecessaryCond.Formats);
        Assert.IsNull(accepted.ForSearchJob(sibling.Id)!.ForSearchJob(first.Id));
        Assert.IsNull(accepted.ForSearchJob(Guid.NewGuid()));
    }

    [TestMethod]
    public void MaterializedPlanChildReplayUsesFreshAcceptedIdsWithoutRecapturingSettings()
    {
        var firstSettings = new DownloadSettings();
        firstSettings.Search.NecessaryCond.Formats = ["txt"];
        firstSettings.YouTube.ApiKey = "original-secret";
        var siblingSettings = new DownloadSettings();
        siblingSettings.Search.NecessaryCond.Formats = ["flac"];
        // Identical queries must not become an ambiguous fallback identity.
        var first = new SearchJob("same query") { PlannedSettings = JobSettingsSnapshot.Capture(firstSettings) };
        var sibling = new SearchJob("same query") { PlannedSettings = JobSettingsSnapshot.Capture(siblingSettings) };
        var root = new ExtractJob("frozen-input.csv", InputType.CSV)
        {
            HasPlannedExtraction = true,
            Result = new JobList("batch", [first, sibling]),
        };
        SubmissionSpecification specification = SubmissionSpecification.Create(
            root, new DownloadSettings(),
            new SubmissionSourceRevision("file", "frozen-input.csv", "frozen-digest", 123, null));
        string firstRetainedSettings = specification.Command.ExtractedResult!.Children![0]
            .PlannedEffectiveSettingsJson!;
        var previousIds = new HashSet<Guid> { first.Id, sibling.Id };
        var credentials = new DownloadSettings();
        credentials.YouTube.ApiKey = "current-secret";

        // Review materializes a frozen plan; rerunning that submission does so again.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var materialized = (ExtractJob)specification.MaterializeJob(credentials);
            var children = ((JobList)materialized.Result!).Jobs.Cast<SearchJob>().ToArray();
            Assert.IsTrue(children.All(child => !previousIds.Contains(child.Id)));
            // Acceptance must retain frozen intent, not recapture mutable runtime settings.
            var runtimeSettings = children[0].PlannedSettings!.ReadSettings();
            runtimeSettings.Search.NecessaryCond.Formats = ["changed-after-materialization"];
            children[0].PlannedSettings = JobSettingsSnapshot.Capture(runtimeSettings);
            SubmissionIdentity.AssignAccepted(materialized, specification);
            SubmissionSpecification accepted = SubmissionSpecificationCodec.Deserialize(
                materialized.SubmissionSpecificationJson!);

            SubmissionSpecification? replay = accepted.ForSearchJob(children[0].Id);
            Assert.IsNotNull(replay, "A reviewed/rerun child's actual ID must resolve its retained command.");
            Assert.AreEqual(firstRetainedSettings, replay.EffectiveSettingsJson);
            CollectionAssert.AreEqual(new[] { "txt" },
                replay.MaterializeSettings(credentials).Search.NecessaryCond.Formats);
            Assert.AreEqual("current-secret", replay.MaterializeSettings(credentials).YouTube.ApiKey);
            Assert.AreEqual("same query", ((SearchJob)replay.MaterializeJob(credentials)).QueryText);
            CollectionAssert.AreEqual(new[] { "flac" },
                accepted.ForSearchJob(children[1].Id)!.MaterializeSettings().Search.NecessaryCond.Formats);
            Assert.AreEqual(specification.SourceRevision, accepted.SourceRevision);
            Assert.IsFalse(materialized.SubmissionSpecificationJson!.Contains("current-secret", StringComparison.Ordinal));
            Assert.IsFalse(materialized.SubmissionSpecificationJson.Contains("original-secret", StringComparison.Ordinal));
            foreach (Guid oldId in previousIds)
            {
                Assert.IsNull(accepted.ForSearchJob(oldId));
                Assert.IsNotNull(specification.ForSearchJob(oldId), "Rebinding must not mutate the source specification.");
            }
            previousIds = children.Select(child => child.Id).ToHashSet();
            specification = accepted;
        }
    }

    [TestMethod]
    public void MaterializedSettingsAreIndependentAndCredentialBindingsStayPerCommand()
    {
        var firstSettings = new DownloadSettings();
        firstSettings.Search.NecessaryCond.Formats = ["flac"];
        firstSettings.Output.OnComplete = ["original"];
        firstSettings.YouTube.ApiKey = "retained-slot-only";
        var secondSettings = SettingsCloner.Clone(firstSettings);
        secondSettings.YouTube.ApiKey = null;
        var root = new JobList("settings isolation",
        [
            new SongJob(new SongQuery { Title = "first" }) { PlannedSettings = JobSettingsSnapshot.Capture(firstSettings) },
            new SongJob(new SongQuery { Title = "second" }) { PlannedSettings = JobSettingsSnapshot.Capture(secondSettings) },
            new SongJob(new SongQuery { Title = "same settings" }) { PlannedSettings = JobSettingsSnapshot.Capture(firstSettings) },
        ]);
        var specification = SubmissionSpecification.Create(root, new DownloadSettings());
        var commands = specification.Command.Children!;
        Assert.AreEqual(commands[0].PlannedEffectiveSettingsJson, commands[1].PlannedEffectiveSettingsJson);
        CollectionAssert.Contains(commands[0].PlannedCredentialBindings!.ToArray(), "youtube.api-key");
        Assert.AreEqual(0, commands[1].PlannedCredentialBindings!.Count);
        foreach (var retained in new[] { specification,
            SubmissionSpecificationCodec.Deserialize(SubmissionSpecificationCodec.Serialize(specification)) })
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var credentials = new DownloadSettings();
                credentials.YouTube.ApiKey = $"current-{attempt}";
                var (restoredJob, rootSettings) = retained.Materialize(credentials);
                var restored = (JobList)restoredJob;
                Assert.IsNull(rootSettings.YouTube.ApiKey);
                rootSettings.Search.NecessaryCond.Formats = ["root-only"];
                var first = restored.Jobs[0].PlannedSettings!.ReadSettings();
                var second = restored.Jobs[1].PlannedSettings!.ReadSettings();
                Assert.AreEqual(credentials.YouTube.ApiKey, first.YouTube.ApiKey);
                Assert.IsNull(second.YouTube.ApiKey, "Equal settings JSON must not merge credential binding slots.");
                Assert.IsNull(retained.MaterializeSettings(credentials).YouTube.ApiKey);
                first.Search.NecessaryCond.Formats[0] = "mutated";
                first.Output.OnComplete!.Add("later");
                first.AppliedAutoProfiles.Add("runtime-only");
                CollectionAssert.AreEqual(new[] { "flac" }, second.Search.NecessaryCond.Formats);
                CollectionAssert.AreEqual(new[] { "original" }, second.Output.OnComplete);
                Assert.AreEqual(0, second.AppliedAutoProfiles.Count);
                var sameSettings = restored.Jobs[2].PlannedSettings!.ReadSettings();
                Assert.AreEqual(credentials.YouTube.ApiKey, sameSettings.YouTube.ApiKey);
                CollectionAssert.AreEqual(new[] { "flac" }, sameSettings.Search.NecessaryCond.Formats);
                CollectionAssert.AreEqual(new[] { "original" }, sameSettings.Output.OnComplete);
                Assert.AreEqual(0, sameSettings.AppliedAutoProfiles.Count);
                CollectionAssert.AreEqual(new[] { "flac" },
                    restored.Jobs[0].PlannedSettings!.ReadSettings().Search.NecessaryCond.Formats);
            }
        }

        var invalid = specification with
        {
            Command = specification.Command with
            {
                Children = [commands[0], commands[1] with { PlannedEffectiveSettingsJson = "{invalid-json" }],
            },
        };
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => invalid.Validate());
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => invalid.MaterializeJob());
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => invalid.MaterializeSettings());
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => invalid.Materialize());
    }

    [TestMethod]
    public void Specification_RoundTripsTypedCommandEffectiveSettingsAndExactPeerSpelling()
    {
        var exactTarget = new PeerFileTarget(
            new PeerFileIdentity("PeerCase", @"Share\MiXeD\Track.FLAC"),
            123,
            ".FLAC",
            bitRate: 900,
            bitDepth: 24,
            sampleRate: 96_000,
            length: 181);
        var remote = new RemoteFileJob(
            exactTarget,
            new RelativeOutputPath(["MiXeD", "Track.FLAC"]))
        {
            ItemNumber = 7,
            LineNumber = 12,
            SourceMutation = SourceMutation.ClearTextLine("/operator/list.txt", 12, 7),
            SourceInputType = InputType.List,
        };
        var search = new SearchJob(new SongQuery
        {
            Artist = "Artist",
            Title = "Title",
            URI = "slsk://PeerCase/Share/MiXeD/Track.FLAC",
        });
        var root = new JobList("review", [search, remote]);
        var settings = SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Music);
        settings.Search.NecessaryCond.Formats = ["flac"];
        settings.Search.PreferredFolderCond.AddRequiredTrackTitles(["Intro", "Finale"]);
        settings.Output.OnComplete = ["-- notify exact — 雪 💿 <&> \"quoted\""];
        var credentials = System.Text.Json.JsonSerializer.Deserialize<DownloadSettings>("""
            {"spotify":{"clientId":"do-not-retain-client-id","clientSecret":"do-not-retain-spotify",
                "token":"do-not-retain-token","refresh":"do-not-retain-refresh"},
             "youTube":{"apiKey":"do-not-retain-youtube"}, "soundCloud":{"authToken":"do-not-retain-soundcloud"}}
            """, RetainedSettingsCodec.CreateOptions())!;
        settings.Spotify.ClientId = credentials.Spotify.ClientId;
        settings.Spotify.ClientSecret = credentials.Spotify.ClientSecret;
        settings.Spotify.Token = credentials.Spotify.Token;
        settings.Spotify.Refresh = credentials.Spotify.Refresh;
        settings.YouTube.ApiKey = credentials.YouTube.ApiKey;
        settings.SoundCloud.AuthToken = credentials.SoundCloud.AuthToken;
        settings.Preprocess.Regex = [(new() { Title = "a.*", Artist = "雪" }, new() { Title = "b$1" })];

        SubmissionSpecification original = SubmissionSpecification.Create(
            root,
            settings,
            new SubmissionSourceRevision("file", "/operator/list.txt", "sha256:abc", 99, DateTimeOffset.UnixEpoch));
        // Single and streaming captures must retain exactly the same values.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            RetainedSettingsCodec.Serialize(writer, settings);
        Assert.AreEqual(original.EffectiveSettingsJson, System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
        string json = SubmissionSpecificationCodec.Serialize(original);
        SubmissionSpecification restored = SubmissionSpecificationCodec.Deserialize(json);
        JobList restoredRoot = (JobList)restored.MaterializeJob();
        DownloadSettings restoredSettings = restored.MaterializeSettings();

        Assert.IsFalse(json.Contains("do-not-retain-", StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(new[] { "spotify.client-id", "spotify.client-secret",
            "spotify.token", "spotify.refresh", "youtube.api-key", "soundcloud.auth-token" }, restored.CredentialBindings.ToArray());
        Assert.AreEqual("do-not-retain-client-id", settings.Spotify.ClientId);
        Assert.AreEqual("do-not-retain-spotify", settings.Spotify.ClientSecret);
        Assert.AreEqual("do-not-retain-token", settings.Spotify.Token);
        Assert.AreEqual("do-not-retain-refresh", settings.Spotify.Refresh);
        Assert.AreEqual("do-not-retain-youtube", settings.YouTube.ApiKey);
        var current = new DownloadSettings();
        current.Spotify.ClientId = "current-client";
        current.Spotify.ClientSecret = "current-secret";
        current.Spotify.Token = "current-token";
        current.Spotify.Refresh = "current-refresh";
        current.YouTube.ApiKey = "current-key";
        current.SoundCloud.AuthToken = "current-soundcloud";
        DownloadSettings rebound = restored.MaterializeSettings(current);
        Assert.AreEqual(current.Spotify.ClientId, rebound.Spotify.ClientId);
        Assert.AreEqual(current.Spotify.ClientSecret, rebound.Spotify.ClientSecret);
        Assert.AreEqual(current.Spotify.Token, rebound.Spotify.Token);
        Assert.AreEqual(current.Spotify.Refresh, rebound.Spotify.Refresh);
        Assert.AreEqual(current.YouTube.ApiKey, rebound.YouTube.ApiKey);
        Assert.AreEqual(current.SoundCloud.AuthToken, rebound.SoundCloud.AuthToken);
        Assert.AreEqual("sha256:abc", restored.SourceRevision?.Digest);
        Assert.AreEqual(2, restoredRoot.Jobs.Count);
        var restoredRemote = (RemoteFileJob)restoredRoot.Jobs[1];
        Assert.AreEqual("PeerCase", restoredRemote.Target.Username);
        Assert.AreEqual(@"Share\MiXeD\Track.FLAC", restoredRemote.Target.Filename);
        CollectionAssert.AreEqual(new[] { "MiXeD", "Track.FLAC" }, restoredRemote.OutputPath.Components.ToArray());
        Assert.AreEqual(12, restoredRemote.SourceMutation?.LineNumber);
        Assert.AreEqual(InputType.List, restoredRemote.SourceInputType);
        CollectionAssert.AreEqual(new[] { "flac" }, restoredSettings.Search.NecessaryCond.Formats);
        CollectionAssert.AreEqual(
            new[] { "Intro", "Finale" },
            restoredSettings.Search.PreferredFolderCond.RequiredTrackTitles.ToArray());
        CollectionAssert.AreEqual(settings.Output.OnComplete, restoredSettings.Output.OnComplete);
        Assert.IsNull(restoredSettings.Spotify.ClientSecret);
        Assert.IsNull(restoredSettings.YouTube.ApiKey);
    }

    [TestMethod]
    public void Specification_RejectsUnsupportedSchemaAndInvalidSettingsReferences()
    {
        var job = new SearchJob("manual.pdf");
        var settings = SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic);
        job.PlannedSettings = JobSettingsSnapshot.Capture(settings);
        SubmissionSpecification specification = SubmissionSpecification.Create(job);
        foreach (int unsupported in new[] { 2, 3, 4, 99 })
        {
            string json = SubmissionSpecificationCodec.Serialize(specification)
                .Replace($"\"schemaVersion\":{SubmissionSpecification.CurrentSchemaVersion}",
                    $"\"schemaVersion\":{unsupported}", StringComparison.Ordinal);
            Assert.ThrowsExactly<NotSupportedException>(() => SubmissionSpecificationCodec.Deserialize(json));
            Assert.ThrowsExactly<NotSupportedException>(() =>
                SubmissionSpecificationCodec.Serialize(specification with { SchemaVersion = unsupported }));
        }

        SubmissionSpecification retained = SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(specification));
        foreach (SubmissionSpecification invalid in new[]
        {
            retained with { SharedSettings = null },
            retained with { Command = retained.Command with { PlannedSettingsRef = -1 } },
            retained with { Command = retained.Command with { PlannedSettingsRef = retained.SharedSettings!.Count } },
            retained with { Command = retained.Command with { PlannedEffectiveSettingsJson = "{}" } },
            retained with { Command = retained.Command with { PlannedCredentialBindings = [] } },
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => invalid.MaterializeJob());
            Assert.ThrowsExactly<InvalidDataException>(() => invalid.MaterializeSettings());
            Assert.ThrowsExactly<InvalidDataException>(() => SubmissionSpecificationCodec.Serialize(invalid));
        }
    }

    [TestMethod]
    public void Specification_PreservesExactDirectoryExclusions()
    {
        var source = new RemoteDirectorySource.PeerDirectory(
            new PeerDirectoryIdentity("PeerCase", @"Root\MiXeD"),
            [@"Root\MiXeD\A.flac", "Root/MiXeD/a.flac"]);
        var specification = SubmissionSpecification.Create(
            new RemoteDirectoryJob(source), new DownloadSettings());

        var restored = (RemoteDirectoryJob)SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(specification)).MaterializeJob();
        var restoredSource = (RemoteDirectorySource.PeerDirectory)restored.Source;

        Assert.AreEqual(source.Directory, restoredSource.Directory);
        CollectionAssert.AreEquivalent(source.ExcludedFilenames.ToArray(), restoredSource.ExcludedFilenames.ToArray());
    }

    [TestMethod]
    [DataRow("Root")]
    [DataRow("")]
    public void IndependentlySelectedFilePlanStaysUngroupedAfterSubmissionReplay(string placementRoot)
    {
        var target = new PeerFileTarget(new PeerFileIdentity("Peer", @"Root\Selected.bin"), 12, ".bin");
        var plan = new DirectoryTransferPlan(placementRoot, [new DirectoryTransferEntry(target, [])],
            representsDirectorySelection: false);
        var specification = SubmissionSpecification.Create(
            new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan)),
            new DownloadSettings());

        var restored = (RemoteDirectoryJob)SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(specification)).MaterializeJob();
        var restoredPlan = ((RemoteDirectorySource.Resolved)restored.Source).Plan;
        Assert.IsFalse(restoredPlan.RepresentsDirectorySelection);
        Assert.AreEqual(placementRoot, restoredPlan.DisplayRoot);
        Assert.AreEqual(target.Identity, restoredPlan.Entries.Single().Target.Identity);
        Assert.IsNull(restoredPlan.SourceDirectory);
    }

    [TestMethod]
    public void ExactDirectoryOriginSurvivesSubmissionReplay()
    {
        var source = new PeerDirectoryIdentity("Peer", @"Artwork\Head Hunters");
        var observation = new PeerDirectorySnapshot(source, [
            new PeerFileTarget(
                new PeerFileIdentity("Peer", @"Artwork\Head Hunters\Cover.jpg"),
                12, ".jpg"),
        ], isComplete: false);
        var plan = DirectoryTransferPlanner.FromObservedSelection(observation);
        var specification = SubmissionSpecification.Create(
            new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan)),
            new DownloadSettings());

        var restored = (RemoteDirectoryJob)SubmissionSpecificationCodec.Deserialize(
            SubmissionSpecificationCodec.Serialize(specification)).MaterializeJob();
        var restoredPlan = ((RemoteDirectorySource.Resolved)restored.Source).Plan;
        Assert.AreEqual(source.Username, restoredPlan.SourceDirectory?.Username);
        Assert.AreEqual(source.FolderPath, restoredPlan.SourceDirectory?.FolderPath);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void GeneratedResultsAndChildrenHaveRolesWithOrWithoutRetainedSubmission(bool accepted)
    {
        DateTimeOffset submittedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var source = new ExtractJob("Artist - Title", InputType.String);
        Guid? submissionId = accepted ? SubmissionIdentity.AssignAccepted(
            source,
            new DownloadSettings(),
            submittedAtUtc: submittedAt) : null;
        Assert.AreEqual(JobSemanticRole.UserRoot, source.SemanticRole);
        var generated = new SongJob(new SongQuery
        {
            Artist = "Artist",
            Title = "Title",
        });

        SubmissionIdentity.AssignGeneratedResult(source, generated);

        Assert.AreEqual(submissionId, generated.SubmissionId);
        Assert.AreEqual(JobSemanticRole.SemanticResult, generated.SemanticRole);
        Assert.IsTrue(generated.CreatedAtUtc > submittedAt,
            "A generated child's creation time is its own registration time, not submission time.");
        Assert.AreEqual(accepted ? submittedAt : (DateTimeOffset?)null, source.CreatedAtUtc);

        var nestedSong = new SongJob(new SongQuery { Title = "Nested" });
        var group = new JobList("Generated group", [nestedSong]);
        SubmissionIdentity.AssignExecutionChild(generated, group);
        Assert.AreEqual(JobSemanticRole.Orchestration, group.SemanticRole);
        Assert.AreEqual(JobSemanticRole.ExecutionChild, nestedSong.SemanticRole);
        Assert.AreEqual(submissionId, nestedSong.SubmissionId);
    }
}
