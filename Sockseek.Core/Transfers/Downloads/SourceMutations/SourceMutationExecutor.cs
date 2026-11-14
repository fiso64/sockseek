using Sockseek.Core.Extractors;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class SourceMutationExecutor
{
    private const int FileLockStripeCount = 64;
    private static readonly SemaphoreSlim[] FileLocks = Enumerable.Range(0, FileLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public async Task ApplyAsync(SourceMutation mutation, DownloadSettings settings)
    {
        switch (mutation.Kind)
        {
            case SourceMutationKind.ClearTextLine:
                await ClearTextLineAsync(mutation.Source, mutation.LineNumber);
                break;

            case SourceMutationKind.ClearCsvRow:
                await ClearCsvRowAsync(mutation.Source, mutation.LineNumber, mutation.CsvColumnCount);
                break;

            case SourceMutationKind.RemoveSpotifyPlaylistTrack:
                await RemoveSpotifyPlaylistTrackAsync(mutation, settings.Spotify);
                break;
        }
    }

    private static Task ClearTextLineAsync(string path, int lineNumber)
        => RewriteLineAsync(path, lineNumber, "");

    private static Task ClearCsvRowAsync(string path, int lineNumber, int columnCount)
        => RewriteLineAsync(
            path,
            lineNumber,
            new string(',', Math.Max(0, columnCount - 1)));

    private static async Task RewriteLineAsync(
        string path,
        int lineNumber,
        string replacement)
    {
        if (lineNumber <= 0 || !File.Exists(path)) return;

        uint hash = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        SemaphoreSlim gate = FileLocks[hash % (uint)FileLockStripeCount];
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lines = await File.ReadAllLinesAsync(path, System.Text.Encoding.UTF8);
            var idx = lineNumber - 1;
            if (idx < 0 || idx >= lines.Length) return;

            lines[idx] = replacement;
            await Utils.WriteAllLinesAsync(path, lines, '\n').ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task RemoveSpotifyPlaylistTrackAsync(SourceMutation mutation, SpotifySettings settings)
    {
        if (string.IsNullOrWhiteSpace(mutation.Source) || string.IsNullOrWhiteSpace(mutation.TrackUri))
            return;

        using var spotify = new Sockseek.Core.Extractors.Spotify(
            settings.ClientId ?? "",
            settings.ClientSecret ?? "",
            settings.Token ?? "",
            settings.Refresh ?? "");
        await spotify.Authorize(login: true, needModify: true);
        await spotify.RemoveTrackFromPlaylist(mutation.Source, mutation.TrackUri);
    }
}
