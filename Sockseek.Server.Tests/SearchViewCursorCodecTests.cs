using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Planning;
using Sockseek.Api;
using Sockseek.Server.Planning;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SearchViewCursorCodecTests
{
    [TestMethod]
    public void CursorsSurviveRestartAndBindViewRevisionAndParent()
    {
        using var directory = new TemporaryDirectory();
        var options = Options.Create(new ServerOptions
        {
            Persistence = new ServerPersistenceOptions
            {
                DataDirectory = directory.Path,
            },
        });
        Guid viewId = Guid.NewGuid();
        var position = new SearchViewFilePosition(9, 8, 7, 6, 5, 4, 3, 2, "row");
        var directoryPosition = new SearchViewDirectoryPosition(
            9, 8, 7, 6, 5, 4, 3, 2, "Peer", @"Share\Folder", "directory");
        var childPosition = new SearchViewDirectoryFilePosition("Track.flac", "file");
        var first = new SearchViewCursorCodec(options);
        first.Initialize();
        string fileCursor = first.EncodeFiles(viewId, 12, position);
        string directoryCursor = first.EncodeDirectories(viewId, 12, directoryPosition);
        string childCursor = first.EncodeDirectoryFiles(
            viewId, "directory", 12, childPosition);

        string orderedFiles = first.EncodeFiles(viewId, 12, position, ServerSearchViewSortOrder.UploadSpeed);
        string orderedDirectories = first.EncodeDirectories(viewId, 12, directoryPosition,
            ServerSearchViewSortOrder.DirectoryNameAscending);
        var restarted = new SearchViewCursorCodec(options);
        Assert.AreEqual(position, restarted.DecodeFiles(fileCursor, viewId, 12));
        Assert.AreEqual(position, restarted.DecodeFiles(orderedFiles, viewId, 12, ServerSearchViewSortOrder.UploadSpeed));
        Assert.AreEqual(directoryPosition, restarted.DecodeDirectories(orderedDirectories, viewId, 12,
            ServerSearchViewSortOrder.DirectoryNameAscending));
        Assert.ThrowsExactly<ArgumentException>(() => restarted.DecodeFiles(orderedFiles, viewId, 12,
            ServerSearchViewSortOrder.QueueDepth));
        Assert.ThrowsExactly<ArgumentException>(() => restarted.DecodeFiles(orderedFiles, viewId, 12));
        Assert.ThrowsExactly<ArgumentException>(() => restarted.DecodeDirectories(orderedDirectories, viewId, 12,
            ServerSearchViewSortOrder.DirectorySizeAscending));
        Assert.AreEqual(directoryPosition, restarted.DecodeDirectories(
            directoryCursor, viewId, 12));
        Assert.AreEqual(childPosition, restarted.DecodeDirectoryFiles(
            childCursor, viewId, "directory", 12));
        Assert.ThrowsExactly<ArgumentException>(() =>
            restarted.DecodeFiles(fileCursor, viewId, 13));
        Assert.ThrowsExactly<ArgumentException>(() =>
            restarted.DecodeFiles(fileCursor + "tampered", viewId, 12));
        Assert.ThrowsExactly<ArgumentException>(() => restarted.DecodeDirectories(
            directoryCursor, viewId, 13));
        Assert.ThrowsExactly<ArgumentException>(() => restarted.DecodeDirectoryFiles(
            childCursor, viewId, "other-directory", 12));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sockseek-search-view-cursor-tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
