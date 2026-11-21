using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Tests.Core;

[TestClass]
[DoNotParallelize]
public class DisplayIdTests
{
    [TestMethod]
    public void AlbumSearchProjection_DoesNotConsumeDisplayIds()
    {
        var before = new SongJob(new SongQuery { Artist = "Artist", Title = "Before" });
        var beforeId = before.EnsureDisplayId();

        var files = Enumerable.Range(1, 500)
            .Select(i => TestHelpers.CreateSlFile($@"Artist\Album\{i:D3}. Artist - Track {i:D3}.flac", length: 180 + i))
            .ToList();
        var response = new SearchResponse("user", 1, true, 100, 0, files);
        var rawResults = files.Select(file => (response, file)).ToList();

        var folders = SearchResultProjector.AlbumFolders(
            rawResults,
            new AlbumQuery { Artist = "Artist", Album = "Album" },
            TestHelpers.CreateDefaultSettings().Download.Search);

        Assert.IsTrue(folders.Sum(folder => folder.Files.Count) >= 500, "The projection should materialize many album file candidates.");

        var after = new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "After" });
        var afterId = after.EnsureDisplayId();

        Assert.AreEqual(
            beforeId + 1,
            afterId,
            "Projected album search-result files are not executable jobs and must not consume user-facing display IDs.");
    }
}
