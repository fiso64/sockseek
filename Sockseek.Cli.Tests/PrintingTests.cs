using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;

namespace Tests.Cli;

[TestClass]
public sealed class PrintingTests
{
    [TestMethod]
    public void FullJobPrintIncludesExtractorInformationForSongsAndAlbums()
    {
        Job[] jobs = [
            new SongJob(new SongQuery { Artist = "Artist", Title = "Track" }) { ExtractorInfo = "Original source title" },
            new AlbumJob(new AlbumQuery { Artist = "Artist", Album = "Album" }) { ExtractorInfo = "Catalog entry: A12" },
        ];
        using var output = new StringWriter();
        using (Printing.RedirectOutput(output))
            JobPrintFormatter.Print(jobs, PrintOption.Jobs | PrintOption.Full);

        StringAssert.Contains(output.ToString(), "Extractor info:     Original source title");
        StringAssert.Contains(output.ToString(), "Extractor info:     Catalog entry: A12");
    }

    [TestMethod]
    [DataRow(int.MaxValue, int.MaxValue, 128L, "1193046:28:14")]
    [DataRow(180, 180, long.MaxValue, "06:00")]
    [DataRow(int.MinValue, 180, 128L, "03:00")]
    [DataRow(0, 0, null, "00:00")]
    public void AlbumHeaderHandlesExtremeAndUnknownMetadata(
        int firstLength, int secondLength, long? size, string expectedDuration)
    {
        var query = new SongQuery { Artist = "Artist", Album = "Album" };
        string folderPath = size.HasValue ? "Root" : "Root\nAlbum";
        var first = new FileCandidate(
            new PeerFileTarget(new PeerFileIdentity("Peer", $@"{folderPath}\First.flac"), size, ".flac",
                bitRate: -1, sampleRate: -1, length: firstLength),
            new SearchPeerSnapshot("Peer", 2, null, null));
        var second = new FileCandidate(
            new PeerFileTarget(new PeerFileIdentity("Peer", $@"{folderPath}\Second.flac"), size, ".flac",
                bitRate: 320, sampleRate: 44100, length: secondLength),
            new SearchPeerSnapshot("Peer", 2, null, null));
        using var output = new StringWriter();
        var folder = new AlbumFolder("Peer", folderPath,
            [new AlbumFile(query, first), new AlbumFile(query, second)]);
        using (Printing.RedirectOutput(output))
            Printing.PrintAlbumHeader(folder);
        string header = output.ToString();
        StringAssert.Contains(header, expectedDuration);
        StringAssert.Contains(header, "320 kbps");
        Assert.IsFalse(header.Contains("-0", StringComparison.Ordinal));
        StringAssert.Contains(header, size.HasValue ? " MB" : "unknown size");
        string file = Printing.DisplayString(query, first, fullpath: true);
        StringAssert.Contains(file, $"{(firstLength >= 0 ? firstLength : -1)}s");
        Assert.IsFalse(file.Contains("kbps", StringComparison.Ordinal));
        Assert.IsFalse(file.Contains("kHz", StringComparison.Ordinal));
        if (size == null)
        {
            StringAssert.Contains(file, "unknown size");
            StringAssert.Contains(header, "Folder: Root␊Album");
            StringAssert.Contains(file, "Root␊Album");
        }
        Assert.AreEqual(firstLength, first.Length);
        Assert.AreEqual(folderPath, folder.FolderPath);
    }
}
