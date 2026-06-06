using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.StringUtils
{
    [TestClass]
    public class StringUtilsTests
    {
        [TestMethod]
        public void RemoveFt_RemovesFeaturingArtists()
        {
            Assert.AreEqual("blah blah", "blah blah ft. blah blah".RemoveFt());
            Assert.AreEqual("blah blah", "blah blah feat. blah blah".RemoveFt());
            Assert.AreEqual("blah blah", "blah (feat. blah blah) blah".RemoveFt());
            Assert.AreEqual("blah blah", "blah (ft. blah blah) blah".RemoveFt());
            Assert.AreEqual("foo - blah", "foo feat. bar - blah".RemoveFt());
            Assert.AreEqual("foo - blah", "foo ft. bar - blah".RemoveFt());
        }

        [TestMethod]
        public void RemoveConsecutiveWs_RemovesExtraWhitespace()
        {
            Assert.AreEqual(" blah blah blah blah ", " blah    blah  blah blah ".RemoveConsecutiveWs());
        }

        [TestMethod]
        public void RemoveSquareBrackets_RemovesBracketsAndContent()
        {
            Assert.AreEqual("foo  bar", "foo [aaa] bar".RemoveSquareBrackets());
        }

        [TestMethod]
        public void ReplaceInvalidChars_HandlesInvalidCharacters()
        {
            Assert.AreEqual("Invalid chars ", "Invalid chars: \\/:|?<>*\"".ReplaceInvalidChars("", true));
            Assert.AreEqual("Invalid chars \\/", "Invalid chars: \\/:|?<>*\"".ReplaceInvalidChars("", true, false));
        }

        [TestMethod]
        public void ContainsWithBoundary_ChecksWordBoundaries()
        {
            Assert.IsTrue("foo blah bar".ContainsWithBoundary("blah"));
            Assert.IsTrue("foo/blah/bar".ContainsWithBoundary("blah"));
            Assert.IsTrue("foo - blah 2".ContainsWithBoundary("blah"));
            Assert.IsFalse("foo blah bar".ContainsWithBoundaryIgnoreWs("blah"));
            Assert.IsFalse("foo - blah 2".ContainsWithBoundaryIgnoreWs("blah"));
            Assert.IsTrue("foo - blah 2 - bar".ContainsWithBoundaryIgnoreWs("blah 2"));
            Assert.IsTrue("foo/blah/bar".ContainsWithBoundaryIgnoreWs("blah"));
            Assert.IsTrue("01 blah".ContainsWithBoundaryIgnoreWs("blah", acceptLeftDigit: true));
            Assert.IsFalse("foo - blah 2blah".ContainsWithBoundaryIgnoreWs("blah", acceptLeftDigit: true));
            Assert.IsTrue("foo - blah 2 blah".ContainsWithBoundaryIgnoreWs("blah", acceptLeftDigit: true));
        }

        [TestMethod]
        public void ContainsWithBoundary_OverlappingMatches_DoesNotSkip()
        {
            Assert.IsTrue("xblah blah blah".ContainsWithBoundary("blah blah"), 
                "Failed to find overlapping match when left boundary fails.");

            Assert.IsTrue("foo foo foo y".ContainsWithBoundary("foo foo"), 
                "Failed to find overlapping match when right boundary fails.");

            Assert.IsTrue("a x - x - x".ContainsWithBoundaryIgnoreWs("x - x"),
                "IgnoreWs: Failed on overlapping left boundary.");

            Assert.IsTrue("- yx-yx-y -".ContainsWithBoundaryIgnoreWs("yx-y"),
                "IgnoreWs: Failed on overlapping right boundary.");
        }

        [TestMethod]
        public void GreatestCommonPath_FindsCommonPath()
        {
            var paths = new string[]
            {
                "/home/user/docs/nested/file",
                "/home/user/docs/nested/folder/",
                "/home/user/docs/letter.txt",
                "/home/user/docs/report.pdf",
                "/home/user/docs/",
            };
            Assert.AreEqual("/home/user/docs/", Utils.GreatestCommonPath(paths));
            Assert.AreEqual("", Utils.GreatestCommonPath(new string[] { "/path/file", "" }));
            Assert.AreEqual("/", Utils.GreatestCommonPath(new string[] { "/path/file", "/" }));
            Assert.AreEqual("/path/", Utils.GreatestCommonPath(new string[] { "/path/dir1", "/path/dir2" }));
            Assert.AreEqual("/path\\", Utils.GreatestCommonPath(new string[] { "/path\\dir1/blah", "/path/dir2\\blah" }));
            Assert.AreEqual("", Utils.GreatestCommonPath(new string[] { "dir1", "dir2" }));
        }

        [TestMethod]
        public void RemoveDiacritics_RemovesAccents()
        {
            Assert.AreEqual(" Cafe Creme a la mode U", " Café Crème à la mode Ü".RemoveDiacritics());
        }

        [TestMethod]
        public void Levenshtein_ComputesEditDistance()
        {
            Assert.AreEqual(0, Utils.Levenshtein("", ""));
            Assert.AreEqual(3, Utils.Levenshtein("", "abc"));
            Assert.AreEqual(3, Utils.Levenshtein("kitten", "sitting"));
            Assert.AreEqual(2, Utils.Levenshtein("flaw", "lawn"));
            Assert.AreEqual(Utils.Levenshtein("Saturday", "Sunday"), Utils.Levenshtein("Sunday", "Saturday"));
        }

        [TestMethod]
        public void ReplaceInvalidChars_CharOverload_HandlesSlashFlag()
        {
            Assert.AreEqual("a_b_c_d_e_f_g_h_i_j", "a:b|c?d>e<f*g\"h/i\\j".ReplaceInvalidChars('_', windows: true));
            Assert.AreEqual("a_b_c_d_e_f_g_h/i\\j", "a:b|c?d>e<f*g\"h/i\\j".ReplaceInvalidChars('_', windows: true, removeSlash: false));
        }

        [TestMethod]
        public void ReplaceInvalidChars_StringOverload_RemovesInvalidCharacters()
        {
            Assert.AreEqual("abcdefghij", "a:b|c?d>e<f*g\"h/i\\j".ReplaceInvalidChars("", windows: true));
            Assert.AreEqual("abcdefgh/i\\j", "a:b|c?d>e<f*g\"h/i\\j".ReplaceInvalidChars("", windows: true, removeSlash: false));
        }

        [TestMethod]
        public void ReplaceInvalidChars_StringOverload_ReplacesWithMultiCharString()
        {
            Assert.AreEqual("a--b--c--d", "a:b/c\\d".ReplaceInvalidChars("--", windows: true));
            Assert.AreEqual("a--b/c\\d", "a:b/c\\d".ReplaceInvalidChars("--", windows: true, removeSlash: false));
        }

        [TestMethod]
        public void ReplaceInvalidChars_NoInvalidCharacters_ReturnsOriginalString()
        {
            const string value = "abc def 123";
            Assert.AreSame(value, value.ReplaceInvalidChars('_', windows: true));
            Assert.AreSame(value, value.ReplaceInvalidChars("_", windows: true));
        }

        [TestMethod]
        public void ReplaceSpecialChars_RemovesAsciiAndUnicodeSpecialCharacters()
        {
            Assert.AreEqual("abc", "a:b/c".ReplaceSpecialChars(""));
            Assert.AreEqual("abcdef", "a–b—c―d“e”f".ReplaceSpecialChars(""));
            Assert.AreEqual("abcdef", "a【b】c「d」e《f》".ReplaceSpecialChars(""));
        }

        [TestMethod]
        public void ReplaceSpecialChars_ReplacesWithMultiCharString()
        {
            Assert.AreEqual("a--b--c", "a:b/c".ReplaceSpecialChars("--"));
        }

        [TestMethod]
        public void ExpandVariables_ConfigDirPrefix_ResolvesAgainstContext()
        {
            var result = Utils.ExpandVariables("{configdir}/lists/tracks.txt", new PathVariableContext(ConfigDir: @"C:\Sockseek\Config"));

            Assert.AreEqual(Path.Join(@"C:\Sockseek\Config", "lists", "tracks.txt"), result);
        }
    }
}
