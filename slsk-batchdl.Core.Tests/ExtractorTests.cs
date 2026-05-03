using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Sldl.Core;
using Sldl.Core.Extractors;
using Sldl.Core.Settings;

namespace Tests.Extractors
{
    [TestClass]
    public class StringExtractorTests
    {
        private StringExtractor extractor = null!;
        private DownloadSettings config = null!;
        private List<string> testStrings = null!;

        // Expected SongQuery fields in song mode: (title, artist, album, length)
        private List<(string title, string artist, string album, int length)> expectedSongs = null!;

        // Expected AlbumQuery fields in album mode: (album, artist)
        private List<(string album, string artist)> expectedAlbums = null!;

        [TestInitialize]
        public void Setup()
        {
            extractor = new StringExtractor();
            config = new DownloadSettings();

            testStrings = new List<string>
            {
                "Some Title",
                "Some, Title",
                "artist = Some artist, title = some title",
                "Artist - Title, length = 42",
                "title=Some, Title, artist=Some, Artist, album = Some, Album, length= 42",
                "Some, Artist = a - Some, Title = b, album = Some, Album, length = 42",
                "artist=artist, title=title, album=",
                "artist=, title=title, album=album",
            };

            expectedSongs = new List<(string, string, string, int)>
            {
                ("Some Title",          "",                "",              -1),
                ("Some, Title",         "",                "",              -1),
                ("some title",          "Some artist",     "",              -1),
                ("Title",               "Artist",          "",              42),
                ("Some, Title",         "Some, Artist",    "Some, Album",   42),
                ("Some, Title = b",     "Some, Artist = a","Some, Album",   42),
                ("title",               "artist",          "",              -1),
                ("title",               "",                "album",         -1),
            };

            // In album mode:
            //   - When album= is given, Album = that value (normal album search)
            //   - When only title= is given, Album = "" and Title = that value (search by song title, no folder-name filter)
            //   - When neither is given but other text is present, Album = the text
            expectedAlbums = new List<(string album, string artist)>
            {
                ("Some Title",   ""),            // bare text → Album
                ("Some, Title",  ""),            // bare text → Album
                ("",             "Some artist"), // title= only → Album stays empty (Title holds the hint)
                ("Title",        "Artist"),      // "Artist - Title" → Album = Title part
                ("Some, Album",  "Some, Artist"),// album= explicit → Album
                ("Some, Album",  ""),            // album= explicit → Album; other part ignored with warning
                ("",             "artist"),      // album="" explicit empty → Album stays ""
                ("album",        ""),            // album= explicit → Album
            };
        }

        [TestMethod]
        public async Task GetTracks_WithSongMode_ExtractsCorrectTrackInfo()
        {
            config.Extraction.IsAlbum = false;

            for (int i = 0; i < testStrings.Count; i++)
            {
                var result = await extractor.GetTracks(testStrings[i], config.Extraction);
                var song = (SongJob)result;
                var q = song.Query;

                Assert.IsTrue(StringExtractor.InputMatches(testStrings[i]));
                Assert.AreEqual(expectedSongs[i].title,  q.Title,  $"Case {i}: Title mismatch");
                Assert.AreEqual(expectedSongs[i].artist, q.Artist, $"Case {i}: Artist mismatch");
                Assert.AreEqual(expectedSongs[i].album,  q.Album,  $"Case {i}: Album mismatch");
                Assert.AreEqual(expectedSongs[i].length, q.Length, $"Case {i}: Length mismatch");
            }
        }

        [TestMethod]
        public async Task GetTracks_WithAlbumMode_ExtractsCorrectAlbumInfo()
        {
            config.Extraction.IsAlbum = true;

            for (int i = 0; i < testStrings.Count; i++)
            {
                var result = await extractor.GetTracks(testStrings[i], config.Extraction);
                var q = ((AlbumJob)result).Query;

                Assert.IsTrue(StringExtractor.InputMatches(testStrings[i]));
                Assert.AreEqual(expectedAlbums[i].album,  q.Album,  $"Case {i}: Album mismatch");
                Assert.AreEqual(expectedAlbums[i].artist, q.Artist, $"Case {i}: Artist mismatch");

                // When Album is empty, song-title hint should be in SearchHint for network search.
                if (q.Album.Length == 0 && expectedSongs[i].title.Length > 0)
                    Assert.AreEqual(expectedSongs[i].title, q.SearchHint, $"Case {i}: SearchHint mismatch (needed for search when Album is empty)");
            }
        }
    }
}
