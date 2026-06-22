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
            ])
        {
            IsFullyRetrieved = true,
        };

        var ok = InteractiveModeManager.TryBuildSelectedFolder(folder, "2", out var selected, out var error);

        Assert.IsTrue(ok, error);
        Assert.IsTrue(selected.IsFullyRetrieved);
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
                    return Task.FromResult(new InteractiveModeManager.RetrievedFolder(f, 1));
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

    [TestMethod]
    [DataTestMethod]
    [DataRow('S', ConsoleKey.S)]
    [DataRow('Q', ConsoleKey.Q)]
    public async Task RestKeys_ReturnSkipRemainingNewPromptsAction(char keyChar, ConsoleKey key)
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
                retrieveFolderCallback: _ => throw new AssertFailedException("Skip-all should not retrieve."));

            EnqueueKey(keyChar, key);

            var result = await manager.Run();

            Assert.AreEqual(InteractiveModeManager.RunAction.SkipRemainingNewPrompts, result.Action);
            Assert.IsNull(result.Folder);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [DataTestMethod]
    [DataRow('s', ConsoleKey.S)]
    [DataRow('q', ConsoleKey.Q)]
    [DataRow('\u001b', ConsoleKey.Escape)]
    public async Task SkipKeys_ReturnSkipCurrentAction(char keyChar, ConsoleKey key)
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
                retrieveFolderCallback: _ => throw new AssertFailedException("Skip should not retrieve."));

            EnqueueKey(keyChar, key);

            var result = await manager.Run();

            Assert.AreEqual(InteractiveModeManager.RunAction.SkipCurrent, result.Action);
            Assert.IsNull(result.Folder);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public async Task CdParent_ReplacesCurrentFolderWithRetrievedParentFolder()
    {
        var folder = new AlbumFolder(
            "local",
            @"Artist\Album\Disc 1",
            [CreateSong(@"Artist\Album\Disc 1\01. Artist - One.mp3")]);

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
                    Assert.AreEqual(@"Artist\Album", f.FolderPath);
                    var parent = new AlbumFolder(
                        "local",
                        @"Artist\Album",
                        [
                            CreateSong(@"Artist\Album\Disc 1\01. Artist - One.mp3"),
                            CreateSong(@"Artist\Album\Disc 2\01. Artist - Two.mp3"),
                        ])
                    {
                        IsFullyRetrieved = true,
                    };
                    return Task.FromResult(new InteractiveModeManager.RetrievedFolder(parent, 0));
                });

            EnqueueText("cd ..");
            EnqueueKey('\r', ConsoleKey.Enter);
            EnqueueKey('\r', ConsoleKey.Enter);

            var result = await manager.Run();

            Assert.AreEqual(0, result.Index);
            Assert.IsNotNull(result.Folder);
            Assert.AreEqual(@"Artist\Album", result.Folder.FolderPath);
            Assert.AreEqual(2, result.Folder.Files.Count);
            var text = output.ToString();
            Assert.IsTrue(text.Contains(@"Folder: Artist\Album", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains(@"Changed folder: Artist\Album", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("Retrieved folder: no new files found.", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public async Task CdSubdir_ReplacesCurrentFolderWithChildFolderPath()
    {
        var folder = new AlbumFolder(
            "local",
            @"Artist\Album",
            [
                CreateSong(@"Artist\Album\Disc 1\01. Artist - One.mp3"),
                CreateSong(@"Artist\Album\Disc 10\01. Artist - Ten.mp3"),
                CreateSong(@"Artist\Album\Disc 2\01. Artist - Two.mp3"),
            ]);

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
                retrieveFolderCallback: _ => throw new AssertFailedException("cd subdir should not retrieve automatically."));

            EnqueueText("cd Disc 1");
            EnqueueKey('\r', ConsoleKey.Enter);
            EnqueueKey('\r', ConsoleKey.Enter);

            var result = await manager.Run();

            Assert.AreEqual(0, result.Index);
            Assert.IsNotNull(result.Folder);
            Assert.AreEqual(@"Artist\Album\Disc 1", result.Folder.FolderPath);
            Assert.AreEqual(1, result.Folder.Files.Count);
            Assert.AreEqual(@"Artist\Album\Disc 1\01. Artist - One.mp3", result.Folder.Files[0].ResolvedTarget!.Filename);
            var text = output.ToString();
            Assert.IsTrue(text.Contains(@"Folder: Album\Disc 1", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains(@"Changed folder: Artist\Album\Disc 1", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [TestMethod]
    public async Task CdSubdirThenParent_RestoresCachedParentWithoutRetrievingAgain()
    {
        var folder = new AlbumFolder(
            "local",
            @"Artist\Album",
            [
                CreateSong(@"Artist\Album\Disc 1\01. Artist - One.mp3"),
                CreateSong(@"Artist\Album\Disc 2\01. Artist - Two.mp3"),
                CreateSong(@"Artist\Album\cover.jpg"),
            ])
        {
            IsFullyRetrieved = true,
        };

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
                retrieveFolderCallback: _ => throw new AssertFailedException("cd .. should restore the cached fully retrieved parent folder."));

            EnqueueText("cd Disc 1");
            EnqueueKey('\r', ConsoleKey.Enter);
            EnqueueText("cd ..");
            EnqueueKey('\r', ConsoleKey.Enter);
            EnqueueKey('\r', ConsoleKey.Enter);

            var result = await manager.Run();

            Assert.AreEqual(0, result.Index);
            Assert.IsNotNull(result.Folder);
            Assert.AreEqual(@"Artist\Album", result.Folder.FolderPath);
            Assert.IsTrue(result.Folder.IsFullyRetrieved);
            Assert.AreEqual(3, result.Folder.Files.Count);
            var text = output.ToString();
            Assert.IsTrue(text.Contains(@"Changed folder: Artist\Album\Disc 1", StringComparison.Ordinal));
            Assert.IsTrue(text.Contains(@"Changed folder: Artist\Album", StringComparison.Ordinal));
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

    private static void EnqueueText(string text)
    {
        foreach (var ch in text)
            EnqueueKey(ch, ch == ' ' ? ConsoleKey.Spacebar : default);
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
