using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Sockseek.Core.Models;

public enum SourceMutationKind
{
    ClearTextLine,
    ClearCsvRow,
    RemoveSpotifyPlaylistTrack,
}

public sealed record SourceMutation(
    SourceMutationKind Kind,
    string Source,
    int LineNumber = 0,
    int ItemNumber = 1,
    int CsvColumnCount = 0,
    string? TrackUri = null,
    string? RecordHash = null)
{
    [JsonIgnore]
    public string Key => $"{Kind}|{Source}|{LineNumber}|{TrackUri ?? string.Empty}";

    internal static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static SourceMutation ClearTextLine(string path, int lineNumber, string reviewedLine, int itemNumber = 1)
        => new(SourceMutationKind.ClearTextLine, path, lineNumber, itemNumber, RecordHash: Fingerprint(reviewedLine));

    public static SourceMutation ClearCsvRow(string path, int lineNumber, int itemNumber, int columnCount, string recordHash)
        => new(SourceMutationKind.ClearCsvRow, path, lineNumber, itemNumber, columnCount, RecordHash: recordHash);

    public static SourceMutation RemoveSpotifyPlaylistTrack(string playlistId, string trackUri, int itemNumber = 1)
        => new(SourceMutationKind.RemoveSpotifyPlaylistTrack, playlistId, 0, itemNumber, TrackUri: trackUri);
}
