using System.Globalization;
using System.Text;

namespace Sockseek.Core.UserProfiles;

public static class UserProfileText
{
    public const int MaximumDescriptionUtf8Bytes = 64 * 1024;

    /// <summary>
    /// Produces bounded plain text without bidi/format controls or malformed
    /// scalar fragments. Line endings are normalized to LF.
    /// </summary>
    public static string NormalizeDescription(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var result = new StringBuilder(Math.Min(value.Length, MaximumDescriptionUtf8Bytes));
        int utf8Bytes = 0;
        bool previousWasCarriageReturn = false;

        foreach (Rune rune in value.EnumerateRunes())
        {
            Rune output = rune;
            if (rune.Value == '\r')
            {
                output = new Rune('\n');
                previousWasCarriageReturn = true;
            }
            else if (rune.Value == '\n' && previousWasCarriageReturn)
            {
                previousWasCarriageReturn = false;
                continue;
            }
            else
            {
                previousWasCarriageReturn = false;
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
                {
                    output = new Rune('\n');
                }
                else if (category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate
                    && rune.Value != '\t'
                    && rune.Value != '\n')
                {
                    continue;
                }
            }

            int size = output.Utf8SequenceLength;
            if (utf8Bytes + size > MaximumDescriptionUtf8Bytes)
                break;
            result.Append(output);
            utf8Bytes += size;
        }

        return result.ToString();
    }
}
