using Sockseek.Core.Extractors;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Services;

public sealed class SourceMutationExecutor
{
    public async Task ApplyAsync(SourceMutation mutation, DownloadSettings settings)
    {
        switch (mutation.Kind)
        {
            case SourceMutationKind.ClearTextLine:
            case SourceMutationKind.ClearCsvRow:
                if ((await ClearReviewedRowsAsync(mutation.Source, [mutation])).Count > 0)
                    throw new InvalidDataException(ChangedRowMessage);
                break;

            case SourceMutationKind.RemoveSpotifyPlaylistTrack:
                await RemoveSpotifyPlaylistTrackAsync(mutation, settings.Spotify);
                break;
        }
    }

    internal const string ChangedRowMessage = "The source row no longer matches the reviewed request. It was kept.";

    internal static async Task<HashSet<SourceMutation>> ClearReviewedRowsAsync(string path, IReadOnlyList<SourceMutation> records)
    {
        var unmatched = records.ToHashSet();
        await Task.Run(() => Utils.UpdateFile(path, () => ClearMatchingRows(path, records, unmatched)));
        return unmatched;
    }

    private static void ClearMatchingRows(string path, IReadOnlyList<SourceMutation> records, HashSet<SourceMutation> unmatched)
    {
        bool csv = records[0].Kind == SourceMutationKind.ClearCsvRow;
        var requested = records.ToLookup(record => record.LineNumber);
        int last = requested.Max(group => group.Key);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long originalLength = original.Length;
        Span<byte> prefix = stackalloc byte[4];
        int prefixSize = original.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        original.Position = 0;
        using var reader = new SourceRecordReader(original);
        reader.Peek(); // Detect UTF-8, UTF-16 or UTF-32 before measuring record boundaries.
        var encoding = reader.CurrentEncoding;
        long offset = prefix[..prefixSize].StartsWith(encoding.Preamble) ? encoding.Preamble.Length : 0;
        string? header = null;
        var replacements = new List<(long Start, long End, byte[] Bytes)>();
        for (int number = 1; number <= last && reader.ReadRecord(csv) is { } fields; number++)
        {
            string raw = reader.RawRecord;
            if (csv && header == null)
            {
                if (fields.Any(field => !string.IsNullOrWhiteSpace(field))) header = reader.Fingerprint();
            }
            else if (requested.Contains(number))
            {
                bool blank = fields.All(string.IsNullOrWhiteSpace);
                string hash = reader.Fingerprint(header ?? "");
                bool clear = false;
                foreach (var record in requested[number])
                {
                    if (record.RecordHash == null || !blank && record.RecordHash != hash) continue;
                    unmatched.Remove(record);
                    clear = !blank;
                }
                if (clear)
                {
                    long end = offset + encoding.GetByteCount(raw.TrimEnd('\r', '\n'));
                    replacements.Add((offset, end, encoding.GetBytes(new string(',', fields.Count - 1))));
                }
            }
            offset += encoding.GetByteCount(raw);
        }
        if (replacements.Count == 0) return;
        Utils.WriteAtomically(path, output =>
        {
            original.Position = 0;
            byte[] buffer = new byte[65536];
            foreach (var (start, end, replacement) in replacements)
            {
                while (original.Position < start)
                {
                    int count = (int)Math.Min(buffer.Length, start - original.Position);
                    original.ReadExactly(buffer, 0, count);
                    output.Write(buffer, 0, count);
                }
                output.Write(replacement);
                original.Position = end;
            }
            original.CopyTo(output);
            if (File.GetLastWriteTimeUtc(path) != modified || new FileInfo(path).Length != originalLength)
                throw new IOException("The source changed while the completed rows were being removed.");
        });
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
