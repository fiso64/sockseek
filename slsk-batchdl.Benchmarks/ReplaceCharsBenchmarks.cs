using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using BenchmarkDotNet.Attributes;
using Sldl.Core;

namespace slsk_batchdl.Benchmarks;

// ── ReplaceInvalidChars (char overload) ────────────────────────────────────

[Config(typeof(QuickBenchmarkConfig))]
public class ReplaceInvalidCharsBenchmarks
{
    private string[] _strings = null!;

    [Params(1_000_000)]
    public int Count { get; set; }

    [Params(true, false)]
    public bool IsHit { get; set; }

    [GlobalSetup]
    public void Setup()
        => _strings = IsHit
            ? BenchmarkDataFactory.CreateInvalidCharHits(Count)
            : BenchmarkDataFactory.CreateInvalidCharMisses(Count);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = Utils.ReplaceInvalidChars(s, ' ', windows: true);
            n += r.Length;
        }
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = ReplaceCharsNew.ReplaceInvalidChars(s, ' ', windows: true);
            n += r.Length;
        }
        return n;
    }
}

// ── ReplaceSpecialChars ────────────────────────────────────────────────────

[Config(typeof(QuickBenchmarkConfig))]
public class ReplaceSpecialCharsBenchmarks
{
    private string[] _strings = null!;

    [Params(1_000_000)]
    public int Count { get; set; }

    [Params(true, false)]
    public bool IsHit { get; set; }

    [Params("", " ")]
    public string ReplaceWith { get; set; } = "";

    [GlobalSetup]
    public void Setup()
        => _strings = IsHit
            ? BenchmarkDataFactory.CreateSpecialCharHits(Count)
            : BenchmarkDataFactory.CreateSpecialCharMisses(Count);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = Utils.ReplaceSpecialChars(s, ReplaceWith);
            n += r.Length;
        }
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = ReplaceCharsNew.ReplaceSpecialChars(s, ReplaceWith);
            n += r.Length;
        }
        return n;
    }
}

public enum LongPathLikeStringCase
{
    CleanSeparatorsOnly,
    MetadataHeavy
}

// ── Longer path-like strings ────────────────────────────────────────────────

[Config(typeof(QuickBenchmarkConfig))]
public class ReplaceInvalidCharsLongPathBenchmarks
{
    private string[] _strings = null!;

    [Params(100_000)]
    public int Count { get; set; }

    [Params(LongPathLikeStringCase.CleanSeparatorsOnly, LongPathLikeStringCase.MetadataHeavy)]
    public LongPathLikeStringCase StringCase { get; set; }

    [Params(true, false)]
    public bool RemoveSlash { get; set; }

    [GlobalSetup]
    public void Setup()
        => _strings = BenchmarkDataFactory.CreateLongPathLikeStrings(
            Count,
            metadataHeavy: StringCase == LongPathLikeStringCase.MetadataHeavy);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = Utils.ReplaceInvalidChars(s, ' ', windows: true, removeSlash: RemoveSlash);
            n += r.Length;
        }
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = ReplaceCharsNew.ReplaceInvalidChars(s, ' ', windows: true, removeSlash: RemoveSlash);
            n += r.Length;
        }
        return n;
    }
}

[Config(typeof(QuickBenchmarkConfig))]
public class ReplaceSpecialCharsLongPathBenchmarks
{
    private string[] _strings = null!;

    [Params(100_000)]
    public int Count { get; set; }

    [Params(LongPathLikeStringCase.CleanSeparatorsOnly, LongPathLikeStringCase.MetadataHeavy)]
    public LongPathLikeStringCase StringCase { get; set; }

    [Params("", " ")]
    public string ReplaceWith { get; set; } = "";

    [GlobalSetup]
    public void Setup()
        => _strings = BenchmarkDataFactory.CreateLongPathLikeStrings(
            Count,
            metadataHeavy: StringCase == LongPathLikeStringCase.MetadataHeavy);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = Utils.ReplaceSpecialChars(s, ReplaceWith);
            n += r.Length;
        }
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            var r = ReplaceCharsNew.ReplaceSpecialChars(s, ReplaceWith);
            n += r.Length;
        }
        return n;
    }
}

// ── New implementations ────────────────────────────────────────────────────

file static class ReplaceCharsNew
{
    private static readonly SearchValues<char> s_windowsInvalid =
        SearchValues.Create([':', '|', '?', '>', '<', '*', '"', '/', '\\']);

    private static readonly SearchValues<char> s_windowsInvalidNoSlash =
        SearchValues.Create([':', '|', '?', '>', '<', '*', '"']);

    private static readonly SearchValues<char> s_platformInvalid =
        SearchValues.Create(Path.GetInvalidFileNameChars());

    private static readonly SearchValues<char> s_platformInvalidNoSlash =
        SearchValues.Create(Path.GetInvalidFileNameChars()
            .Where(static c => c is not '/' and not '\\')
            .ToArray());

    // Must match Utils.ReplaceSpecialChars's special string exactly.
    // Exact chars from Utils.ReplaceSpecialChars's special string (no space).
    private static readonly SearchValues<char> s_special =
        SearchValues.Create(";:'\"|?!<>*/\\[]{}()-\u2013\u2014\u2015&%^$#@+=`~_\u2018\u2019\u201C\u201D\u2022\u00B7\u3010\u3011\u300C\u300D\u300E\u300F\u300A\u300B\uFF5E\u00AB\u00BB");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SearchValues<char> InvalidSet(bool windows, bool removeSlash) => windows
        ? (removeSlash ? s_windowsInvalid : s_windowsInvalidNoSlash)
        : (removeSlash ? s_platformInvalid : s_platformInvalidNoSlash);

    public static string ReplaceInvalidChars(string str, char replaceChar, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        var invalid = InvalidSet(windows, removeSlash);
        int first = str.AsSpan().IndexOfAny(invalid);
        if (first < 0)
            return str;

        return string.Create(str.Length, (str, replaceChar, invalid, first), static (dst, state) =>
        {
            state.str.AsSpan().CopyTo(dst);
            dst[state.first] = state.replaceChar;
            for (int i = state.first + 1; i < dst.Length; i++)
                if (state.invalid.Contains(dst[i]))
                    dst[i] = state.replaceChar;
        });
    }

    public static string ReplaceInvalidChars(string str, string replaceStr, bool windows = false, bool removeSlash = true)
    {
        if (str.Length == 0)
            return str;

        var invalid = InvalidSet(windows, removeSlash);
        int first = str.AsSpan().IndexOfAny(invalid);
        if (first < 0)
            return str;

        if (replaceStr.Length == 1)
            return ReplaceInvalidChars(str, replaceStr[0], windows, removeSlash);

        if (replaceStr.Length == 0)
        {
            int keep = first;
            for (int i = first; i < str.Length; i++)
                if (!invalid.Contains(str[i])) keep++;

            return string.Create(keep, (str, invalid, first), static (dst, state) =>
            {
                state.str.AsSpan()[..state.first].CopyTo(dst);
                int di = state.first;
                for (int i = state.first; i < state.str.Length; i++)
                    if (!state.invalid.Contains(state.str[i]))
                        dst[di++] = state.str[i];
            });
        }

        var sb = new StringBuilder(str.Length * 2);
        sb.Append(str, 0, first);
        sb.Append(replaceStr);
        for (int i = first + 1; i < str.Length; i++)
        {
            if (invalid.Contains(str[i])) sb.Append(replaceStr);
            else sb.Append(str[i]);
        }
        return sb.ToString();
    }

    public static string ReplaceSpecialChars(string str, string replaceStr)
    {
        if (str.Length == 0)
            return str;

        int first = str.AsSpan().IndexOfAny(s_special);
        if (first < 0)
            return str;

        if (replaceStr.Length == 1)
        {
            char rc = replaceStr[0];
            return string.Create(str.Length, (str, rc, first), static (dst, state) =>
            {
                state.str.AsSpan().CopyTo(dst);
                dst[state.first] = state.rc;
                for (int i = state.first + 1; i < dst.Length; i++)
                    if (s_special.Contains(dst[i]))
                        dst[i] = state.rc;
            });
        }

        if (replaceStr.Length == 0)
        {
            int keep = first;
            for (int i = first; i < str.Length; i++)
                if (!s_special.Contains(str[i])) keep++;

            return string.Create(keep, (str, first), static (dst, state) =>
            {
                state.str.AsSpan()[..state.first].CopyTo(dst);
                int di = state.first;
                for (int i = state.first; i < state.str.Length; i++)
                    if (!s_special.Contains(state.str[i]))
                        dst[di++] = state.str[i];
            });
        }

        var sb = new StringBuilder(str.Length * 2);
        sb.Append(str, 0, first);
        sb.Append(replaceStr);
        for (int i = first + 1; i < str.Length; i++)
        {
            if (s_special.Contains(str[i])) sb.Append(replaceStr);
            else sb.Append(str[i]);
        }
        return sb.ToString();
    }
}
