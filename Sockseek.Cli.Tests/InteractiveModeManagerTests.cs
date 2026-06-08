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

    [TestMethod]
    public async Task RetrieveFolder_RedrawsUpdatedFolderWithoutTransientFoundLine()
    {
        var folder = new AlbumFolder(
            "local",
            @"Artist\Album",
            [CreateSong(@"Artist\Album\01. Artist - One.mp3")]);

        using var output = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(output);
        try
        {
            var manager = new InteractiveModeManager(
                new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }),
                new JobList(),
                [folder],
                canRetrieve: true,
                retrievedFolders: [],
                retrieveFolderCallback: f =>
                {
                    f.Files.Add(CreateSong(@"Artist\Album\02. Artist - Two.mp3"));
                    f.IsFullyRetrieved = true;
                    return Task.FromResult(1);
                });

            EnqueueKey('r');
            EnqueueKey('\r', ConsoleKey.Enter);

            var result = await manager.Run();

            Assert.AreEqual(0, result.Index);
            Assert.IsNotNull(result.Folder);
            Assert.AreEqual(2, result.Folder.Files.Count);
            Assert.IsFalse(output.ToString().Contains("more files in the folder", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static void EnqueueKey(char keyChar, ConsoleKey key = default)
    {
        var field = typeof(ConsoleInputManager).GetField("_keyChannel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(field);
        var channel = (System.Threading.Channels.Channel<ConsoleKeyInfo>)field.GetValue(null)!;
        channel.Writer.TryWrite(new ConsoleKeyInfo(keyChar, key == default ? (ConsoleKey)char.ToUpperInvariant(keyChar) : key, false, false, false));
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
