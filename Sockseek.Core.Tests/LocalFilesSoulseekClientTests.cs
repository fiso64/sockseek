using Microsoft.VisualStudio.TestTools.UnitTesting;
using Soulseek;
using Sockseek.Core.Services;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace Tests.Core;

[TestClass]
public class LocalFilesSoulseekClientTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FactoryDownloadsRespectOutputOwnership(bool disposeOutput)
    {
        var file = new Soulseek.File(1, @"Shared\Track.bin", 8, ".bin");
        var client = new LocalFilesSoulseekClient([new SearchResponse("peer", 1, true, 100, 0, [file])]);
        using var output = new MemoryStream();
        var transfer = await client.DownloadAsync("peer", file.Filename, () => Task.FromResult<Stream>(output),
            options: new TransferOptions(disposeOutputStreamOnCompletion: disposeOutput));
        Assert.AreEqual(TransferStates.Completed, transfer.State);
        Assert.AreEqual(!disposeOutput, output.CanWrite);
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => (byte)i).ToArray(), output.ToArray());
    }

    [TestMethod]
    public async Task ResponseHandlerSearch_PropagatesCancellationWithoutAggregateException()
    {
        var indexedFile = new Soulseek.File(
            code: 1,
            filename: @"Artist\Track.mp3",
            size: 4,
            extension: "mp3");
        var response = new SearchResponse(
            username: "local",
            token: 1,
            hasFreeUploadSlot: true,
            uploadSpeed: 100,
            queueLength: 0,
            fileList: [indexedFile]);
        var client = new LocalFilesSoulseekClient([response]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            client.SearchAsync(
                new SearchQuery("Track"),
                responseHandler: _ => { },
                cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task FromLocalPaths_UsesSoulseekRelativeIdentityAndDownloadsFromLocalSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-local-files-identity-" + Guid.NewGuid());
        string albumDir = Path.Combine(root, "Artist", "Album");
        string outputPath = Path.Combine(Path.GetTempPath(), "Sockseek-local-files-download-" + Guid.NewGuid() + ".mp3");
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

    [TestMethod]
    public async Task FromLocalPaths_FailDownloads_FailsInitialDownloadAttempts()
    {
        string root = Path.Combine(Path.GetTempPath(), "Sockseek-local-files-fail-" + Guid.NewGuid());
        string outputPath = Path.Combine(Path.GetTempPath(), "Sockseek-local-files-fail-out-" + Guid.NewGuid() + ".mp3");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "Artist - Track.mp3"), [1, 2, 3, 4]);

        try
        {
            var client = LocalFilesSoulseekClient.FromLocalPaths(useTags: false, slowMode: false, failDownloads: 1, root);
            var result = await client.SearchAsync(new SearchQuery("Artist Track"));
            var filename = result.Responses.First().Files.First().Filename;

            await Assert.ThrowsExactlyAsync<SoulseekClientException>(async () =>
                await client.DownloadAsync("local", filename, outputPath));

            var transfer = await client.DownloadAsync("local", filename, outputPath);

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
