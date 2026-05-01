using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Soulseek;

namespace Tests.FileConditionsTests
{
    [TestClass]
    public class BoundCheckTests
    {
        [TestMethod]
        public void BothMinAndMaxNull_ReturnsTrue()
        {
            var fc = new FileConditions();
            Assert.IsTrue(fc.BoundCheck(100, null, null));
        }

        [TestMethod]
        public void ValueNull_AcceptMissingPropsNull_ReturnsTrue()
        {
            var fc = new FileConditions { AcceptMissingProps = null };
            Assert.IsTrue(fc.BoundCheck(null, 0, 100));
        }

        [TestMethod]
        public void ValueNull_AcceptMissingPropsTrue_ReturnsTrue()
        {
            var fc = new FileConditions { AcceptMissingProps = true };
            Assert.IsTrue(fc.BoundCheck(null, 0, 100));
        }

        [TestMethod]
        public void ValueNull_AcceptMissingPropsFalse_ReturnsFalse()
        {
            var fc = new FileConditions { AcceptMissingProps = false };
            Assert.IsFalse(fc.BoundCheck(null, 0, 100));
        }

        [TestMethod]
        public void ValueBelowMin_ReturnsFalse()
        {
            var fc = new FileConditions();
            Assert.IsFalse(fc.BoundCheck(5, 10, 100));
        }

        [TestMethod]
        public void ValueAboveMax_ReturnsFalse()
        {
            var fc = new FileConditions();
            Assert.IsFalse(fc.BoundCheck(150, 10, 100));
        }

        [TestMethod]
        public void ValueInRange_ReturnsTrue()
        {
            var fc = new FileConditions();
            Assert.IsTrue(fc.BoundCheck(50, 10, 100));
        }

        [TestMethod]
        public void ValueAtExactMin_ReturnsTrue()
        {
            var fc = new FileConditions();
            Assert.IsTrue(fc.BoundCheck(10, 10, 100));
        }

        [TestMethod]
        public void ValueAtExactMax_ReturnsTrue()
        {
            var fc = new FileConditions();
            Assert.IsTrue(fc.BoundCheck(100, 10, 100));
        }
    }

    [TestClass]
    public class BitrateSatisfiesTests
    {
        [TestMethod]
        public void NoBounds_ReturnsTrue()
        {
            var fc = new FileConditions();
            Assert.IsTrue(fc.BitrateSatisfies((int?)320));
        }

        [TestMethod]
        public void BelowMin_ReturnsFalse()
        {
            var fc = new FileConditions { MinBitrate = 200 };
            Assert.IsFalse(fc.BitrateSatisfies((int?)128));
        }

        [TestMethod]
        public void AboveMax_ReturnsFalse()
        {
            var fc = new FileConditions { MaxBitrate = 256 };
            Assert.IsFalse(fc.BitrateSatisfies((int?)320));
        }

        [TestMethod]
        public void InRange_ReturnsTrue()
        {
            var fc = new FileConditions { MinBitrate = 128, MaxBitrate = 320 };
            Assert.IsTrue(fc.BitrateSatisfies((int?)256));
        }

        [TestMethod]
        public void NullBitrate_AcceptMissingPropsTrue_ReturnsTrue()
        {
            var fc = new FileConditions { MinBitrate = 128, AcceptMissingProps = true };
            Assert.IsTrue(fc.BitrateSatisfies((int?)null));
        }

        [TestMethod]
        public void NullBitrate_AcceptMissingPropsNull_ReturnsTrue()
        {
            // Some Soulseek clients do not broadcast bitrate. Non-strict conditions
            // should not reject those peers solely because the metadata is absent.
            var fc = new FileConditions { MinBitrate = 128, AcceptMissingProps = null };
            Assert.IsTrue(fc.BitrateSatisfies((int?)null));
        }

        [TestMethod]
        public void NullBitrate_AcceptMissingPropsFalse_ReturnsFalse()
        {
            var fc = new FileConditions { MinBitrate = 128, AcceptMissingProps = false };
            Assert.IsFalse(fc.BitrateSatisfies((int?)null));
        }
    }

    [TestClass]
    public class SampleRateSatisfiesTests
    {
        [TestMethod]
        public void NullSampleRate_AcceptMissingPropsNull_ReturnsTrue()
        {
            // Like bitrate, sample rate availability depends on the peer's Soulseek client.
            // Non-strict conditions accept missing metadata; strict mode flips AcceptMissingProps.
            var fc = new FileConditions { MinSampleRate = 44100, AcceptMissingProps = null };
            Assert.IsTrue(fc.SampleRateSatisfies((int?)null));
        }

        [TestMethod]
        public void NullSampleRate_AcceptMissingPropsFalse_ReturnsFalse()
        {
            var fc = new FileConditions { MinSampleRate = 44100, AcceptMissingProps = false };
            Assert.IsFalse(fc.SampleRateSatisfies((int?)null));
        }
    }

    [TestClass]
    public class LengthToleranceSatisfiesTests
    {
        [TestMethod]
        public void NullLengthTolerance_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = null };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)100, 200));
        }

        [TestMethod]
        public void NegativeLengthTolerance_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = -1 };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)100, 200));
        }

        [TestMethod]
        public void NegativeWantedLength_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = 3 };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)100, -1));
        }

        [TestMethod]
        public void NullLength_AcceptNoLengthTrue_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = 3, AcceptNoLength = true };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)null, 100));
        }

        [TestMethod]
        public void NullLength_AcceptNoLengthFalse_ReturnsFalse()
        {
            var fc = new FileConditions { LengthTolerance = 3, AcceptNoLength = false };
            Assert.IsFalse(fc.LengthToleranceSatisfies((int?)null, 100));
        }

        [TestMethod]
        public void NullLength_AcceptNoLengthNull_ReturnsTrue()
        {
            // AcceptNoLength == null => `AcceptNoLength == null || AcceptNoLength.Value` short-circuits to true
            var fc = new FileConditions { LengthTolerance = 3, AcceptNoLength = null };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)null, 100));
        }

        [TestMethod]
        public void WithinTolerance_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = 3 };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)102, 100));
        }

        [TestMethod]
        public void OutOfTolerance_ReturnsFalse()
        {
            var fc = new FileConditions { LengthTolerance = 3 };
            Assert.IsFalse(fc.LengthToleranceSatisfies((int?)110, 100));
        }

        [TestMethod]
        public void ExactBoundary_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = 5 };
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)105, 100));
            Assert.IsTrue(fc.LengthToleranceSatisfies((int?)95, 100));
        }
    }

    [TestClass]
    public class FormatSatisfiesTests
    {
        [TestMethod]
        public void NullFormats_ReturnsTrue()
        {
            var fc = new FileConditions { Formats = null };
            Assert.IsTrue(fc.FormatSatisfies("song.mp3"));
        }

        [TestMethod]
        public void EmptyFormats_ReturnsTrue()
        {
            var fc = new FileConditions { Formats = Array.Empty<string>() };
            Assert.IsTrue(fc.FormatSatisfies("song.mp3"));
        }

        [TestMethod]
        public void MatchingFormat_ReturnsTrue()
        {
            var fc = new FileConditions { Formats = new[] { "mp3", "flac" } };
            Assert.IsTrue(fc.FormatSatisfies("song.flac"));
        }

        [TestMethod]
        public void NonMatchingFormat_ReturnsFalse()
        {
            var fc = new FileConditions { Formats = new[] { "mp3", "flac" } };
            Assert.IsFalse(fc.FormatSatisfies("song.ogg"));
        }

        [TestMethod]
        public void NoExtension_ReturnsFalse()
        {
            var fc = new FileConditions { Formats = new[] { "mp3" } };
            Assert.IsFalse(fc.FormatSatisfies("song"));
        }
    }

    [TestClass]
    public class StrictSatisfiesTests
    {
        [TestMethod]
        public void StrictTitleNull_ReturnsTrue()
        {
            var fc = new FileConditions { StrictTitle = null };
            Assert.IsTrue(fc.StrictTitleSatisfies("some file.mp3", "blah"));
        }

        [TestMethod]
        public void StrictTitleFalse_ReturnsTrue()
        {
            var fc = new FileConditions { StrictTitle = false };
            Assert.IsTrue(fc.StrictTitleSatisfies("some file.mp3", "blah"));
        }

        [TestMethod]
        public void StrictTitleTrue_EmptyTrackName_ReturnsTrue()
        {
            var fc = new FileConditions { StrictTitle = true };
            Assert.IsTrue(fc.StrictTitleSatisfies("some file.mp3", ""));
        }

        [TestMethod]
        public void StrictTitleTrue_MatchInFilename_ReturnsTrue()
        {
            var fc = new FileConditions { StrictTitle = true };
            Assert.IsTrue(fc.StrictTitleSatisfies("Music\\Artist\\Album\\01 - My Song.mp3", "My Song"));
        }

        [TestMethod]
        public void StrictTitleTrue_NoMatch_ReturnsFalse()
        {
            var fc = new FileConditions { StrictTitle = true };
            Assert.IsFalse(fc.StrictTitleSatisfies("Music\\Artist\\Album\\01 - Other Song.mp3", "My Song"));
        }

        [TestMethod]
        public void StrictArtistTrue_MatchInPath_ReturnsTrue()
        {
            var fc = new FileConditions { StrictArtist = true };
            Assert.IsTrue(fc.StrictArtistSatisfies("Music\\Cool Artist\\Album\\01 - Song.mp3", "Cool Artist"));
        }

        [TestMethod]
        public void StrictAlbumTrue_MatchInDirectory_ReturnsTrue()
        {
            var fc = new FileConditions { StrictAlbum = true };
            Assert.IsTrue(fc.StrictAlbumSatisfies("Music\\Artist\\Great Album\\01 - Song.mp3", "Great Album"));
        }

        [TestMethod]
        public void StrictAlbumTrue_NoMatch_ReturnsFalse()
        {
            var fc = new FileConditions { StrictAlbum = true };
            Assert.IsFalse(fc.StrictAlbumSatisfies("Music\\Artist\\Other Album\\01 - Song.mp3", "Great Album"));
        }

        [TestMethod]
        public void StrictStringPreprocess_ReplacesInvalidCharsAndCollapsesSpaces()
        {
            string input = "  A__B:C|D?E<F>G*H\"I/J\\K  Ä  ";

            string result = FileConditions.StrictStringPreprocess(input);

            Assert.AreEqual("A B C D E F G H I/J\\K A", result);
        }
    }

    [TestClass]
    public class BannedUsersSatisfiesTests
    {
        [TestMethod]
        public void NullResponse_ReturnsTrue()
        {
            var fc = new FileConditions { BannedUsers = new[] { "baduser" } };
            Assert.IsTrue(fc.BannedUsersSatisfies(null));
        }

        [TestMethod]
        public void NullBannedUsers_ReturnsTrue()
        {
            var fc = new FileConditions { BannedUsers = null };
            var response = new SearchResponse("someuser", 1, true, 100, 0, new List<Soulseek.File>());
            Assert.IsTrue(fc.BannedUsersSatisfies(response));
        }

        [TestMethod]
        public void UserBanned_ReturnsFalse()
        {
            var fc = new FileConditions { BannedUsers = new[] { "baduser" } };
            var response = new SearchResponse("baduser", 1, true, 100, 0, new List<Soulseek.File>());
            Assert.IsFalse(fc.BannedUsersSatisfies(response));
        }

        [TestMethod]
        public void UserNotBanned_ReturnsTrue()
        {
            var fc = new FileConditions { BannedUsers = new[] { "baduser" } };
            var response = new SearchResponse("gooduser", 1, true, 100, 0, new List<Soulseek.File>());
            Assert.IsTrue(fc.BannedUsersSatisfies(response));
        }
    }

    [TestClass]
    public class BracketCheckTests
    {
        [TestMethod]
        public void TrackHasBracket_ReturnsTrue()
        {
            var track = TestHelpers.CreateQuery(title: "Song (Remix)");
            var other = TestHelpers.CreateQuery(title: "Song (Remix)");
            Assert.IsTrue(FileConditions.BracketCheck(track, other));
        }

        [TestMethod]
        public void OtherHasNoBracket_ReturnsTrue()
        {
            var track = TestHelpers.CreateQuery(title: "Song");
            var other = TestHelpers.CreateQuery(title: "Song");
            Assert.IsTrue(FileConditions.BracketCheck(track, other));
        }

        [TestMethod]
        public void OtherHasBracket_TrackDoesNot_ReturnsFalse()
        {
            var track = TestHelpers.CreateQuery(title: "Song");
            var other = TestHelpers.CreateQuery(title: "Song (Remix)");
            Assert.IsFalse(FileConditions.BracketCheck(track, other));
        }
    }

    [TestClass]
    public class FileSatisfiesTests
    {
        [TestMethod]
        public void AllConditionsMet_ReturnsTrue()
        {
            var fc = new FileConditions
            {
                MinBitrate = 128,
                MaxBitrate = 320,
                LengthTolerance = 3,
                Formats = new[] { "mp3" },
                StrictTitle = true,
                StrictArtist = true,
            };
            var file = TestHelpers.CreateSlFile(
                "Music\\Cool Artist\\Album\\01 - My Song.mp3",
                bitrate: 256,
                length: 200);
            var track = TestHelpers.CreateQuery(artist: "Cool Artist", title: "My Song", length: 201);

            Assert.IsTrue(fc.FileSatisfies(file, track, null));
        }

        [TestMethod]
        public void OneConditionFails_ReturnsFalse()
        {
            var fc = new FileConditions
            {
                MinBitrate = 256,
                LengthTolerance = 3,
                Formats = new[] { "mp3" },
            };
            var file = TestHelpers.CreateSlFile(
                "Music\\Artist\\Album\\Song.mp3",
                bitrate: 128,
                length: 200);
            var track = TestHelpers.CreateQuery(length: 200);

            Assert.IsFalse(fc.FileSatisfies(file, track, null));
        }
    }

    [TestClass]
    public class WithAddConditionsCopyTests
    {
        [TestMethod]
        public void With_MergesCorrectly()
        {
            var fc = new FileConditions { MinBitrate = 128, LengthTolerance = 3 };
            var mod = new FileConditions { MinBitrate = 256, Formats = new[] { "flac" } };

            var result = fc.With(mod);

            Assert.AreEqual(256, result.MinBitrate);
            Assert.AreEqual(3, result.LengthTolerance);
            Assert.IsNotNull(result.Formats);
            CollectionAssert.AreEqual(new[] { "flac" }, result.Formats);
            // Original unchanged
            Assert.AreEqual(128, fc.MinBitrate);
            Assert.IsNull(fc.Formats);
        }

        [TestMethod]
        public void CopyConstructor_CreatesIndependentCopy()
        {
            var original = new FileConditions
            {
                MinBitrate = 128,
                Formats = new[] { "mp3", "flac" },
                BannedUsers = new[] { "user1" },
                StrictTitle = true,
            };
            var copy = new FileConditions(original);

            Assert.AreEqual(original.MinBitrate, copy.MinBitrate);
            Assert.AreEqual(original.StrictTitle, copy.StrictTitle);
            CollectionAssert.AreEqual(original.Formats, copy.Formats);

            // Modifying copy does not affect original
            copy.MinBitrate = 320;
            Assert.IsNotNull(copy.Formats);
            copy.Formats[0] = "ogg";
            Assert.AreEqual(128, original.MinBitrate);
            Assert.IsNotNull(original.Formats);
            Assert.AreEqual("mp3", original.Formats[0]);
        }

        [TestMethod]
        public void AddConditions_ReturnsUndoModifier()
        {
            var fc = new FileConditions { MinBitrate = 128, MaxBitrate = 320 };
            var mod = new FileConditions { MinBitrate = 256 };

            var undo = fc.AddConditions(mod);

            // fc was modified in place
            Assert.AreEqual(256, fc.MinBitrate);
            Assert.AreEqual(320, fc.MaxBitrate);

            // undo holds the old value
            Assert.AreEqual(128, undo.MinBitrate);
            Assert.IsNull(undo.MaxBitrate); // MaxBitrate was not in mod, so not in undo

            // Apply undo to restore
            fc.AddConditions(undo);
            Assert.AreEqual(128, fc.MinBitrate);
        }
    }
}
