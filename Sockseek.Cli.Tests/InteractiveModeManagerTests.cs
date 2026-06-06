using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Tests.InteractiveModeManagerTests;

[TestClass]
public class InteractiveModeManagerTests
{
    [TestMethod]
    public void DownloadRange_ReturnsTrimmedFolder()
    {
        var folder = new AlbumFolder(
            "local",
            @"Artist\Album",
            [
                CreateSong(@"Artist\Album\01. Artist - One.mp3"),
                CreateSong(@"Artist\Album\02. Artist - Two.mp3"),
                CreateSong(@"Artist\Album\03. Artist - Three.mp3"),
            ]);

        var ok = InteractiveModeManager.TryBuildSelectedFolder(folder, "2", out var selected, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(1, selected.Files.Count);
        Assert.AreEqual(@"Artist\Album\02. Artist - Two.mp3", selected.Files[0].ResolvedTarget!.Filename);
    }

    [TestMethod]
    public void DownloadRange_RejectsOutOfBoundsSelection()
    {
        var folder = new AlbumFolder(
            "local",
            @"Artist\Album",
            [CreateSong(@"Artist\Album\01. Artist - One.mp3")]);

        var ok = InteractiveModeManager.TryBuildSelectedFolder(folder, "2", out _, out var error);

        Assert.IsFalse(ok);
        Assert.AreEqual("Invalid range", error);
    }

    private static SongJob CreateSong(string filename)
    {
        var response = new Soulseek.SearchResponse("local", 1, true, 100, 0, []);
        var file = new Soulseek.File(1, filename, 100, ".mp3");
        return new SongJob(new SongQuery { Artist = "Artist", Title = Path.GetFileNameWithoutExtension(filename) })
        {
            ResolvedTarget = new FileCandidate(response, file),
        };
    }
}
