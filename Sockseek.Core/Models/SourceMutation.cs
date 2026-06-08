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
    string? TrackUri = null)
{
    public string Key => $"{Kind}|{Source}|{LineNumber}|{TrackUri ?? string.Empty}";

    public static SourceMutation ClearTextLine(string path, int lineNumber, int itemNumber = 1)
        => new(SourceMutationKind.ClearTextLine, path, lineNumber, itemNumber);

    public static SourceMutation ClearCsvRow(string path, int lineNumber, int itemNumber, int columnCount)
        => new(SourceMutationKind.ClearCsvRow, path, lineNumber, itemNumber, columnCount);

    public static SourceMutation RemoveSpotifyPlaylistTrack(string playlistId, string trackUri, int itemNumber = 1)
        => new(SourceMutationKind.RemoveSpotifyPlaylistTrack, playlistId, 0, itemNumber, TrackUri: trackUri);
}
