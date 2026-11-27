using Sockseek.Core.Services;
using Sockseek.Core.Settings;
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
        public void ValueNull_DefaultAcceptMissingProps_ReturnsTrue()
        {
            var fc = new FileConditions();
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
        public void NullBitrate_DefaultAcceptMissingProps_ReturnsTrue()
        {
            // Some Soulseek clients do not broadcast bitrate. Non-strict conditions
            // should not reject those peers solely because the metadata is absent.
            var fc = new FileConditions { MinBitrate = 128 };
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
        public void NullSampleRate_DefaultAcceptMissingProps_ReturnsTrue()
        {
            // Like bitrate, sample rate availability depends on the peer's Soulseek client.
            // Non-strict conditions accept missing metadata; strict mode flips AcceptMissingProps.
            var fc = new FileConditions { MinSampleRate = 44100 };
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
        public void NullLength_DefaultAcceptNoLength_ReturnsTrue()
        {
            var fc = new FileConditions { LengthTolerance = 3 };
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
        public void EmptyFormatsByDefault_ReturnsTrue()
        {
            var fc = new FileConditions();
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

        [TestMethod]
        public void DefaultFormats_AreNotAnActiveOverride()
        {
            var settings = new SearchSettings();

            Assert.IsFalse(settings.NecessaryCond.HasActiveFormatCondition());
            CollectionAssert.AreEqual(SearchDefaults.Formats.ToArray(), settings.NecessaryCond.Formats);
        }

        [TestMethod]
        public void ArbitraryFormat_IsAnActiveOverride()
        {
            var conditions = new FileConditions { Formats = ["pdf"] };

            Assert.IsTrue(conditions.HasActiveFormatCondition());
        }

        [TestMethod]
        public void KnownAiffClassification_DoesNotChangeDefaultRequestedFormats()
        {
            var settings = new SearchSettings();

            Assert.IsTrue(Utils.KnownAudioExtensions.Contains(".aif"));
            Assert.IsTrue(Utils.KnownAudioExtensions.Contains(".aiff"));
            Assert.IsFalse(settings.NecessaryCond.Formats.Contains("aif"));
            Assert.IsFalse(settings.NecessaryCond.Formats.Contains("aiff"));
            Assert.IsFalse(settings.NecessaryCond.HasActiveFormatCondition());
        }

        [TestMethod]
        public void FormatOnly_IsAnAlbumTrackConditionButNotAnAudioPropertyCondition()
        {
            var conditions = new FileConditions { Formats = ["flac"] };

            Assert.IsFalse(conditions.HasActiveAudioPropertyConditions());
            Assert.IsTrue(conditions.HasActiveAlbumTrackConditions());
        }

        [TestMethod]
        public void RemovingAudioProperties_RetainsFormats()
        {
            var conditions = new FileConditions
            {
                Formats = ["pdf"],
                MinBitrate = 128,
                MinSampleRate = 44_100,
                MinBitDepth = 16,
            };

            var withoutProperties = conditions.WithoutAudioPropertyConditions();
            var withoutAlbumTrackConditions = conditions.WithoutAlbumTrackConditions();

            CollectionAssert.AreEqual(new[] { "pdf" }, withoutProperties.Formats);
            Assert.IsFalse(withoutProperties.HasActiveAudioPropertyConditions());
            CollectionAssert.AreEqual(Array.Empty<string>(), withoutAlbumTrackConditions.Formats);
            Assert.IsFalse(withoutAlbumTrackConditions.HasActiveAlbumTrackConditions());
        }
    }

    [TestClass]
    public class StrictSatisfiesTests
    {
        [TestMethod]
        public void StrictTitleDefaultFalse_ReturnsTrue()
        {
            var fc = new FileConditions();
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
        [DataRow("AC/DC", @"music\AC_DC\1979 - Highway to Hell\01 - Highway to Hell.flac")]
        [DataRow("AC/DC", @"downloads\AC DC\1979 - Highway to Hell\01 Highway to Hell.flac")]
        [DataRow("Miksu / Macloud", @"Miksu _ Macloud\Nachts wach\Nachts wach.flac")]
        [DataRow(@"Axwell /\ Ingrosso", @"Axwell __ Ingrosso\Axwell __ Ingrosso - More Than You Know\13 - Dreamer.flac")]
        public void StrictArtistTrue_SlashInArtist_MatchesPeerSpelling(string artist, string path)
        {
            var fc = new FileConditions { StrictArtist = true };
            Assert.IsTrue(fc.StrictArtistSatisfies(path, artist));
        }

        [TestMethod]
        [DataRow("0 To 100 / The Catch Up", @"Drake\2014 - 0 to 100 _ The Catch Up\01 - 0 to 100 _ The Catch Up.flac")]
        [DataRow("295/Simplicity", @"Fullempty\Unknown Album\295_Simplicity.mp3")]
        [DataRow("Chabos wissen, wer der Babo ist - Swing / Jazz Version", @"Marti Fischer\Marti Fischer - Chabos wissen, wer der Babo ist - Swing Jazz Version.flac")]
        public void StrictTitleTrue_SlashInTitle_MatchesPeerSpelling(string title, string path)
        {
            var fc = new FileConditions { StrictTitle = true };
            Assert.IsTrue(fc.StrictTitleSatisfies(path, title));
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
        [DataRow(@"Music\Led Zeppelin\Led Zeppelin (1969)\01 - Good Times Bad Times.flac", true)]
        [DataRow(@"Music\Led Zeppelin\1969 - Led Zeppelin - Led Zeppelin [FLAC]\CD1\01 - Good Times Bad Times.flac", true)]
        [DataRow(@"Music\Led Zeppelin\Led Zeppelin II (1969)\01 - Whole Lotta Love.flac", false)]
        [DataRow(@"Music\Led Zeppelin\Led Zeppelin - 2\01 - Whole Lotta Love.flac", false)]
        [DataRow(@"Music\Led Zeppelin\Led Zeppelin - Houses of the Holy (1973)\01 - The Song Remains the Same.flac", false)]
        [DataRow(@"Music\Led Zeppelin\Presence\01 - Achilles Last Stand.flac", false)]
        public void StrictAlbumTrue_SelfTitled_FolderMustNameNothingElse(string path, bool expected)
        {
            var fc = new FileConditions { StrictAlbum = true };
            Assert.AreEqual(expected, fc.StrictAlbumSatisfies(path, "Led Zeppelin", "Led Zeppelin"));
            Assert.IsTrue(fc.StrictAlbumSatisfies(path, "Led Zeppelin"), "Without an artist the name is matched anywhere in the path.");
        }

        [TestMethod]
        [DataRow("01 - Iron Maiden.flac", true)]
        [DataRow("Iron Maiden - Iron Maiden - 1-08 - Iron Maiden (1998 Remaster).flac", true)]
        [DataRow("Iron Maiden - Killers - 11 - Drifter.flac", false)]
        [DataRow("Iron Maiden - Iron Maiden - 02 - Remember Tomorrow.flac", false)]
        public void StrictTitleTrue_SelfTitled_FilenameMustNameNothingElse(string filename, bool expected)
        {
            var fc = new FileConditions { StrictTitle = true };
            Assert.AreEqual(expected, fc.StrictTitleSatisfies(@"Music\Iron Maiden\Album\" + filename, "Iron Maiden", artist: "Iron Maiden"));
        }

        [TestMethod]
        public void StrictAlbumTrue_FuzzyPunctuationOnlyMatch_ReturnsFalse()
        {
            var fc = new FileConditions { StrictAlbum = true };

            Assert.IsFalse(fc.StrictAlbumSatisfies(@"Music\Diverse System\AD：PIANO X\01 - Song.flac", "AD:PIANO X"));
        }

        [TestMethod]
        public void StrictStringPreprocess_ReplacesInvalidCharsAndCollapsesSpaces()
        {
            string input = "  A__B:C|D?E<F>G*H\"I/J\\K  Ä  ";

            string result = FileConditions.StrictStringPreprocess(input);

            Assert.AreEqual("A B C D E F G H I/J\\K A", result);
            Assert.AreEqual("A B C D E F G H I J K A",
                FileConditions.StrictStringPreprocess(input, removeSlash: true));
            Assert.AreEqual("Ä", FileConditions.StrictStringPreprocess(" Ä ", diacrRemove: false));
            Assert.AreEqual("", FileConditions.StrictStringPreprocess("\t  \r\n"));
            Assert.AreEqual("A\tB", FileConditions.StrictStringPreprocess("  A\tB  "));
            Assert.AreEqual(@"Music\Artist\Album\01 - Track.flac",
                FileConditions.StrictStringPreprocess(@"Music\Artist\Album\01 - Track.flac"));
            foreach (int length in new[] { 512, 513, 2048 })
            {
                string longName = new('a', length);
                Assert.AreEqual(longName, FileConditions.StrictStringPreprocess(longName));
                Assert.AreEqual(longName, FileConditions.StrictStringPreprocess("_" + longName + "_"));
            }
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
        public void EmptyBannedUsersByDefault_ReturnsTrue()
        {
            var fc = new FileConditions();
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
    public class AllowedUsersSatisfiesTests
    {
        [TestMethod]
        public void NullResponse_ReturnsTrue()
        {
            var fc = new FileConditions { AllowedUsers = new[] { "gooduser" } };
            Assert.IsTrue(fc.AllowedUsersSatisfies(null));
        }

        [TestMethod]
        public void EmptyAllowedUsersByDefault_ReturnsTrue()
        {
            var fc = new FileConditions();
            var response = new SearchResponse("someuser", 1, true, 100, 0, new List<Soulseek.File>());
            Assert.IsTrue(fc.AllowedUsersSatisfies(response));
        }

        [TestMethod]
        public void UserAllowed_ReturnsTrue()
        {
            var fc = new FileConditions { AllowedUsers = new[] { "gooduser" } };
            var response = new SearchResponse("gooduser", 1, true, 100, 0, new List<Soulseek.File>());
            Assert.IsTrue(fc.AllowedUsersSatisfies(response));
        }

        [TestMethod]
        public void UserNotAllowed_ReturnsFalse()
        {
            var fc = new FileConditions { AllowedUsers = new[] { "gooduser" } };
            var response = new SearchResponse("baduser", 1, true, 100, 0, new List<Soulseek.File>());
            Assert.IsFalse(fc.AllowedUsersSatisfies(response));
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

            Assert.IsTrue(ConditionSatisfactionPolicy.SearchFileSatisfies(fc, null, file, track));
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

            Assert.IsFalse(ConditionSatisfactionPolicy.SearchFileSatisfies(fc, null, file, track));
        }
    }

    [TestClass]
    public class WithAddConditionsCopyTests
    {
        [TestMethod]
        public void FileConditionPatchMerge_FillsMissingFieldsWithoutOverwritingPrimary()
        {
            var primary = new FileConditionPatch { Formats = ["flac"] };
            var fallback = new FileConditionPatch { Formats = ["mp3"], StrictAlbum = true };

            var result = FileConditionPatch.Merge(primary, fallback);

            Assert.AreSame(primary, result);
            CollectionAssert.AreEqual(new[] { "flac" }, result!.Formats);
            Assert.AreEqual(true, result.StrictAlbum);
        }

        [TestMethod]
        public void FolderConditionPatchMerge_FillsMissingFieldsAndUnionsRequiredTitles()
        {
            var primary = new FolderConditionPatch();
            primary.AddRequiredTrackTitle("Existing");
            var fallback = new FolderConditionPatch { MinTrackCount = 10, MaxTrackCount = 10 };
            fallback.AddRequiredTrackTitle("Fallback");

            var result = FolderConditionPatch.Merge(primary, fallback);

            Assert.AreSame(primary, result);
            Assert.AreEqual(10, result!.MinTrackCount);
            Assert.AreEqual(10, result.MaxTrackCount);
            CollectionAssert.AreEqual(new[] { "Existing", "Fallback" }, result.RequiredTrackTitles);
        }

        [TestMethod]
        public void With_MergesCorrectly()
        {
            var fc = new FileConditions { MinBitrate = 128, LengthTolerance = 3 };
            var mod = new FileConditionPatch { MinBitrate = 256, Formats = new[] { "flac" } };

            var result = fc.With(mod);

            Assert.AreEqual(256, result.MinBitrate);
            Assert.AreEqual(3, result.LengthTolerance);
            Assert.IsNotNull(result.Formats);
            CollectionAssert.AreEqual(new[] { "flac" }, result.Formats);
            // Original unchanged
            Assert.AreEqual(128, fc.MinBitrate);
            CollectionAssert.AreEqual(Array.Empty<string>(), fc.Formats);
        }

        [TestMethod]
        public void CopyConstructor_CreatesIndependentCopy()
        {
            var original = new FileConditions
            {
                MinBitrate = 128,
                Formats = new[] { "mp3", "flac" },
                BannedUsers = new[] { "user1" },
                AllowedUsers = new[] { "user2" },
                StrictTitle = true,
            };
            var copy = new FileConditions(original);

            Assert.AreEqual(original.MinBitrate, copy.MinBitrate);
            Assert.AreEqual(original.StrictTitle, copy.StrictTitle);
            CollectionAssert.AreEqual(original.Formats, copy.Formats);
            CollectionAssert.AreEqual(original.BannedUsers, copy.BannedUsers);
            CollectionAssert.AreEqual(original.AllowedUsers, copy.AllowedUsers);

            // Modifying copy does not affect original
            copy.MinBitrate = 320;
            Assert.IsNotNull(copy.Formats);
            copy.Formats[0] = "ogg";
            copy.AllowedUsers[0] = "user3";
            Assert.AreEqual(128, original.MinBitrate);
            Assert.IsNotNull(original.Formats);
            Assert.AreEqual("mp3", original.Formats[0]);
            Assert.AreEqual("user2", original.AllowedUsers[0]);
        }

        [TestMethod]
        public void AddConditions_ChangesOnlySuppliedFields()
        {
            var fc = new FileConditions { MinBitrate = 128, MaxBitrate = 320 };
            var mod = new FileConditionPatch { MinBitrate = 256 };

            fc.AddConditions(mod);

            Assert.AreEqual(256, fc.MinBitrate);
            Assert.AreEqual(320, fc.MaxBitrate);
        }
    }
}
