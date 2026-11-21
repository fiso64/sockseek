using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;

namespace Tests.Cli;

[TestClass]
public sealed class CliSearchViewParityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LocalResultRendererConsumesTheSharedKernelIncludingLockedRows(bool albumMode)
    {
        var query = new SongQuery { Title = "Track" };
        var job = albumMode
            ? new SearchJob(new AlbumQuery { Album = "Album" })
            : new SearchJob(query);
        DownloadSettings settings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Music);
        job.Config = settings;
        job.Session.AddResponse(new SearchResponse(
            "exact-peer",
            token: 1,
            hasFreeUploadSlot: false,
            uploadSpeed: 12_345,
            queueLength: 7,
            fileList: [File(@"Music\Album\Track public.flac")],
            lockedFileList: [File(@"Private\Album\Track locked.flac")]));
        job.Session.Complete();
        using var output = new StringWriter();
        using (Printing.RedirectOutput(output))
        {
            Printing.PrintResults(
                job,
                PrintOption.Json | PrintOption.Full,
                settings.Search,
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        string json = output.ToString();
        StringAssert.Contains(json, "Track public.flac");
        StringAssert.Contains(json, "Track locked.flac");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(2, document.RootElement.GetArrayLength());
        var fileRows = albumMode
            ? document.RootElement.EnumerateArray().SelectMany(folder => folder.GetProperty("Files").EnumerateArray())
            : document.RootElement.EnumerateArray().Select(result => result.GetProperty("File"));
        Assert.AreEqual(1, fileRows.Count(file => file.TryGetProperty("Visibility", out var visibility)
            && visibility.GetString() == "Locked"));
        output.GetStringBuilder().Clear();
        using (Printing.RedirectOutput(output))
            Printing.PrintResults(job, PrintOption.Results | PrintOption.Full, settings.Search);
        string rendered = output.ToString();
        Assert.AreEqual(1, rendered.Split("[locked]", StringSplitOptions.None).Length - 1, rendered);
        Assert.IsFalse(rendered.Contains("conditions:unavailable", StringComparison.Ordinal), rendered);
    }

    [TestMethod]
    [DataRow("song")]
    [DataRow("album")]
    [DataRow("song-groups")]
    [DataRow("album-groups")]
    public void ResultOutputUsesRetainedFactsAndPublicLinkTargets(string mode)
    {
        var query = new SongQuery { Title = "Track" };
        DownloadSettings settings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Music);
        settings.Search.PreferredCond = new FileConditions { MinBitrate = 1 };
        var candidate = new FileCandidate(
            new PeerFileTarget(
                new PeerFileIdentity("peer", @"Music\Track.flac"),
                1_000,
                "flac",
                bitRate: 900),
            new SearchPeerSnapshot("peer", 1, 12_345, true),
            projectionFacts: new SearchConditionFacts(
                NecessaryConditionsSatisfied: true,
                PreferredConditionsSatisfied: false,
                SatisfiedPreferredConditions: [],
                ConfiguredPreferredConditions: [SearchPreferenceCondition.Bitrate]));
        var song = new SongJob(query) { Candidates = [candidate] };
        var albumQuery = new AlbumQuery { Album = "Album" };
        var album = new AlbumJob(albumQuery)
        {
            Results = [new AlbumFolder("peer", @"Music", [new AlbumFile(query, candidate)])],
        };
        Job job = mode switch
        {
            "song" => song,
            "album" => album,
            "song-groups" => new SongGroupsJob(query) { Songs = [song] },
            "album-groups" => new AlbumGroupsJob(albumQuery) { Albums = [album] },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        using var output = new StringWriter();
        using (Printing.RedirectOutput(output))
        {
            Printing.PrintResults(
                job,
                PrintOption.Results | PrintOption.Full,
                settings.Search,
                new Dictionary<string, int>(StringComparer.Ordinal));
        }

        // The current settings and file would satisfy MinBitrate=1. Rendering
        // "Bitrate fails" therefore proves presentation consumed the facts
        // computed by the shared projection instead of evaluating conditions again.
        StringAssert.Contains(output.ToString(), "prf:Bitrate fails");
        output.GetStringBuilder().Clear();
        using (Printing.RedirectOutput(output))
            Printing.PrintResults(job, PrintOption.Results, settings.Search);
        Assert.IsFalse(output.ToString().Contains("(nec:", StringComparison.Ordinal),
            "Compact output should not gain full-result explanations.");
        var locked = new FileCandidate(
            new PeerFileTarget(new PeerFileIdentity("locked-peer", @"Private\Locked.flac"), 1_000, "flac"),
            new SearchPeerSnapshot("locked-peer", 1, 100_000, true),
            candidate.Evidence with { Visibility = SearchResultVisibility.Locked });
        song.Candidates!.Insert(0, locked);
        album.Results.Insert(0, new AlbumFolder("locked-peer", @"Private", [new AlbumFile(query, locked)]));
        output.GetStringBuilder().Clear();
        using (Printing.RedirectOutput(output))
            Printing.PrintResults(job, PrintOption.Link, settings.Search);
        Assert.AreEqual(mode is "album" or "album-groups"
            ? "slsk://peer/Music/" : "slsk://peer/Music/Track.flac", output.ToString().Trim());
        song.Candidates.Remove(candidate);
        album.Results.RemoveAll(folder => folder.Username == "peer");
        output.GetStringBuilder().Clear();
        using (Printing.RedirectOutput(output))
            Printing.PrintResults(job, PrintOption.Link, settings.Search);
        Assert.AreEqual("", output.ToString(), "Locked-only observations must not become download links.");
    }

    private static Soulseek.File File(string path)
        => new(1, path, 1_000, "flac", []);
}
