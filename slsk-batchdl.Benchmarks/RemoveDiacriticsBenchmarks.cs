using BenchmarkDotNet.Attributes;
using Sldl.Core;

namespace slsk_batchdl.Benchmarks;

[Config(typeof(QuickBenchmarkConfig))]
public class RemoveDiacriticsBenchmarks
{
    private string[] _strings = null!;

    [Params(100_000)]
    public int Count { get; set; }

    [Params(false, true)]
    public bool HasDiacritics { get; set; }

    [Params(false, true)]
    public bool IsLong { get; set; }

    [GlobalSetup]
    public void Setup()
        => _strings = BenchmarkDataFactory.CreateDiacriticStrings(Count, HasDiacritics, IsLong);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
            n += s.RemoveDiacritics().Length;
        return n;
    }

    [Benchmark]
    public int New()
    {
        int n = 0;
        foreach (var s in _strings)
            n += RemoveDiacriticsNew.RemoveDiacritics(s).Length;
        return n;
    }
}

[Config(typeof(QuickBenchmarkConfig))]
public class RemoveDiacriticsIfExistBenchmarks
{
    private string[] _strings = null!;

    [Params(100_000)]
    public int Count { get; set; }

    [Params(false, true)]
    public bool HasDiacritics { get; set; }

    [Params(false, true)]
    public bool IsLong { get; set; }

    [GlobalSetup]
    public void Setup()
        => _strings = BenchmarkDataFactory.CreateDiacriticStrings(Count, HasDiacritics, IsLong);

    [Benchmark(Baseline = true)]
    public int Old()
    {
        int n = 0;
        foreach (var s in _strings)
        {
            if (s.RemoveDiacriticsIfExist(out var r))
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
            if (RemoveDiacriticsNew.RemoveDiacriticsIfExist(s, out var r))
                n += r.Length;
        }
        return n;
    }
}

file static class RemoveDiacriticsNew
{
    public static bool RemoveDiacriticsIfExist(string s, out string res)
    {
        res = RemoveDiacritics(s);
        return !ReferenceEquals(res, s);
    }

    public static string RemoveDiacritics(string s)
    {
        if (s.Length == 0)
            return s;

        int first = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 128 && DiacriticChars.ContainsKey(c))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
            return s;

        return string.Create(s.Length, (s, first), static (dst, state) =>
        {
            state.s.AsSpan().CopyTo(dst);

            if (DiacriticChars.TryGetValue(dst[state.first], out char firstReplacement))
                dst[state.first] = firstReplacement;

            for (int i = state.first + 1; i < dst.Length; i++)
                if (dst[i] >= 128 && DiacriticChars.TryGetValue(dst[i], out char replacement))
                    dst[i] = replacement;
        });
    }

    private static readonly Dictionary<char, char> DiacriticChars = new()
    {
        { 'ä', 'a' }, { 'æ', 'a' }, { 'ǽ', 'a' }, { 'œ', 'o' }, { 'ö', 'o' }, { 'ü', 'u' },
        { 'Ä', 'A' }, { 'Ü', 'U' }, { 'Ö', 'O' }, { 'À', 'A' }, { 'Á', 'A' }, { 'Â', 'A' },
        { 'Ã', 'A' }, { 'Å', 'A' }, { 'Ǻ', 'A' }, { 'Ā', 'A' }, { 'Ă', 'A' }, { 'Ą', 'A' },
        { 'Ǎ', 'A' }, { 'Ά', 'A' }, { 'Ả', 'A' }, { 'Ạ', 'A' }, { 'Ầ', 'A' }, { 'Ấ', 'A' },
        { 'Ẫ', 'A' }, { 'Ẩ', 'A' }, { 'Ậ', 'A' }, { 'à', 'a' }, { 'á', 'a' }, { 'â', 'a' },
        { 'ã', 'a' }, { 'å', 'a' }, { 'ǻ', 'a' }, { 'ā', 'a' }, { 'ă', 'a' }, { 'ą', 'a' },
        { 'ǎ', 'a' }, { 'ả', 'a' }, { 'ạ', 'a' }, { 'Ç', 'C' }, { 'Ć', 'C' }, { 'Ĉ', 'C' },
        { 'Ċ', 'C' }, { 'Č', 'C' }, { 'ç', 'c' }, { 'ć', 'c' }, { 'ĉ', 'c' }, { 'ċ', 'c' },
        { 'č', 'c' }, { 'Ð', 'D' }, { 'Ď', 'D' }, { 'Đ', 'D' }, { 'ð', 'd' }, { 'ď', 'd' },
        { 'đ', 'd' }, { 'È', 'E' }, { 'É', 'E' }, { 'Ê', 'E' }, { 'Ë', 'E' }, { 'Ē', 'E' },
        { 'Ĕ', 'E' }, { 'Ė', 'E' }, { 'Ę', 'E' }, { 'Ě', 'E' }, { 'Έ', 'E' }, { 'Ẽ', 'E' },
        { 'Ẻ', 'E' }, { 'Ẹ', 'E' }, { 'Ề', 'E' }, { 'Ế', 'E' }, { 'Ễ', 'E' }, { 'Ể', 'E' },
        { 'Ệ', 'E' }, { 'è', 'e' }, { 'é', 'e' }, { 'ê', 'e' }, { 'ë', 'e' }, { 'ē', 'e' },
        { 'ĕ', 'e' }, { 'ė', 'e' }, { 'ę', 'e' }, { 'ě', 'e' }, { 'ẽ', 'e' }, { 'ẻ', 'e' },
        { 'ẹ', 'e' }, { 'Ĝ', 'G' }, { 'Ğ', 'G' }, { 'Ġ', 'G' }, { 'Ģ', 'G' }, { 'ĝ', 'g' },
        { 'ğ', 'g' }, { 'ġ', 'g' }, { 'ģ', 'g' }, { 'Ĥ', 'H' }, { 'Ħ', 'H' }, { 'Ή', 'H' },
        { 'ĥ', 'h' }, { 'ħ', 'h' }, { 'Ì', 'I' }, { 'Í', 'I' }, { 'Î', 'I' }, { 'Ï', 'I' },
        { 'Ĩ', 'I' }, { 'Ī', 'I' }, { 'Ĭ', 'I' }, { 'Ǐ', 'I' }, { 'Į', 'I' }, { 'İ', 'I' },
        { 'Ί', 'I' }, { 'Ϊ', 'I' }, { 'Ỉ', 'I' }, { 'Ị', 'I' }, { 'Ї', 'I' }, { 'ì', 'i' },
        { 'í', 'i' }, { 'î', 'i' }, { 'ï', 'i' }, { 'ĩ', 'i' }, { 'ī', 'i' }, { 'ĭ', 'i' },
        { 'ǐ', 'i' }, { 'į', 'i' }, { 'ı', 'i' }, { 'ΰ', 'y' }, { 'Ĵ', 'J' }, { 'ĵ', 'j' },
        { 'Ķ', 'K' }, { 'ķ', 'k' }, { 'Ĺ', 'L' }, { 'Ļ', 'L' }, { 'Ľ', 'L' }, { 'Ŀ', 'L' },
        { 'Ł', 'L' }, { 'ĺ', 'l' }, { 'ļ', 'l' }, { 'ľ', 'l' }, { 'ŀ', 'l' }, { 'ł', 'l' },
        { 'Ñ', 'N' }, { 'Ń', 'N' }, { 'Ņ', 'N' }, { 'Ň', 'N' }, { 'ñ', 'n' }, { 'ń', 'n' },
        { 'ņ', 'n' }, { 'ň', 'n' }, { 'ŉ', 'n' }, { 'Ò', 'O' }, { 'Ó', 'O' }, { 'Ô', 'O' },
        { 'Õ', 'O' }, { 'Ō', 'O' }, { 'Ŏ', 'O' }, { 'Ǒ', 'O' }, { 'Ő', 'O' }, { 'Ơ', 'O' },
        { 'Ø', 'O' }, { 'Ǿ', 'O' }, { 'Ό', 'O' }, { 'Ỏ', 'O' }, { 'Ọ', 'O' }, { 'Ồ', 'O' },
        { 'Ố', 'O' }, { 'Ỗ', 'O' }, { 'Ổ', 'O' }, { 'Ộ', 'O' }, { 'Ờ', 'O' }, { 'Ớ', 'O' },
        { 'Ỡ', 'O' }, { 'Ở', 'O' }, { 'Ợ', 'O' }, { 'ò', 'o' }, { 'ó', 'o' }, { 'ô', 'o' },
        { 'õ', 'o' }, { 'ō', 'o' }, { 'ŏ', 'o' }, { 'ǒ', 'o' }, { 'ő', 'o' }, { 'ơ', 'o' },
        { 'ø', 'o' }, { 'ǿ', 'o' }, { 'º', 'o' }, { 'ό', 'o' }, { 'ỏ', 'o' }, { 'ọ', 'o' },
        { 'ồ', 'o' }, { 'ố', 'o' }, { 'ỗ', 'o' }, { 'ổ', 'o' }, { 'ộ', 'o' }, { 'ờ', 'o' },
        { 'ớ', 'o' }, { 'ỡ', 'o' }, { 'ở', 'o' }, { 'ợ', 'o' }, { 'Ŕ', 'R' }, { 'Ŗ', 'R' },
        { 'Ř', 'R' }, { 'ŕ', 'r' }, { 'ŗ', 'r' }, { 'ř', 'r' }, { 'Ś', 'S' }, { 'Ŝ', 'S' },
        { 'Ş', 'S' }, { 'Ș', 'S' }, { 'Š', 'S' }, { 'ś', 's' }, { 'ŝ', 's' }, { 'ş', 's' },
        { 'ș', 's' }, { 'š', 's' }, { 'Ț', 'T' }, { 'Ţ', 'T' }, { 'Ť', 'T' }, { 'Ŧ', 'T' },
        { 'Т', 'T' }, { 'ț', 't' }, { 'ţ', 't' }, { 'ť', 't' }, { 'ŧ', 't' }, { 'Ù', 'U' },
        { 'Ú', 'U' }, { 'Û', 'U' }, { 'Ũ', 'U' }, { 'Ū', 'U' }, { 'Ŭ', 'U' }, { 'Ů', 'U' },
        { 'Ű', 'U' }, { 'Ų', 'U' }, { 'Ư', 'U' }, { 'Ǔ', 'U' }, { 'Ǖ', 'U' }, { 'Ǘ', 'U' },
        { 'Ǚ', 'U' }, { 'Ǜ', 'U' }, { 'Ủ', 'U' }, { 'Ụ', 'U' }, { 'Ừ', 'U' }, { 'ё', 'e' },
        { 'Ứ', 'U' }, { 'Ữ', 'U' }, { 'Ử', 'U' }, { 'Ự', 'U' }, { 'ù', 'u' }, { 'ú', 'u' },
        { 'û', 'u' }, { 'ũ', 'u' }, { 'ū', 'u' }, { 'ŭ', 'u' }, { 'ů', 'u' }, { 'ű', 'u' },
        { 'ų', 'u' }, { 'ư', 'u' }, { 'ǔ', 'u' }, { 'ǖ', 'u' }, { 'ǘ', 'u' }, { 'ǚ', 'u' },
        { 'ǜ', 'u' }, { 'ủ', 'u' }, { 'ụ', 'u' }, { 'ừ', 'u' }, { 'ứ', 'u' }, { 'ữ', 'u' },
        { 'ử', 'u' }, { 'ự', 'u' }, { 'Ý', 'Y' }, { 'Ÿ', 'Y' }, { 'Ŷ', 'Y' }, { 'Ύ', 'Y' },
        { 'Ϋ', 'Y' }, { 'Ỳ', 'Y' }, { 'Ỹ', 'Y' }, { 'Ỷ', 'Y' }, { 'Ỵ', 'Y' }, { 'й', 'и' },
        { 'ý', 'y' }, { 'ÿ', 'y' }, { 'ŷ', 'y' }, { 'ỳ', 'y' }, { 'ỹ', 'y' }, { 'ỷ', 'y' },
        { 'ỵ', 'y' }, { 'Ŵ', 'W' }, { 'ŵ', 'w' }, { 'Ź', 'Z' }, { 'Ż', 'Z' }, { 'Ž', 'Z' },
        { 'ź', 'z' }, { 'ż', 'z' }, { 'ž', 'z' }, { 'Æ', 'A' }, { 'ß', 's' }, { 'Œ', 'O' },
        { 'Ё', 'E' },
    };
}