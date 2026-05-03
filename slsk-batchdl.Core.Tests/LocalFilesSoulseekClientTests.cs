using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sldl.Core.Services;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Tests.Core;

[TestClass]
public class LocalFilesSoulseekClientTests
{
    [TestMethod]
    public async Task FromLocalPaths_UsesSoulseekRelativeIdentityAndDownloadsFromLocalSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "sldl-local-files-identity-" + Guid.NewGuid());
        string albumDir = Path.Combine(root, "Artist", "Album");
        string outputPath = Path.Combine(Path.GetTempPath(), "sldl-local-files-download-" + Guid.NewGuid() + ".mp3");
        Directory.CreateDirectory(albumDir);
        File.WriteAllBytes(Path.Combine(albumDir, "01. Artist - Track.mp3"), [1, 2, 3, 4]);

        try
        {
            var client = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, root);
            var result = await client.SearchAsync(new SearchQuery("Artist Track"));

            Assert.AreEqual(1, result.Responses.Count);
            Assert.AreEqual(@"Artist\Album\01. Artist - Track.mp3", result.Responses.First().Files.First().Filename);

            var transfer = await client.DownloadAsync("local", result.Responses.First().Files.First().Filename, outputPath);

            Assert.AreEqual(TransferStates.Completed, transfer.State);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(outputPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }
}
