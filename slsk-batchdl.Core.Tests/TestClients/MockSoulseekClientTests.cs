using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using File = Soulseek.File;

namespace Tests.ClientTests
{
    [TestClass]
    public class MockSoulseekClientTests
    {
        private List<SearchResponse> CreateTestIndex()
        {
            var index = new List<SearchResponse>();

            var user1SearchResponse = new SearchResponse(
                username: "user1",
                token: 1,
                hasFreeUploadSlot: true,
                uploadSpeed: 100,
                queueLength: 0,
                fileList: new List<File>
                {
                    new File(1, "Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3", 10000, ".mp3"),
                    new File(2, "Music\\Artist1\\Album1\\Artist1 - Album1 - 02 - Track2.mp3", 12000, ".mp3"),
                    new File(3, "Music\\Artist1\\Album1\\Artist1 - Album1 - 03 - Track3.flac", 25000, ".flac"),
                    new File(4, "Music\\Artist1\\Album1\\Artist1 - Album1 - cover.jpg", 500, ".jpg"),
                    new File(5, "Music\\Artist1\\Album1\\Artist1 - Album1.cue", 100, ".cue"),
                    new File(6, "Music\\Artist1\\Album2\\Artist1 - Album2 - 01 - Track1.mp3", 11000, ".mp3"),
                    new File(7, "Music\\Artist1\\Album2\\Artist1 - Album2 - 02 - Track2.flac", 28000, ".flac"),
                    new File(8, "Music\\Artist1\\Album2\\Artist1 - Album2 - cover.jpg", 600, ".jpg")
                }
            );

            var user2SearchResponse = new SearchResponse(
                username: "user2",
                token: 2,
                hasFreeUploadSlot: false,
                uploadSpeed: 50,
                queueLength: 5,
                fileList: new List<File>
                {
                    new File(9,  "Music\\Artist2\\Album1\\Artist2 - Album1 - 01 - Track1.mp3", 9000, ".mp3"),
                    new File(10, "Music\\Artist2\\Album1\\Artist2 - Album1 - 02 - Track2.mp3", 11000, ".mp3"),
                    new File(11, "Music\\Artist2\\Album1\\Artist2 - Album1 - cover.jpg", 400, ".jpg"),
                    new File(12, "Music\\Artist2\\Artist2 - Single.flac", 30000, ".flac"),
                    new File(13, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\01 Track 1.mp3", 8000, ".mp3"),
                    new File(14, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\02 Track 2.mp3", 8000, ".mp3"),
                    new File(15, "Music\\Artist3\\Some Great Album\\Artist3\\Some Great Album\\cover.jpg", 300, ".jpg"),
                }
            );

            var user3SearchResponse = new SearchResponse(
                username: "user3",
                token: 3,
                hasFreeUploadSlot: true,
                uploadSpeed: 75,
                queueLength: 2,
                fileList: new List<File>
                {
                    new File(16, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0101. testartist - testsong.mp3", 15000, ".mp3"),
                    new File(17, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0102. testartist - testsong2.mp3", 16000, ".mp3"),
                    new File(18, "Music\\music\\testartist\\(2011) testalbum [MP3]\\0103. testartist - testsong3.mp3", 17000, ".mp3"),
                    new File(19, "Music\\music\\testartist\\(2011) testalbum [MP3]\\cover.jpg", 700, ".jpg")
                }
            );

            index.Add(user1SearchResponse);
            index.Add(user2SearchResponse);
            index.Add(user3SearchResponse);

            return index;
        }

        [TestMethod]
        public async Task BrowseAsync_ExistingUser_ReturnsCorrectFiles()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);

            // Act
            var result = await client.BrowseAsync("user1");

            // Assert
            Assert.AreEqual(2, result.DirectoryCount);
            Assert.AreEqual("Music\\Artist1\\Album1", result.Directories.First().Name);
            Assert.AreEqual(5, result.Directories.First().FileCount);
            Assert.AreEqual("Music\\Artist1\\Album2", result.Directories.ElementAt(1).Name);
            Assert.AreEqual(3, result.Directories.ElementAt(1).FileCount);
        }

        [TestMethod]
        [ExpectedException(typeof(UserNotFoundException))]
        public async Task BrowseAsync_NonExistentUser_ThrowsUserNotFoundException()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);

            // Act
            await client.BrowseAsync("nonexistentUser");
        }

        [TestMethod]
        public async Task SearchAsync_MatchingQuery_ReturnsResults()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);
            var query = new SearchQuery("Artist1");

            // Act
            var result = await client.SearchAsync(query);

            // Assert
            Assert.AreEqual(1, result.Responses.Count);
            Assert.AreEqual(8, result.Responses.First().FileCount);
            Assert.AreEqual("Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3", result.Responses.First().Files.First().Filename);
        }

        [TestMethod]
        public async Task SearchAsync_MatchingQuery_ReturnsResults_2()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);
            var query = new SearchQuery("testartist testalbum");

            // Act
            var results = await client.SearchAsync(query);

            // Assert
            Assert.AreEqual(1, results.Responses.Count);
            Assert.AreEqual(4, results.Responses.First().FileCount);
            Assert.AreEqual("Music\\music\\testartist\\(2011) testalbum [MP3]\\cover.jpg", results.Responses.First().Files.Last().Filename);
        }

        [TestMethod]
        public async Task SearchAsync_NoMatches_ReturnsEmptyResults()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);
            var query = new SearchQuery("nonexistent");

            // Act
            var result = await client.SearchAsync(query);

            // Assert
            Assert.AreEqual(0, result.Responses.Count);
        }

        [TestMethod]
        public async Task DownloadAsync_ExistingFile_StartsTransfer()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);
            var tempFile = Path.GetTempFileName();

            try
            {
                // Act
                var transfer = await client.DownloadAsync(
                    username: "user1",
                    remoteFilename: "Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3",
                    localFilename: tempFile);

                // Assert
                Assert.AreEqual(TransferStates.Completed, transfer.State);
                Assert.AreEqual("Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3", transfer.Filename);
                Assert.AreEqual(10000, transfer.Size);
                Assert.IsTrue(System.IO.File.Exists(tempFile), "Downloaded file should exist");
                Assert.AreEqual(10000, new System.IO.FileInfo(tempFile).Length, "File size should match expected size");
            }
            finally
            {
                // Cleanup
                if (System.IO.File.Exists(tempFile))
                {
                    System.IO.File.Delete(tempFile);
                }
            }
        }

        [TestMethod]
        [ExpectedException(typeof(UserNotFoundException))]
        public async Task DownloadAsync_NonexistentUser_ThrowsUserNotFoundException()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);

            // Act
            await client.DownloadAsync(
                username: "nonexistentUser",
                remoteFilename: "Music\\Artist1\\Album1\\Artist1 - Album1 - 01 - Track1.mp3",
                localFilename: "test.mp3");
        }

        [TestMethod]
        [ExpectedException(typeof(FileNotFoundException))]
        public async Task DownloadAsync_NonexistentFile_ThrowsFileNotFoundException()
        {
            // Arrange
            var index = CreateTestIndex();
            var client = new MockSoulseekClient(index);

            // Act
            await client.DownloadAsync(
                username: "user1",
                remoteFilename: "Music\\Artist1\\Album1\\nonexistent.mp3",
                localFilename: "test.mp3");
        }
    }
}
