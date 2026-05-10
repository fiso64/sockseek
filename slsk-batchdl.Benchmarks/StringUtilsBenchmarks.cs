using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Sldl.Core;

namespace slsk_batchdl.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class StringUtilsBenchmarks
{
    private string[] _paths = null!;
    private const string SearchTerm = "Electric Light Orchestra";

    [Params(1_000_000)]
    public int FileCount { get; set; }

    [Params(true, false)]
    public bool IsHit { get; set; }

    [GlobalSetup]
    public void Setup()
        => _paths = IsHit
            ? BenchmarkDataFactory.CreateHitPaths(FileCount)
            : BenchmarkDataFactory.CreateMissPaths(FileCount);

    [Benchmark(Baseline = true)]
    public int ContainsWithBoundary_Old()
    {
        int hits = 0;
        foreach (var path in _paths)
            if (Utils.ContainsWithBoundary(path, SearchTerm, ignoreCase: true)) hits++;
        return hits;
    }

    [Benchmark]
    public int ContainsWithBoundary_New()
    {
        int hits = 0;
        foreach (var path in _paths)
            if (StringBoundaryExtensions.ContainsWithBoundary(path, SearchTerm, ignoreCase: true)) hits++;
        return hits;
    }

    [Benchmark]
    public int ContainsWithBoundaryIgnoreWs_Old()
    {
        int hits = 0;
        foreach (var path in _paths)
            if (Utils.ContainsWithBoundaryIgnoreWs(path, SearchTerm, ignoreCase: true, acceptLeftDigit: true)) hits++;
        return hits;
    }

    [Benchmark]
    public int ContainsWithBoundaryIgnoreWs_New()
    {
        int hits = 0;
        foreach (var path in _paths)
            if (StringBoundaryExtensions.ContainsWithBoundaryIgnoreWs(path, SearchTerm, ignoreCase: true, acceptLeftDigit: true)) hits++;
        return hits;
    }
}

file static class StringBoundaryExtensions
{
    private static readonly bool[] s_asciiBoundary = BuildAsciiBoundaryTable();

    private static bool[] BuildAsciiBoundaryTable()
    {
        var t = new bool[128];
        const string chars = " -|./\\_()[],:?!;@#*=+{}'\"$^&`~%<>";
        foreach (var c in chars)
            t[c] = true;
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsBoundaryChar(char c)
        => (uint)c < 128u
            ? s_asciiBoundary[c]
            : c is '—' or '–' or '―'
            or '「' or '」' or '【' or '】' or '『' or '』' or '《' or '》'
            or '\u2018' or '\u2019' or '\u201C' or '\u201D'
            or '～' or '•' or '·' or '«' or '»';

    public static bool ContainsWithBoundary(this string str, string? value, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            bool hasLeftBoundary  = index == 0 || IsBoundaryChar(str[index - 1]);
            int  rightIdx         = index + value.Length;
            bool hasRightBoundary = rightIdx >= str.Length || IsBoundaryChar(str[rightIdx]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;
            index++;
        }
        return false;
    }

    public static bool ContainsWithBoundaryIgnoreWs(
        this string str, string? value, bool ignoreCase = false, bool acceptLeftDigit = false)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        if (str.Length == 0)
            return false;

        var comp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int index = 0;
        while ((index = str.IndexOf(value, index, comp)) != -1)
        {
            int leftIndex = index - 1;
            while (leftIndex >= 0 && str[leftIndex] == ' ')
                leftIndex--;

            bool hasLeftBoundary =
                leftIndex < 0
                || IsBoundaryChar(str[leftIndex])
                || (acceptLeftDigit && leftIndex < index - 1 && char.IsDigit(str[leftIndex]));

            int rightIndex = index + value.Length;
            while (rightIndex < str.Length && str[rightIndex] == ' ')
                rightIndex++;
            bool hasRightBoundary = rightIndex >= str.Length || IsBoundaryChar(str[rightIndex]);

            if (hasLeftBoundary && hasRightBoundary)
                return true;
            index++;
        }
        return false;
    }
}
