using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Soulseek;
using Tests.ClientTests;
using Directory = System.IO.Directory;

namespace Tests.Core;

[TestClass]
public class SearchDownloadIntegrationTests
{
    [TestMethod]
    [DataRow("song", false)]
    [DataRow("album", false)]
    [DataRow("aggregate", false)]
    [DataRow("album-aggregate", false)]
    [DataRow("song", true)]
    [DataRow("album", true)]
    [DataRow("aggregate", true)]
    [DataRow("album-aggregate", true)]
    public async Task MultiItemSearch_LibraryCleanupFailure_PreservesResultsAndDownloads(string mode, bool printOnly)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "sockseek-search-modes-" + Guid.NewGuid());
        Directory.CreateDirectory(outputDir);
        try
        {
            var files = (from artist in Enumerable.Range(1, 3)
                         from album in Enumerable.Range(1, 2)
                         from track in Enumerable.Range(1, 2)
                         select TestHelpers.CreateSlFile(
                             $@"Artist{artist}\Album{album}\Artist{artist} - Album{album} Track{track}.mp3",
                             length: 180 + album * 10 + track)).ToArray();
            var responses = new List<SearchResponse>
            {
                new("peer1", 1, true, 100_000, 0, files),
                new("peer2", 2, true, 100_000, 0, files),
            };
            var cleanupFailure = SearchTestFaults.CreateDisposalFailure();
            int searches = 0;
            var client = new MockSoulseekClient(responses)
            {
                SearchHandler = (query, callback, options, ct) =>
                {
                    Interlocked.Increment(ref searches);
                    foreach (var peer in responses)
                    {
                        ct.ThrowIfCancellationRequested();
                        var matches = peer.Files.Where(file => query.Terms.All(term =>
                            file.Filename.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
                        if (matches.Length > 0)
                            callback(new SearchResponse(peer.Username, 1, true, 100_000, 0, matches));
                    }
                    options.StateChanged?.Invoke((SearchStates.InProgress,
                        new Search(query, new SearchScope(SearchScopeType.Network), 1, SearchStates.Completed, 2, 0, 0)));
                    return Task.FromException<Search>(cleanupFailure);
                },
            };
            var engineSettings = new EngineSettings { Username = "test", Password = "test", ConcurrentJobs = 3 };
            var settings = new DownloadSettings { PrintOption = printOnly ? PrintOption.Results : PrintOption.None };
            settings.Output.ParentDir = outputDir;
            settings.Output.NameFormat = "{filename}";
            settings.Output.WriteIndex = false;
            settings.Skip.SkipExisting = false;
            settings.Search.NoBrowseFolder = true;
            var jobs = Enumerable.Range(1, 3).Select(i => CreateJob(mode, $"Artist{i}")).ToArray();
            await using var engine = new DownloadEngine(engineSettings, new SoulseekClientManager(engineSettings, client));
            engine.Enqueue(new JobList("batch", jobs), settings);
            engine.CompleteEnqueue();

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await engine.RunAsync(watchdog.Token);

            Assert.IsTrue(searches >= jobs.Length, "Every input must perform a search through the failing cleanup boundary.");
            foreach (var job in jobs)
            {
                Assert.AreEqual(JobTerminalOutcome.Succeeded, job.TerminalOutcome, $"{mode}: {job.FailureReason}");
                Assert.AreEqual(mode switch { "song" => 1, "album" => 2, _ => 4 }, ResultTrackCount(job));
            }

            var expected = files.Where(file => mode switch
            {
                "song" => file.Filename.Contains("Album1 Track1", StringComparison.Ordinal),
                "album" => file.Filename.Contains(@"\Album1\", StringComparison.Ordinal),
                _ => true,
            }).Select(file => file.Filename.Split('\\')[^1]).ToArray();
            var downloaded = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            CollectionAssert.AreEquivalent(printOnly ? Array.Empty<string>() : expected,
                downloaded.Select(Path.GetFileName).ToArray());
            Assert.AreEqual(printOnly ? 0 : expected.Length, client.DownloadCallCount);
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    private static Job CreateJob(string mode, string artist) => mode switch
    {
        "song" => new SongJob(new SongQuery { Artist = artist, Title = "Album1 Track1" }),
        "album" => new AlbumJob(new AlbumQuery { Artist = artist, Album = "Album1" }),
        "aggregate" => new AggregateJob(new SongQuery { Artist = artist }),
        "album-aggregate" => new AlbumAggregateJob(new AlbumQuery { Artist = artist }),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static int ResultTrackCount(Job job) => job switch
    {
        SongJob song => song.Candidates?.Count > 0 ? 1 : 0,
        AlbumJob album => album.Results.First().Files.Count,
        AggregateJob aggregate => aggregate.Songs.Count,
        AlbumAggregateJob aggregate => aggregate.Albums.Sum(album => album.Results.First().Files.Count),
        _ => throw new ArgumentOutOfRangeException(nameof(job)),
    };
}
