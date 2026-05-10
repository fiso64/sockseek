using System.Buffers;
using Sldl.Core;

namespace slsk_batchdl.Benchmarks;

internal static class StrictStringPreprocessLazyBuffer
{
    public static string StrictStringPreprocess(string str, bool diacrRemove = true)
    {
        if (str.Length == 0)
            return str;

        char[]? buffer = null;

        try
        {
            int length = 0;
            int trimmedLength = 0;
            bool previousWasSpace = false;
            bool hasOutput = false;
            bool changed = false;

            for (int i = 0; i < str.Length; i++)
            {
                char original = str[i];
                char c = NormalizeStrictChar(original, diacrRemove);
                bool charChanged = c != original;

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

                if ((changed || charChanged) && buffer == null)
                {
                    buffer = ArrayPool<char>.Shared.Rent(str.Length);
                    str.AsSpan(0, length).CopyTo(buffer);
                }

                if (buffer != null)
                    buffer[length] = c;

                length++;
                hasOutput = true;
                changed |= charChanged;

                if (!char.IsWhiteSpace(c))
                    trimmedLength = length;
            }

            if (trimmedLength == 0)
                return string.Empty;

            if (buffer == null)
                return trimmedLength == str.Length ? str : str[..trimmedLength];

            return new string(buffer, 0, trimmedLength);
        }
        finally
        {
            if (buffer != null)
                ArrayPool<char>.Shared.Return(buffer);
        }
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