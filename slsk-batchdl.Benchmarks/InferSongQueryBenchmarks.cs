using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using Sldl.Core;
using Sldl.Core.Models;
using Sldl.Core.Services;

namespace slsk_batchdl.Benchmarks;

public enum InferSongQueryCase
{
    CleanArtistTitle,
    TrackNumStart,
    TrackNumMiddle,
    TrackNumMiddleDash,
    ThreePartAlbumArtistTitle,
    BracketedArtist,
    RemixLike,
    DirtyMetadata,
    DiacriticsAndFt
}

[Config(typeof(QuickBenchmarkConfig))]
public class InferSongQueryBenchmarks
{
    private (string Filename, SongQuery Query)[] _cases = null!;

    [Params(100_000)]
    public int Count { get; set; }

    [Params(
        InferSongQueryCase.CleanArtistTitle,
        InferSongQueryCase.TrackNumStart,
        InferSongQueryCase.TrackNumMiddle,
        InferSongQueryCase.TrackNumMiddleDash,
        InferSongQueryCase.ThreePartAlbumArtistTitle,
        InferSongQueryCase.BracketedArtist,
        InferSongQueryCase.RemixLike,
        InferSongQueryCase.DirtyMetadata,
        InferSongQueryCase.DiacriticsAndFt)]
    public InferSongQueryCase QueryCase { get; set; }

    [GlobalSetup]
    public void Setup()
        => _cases = BenchmarkDataFactory.CreateInferSongQueryCases(Count, QueryCase);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var x in _cases)
        {
            var q = Searcher.InferSongQuery(x.Filename, x.Query);
            n += q.Artist.Length + q.Title.Length * 3 + q.Album.Length * 7 + (q.ArtistMaybeWrong ? 11 : 0);
        }
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var x in _cases)
        {
            var q = InferSongQueryNew.InferSongQuery(x.Filename, x.Query);
            n += q.Artist.Length + q.Title.Length * 3 + q.Album.Length * 7 + (q.ArtistMaybeWrong ? 11 : 0);
        }
        return n;
    }
}

file static class InferSongQueryNew
{
    private static readonly Regex TrackNumStartRegex =
        new(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)", RegexOptions.Compiled);

    private static readonly Regex TrackNumMiddleRegex =
        new(@"(?<= - )((\d-)?\d{2,3}|\d{2,3}\.?)\s+", RegexOptions.Compiled);

    private static readonly Regex TrackNumMiddleAltRegex =
        new(@"\s+-(\d{2,3})-\s+", RegexOptions.Compiled);

    private static readonly Regex TrackNumPlaceholderRegex =
        new(@"-\s*<<tracknum>>\s*-", RegexOptions.Compiled);

    public static SongQuery InferSongQuery(string filename, SongQuery defaultQuery)
    {
        string artist = defaultQuery.Artist;
        string title = defaultQuery.Title;
        string album = defaultQuery.Album;
        bool artistMaybeWrong = defaultQuery.ArtistMaybeWrong;

        filename = Utils.GetFileNameWithoutExtSlsk(filename);

        if (filename.Length >= 6 && filename[0] == '(' && char.IsDigit(filename[1]) && char.IsDigit(filename[2])
            && filename[3] == ')' && filename[4] == ' ' && filename[5] == '[')
        {
            int close = filename.IndexOf(']', 6);
            if (close > 6)
            {
                int titleStart = close + 1;
                if (titleStart < filename.Length && filename[titleStart] == ' ') titleStart++;
                if (titleStart < filename.Length)
                {
                    artist = filename[6..close];
                    title = filename[titleStart..];
                    return new SongQuery(defaultQuery) { Artist = artist.RemoveFt(), Title = title.RemoveFt(), ArtistMaybeWrong = false };
                }
            }
        }

        filename = filename.Replace(" — ", " - ").Replace('_', ' ').Trim().RemoveConsecutiveWs();

        var startMatch = TrackNumStartRegex.Match(filename);
        if (startMatch.Success)
        {
            filename = filename[startMatch.Length..].Trim();
            if (filename.StartsWith("- ")) filename = filename[2..].Trim();
        }
        else
        {
            Regex? reg = null;
            var middleMatch = TrackNumMiddleRegex.Match(filename);
            if (middleMatch.Success)
                reg = TrackNumMiddleRegex;
            else if (TrackNumMiddleAltRegex.IsMatch(filename))
                reg = TrackNumMiddleAltRegex;

            if (reg != null && !reg.IsMatch(defaultQuery.ToString(noInfo: true)))
            {
                filename = reg.Replace(filename, "<<tracknum>>", 1).Trim();
                filename = TrackNumPlaceholderRegex.Replace(filename, "-");
                filename = filename.Replace("<<tracknum>>", "");
            }
        }

        string aname = NormalizeInferName(artist, removeFt: true);
        string tname = NormalizeInferName(title, removeFt: true);
        string alname = NormalizeInferName(album, removeFt: true);
        string fname = NormalizeInferName(filename, removeFt: false);

        bool maybeRemix = MaybeRemix(fname, aname);
        string[] parts = fname.Split([" - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] realParts = filename.Split([" - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != realParts.Length) realParts = parts;

        if (parts.Length == 1)
        {
            if (maybeRemix) artistMaybeWrong = true;
            title = parts[0];
        }
        else if (parts.Length == 2)
        {
            artist = realParts[0];
            title = realParts[1];
            if (maybeRemix)
                artistMaybeWrong = true;
            else if (!parts[0].ContainsIgnoreCase(aname) || !parts[1].ContainsIgnoreCase(tname))
                artistMaybeWrong = true;
        }
        else if (parts.Length == 3)
        {
            bool hasTitle = tname.Length > 0 && parts[2].ContainsIgnoreCase(tname);
            if (hasTitle) title = realParts[2];

            int artistPos = -1, albumPos = -1;
            if (aname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(aname)) artistPos = 0;
                else if (parts[1].ContainsIgnoreCase(aname)) artistPos = 1;
                else artistMaybeWrong = true;
            }
            if (alname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(alname)) albumPos = 0;
                else if (parts[1].ContainsIgnoreCase(alname)) albumPos = 1;
            }
            if (artistPos >= 0 && artistPos == albumPos) { artistPos = 0; albumPos = 1; }
            if (artistPos == -1 && maybeRemix) { artistMaybeWrong = true; artistPos = 0; albumPos = 1; }

            if (artistPos == -1 && albumPos == -1)
            { artistMaybeWrong = true; artist = realParts[0] + " - " + realParts[1]; }
            else if (artistPos >= 0)
            { artist = parts[artistPos]; }

            title = parts[2];
        }
        else
        {
            if (aname.Length > 0)
            {
                var s = parts.Select((p, i) => (p, i)).Where(x => x.p.ContainsIgnoreCase(aname));
                if (s.Any())
                {
                    int pos = s.MinBy(x => Math.Abs(x.p.Length - aname.Length)).i;
                    artist = parts[pos];
                }
            }
            if (tname.Length > 0)
            {
                int artistPos2 = artist == defaultQuery.Artist ? -1 :
                    parts.Select((p, i) => (p, i)).FirstOrDefault(x => x.p == artist).i;
                var ss = parts.Select((p, i) => (p, i)).Where(x => x.i != artistPos2 && x.p.ContainsIgnoreCase(tname));
                if (ss.Any())
                    title = parts[ss.MinBy(x => Math.Abs(x.p.Length - tname.Length)).i];
            }
        }

        if (title.Length == 0)
        {
            title = fname;
            artistMaybeWrong = true;
        }
        else if (artist.Length > 0 && !title.ContainsIgnoreCase(defaultQuery.Title) && !artist.ContainsIgnoreCase(defaultQuery.Artist))
        {
            string[] x = [artist, album, title];
            var perm = (0, 1, 2);
            (int, int, int)[] permutations = [(0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)];
            foreach (var p in permutations)
            {
                if (x[p.Item1].ContainsIgnoreCase(defaultQuery.Artist) && x[p.Item3].ContainsIgnoreCase(defaultQuery.Title))
                { perm = p; break; }
            }
            artist = x[perm.Item1];
            album = x[perm.Item2];
            title = x[perm.Item3];
        }

        return new SongQuery(defaultQuery)
        {
            Artist = artist.RemoveFt().Trim(),
            Title = title.RemoveFt().Trim(),
            Album = album.Trim(),
            ArtistMaybeWrong = artistMaybeWrong,
        };
    }

    private static string NormalizeInferName(string str, bool removeFt)
    {
        if (str.Length == 0)
            return str;

        Span<char> buffer = str.Length <= 256 ? stackalloc char[str.Length] : new char[str.Length];
        int length = 0;
        bool changed = false;

        foreach (char original in str)
        {
            char c = original switch
            {
                '—' => '-',
                '_' => ' ',
                '[' => '(',
                ']' => ')',
                _ => original,
            };

            if (IsInvalidInferNameChar(c))
            {
                changed = true;
                continue;
            }

            if (c != original)
                changed = true;

            buffer[length++] = c;
        }

        string result = changed ? new string(buffer[..length]) : str;
        if (removeFt)
            result = result.RemoveFt();

        return result.RemoveConsecutiveWs().Trim();
    }

    private static bool IsInvalidInferNameChar(char c)
        => c is ':' or '|' or '?' or '>' or '<' or '*' or '"' or '/' or '\\';

    private static bool MaybeRemix(string fname, string aname)
    {
        if (aname.Length == 0)
            return false;

        int searchStart = 0;
        while (searchStart < fname.Length)
        {
            int open = fname.IndexOf('(', searchStart);
            if (open < 0)
                return false;

            int contentStart = open + 1;
            if (contentStart + aname.Length + 1 < fname.Length
                && fname.AsSpan(contentStart, aname.Length).Equals(aname, StringComparison.OrdinalIgnoreCase)
                && fname[contentStart + aname.Length] == ' '
                && fname.IndexOf(')', contentStart + aname.Length + 2) >= 0)
            {
                return true;
            }

            searchStart = open + 1;
        }

        return false;
    }
}