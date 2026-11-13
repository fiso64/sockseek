using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Concurrent;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;

namespace Tests;

[TestClass]
public sealed class JobSettingsComposerTests
{
    [TestMethod]
    public void ExplicitFalseOverridesMusicSkippingFromConfigurationAndProfiles()
    {
        var settings = new DownloadSettings { Skip = { SkipExisting = true, SkipMusicDir = "/library" },
            Output = { IndexFilePath = "/completion.csv" } };
        var composer = new JobSettingsComposer(settings, new ProfileCatalog
        {
            NamedProfiles = [new SettingsProfile { Name = "history", Download = new SettingsPatch([new("Skip.SkipExisting", true)]) }],
        });
        var resolved = composer.ComposeDetailed(settings, new SongJob(new SongQuery { Title = "Track" }),
            request: new JobSettingsRequestLayers(ProfileNames: ["history"], Download: new SettingsPatch([new("Skip.SkipExisting", false)])));
        Assert.IsFalse(resolved.Settings.Skip.SkipExisting);
        Assert.AreEqual("/library", resolved.Settings.Skip.SkipMusicDir);
        Assert.AreEqual("/completion.csv", resolved.Settings.Output.IndexFilePath);
        Assert.IsTrue(settings.Skip.SkipExisting);
    }

    [TestMethod]
    public void CommandFactsComposeEverySemanticModeWithoutAnExecutionObject()
    {
        foreach (var (kind, mode, projection) in new[]
        {
            (JobSnapshotKind.Song, "song", (SearchDefaultProjectionKind?)null),
            (JobSnapshotKind.Album, "album", null),
            (JobSnapshotKind.SongGroups, "song-groups", null),
            (JobSnapshotKind.AlbumGroups, "album-groups", null),
            (JobSnapshotKind.RemoteFile, "generic", null),
            (JobSnapshotKind.RemoteDirectory, "generic", null),
            (JobSnapshotKind.Search, "song", SearchDefaultProjectionKind.Track),
            (JobSnapshotKind.Search, "album", SearchDefaultProjectionKind.Album),
        })
        {
            var command = new NormalizedJobCommand
            {
                Kind = kind, SourceInputType = InputType.CSV,
                SongQuery = projection == SearchDefaultProjectionKind.Track
                    ? new("Artist", "Track", "", "", -1, false) : null,
                AlbumQuery = projection == SearchDefaultProjectionKind.Album
                    ? new("Artist", "Album", "", "", false) : null,
            };
            var profile = Profile("matching", value => value.Search.MaxStaleTime = 4321,
                $"input-type == \"csv\" && download-mode == \"{mode}\"");
            var composer = new JobSettingsComposer(null, new ProfileCatalog { AutoProfiles = [profile] });
            JobSettingsCompositionResult result = composer.ComposeDetailed(
                new DownloadSettings(), JobSettingsContext.FromCommand(command, Guid.Empty));

            Assert.AreEqual(4321, result.Settings.Search.MaxStaleTime, mode);
            CollectionAssert.AreEqual(new[] { "matching" }, result.Settings.AppliedAutoProfiles.ToArray(), mode);
            Assert.AreEqual(kind is JobSnapshotKind.RemoteFile or JobSnapshotKind.RemoteDirectory
                ? SearchSettingsBaselineKind.Generic : SearchSettingsBaselineKind.Music, result.Baseline, mode);
        }
    }

    [TestMethod]
    public void InteractiveSourcesAndSearchModesAreIndependentOfConfiguredInput()
    {
        var settings = new DownloadSettings
        {
            Extraction = { Input = "https://open.spotify.com/playlist/unused", InputType = InputType.Spotify,
                RequestedMode = ExtractionMode.Album },
            Search = { GroupResults = true },
        };
        (Job Job, string Source, string Mode)[] cases =
        [
            (new SearchJob("query"), "manual-search", "generic"),
            (new SearchJob(new SongQuery { Title = "track" }), "manual-search", "song"),
            (new SearchJob(new SongQuery { Title = "track" }) { GroupResults = true }, "manual-search", "song-groups"),
            (new SearchJob(new AlbumQuery { Album = "album" }), "manual-search", "album"),
            (new SearchJob(new AlbumQuery { Album = "album" }) { GroupResults = true }, "manual-search", "album-groups"),
            (new SongJob(new SongQuery()) { SelectionSource = JobSelectionSource.ManualSearch }, "manual-search", "song"),
            (new AlbumJob(new AlbumQuery()) { SelectionSource = JobSelectionSource.ManualSearch }, "manual-search", "album"),
            (new RemoteDirectoryJob(new RemoteDirectorySource.PeerDirectory(new PeerDirectoryIdentity("peer", "folder")))
                { SelectionSource = JobSelectionSource.PeerShares }, "peer-shares", "generic"),
            (new ExtractJob("https://www.youtube.com/watch?v=test") { SourceInputType = InputType.List }, "youtube", "album-groups"),
        ];
        foreach (var (job, source, mode) in cases)
        {
            var profile = Profile("match", value => value.Search.MaxStaleTime = 4321,
                $"input-type == \"{source}\" && download-mode == \"{mode}\" && !interactive && !progress-json && !no-progress");
            var composer = new JobSettingsComposer(settings, new ProfileCatalog { AutoProfiles = [profile] });
            Assert.AreEqual(4321, composer.Compose(settings, job).Search.MaxStaleTime, source + "/" + mode);
            var command = SubmissionSpecification.Create(job, settings).Command;
            Assert.AreEqual(4321, composer.Compose(settings, JobSettingsContext.FromCommand(command, Guid.Empty))
                .Search.MaxStaleTime, "Retained " + mode);
            if (job is not ExtractJob)
                Assert.AreEqual(mode.EndsWith("-groups"), ProfileConditionEvaluator.Satisfied("group", settings, job));
        }
        Assert.ThrowsExactly<Exception>(() => ProfileConditionEvaluator.Satisfied("typo", settings));
    }

    [TestMethod]
    public void CapturedSettingsFactsDoNotFollowLaterRuntimeMutations()
    {
        var extract = new ExtractJob("Artist - Track", InputType.String)
        {
            RequestedModeOverride = ExtractionMode.Song,
        };
        JobSettingsContext captured = extract;
        extract.InputType = InputType.List;
        extract.RequestedModeOverride = ExtractionMode.Album;
        var profile = Profile("original", value => value.Search.MaxStaleTime = 4321,
            "input-type == \"string\" && download-mode == \"song\"");
        var composer = new JobSettingsComposer(null, new ProfileCatalog { AutoProfiles = [profile] });

        Assert.AreEqual(4321, composer.Compose(new DownloadSettings(), captured).Search.MaxStaleTime);
        Assert.IsFalse(composer.Compose(new DownloadSettings(), extract).AppliedAutoProfiles.Contains("original"));
    }

    [TestMethod]
    public void GroupProjectionCapabilityDoesNotInventADefaultSearchMode()
    {
        var search = new SearchJob("query")
        {
            DefaultAlbumGroupProjection = new(new AlbumQuery { Artist = "Artist", Album = "Album" }),
        };
        var profile = Profile("album", value => value.Search.MaxStaleTime = 4321,
            "download-mode == \"album\"");
        var composer = new JobSettingsComposer(null, new ProfileCatalog { AutoProfiles = [profile] });
        JobSettingsCompositionResult result = composer.ComposeDetailed(new DownloadSettings(), search);

        Assert.AreEqual(SearchSettingsBaselineKind.Music, result.Baseline);
        Assert.AreEqual(0, result.Settings.AppliedAutoProfiles.Count);
    }

    [TestMethod]
    public void GenericSearchBaseline_HasNoImplicitMusicConditions()
    {
        var composer = new JobSettingsComposer(null, ProfileCatalog.Empty);

        DownloadSettings settings = composer.Compose(
            new DownloadSettings(),
            new SearchJob("manual.pdf"));

        AssertNeutral(settings.Search.NecessaryCond);
        AssertNeutral(settings.Search.PreferredCond);
        Assert.IsFalse(settings.Search.NecessaryCond.ExcludeLocked);
        Assert.IsTrue(settings.Search.PreferredCond.ExcludeLocked);
        Assert.AreEqual(0, settings.Search.NecessaryFolderCond.RequiredTrackTitles.Count);
        Assert.AreEqual(0, settings.Search.PreferredFolderCond.RequiredTrackTitles.Count);
    }

    [TestMethod]
    public void TrackAndAlbumSearchBaselines_UseTheSameMusicConditions()
    {
        var composer = new JobSettingsComposer(null, ProfileCatalog.Empty);

        DownloadSettings track = composer.Compose(
            new DownloadSettings(),
            new SearchJob(new SongQuery { Artist = "Artist", Title = "Track" }));
        DownloadSettings album = composer.Compose(
            new DownloadSettings(),
            new SearchJob(new AlbumQuery { Artist = "Artist", Album = "Album" }));

        Assert.AreEqual(track.Search.NecessaryCond, album.Search.NecessaryCond);
        Assert.AreEqual(track.Search.PreferredCond, album.Search.PreferredCond);
        CollectionAssert.AreEqual(SearchDefaults.Formats.ToArray(), track.Search.NecessaryCond.Formats);
        Assert.AreEqual(0, track.Search.PreferredCond.Formats.Length);
        Assert.AreEqual(3, track.Search.NecessaryCond.LengthTolerance);
        Assert.IsNull(track.Search.PreferredCond.MinBitrate);
        Assert.IsNull(track.Search.PreferredCond.MaxBitrate);
        Assert.IsNull(track.Search.PreferredCond.MaxSampleRate);
        Assert.AreEqual(3, track.Search.PreferredCond.LengthTolerance);
        Assert.IsTrue(track.Search.PreferredCond.StrictTitle);
        Assert.IsTrue(track.Search.PreferredCond.StrictAlbum);
        Assert.IsFalse(track.Search.NecessaryCond.ExcludeLocked);
        Assert.IsTrue(track.Search.PreferredCond.ExcludeLocked);
    }

    [TestMethod]
    public void GenericSearch_StillHonorsExplicitProfileConditions()
    {
        var explicitProfile = Profile("documents", settings =>
        {
            settings.Search.NecessaryCond.Formats = ["pdf"];
            settings.Search.PreferredCond.MinBitrate = 17;
        });
        var catalog = new ProfileCatalog { NamedProfiles = [explicitProfile] };
        var composer = new JobSettingsComposer(null, catalog);

        DownloadSettings settings = composer.Compose(
            new DownloadSettings(),
            new SearchJob("manual"),
            request: new JobSettingsRequestLayers(ProfileNames: ["documents"]));

        CollectionAssert.AreEqual(new[] { "pdf" }, settings.Search.NecessaryCond.Formats);
        Assert.AreEqual(17, settings.Search.PreferredCond.MinBitrate);
        Assert.IsNull(settings.Search.NecessaryCond.LengthTolerance);
        Assert.IsNull(settings.Search.PreferredCond.LengthTolerance);
        Assert.IsFalse(settings.Search.PreferredCond.StrictTitle);
        Assert.IsFalse(settings.Search.PreferredCond.StrictAlbum);
    }

    [TestMethod]
    public void Composition_UsesOneSharedPrecedenceAndRequestForAutoMatching()
    {
        foreach (bool matches in new[] { false, true })
        {
            var defaultProfile = Profile("default", settings => settings.Search.MaxStaleTime = 1);
            var autoProfile = Profile("group-auto", settings => settings.Search.MaxStaleTime = 2,
                condition: "group");
            var namedProfile = Profile("named", settings => settings.Search.MaxStaleTime = 3);
            var launch = new SettingsPatch([new("Search.MaxStaleTime", 4)]);
            var request = new SettingsPatch([new("Search.GroupResults", matches), new("Search.MaxStaleTime", 5)]);
            var catalog = new ProfileCatalog
            {
                DefaultProfile = defaultProfile,
                AutoProfiles = [autoProfile],
                NamedProfiles = [namedProfile],
            };
            var composer = new JobSettingsComposer(
                null,
                catalog,
                launchNamedProfiles: [namedProfile],
                launchDownload: launch);

            var job = new ExtractJob("Artist - Track", InputType.String) { RequestedModeOverride = ExtractionMode.Song };
            var layers = new JobSettingsRequestLayers(Download: request);
            DownloadSettings settings = composer.Compose(new DownloadSettings(), job, request: layers);
            JobSettingsCompositionResult detailed = composer.ComposeDetailed(new DownloadSettings(), job, request: layers);
            Assert.AreEqual("request", detailed.Provenance["Search.MaxStaleTime"]);
            Assert.AreEqual(SubmissionSpecification.CreateShallow(job, settings).EffectiveSettingsJson,
                SubmissionSpecification.CreateShallow(job, detailed.Settings).EffectiveSettingsJson,
                "Ordinary and detailed composition must resolve the same complete settings, even when no auto-profile matches.");

            CollectionAssert.AreEqual(matches ? new[] { "group-auto" } : [], settings.AppliedAutoProfiles.ToArray());
            Assert.AreEqual(matches, settings.Search.GroupResults);
            Assert.AreEqual(5, settings.Search.MaxStaleTime);
        }
    }

    [TestMethod]
    public void Composition_ProvenanceHonorsExplicitEqualValueOverrides()
    {
        var profilePatch = new SettingsPatch([new("Skip.SkipExisting", true)]);
        var requestPatch = new SettingsPatch([new("Skip.SkipExisting", true)]);
        var composer = new JobSettingsComposer(null, new ProfileCatalog
        {
            NamedProfiles =
            [
                new SettingsProfile { Name = "same", Download = profilePatch },
            ],
        });

        JobSettingsCompositionResult profile = composer.ComposeDetailed(
            new DownloadSettings(),
            new SearchJob("query"),
            request: new JobSettingsRequestLayers(ProfileNames: ["same"]));
        JobSettingsCompositionResult request = composer.ComposeDetailed(
            new DownloadSettings(),
            new SearchJob("query"),
            request: new JobSettingsRequestLayers(
                ProfileNames: ["same"],
                Download: requestPatch));

        Assert.AreEqual("profile", profile.Provenance["Skip.SkipExisting"]);
        Assert.AreEqual("request", request.Provenance["Skip.SkipExisting"]);
    }

    [TestMethod]
    public void IncrementalSorter_DerivesPreferenceTierFromAllPreferredConditions()
    {
        var search = SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic).Search;
        search.PreferredCond.Formats = ["mp3"];
        search.PreferredCond.MinBitrate = 200;
        search.PreferredCond.StrictTitle = true;
        var sorter = new IncrementalResultSorter(
            new SongQuery { Title = "Wanted" },
            search,
            new ConcurrentDictionary<string, int>());
        sorter.AddRange([
            Input(1, @"Share\Wanted.mp3", bitrate: 320),
            Input(2, @"Share\Wanted.flac", bitrate: 900),
            Input(3, @"Share\Wanted low.mp3", bitrate: 128),
            Input(4, @"Share\Different.mp3", bitrate: 320),
        ]);

        IReadOnlyDictionary<string, ProjectedFileCandidate> projected = sorter
            .SnapshotProjectedFiles()
            .ToDictionary(item => item.Input.Filename);
        Assert.AreEqual(
            SearchPreferenceTier.Preferred,
            projected[@"Share\Wanted.mp3"].ConditionFacts.PreferenceTier);
        Assert.AreEqual(
            SearchPreferenceTier.Other,
            projected[@"Share\Wanted.flac"].ConditionFacts.PreferenceTier);
        Assert.AreEqual(
            SearchPreferenceTier.Other,
            projected[@"Share\Wanted low.mp3"].ConditionFacts.PreferenceTier);
        Assert.AreEqual(
            SearchPreferenceTier.Other,
            projected[@"Share\Different.mp3"].ConditionFacts.PreferenceTier);

        var neutralSorter = new IncrementalResultSorter(
            new SongQuery { Title = "anything" },
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic).Search,
            new ConcurrentDictionary<string, int>());
        neutralSorter.AddRange([Input(5, @"Share\arbitrary.bin", bitrate: null)]);
        Assert.AreEqual(
            SearchPreferenceTier.Preferred,
            neutralSorter.SnapshotProjectedFiles().Single().ConditionFacts.PreferenceTier);
    }

    [TestMethod]
    public void SearchDefinition_RoundTripsStableProjectionInputsAndRejectsUnknownSchema()
    {
        var job = new SearchJob(new AlbumQuery
        {
            Artist = "Exact Artist",
            Album = "Exact Album",
            SearchHint = "hint",
            URI = "slsk://Peer/Share",
            ArtistMaybeWrong = true,
        });
        SearchSettings settings = SearchSettingsBaselines
            .Create(SearchSettingsBaselineKind.Music)
            .Search;
        settings.NecessaryCond.Formats = ["flac"];
        settings.PreferredFolderCond.MinTrackCount = 7;
        settings.PreferredFolderCond.AddRequiredTrackTitles(["Intro", "Finale"]);
        SearchDefinition original = SearchDefinition.Create(job, settings);

        string json = SearchDefinitionCodec.Serialize(original);
        SearchDefinition restored = SearchDefinitionCodec.Deserialize(json);
        SearchSettings restoredSettings = restored.ProjectionSettings.ToSettings();

        Assert.AreEqual(SearchDefinition.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.AreEqual(SearchSettingsBaselineKind.Music, restored.Baseline);
        Assert.AreEqual(SearchDefaultProjectionKind.Album, restored.DefaultProjection);
        Assert.AreEqual(job.QueryText, restored.NetworkQuery);
        Assert.AreEqual("Exact Artist", restored.AlbumQuery?.Artist);
        Assert.AreEqual("Exact Album", restored.AlbumQuery?.Album);
        Assert.AreEqual("hint", restored.AlbumQuery?.SearchHint);
        CollectionAssert.AreEqual(new[] { "flac" }, restoredSettings.NecessaryCond.Formats);
        Assert.AreEqual(7, restoredSettings.PreferredFolderCond.MinTrackCount);
        CollectionAssert.AreEqual(
            new[] { "Intro", "Finale" },
            restoredSettings.PreferredFolderCond.RequiredTrackTitles.ToArray());

        string unsupported = json.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":99",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            SearchDefinitionCodec.Deserialize(unsupported));
    }

    private static SettingsProfile Profile(
        string name,
        Action<DownloadSettings> apply,
        string? condition = null)
    {
        var before = new DownloadSettings();
        var after = SettingsCloner.Clone(before);
        apply(after);
        var patch = SettingsPatch.Difference(before, after) ?? SettingsPatch.Empty;
        return new SettingsProfile { Name = name, Condition = condition, Download = patch };
    }

    private static void AssertNeutral(FileConditions conditions)
    {
        Assert.AreEqual(0, conditions.Formats.Length);
        Assert.IsNull(conditions.LengthTolerance);
        Assert.IsNull(conditions.MinBitrate);
        Assert.IsNull(conditions.MaxBitrate);
        Assert.IsNull(conditions.MinSampleRate);
        Assert.IsNull(conditions.MaxSampleRate);
        Assert.IsNull(conditions.MinBitDepth);
        Assert.IsNull(conditions.MaxBitDepth);
        Assert.IsFalse(conditions.StrictTitle);
        Assert.IsFalse(conditions.StrictArtist);
        Assert.IsFalse(conditions.StrictAlbum);
    }

    private static SearchProjectionInput Input(
        long sequence,
        string filename,
        int? bitrate)
        => new(
            sequence,
            checked((int)sequence),
            "peer",
            1,
            filename,
            100,
            bitrate,
            null,
            null,
            null,
            Path.GetExtension(filename).TrimStart('.'),
            1000,
            true,
            null,
            DateTimeOffset.UnixEpoch);
}
