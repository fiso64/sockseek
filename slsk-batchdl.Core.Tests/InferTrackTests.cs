using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;

namespace Tests.InferTrackTests
{
    [TestClass]
    public class InferTrackTests
    {
        private SongQuery DefaultQuery(string artist = "DefaultArtist", string title = "DefaultTitle", string album = "DefaultAlbum")
        {
            return new SongQuery { Artist = artist, Title = title, Album = album };
        }

        // --- Single-part filenames (no " - " delimiter) ---

        [TestMethod]
        public void InferTrack_SinglePart_SetsTitleOnly()
        {
            var result = Searcher.InferSongQuery("Music\\SomeFolder\\MySong.mp3", DefaultQuery());
            Assert.AreEqual("MySong", result.Title);
        }

        [TestMethod]
        public void InferTrack_SinglePartWithRemix_SetsArtistMaybeWrong()
        {
            var def = DefaultQuery(artist: "DJ Cool");
            var result = Searcher.InferSongQuery("Music\\Folder\\Song (DJ Cool Remix).mp3", def);
            Assert.IsTrue(result.ArtistMaybeWrong);
        }

        // --- Two-part filenames ---

        [TestMethod]
        public void InferTrack_TwoParts_SetsArtistAndTitle()
        {
            var result = Searcher.InferSongQuery("Music\\Folder\\SomeArtist - SomeTitle.mp3", DefaultQuery());
            Assert.AreEqual("SomeArtist", result.Artist);
            Assert.AreEqual("SomeTitle", result.Title);
        }

        [TestMethod]
        public void InferTrack_TwoParts_MatchingDefaults_NoArtistMaybeWrong()
        {
            var def = DefaultQuery(artist: "TheArtist", title: "TheTitle");
            var result = Searcher.InferSongQuery("Music\\Folder\\TheArtist - TheTitle.mp3", def);
            Assert.IsFalse(result.ArtistMaybeWrong);
        }

        [TestMethod]
        public void InferTrack_TwoParts_NonMatchingDefaults_SetsArtistMaybeWrong()
        {
            var def = DefaultQuery(artist: "ExpectedArtist", title: "ExpectedTitle");
            var result = Searcher.InferSongQuery("Music\\Folder\\DifferentArtist - DifferentTitle.mp3", def);
            Assert.IsTrue(result.ArtistMaybeWrong);
        }

        // --- Three-part filenames ---

        [TestMethod]
        public void InferTrack_ThreeParts_ArtistAlbumTitle()
        {
            var def = DefaultQuery(artist: "MyArtist", title: "MyTitle", album: "MyAlbum");
            var result = Searcher.InferSongQuery("Music\\Folder\\MyArtist - MyAlbum - MyTitle.mp3", def);
            Assert.AreEqual("MyTitle", result.Title);
            Assert.AreEqual("MyArtist", result.Artist);
        }

        [TestMethod]
        public void InferTrack_ThreeParts_ArtistUnknown_SetsMaybeWrong()
        {
            var def = DefaultQuery(artist: "Unknown", title: "MyTitle");
            var result = Searcher.InferSongQuery("Music\\Folder\\PartA - PartB - MyTitle.mp3", def);
            Assert.AreEqual("MyTitle", result.Title);
            Assert.IsTrue(result.ArtistMaybeWrong);
        }

        [TestMethod]
        public void InferTrack_ThreeParts_AlbumDetected_CorrectPosition()
        {
            var def = DefaultQuery(artist: "TheArtist", title: "TheTitle", album: "TheAlbum");
            var result = Searcher.InferSongQuery("Music\\Folder\\TheArtist - TheAlbum - TheTitle.mp3", def);
            Assert.AreEqual("TheArtist", result.Artist);
            Assert.AreEqual("TheTitle", result.Title);
        }

        // --- Four+ part filenames ---

        [TestMethod]
        public void InferTrack_FourParts_FindsArtistAndTitle()
        {
            var def = DefaultQuery(artist: "ArtistX", title: "TitleX");
            var result = Searcher.InferSongQuery("Music\\Folder\\ArtistX - Foo - Bar - TitleX.mp3", def);
            Assert.AreEqual("ArtistX", result.Artist);
            Assert.AreEqual("TitleX", result.Title);
        }

        [TestMethod]
        public void InferTrack_FourParts_NoMatch_UsesFallback()
        {
            var def = DefaultQuery(artist: "Nobody", title: "Nothing");
            var result = Searcher.InferSongQuery("Music\\Folder\\A - B - C - D.mp3", def);
            // When no match, title should be set (not empty)
            Assert.IsTrue(result.Title.Length > 0);
        }

        // --- Track number removal ---

        [TestMethod]
        public void InferTrack_TrackNumAtStart_Removed()
        {
            var def = DefaultQuery(artist: "Artist", title: "Title");
            var result = Searcher.InferSongQuery("Music\\Folder\\01. Artist - Title.mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_TrackNumAtStart_ThreeDigit_Removed()
        {
            var def = DefaultQuery(artist: "Artist", title: "Title");
            var result = Searcher.InferSongQuery("Music\\Folder\\101 Artist - Title.mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_TrackNumMiddle_Removed()
        {
            var def = DefaultQuery(artist: "Artist", title: "Title");
            var result = Searcher.InferSongQuery("Music\\Folder\\Artist - 01 Title.mp3", def);
            Assert.AreEqual("Title", result.Title);
        }

        // --- Special format (NN) [Artist] Title ---

        [TestMethod]
        public void InferTrack_SpecialFormat_NoClosingBracket_FallsThrough()
        {
            var result = Searcher.InferSongQuery("Music\\Folder\\(01) [NoClosing.mp3", DefaultQuery());
            // Should not crash, falls through to normal parsing
            Assert.IsTrue(result.Title.Length > 0);
        }

        // --- Edge cases ---

        [TestMethod]
        public void InferTrack_EmptyFilename_ReturnsDefault()
        {
            var def = DefaultQuery();
            var result = Searcher.InferSongQuery(".mp3", def);
            // Should handle gracefully
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void InferTrack_UnderscoresReplacedWithSpaces()
        {
            var result = Searcher.InferSongQuery("Music\\Folder\\Some_Artist - Some_Title.mp3", DefaultQuery());
            Assert.AreEqual("Some Artist", result.Artist);
            Assert.AreEqual("Some Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_EmDashReplacedWithHyphen()
        {
            var result = Searcher.InferSongQuery("Music\\Folder\\Artist — Title.mp3", DefaultQuery());
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_FeatRemoved_FromResult()
        {
            var result = Searcher.InferSongQuery("Music\\Folder\\Artist feat. Other - Title ft. Someone.mp3", DefaultQuery());
            Assert.IsFalse(result.Title.Contains("ft."));
            Assert.IsFalse(result.Artist.Contains("feat."));
        }

        // --- Permutation fallback ---

        [TestMethod]
        public void InferTrack_WrongOrder_PermutationFixesIt()
        {
            var def = DefaultQuery(artist: "RealArtist", title: "RealTitle");
            // Two parts: "RealTitle - RealArtist" — parts[0] contains title, parts[1] contains artist
            var result = Searcher.InferSongQuery("Music\\Folder\\RealTitle - RealArtist.mp3", def);
            // The permutation logic should detect the swap and fix it
            Assert.AreEqual("RealArtist", result.Artist);
            Assert.AreEqual("RealTitle", result.Title);
        }

        [TestMethod]
        public void InferTrack_TwoPartWithTrackNumber_ParsesCorrectly()
        {
            var def = DefaultQuery(artist: "Pink Floyd", title: "Comfortably Numb");
            var result = Searcher.InferSongQuery("Music\\Folder\\06 Pink Floyd - Comfortably Numb.mp3", def);
            Assert.AreEqual("Pink Floyd", result.Artist);
            Assert.AreEqual("Comfortably Numb", result.Title);
            Assert.IsFalse(result.ArtistMaybeWrong);
        }

        [TestMethod]
        public void InferTrack_DiscTrackNumber_Removed()
        {
            var def = DefaultQuery(artist: "Artist", title: "Song");
            var result = Searcher.InferSongQuery("Music\\Folder\\1-01 Artist - Song.mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Song", result.Title);
        }

        // --- Multi-share grouping edge cases ---

        [TestMethod]
        public void InferTrack_RemixDetected_SetsMaybeWrong()
        {
            var def = DefaultQuery(artist: "Daft Punk", title: "One More Time");
            // Filename has both "Daft Punk" and "One More Time", but is a remix.
            var result = Searcher.InferSongQuery(" (Daft Punk - One More Time) Robin Schulz Remix.mp3", def);
            Assert.IsTrue(result.ArtistMaybeWrong, "Remix should be flagged as ArtistMaybeWrong.");
        }

        [TestMethod]
        public void InferTrack_SwapArtistsTitle_DetectsAndFixes()
        {
            var def = DefaultQuery(artist: "ELO", title: "Mr Blue Sky");
            var result = Searcher.InferSongQuery("Mr Blue Sky - ELO.mp3", def);
            Assert.AreEqual("ELO", result.Artist);
            Assert.AreEqual("Mr Blue Sky", result.Title);
        }

        [TestMethod]
        public void InferTrack_FtInVariousPositions_Removed()
        {
            var def = DefaultQuery(artist: "Artist", title: "Title");
            var result = Searcher.InferSongQuery("Artist feat. X - Title (ft. Y).mp3", def);
            Assert.AreEqual("Artist", result.Artist);
            Assert.AreEqual("Title", result.Title);
        }

        [TestMethod]
        public void InferTrack_ComplexBrackets_StrippedFromTitle()
        {
            var def = DefaultQuery(artist: "Artist", title: "Title");
            var result = Searcher.InferSongQuery("Artist - Title [Remaster] (HQ).mp3", def);
            // Internal title should at least contain the base title
            Assert.IsTrue(result.Title.Contains("Title"));
        }
    }
}
