using System.Collections.Concurrent;
using Sockseek.Core.Extractors;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class SourceMutationExecutor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

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

    private static async Task ClearTextLineAsync(string path, int lineNumber)
    {
        if (lineNumber <= 0 || !File.Exists(path)) return;

        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(path, System.Text.Encoding.UTF8);
            var idx = lineNumber - 1;
            if (idx < 0 || idx >= lines.Length) return;

            lines[idx] = "";
            await Utils.WriteAllLinesAsync(path, lines, '\n');
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task ClearCsvRowAsync(string path, int lineNumber, int columnCount)
    {
        if (lineNumber <= 0 || !File.Exists(path)) return;

        var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(path, System.Text.Encoding.UTF8);
            var idx = lineNumber - 1;
            if (idx < 0 || idx >= lines.Length) return;

            lines[idx] = new string(',', Math.Max(0, columnCount - 1));
            await Utils.WriteAllLinesAsync(path, lines, '\n');
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

        var spotify = new Sockseek.Core.Extractors.Spotify(settings.ClientId, settings.ClientSecret, settings.Token, settings.Refresh);
        await spotify.Authorize(login: true, needModify: true);
        await spotify.RemoveTrackFromPlaylist(mutation.Source, mutation.TrackUri);
    }
}
