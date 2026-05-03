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
    }
}
