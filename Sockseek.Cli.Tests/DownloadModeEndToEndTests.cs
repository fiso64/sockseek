using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;

namespace Tests.Cli;

[TestClass]
[DoNotParallelize] // Program.Main and console capture use process-wide state.
public class DownloadModeEndToEndTests
{
    [TestMethod]
    [DataRow("song", "list", false)]
    [DataRow("album", "list", false)]
    [DataRow("aggregate", "list", false)]
    [DataRow("album-aggregate", "list", false)]
    [DataRow("song", "list", true)]
    [DataRow("album", "list", true)]
    [DataRow("aggregate", "list", true)]
    [DataRow("album-aggregate", "list", true)]
    [DataRow("song", "csv", false)]
    [DataRow("album", "csv", false)]
    [DataRow("song", "csv", true)]
    [DataRow("album", "csv", true)]
    public async Task Main_MultiItemInput_DownloadsOrPrintsEveryExpectedResult(string mode, string inputType, bool printOnly)
    {
        using var fixture = new MusicFixture();
        var artists = Enumerable.Range(1, 3).Select(i => $"Artist{i}").ToArray();
        var input = Path.Combine(fixture.Root, "input." + (inputType == "csv" ? "csv" : "txt"));
        File.WriteAllLines(input, inputType == "csv"
            ? new[] { mode == "song" ? "Artist,Title" : "Artist,Album" }
                .Concat(artists.Select(artist => $"{artist}," + (mode == "song" ? "Album1 Track1" : "Album1")))
            : artists.Select(artist => ListQuery(mode, artist)));
        var expected = fixture.Files.Where(path => mode switch
        {
            "song" => path.Contains("Album1 Track1", StringComparison.Ordinal),
            "album" => path.Contains("Album1", StringComparison.Ordinal),
            _ => true,
        }).ToArray();
        var args = fixture.Args(input).ToList();
        args.AddRange(["--input-type", inputType]);
        if (mode is "album" or "album-aggregate") args.Add("--album");
        if (mode is "aggregate" or "album-aggregate") args.Add("--aggregate");
        if (printOnly) args.AddRange(["--print-results-full", "--pref-format", "flac"]);

        var (exitCode, stdout, stderr) = await RunMainAsync(args.ToArray());

        Assert.AreEqual(0, exitCode, stdout + stderr);
        Assert.AreEqual("", stderr, "A successful offline run should not report errors.");
        if (printOnly)
        {
            foreach (var path in expected)
                StringAssert.Contains(stdout, Path.GetFileName(path));
            foreach (var path in fixture.Files.Except(expected))
                Assert.IsFalse(stdout.Contains(Path.GetFileName(path), StringComparison.Ordinal),
                    $"An unrelated result was printed: {path}");
            Assert.AreEqual(expected.Length, stdout.Split("(nec:", StringSplitOptions.None).Length - 1,
                "Full results must explain every printed file using shared condition facts.");
            Assert.AreEqual(expected.Length, stdout.Split("Format fails", StringSplitOptions.None).Length - 1);
            Assert.IsFalse(stdout.Contains("conditions:unavailable", StringComparison.Ordinal), stdout);
            Assert.AreEqual(0, Directory.GetFiles(fixture.OutputDir, "*", SearchOption.AllDirectories).Length,
                "Printing results must not download anything or create an index.");
        }
        else
        {
            fixture.AssertDownloaded(expected);
        }
    }

    [TestMethod]
    [DataRow("song")]
    [DataRow("album")]
    [DataRow("aggregate")]
    [DataRow("album-aggregate")]
    public async Task Main_OneMissingItem_ReportsFailureButDownloadsOtherItems(string mode)
    {
        using var fixture = new MusicFixture();
        var input = Path.Combine(fixture.Root, "mixed.txt");
        File.WriteAllLines(input, new[] { "Artist1", "MissingArtist", "Artist3" }.Select(artist => ListQuery(mode, artist)));
        var args = fixture.Args(input).ToList();
        args.AddRange(["--input-type", "list"]);
        if (mode is "album" or "album-aggregate") args.Add("--album");
        if (mode is "aggregate" or "album-aggregate") args.Add("--aggregate");

        var (exitCode, stdout, stderr) = await RunMainAsync(args.ToArray());

        Assert.AreEqual(1, exitCode, stdout + stderr);
        StringAssert.Contains(stdout + stderr, "MissingArtist");
        fixture.AssertDownloaded(fixture.Files.Where(path =>
            !path.Contains("Artist2", StringComparison.Ordinal) && (mode switch
            {
                "song" => path.Contains("Album1 Track1", StringComparison.Ordinal),
                "album" => path.Contains("Album1", StringComparison.Ordinal),
                _ => true,
            })).ToArray());
    }

    private static string ListQuery(string mode, string artist) => $"artist={artist}" + (mode switch
    {
        "song" => ",title=\"Album1 Track1\"",
        "album" => ",album=Album1",
        _ => "",
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Main_DirectSoulseekLink_DownloadsExactFileOrFolder(bool folder)
    {
        using var fixture = new MusicFixture();
        var remotePath = "Artist1/Album1/" + (folder ? "" : "Artist1 - Album1 Track1.mp3");
        var link = "slsk://local/" + remotePath;

        var (exitCode, stdout, stderr) = await RunMainAsync(fixture.Args(link));

        Assert.AreEqual(0, exitCode, stdout + stderr);
        Assert.AreEqual("", stderr);
        fixture.AssertDownloaded(fixture.Files.Where(path => folder
            ? path.Contains("Artist1 - Album1", StringComparison.Ordinal)
            : path.EndsWith("Artist1 - Album1 Track1.mp3", StringComparison.Ordinal)).ToArray());
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunMainAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = await Sockseek.Cli.Program.Main(args).WaitAsync(TimeSpan.FromSeconds(10));
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class MusicFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sockseek-cli-modes-" + Guid.NewGuid());
        public string MusicDir => Path.Combine(Root, "music");
        public string OutputDir => Path.Combine(Root, "output");
        public List<string> Files { get; } = [];

        public MusicFixture()
        {
            Directory.CreateDirectory(OutputDir);
            for (int artist = 1; artist <= 3; artist++)
            for (int album = 1; album <= 2; album++)
            for (int track = 1; track <= 2; track++)
            {
                var directory = Path.Combine(MusicDir, $"Artist{artist}", $"Album{album}");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"Artist{artist} - Album{album} Track{track}.mp3");
                File.WriteAllBytes(path, [(byte)artist, (byte)album, (byte)track]);
                Files.Add(path);
            }
        }

        public string[] Args(string input) =>
        [
            input, "--no-config", "--no-progress", "--no-write-index", "--no-skip-existing",
            "--mock-files-dir", MusicDir, "--mock-files-no-read-tags",
            "--path", OutputDir, "--name-format", "{filename}", "--min-shares-aggregate", "1",
        ];

        public void AssertDownloaded(string[] expected)
        {
            var actual = Directory.GetFiles(OutputDir, "*", SearchOption.AllDirectories);
            CollectionAssert.AreEquivalent(expected.Select(Path.GetFileName).ToArray(), actual.Select(Path.GetFileName).ToArray());
            foreach (var source in expected)
                CollectionAssert.AreEqual(File.ReadAllBytes(source),
                    File.ReadAllBytes(actual.Single(path => Path.GetFileName(path) == Path.GetFileName(source))));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
