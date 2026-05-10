using BenchmarkDotNet.Attributes;
using Sldl.Core;

namespace slsk_batchdl.Benchmarks;

public enum InferNameNormalizationCase
{
    CleanShort,
    DirtyShort,
    DiacriticsShort,
    FtShort,
    CleanLong,
    DirtyLong,
    DiacriticsLong
}

[Config(typeof(QuickBenchmarkConfig))]
public class InferNameNormalizationBenchmarks
{
    private string[] _strings = null!;

    [Params(1_000_000)]
    public int Count { get; set; }

    [Params(
        InferNameNormalizationCase.CleanShort,
        InferNameNormalizationCase.DirtyShort,
        InferNameNormalizationCase.DiacriticsShort,
        InferNameNormalizationCase.FtShort,
        InferNameNormalizationCase.CleanLong,
        InferNameNormalizationCase.DirtyLong,
        InferNameNormalizationCase.DiacriticsLong)]
    public InferNameNormalizationCase StringCase { get; set; }

    [Params(true, false)]
    public bool RemoveFt { get; set; }

    [GlobalSetup]
    public void Setup()
        => _strings = CreateStrings(Count, StringCase);

    [Benchmark(Baseline = true)]
    public int OldChain()
    {
        int n = 0;
        foreach (var s in _strings)
            n += NormalizeOldChain(s, RemoveFt).Length;
        return n;
    }

    [Benchmark]
    public int NewHelper()
    {
        int n = 0;
        foreach (var s in _strings)
            n += NormalizeNewHelper(s, RemoveFt).Length;
        return n;
    }

    private static string NormalizeOldChain(string str, bool removeFt)
    {
        var result = str
            .Replace('—', '-')
            .Replace('_', ' ')
            .Replace('[', '(')
            .Replace(']', ')')
            .ReplaceInvalidChars("", true);

        if (removeFt)
            result = result.RemoveFt();

        return result.RemoveConsecutiveWs().Trim();
    }

    private static string NormalizeNewHelper(string str, bool removeFt)
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

    private static string[] CreateStrings(int count, InferNameNormalizationCase stringCase)
    {
        string[] source = stringCase switch
        {
            InferNameNormalizationCase.CleanShort =>
            [
                "Electric Light Orchestra",
                "Steely Dan",
                "KNOWER",
                "Tatsuro Yamashita",
            ],

            InferNameNormalizationCase.DirtyShort =>
            [
                "Electric_Light Orchestra: Twilight",
                "Steely Dan - \"Peg\"?",
                "KNOWER___Overtime",
                "Herbie Hancock | Chameleon * Bonus",
            ],

            InferNameNormalizationCase.DiacriticsShort =>
            [
                "Beyoncé Déjà Vu",
                "Sigur Rós Ágætis byrjun",
                "Café Crème à la mode",
                "João Gilberto Águas de Março",
            ],

            InferNameNormalizationCase.FtShort =>
            [
                "Beyoncé feat. Jay-Z",
                "Electric Light Orchestra ft. Someone",
                "Artist (feat. Guest) Title",
                "Artist [ft. Guest] Title",
            ],

            InferNameNormalizationCase.CleanLong =>
            [
                "Electric Light Orchestra Twilight Time Remaster Deluxe Edition Disc One",
                "Steely Dan Peg Aja Original Album Version Lossless Rip",
                "KNOWER Overtime Life Complete Session Master Recording",
                "Tatsuro Yamashita Sparkle For You Studio Album Remaster",
            ],

            InferNameNormalizationCase.DirtyLong =>
            [
                " Electric_Light Orchestra: Twilight [1975 Remaster] / Deluxe Edition ",
                "Steely__Dan - \"Peg\"? [Aja] <Verified> * Bonus",
                "KNOWER___Overtime | Life [Remastered] Disc 1/2",
                "Herbie Hancock — Chameleon: The Head Hunters Sessions",
            ],

            InferNameNormalizationCase.DiacriticsLong =>
            [
                "Beyoncé feat. Jay-Z - Déjà Vu [B'Day Deluxe Edition]",
                "Sigur Rós - Ágætis byrjun (Remastered) Disc 1",
                "Café Tacvba - Crème à la mode São Paulo Session",
                "João Gilberto - Águas de Março / Françoise Hardy",
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(stringCase), stringCase, null),
        };

        var r = new string[count];
        for (int i = 0; i < count; i++)
            r[i] = source[i % source.Length];

        return r;
    }
}