using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;

namespace Tests.TrackParserTest
{
    [TestClass]
    public class TrackTemplateParserTests
    {
        // --- Tests for CreateFromString ---

        [TestMethod]
        public void CreateFromString_Basic_Success()
        {
            string input = "Artist Name - Track Title";
            string template = "{artist} - {title}";
            SongQuery? track = TrackTemplateParser.CreateFromString(input, template);

            Assert.IsNotNull(track);
            Assert.AreEqual("Artist Name", track.Artist);
            Assert.AreEqual("Track Title", track.Title);
            Assert.IsTrue(string.IsNullOrEmpty(track.Album));
        }

        [TestMethod]
        public void CreateFromString_ComplexScenario_Success()
        {
            string input = "  {The Title} by Artist {Name} on [Album Name]  ";
            string template = "{{TITLE}} by {Artist} on [{album}]";
            SongQuery? track = TrackTemplateParser.CreateFromString(input, template);

            Assert.IsNotNull(track);
            Assert.AreEqual("Artist {Name}", track.Artist);
            Assert.AreEqual("Album Name", track.Album);
            Assert.AreEqual("The Title", track.Title);
        }

        [TestMethod]
        public void CreateFromString_NoMatch_ReturnsNull()
        {
            string template = "{artist} - {title}";

            SongQuery? track1 = TrackTemplateParser.CreateFromString("Just some random text", template);
            Assert.IsNull(track1, "Should return null for completely mismatched input.");

            SongQuery? track3 = TrackTemplateParser.CreateFromString("Artist Name / Title", template);
            Assert.IsNull(track3, "Should return null for mismatched literal characters.");
        }

        [TestMethod]
        public void CreateFromString_TemplateWithUnknownField_IgnoresUnknownAndPopulatesKnown()
        {
            string input = "Valid Artist - Valid Title [Some Extra Data]";
            string template = "{artist} - {title} [{unknown_field}]";
            SongQuery? track = TrackTemplateParser.CreateFromString(input, template);

            Assert.IsNotNull(track);
            Assert.AreEqual("Valid Artist", track.Artist);
            Assert.AreEqual("Valid Title", track.Title);
            Assert.IsTrue(string.IsNullOrEmpty(track.Album));
        }


        // --- Tests for TryUpdateSongQuery ---

        [TestMethod]
        public void TryUpdateSongQuery_BasicUpdate_Success()
        {
            string input = "New Artist Name";
            string template = "{artist}";
            var query = new SongQuery { Artist = "Old Artist", Title = "Old Title", Album = "Old Album" };

            bool result = TrackTemplateParser.TryUpdateSongQuery(input, template, ref query);

            Assert.IsTrue(result);
            Assert.AreEqual("New Artist Name", query.Artist);
            Assert.AreEqual("Old Title", query.Title);
            Assert.AreEqual("Old Album", query.Album);
        }

        [TestMethod]
        public void TryUpdateSongQuery_NoMatch_ReturnsFalseAndDoesNotUpdate()
        {
            string input = "This does not match the pattern";
            string template = "{artist} / {title}";
            var query = new SongQuery { Artist = "Original Artist", Title = "Original Title", Album = "Original Album" };
            string originalArtist = query.Artist;
            string originalTitle  = query.Title;
            string originalAlbum  = query.Album;

            bool result = TrackTemplateParser.TryUpdateSongQuery(input, template, ref query);

            Assert.IsFalse(result);
            Assert.AreEqual(originalArtist, query.Artist, "Artist should not change on failed update.");
            Assert.AreEqual(originalTitle,  query.Title,  "Title should not change on failed update.");
            Assert.AreEqual(originalAlbum,  query.Album,  "Album should not change on failed update.");
        }
    }
}
