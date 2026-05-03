using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Core.Models;
using Sldl.Core.Jobs;
using Soulseek;

// TODO: Move this basic test into SearcherTests.cs and delete this file as it doesn't test anything download-related.
namespace Tests.SearchDownloadTests
{
    [TestClass]
    public class MockSoulseekClientTests
    {
        [TestMethod]
        public async Task SearchAlbum_ReturnsMatchingResults()
        {
            // Arrange
            var index = TestHelpers.CreateTestIndex();
            var (engineSettings, rootSettings) = TestHelpers.CreateDefaultSettings();
            var client = new ClientTests.MockSoulseekClient(index);
            var clientManager = TestHelpers.CreateMockClientManager(client, engineSettings);
            var registry = TestHelpers.CreateSessionRegistry();
            var engine = new DownloadEngine(engineSettings, clientManager);
            var searcher = new Searcher(client, registry, registry, new EngineEvents(), 999, 1);
            var job = new AlbumJob(new AlbumQuery { Album = "testalbum", Artist = "testartist" });

            // Act
            await searcher.SearchAlbum(job, rootSettings.Search, new ResponseData(), CancellationToken.None);

            // Assert: the testuser folder (4 files) should be found
            var testUserFolder = job.Results.First(f => f.Username == "testuser");
            Assert.AreEqual(4, testUserFolder.Files.Count);
            CollectionAssert.AreEqual(
                index.First(x => x.Username == "testuser").Files.Select(x => x.Filename).ToList(),
                testUserFolder.Files.Select(x => x.ResolvedTarget!.File.Filename).ToList());
        }
    }
}
