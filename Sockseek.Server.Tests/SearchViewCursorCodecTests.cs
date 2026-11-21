using Microsoft.VisualStudio.TestTools.UnitTesting;
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
        var position = new SearchViewFilePosition(new(9, 8, 7, 5, 4, 3), 2, "row");
        var directoryPosition = new SearchViewDirectoryPosition(
            new(9, 8, 7, 5, 4, 3), 2, "Peer", @"Share\Folder", "directory");
        var childPosition = new SearchViewDirectoryFilePosition("Track.flac", "file");
        var first = new SearchViewCursorCodec();
        string? fileCursor = first.Files(viewId, 12).Encode(position);
        string? directoryCursor = first.Directories(viewId, 12).Encode(directoryPosition);
        string? childCursor = first.DirectoryFiles(viewId, 12, "directory").Encode(childPosition);

        string? orderedFiles = first.Files(viewId, 12, ServerSearchViewSortOrder.UploadSpeed).Encode(position);
        string? orderedDirectories = first.Directories(viewId, 12, ServerSearchViewSortOrder.DirectoryNameAscending).Encode(directoryPosition);
        var restarted = new SearchViewCursorCodec();
        Assert.ThrowsExactly<ArgumentException>(() => restarted.Files(viewId, 12).Decode(fileCursor));
        Assert.AreEqual(position, first.Files(viewId, 12).Decode(fileCursor));
        Assert.ThrowsExactly<ArgumentException>(() => first.Files(Guid.NewGuid(), 12).Decode(fileCursor));
        Assert.ThrowsExactly<ArgumentException>(() => first.TrackGroupOptions(viewId, 12, "group").Decode(fileCursor));
        Assert.AreEqual(position, first.Files(viewId, 12, ServerSearchViewSortOrder.UploadSpeed).Decode(orderedFiles));
        Assert.AreEqual(directoryPosition, first.Directories(viewId, 12, ServerSearchViewSortOrder.DirectoryNameAscending).Decode(orderedDirectories));
        Assert.ThrowsExactly<ArgumentException>(() => first.Files(viewId, 12, ServerSearchViewSortOrder.QueueDepth).Decode(orderedFiles));
        Assert.ThrowsExactly<ArgumentException>(() => first.Files(viewId, 12).Decode(orderedFiles));
        Assert.ThrowsExactly<ArgumentException>(() => first.Directories(viewId, 12, ServerSearchViewSortOrder.DirectorySizeAscending).Decode(orderedDirectories));
        Assert.AreEqual(directoryPosition, first.Directories(viewId, 12).Decode(directoryCursor));
        Assert.AreEqual(childPosition, first.DirectoryFiles(viewId, 12, "directory").Decode(childCursor));
        Assert.ThrowsExactly<ArgumentException>(() =>
            first.Files(viewId, 13).Decode(fileCursor));
        Assert.ThrowsExactly<ArgumentException>(() =>
            first.Files(viewId, 12).Decode(fileCursor + "tampered"));
        Assert.ThrowsExactly<ArgumentException>(() => first.Directories(viewId, 13).Decode(directoryCursor));
        Assert.ThrowsExactly<ArgumentException>(() => first.DirectoryFiles(viewId, 12, "other-directory").Decode(childCursor));
    }

}
