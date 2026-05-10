using BenchmarkDotNet.Attributes;
using Sldl.Core;
using Sldl.Core.Models;

namespace slsk_batchdl.Benchmarks;

public enum StrictStringPreprocessStringCase
{
    CleanTitle,
    DirtyTitle,
    DiacriticsTitle,
    CleanBaseName,
    DirtyBaseName,
    DiacriticsBaseName
}

[Config(typeof(QuickBenchmarkConfig))]
public class StrictStringPreprocessBenchmarks
{
    private string[] _strings = null!;

    [Params(1_000_000)]
    public int Count { get; set; }

    [Params(
        StrictStringPreprocessStringCase.CleanTitle,
        StrictStringPreprocessStringCase.DirtyTitle,
        StrictStringPreprocessStringCase.DiacriticsTitle,
        StrictStringPreprocessStringCase.CleanBaseName,
        StrictStringPreprocessStringCase.DirtyBaseName,
        StrictStringPreprocessStringCase.DiacriticsBaseName)]
    public StrictStringPreprocessStringCase StringCase { get; set; }

    [Params(true, false)]
    public bool DiacrRemove { get; set; }

    [GlobalSetup]
    public void Setup()
        => _strings = BenchmarkDataFactory.CreateStrictStringPreprocessStrings(Count, StringCase);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
            n += FileConditions.StrictStringPreprocess(s, DiacrRemove).Length;
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var s in _strings)
            n += StrictStringPreprocessNew.StrictStringPreprocess(s, DiacrRemove).Length;
        return n;
    }

    [Benchmark]
    public int New_LazyBuffer()
    {
        int n = 0;
        foreach (var s in _strings)
            n += StrictStringPreprocessLazyBuffer.StrictStringPreprocess(s, DiacrRemove).Length;
        return n;
    }
}

file static class StrictStringPreprocessNew
{
    public static string StrictStringPreprocess(string str, bool diacrRemove = true)
    {
        if (str.Length == 0)
            return str;

        int length = 0;
        int trimmedLength = 0;
        bool previousWasSpace = false;
        bool hasOutput = false;
        bool changed = false;

        for (int i = 0; i < str.Length; i++)
        {
            char original = str[i];
            char c = NormalizeStrictChar(original, diacrRemove);

            if (c != original)
                changed = true;

            if (!hasOutput && char.IsWhiteSpace(c))
            {
                changed = true;
                continue;
            }

            if (c == ' ')
            {
                if (previousWasSpace)
                {
                    changed = true;
                    continue;
                }

                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            length++;
            hasOutput = true;

            if (!char.IsWhiteSpace(c))
                trimmedLength = length;
        }

        if (trimmedLength != length)
            changed = true;

        if (!changed)
            return str;

        if (trimmedLength == 0)
            return string.Empty;

        return string.Create(trimmedLength, (str, diacrRemove), static (dst, state) =>
        {
            int di = 0;
            bool previousWasSpace = false;
            bool hasOutput = false;

            for (int i = 0; i < state.str.Length && di < dst.Length; i++)
            {
                char c = NormalizeStrictChar(state.str[i], state.diacrRemove);

                if (!hasOutput && char.IsWhiteSpace(c))
                    continue;

                if (c == ' ')
                {
                    if (previousWasSpace)
                        continue;

                    previousWasSpace = true;
                }
                else
                {
                    previousWasSpace = false;
                }

                dst[di++] = c;
                hasOutput = true;
            }
        });
    }

    private static char NormalizeStrictChar(char c, bool diacrRemove)
    {
        if (c == '_' || IsStrictInvalidChar(c))
            return ' ';

        return diacrRemove && c > 127 ? c.RemoveDiacritics() : c;
    }

    private static bool IsStrictInvalidChar(char c)
        => c is ':' or '|' or '?' or '>' or '<' or '*' or '"';
}