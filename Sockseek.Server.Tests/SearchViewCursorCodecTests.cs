using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Planning;
using Sockseek.Api;
using Sockseek.Server.Planning;

namespace Sockseek.Server.Tests;

[TestClass]
public sealed class SearchViewCursorCodecTests
{
    [TestMethod]
    public void CursorsBindViewRevisionParentAndProcessLifetime()
    {
        Guid viewId = Guid.NewGuid();
        var position = new SearchViewFilePosition(9, 8, 7, 6, 5, 4, 3, 2, "row");
        var directoryPosition = new SearchViewDirectoryPosition(
            9, 8, 7, 6, 5, 4, 3, 2, "Peer", @"Share\Folder", "directory");
        var childPosition = new SearchViewDirectoryFilePosition("Track.flac", "file");
        var first = new SearchViewCursorCodec();
        string fileCursor = first.EncodeFiles(viewId, 12, position);
        string directoryCursor = first.EncodeDirectories(viewId, 12, directoryPosition);
        string childCursor = first.EncodeDirectoryFiles(
            viewId, "directory", 12, childPosition);

        string orderedFiles = first.EncodeFiles(viewId, 12, position, ServerSearchViewSortOrder.UploadSpeed);
        string orderedDirectories = first.EncodeDirectories(viewId, 12, directoryPosition,
            ServerSearchViewSortOrder.DirectoryNameAscending);
        var restarted = new SearchViewCursorCodec();
        Assert.ThrowsExactly<ArgumentException>(() => restarted.DecodeFiles(fileCursor, viewId, 12));
        Assert.AreEqual(position, first.DecodeFiles(fileCursor, viewId, 12));
        Assert.AreEqual(position, first.DecodeFiles(orderedFiles, viewId, 12, ServerSearchViewSortOrder.UploadSpeed));
        Assert.AreEqual(directoryPosition, first.DecodeDirectories(orderedDirectories, viewId, 12,
            ServerSearchViewSortOrder.DirectoryNameAscending));
        Assert.ThrowsExactly<ArgumentException>(() => first.DecodeFiles(orderedFiles, viewId, 12,
            ServerSearchViewSortOrder.QueueDepth));
        Assert.ThrowsExactly<ArgumentException>(() => first.DecodeFiles(orderedFiles, viewId, 12));
        Assert.ThrowsExactly<ArgumentException>(() => first.DecodeDirectories(orderedDirectories, viewId, 12,
            ServerSearchViewSortOrder.DirectorySizeAscending));
        Assert.AreEqual(directoryPosition, first.DecodeDirectories(
            directoryCursor, viewId, 12));
        Assert.AreEqual(childPosition, first.DecodeDirectoryFiles(
            childCursor, viewId, "directory", 12));
        Assert.ThrowsExactly<ArgumentException>(() =>
            first.DecodeFiles(fileCursor, viewId, 13));
        Assert.ThrowsExactly<ArgumentException>(() =>
            first.DecodeFiles(fileCursor + "tampered", viewId, 12));
        Assert.ThrowsExactly<ArgumentException>(() => first.DecodeDirectories(
            directoryCursor, viewId, 13));
        Assert.ThrowsExactly<ArgumentException>(() => first.DecodeDirectoryFiles(
            childCursor, viewId, "other-directory", 12));
    }

}
