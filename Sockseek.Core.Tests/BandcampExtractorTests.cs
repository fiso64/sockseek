using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;

namespace Tests.Core;

[TestClass]
public class BandcampExtractorTests
{
    private const string AlbumPage = """
        <div id="name-section">
          <h2 class="trackTitle">An &amp; Album</h2>
          <h3><span><a>Artist</a></span></h3>
        </div>
        <table id="track_table"><tr><td>First</td></tr><tr><td>Second</td></tr></table>
        """;

    [TestMethod]
    public async Task AlbumPage_ExtractsMetadataAndRequestedTrackCounts()
    {
        var job = Assert.IsInstanceOfType<AlbumJob>(await ExtractAsync(AlbumPage, album: true, countTracks: true));

        Assert.AreEqual("An & Album", job.Query.Album);
        Assert.AreEqual("Artist", job.Query.Artist);
        Assert.AreEqual(2, job.ExtractorFolderCond?.MinTrackCount);
        Assert.AreEqual(2, job.ExtractorFolderCond?.MaxTrackCount);
    }

    [TestMethod]
    public async Task TrackPage_ExtractsSongAlbumAndArtist()
    {
        var job = Assert.IsInstanceOfType<SongJob>(await ExtractAsync("""
            <div id="name-section">
              <h2 class="trackTitle">A Song</h2>
              <h3 class="albumTitle"><span><a>An Album</a></span><span><a>Artist</a></span></h3>
            </div>
            """, album: false));

        Assert.AreEqual("A Song", job.Query.Title);
        Assert.AreEqual("An Album", job.Query.Album);
        Assert.AreEqual("Artist", job.Query.Artist);
    }

    [TestMethod]
    [DataRow("<html></html>", "item details")]
    [DataRow("<div id='name-section'><h3><span><a>Artist</a></span></h3></div>", "title")]
    [DataRow("<div id='name-section'><h2 class='trackTitle'>Album</h2></div>", "artist")]
    public async Task IncompletePage_ReportsMissingMetadata(string html, string field)
    {
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ExtractAsync(html, album: true));

        StringAssert.Contains(error.Message, "Bandcamp");
        StringAssert.Contains(error.Message, field);
    }

    [TestMethod]
    public async Task AlbumPage_RequestedCountsWithoutTracks_ReportsMissingTrackList()
    {
        var html = AlbumPage[..AlbumPage.IndexOf("<table", StringComparison.Ordinal)];
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ExtractAsync(html, album: true, countTracks: true));

        StringAssert.Contains(error.Message, "track list");
    }

    private static async Task<Sockseek.Core.Jobs.Job> ExtractAsync(string html, bool album, bool countTracks = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "sockseek-bandcamp-" + Guid.NewGuid() + ".html");
        try
        {
            await File.WriteAllTextAsync(path, html);
            var extractor = new BandcampExtractor(new BandcampSettings { HtmlFromFile = path });
            return await extractor.GetTracks("https://artist.bandcamp.com/" + (album ? "album/test" : "track/test"),
                new ExtractionSettings { SetAlbumMinTrackCount = countTracks, SetAlbumMaxTrackCount = countTracks });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
