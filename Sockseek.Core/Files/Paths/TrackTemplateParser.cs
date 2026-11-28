using Sockseek.Core.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Sockseek.Core;

public static partial class TrackTemplateParser
{
    private const int CacheCapacity = 128;
    private static readonly Lock cacheLock = new();
    private static readonly Dictionary<string, CachedTemplate> regexCache = [];
    private static readonly Queue<string> cacheOrder = [];

    /// <summary>
    /// Creates a SongQuery by parsing an input string based on a template.
    /// Returns null if the input does not match the template.
    /// </summary>
    public static SongQuery? CreateFromString(string input, string template)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(template);

        (var patternRegex, var fieldNames) = GetOrCreateRegexAndFields(template);
        Match match = patternRegex.Match(input);
        if (!match.Success) return null;

        return ApplyFields(new SongQuery(), fieldNames, match);
    }

    /// <summary>
    /// Attempts to update a SongQuery by parsing an input string against a template.
    /// If the input matches, query is replaced with the updated version and true is returned.
    /// If it does not match, query is unchanged and false is returned.
    /// </summary>
    public static bool TryUpdateSongQuery(string input, string template, ref SongQuery query)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(template);

        (var patternRegex, var fieldNames) = GetOrCreateRegexAndFields(template);
        Match match = patternRegex.Match(input);
        if (!match.Success) return false;

        query = ApplyFields(query, fieldNames, match);
        return true;
    }

    // Builds a new SongQuery from baseQuery with the matched fields overwritten.
    private static SongQuery ApplyFields(
        SongQuery baseQuery,
        IReadOnlyList<string> fieldNames,
        Match match)
    {
        string? artist = null, title = null, album = null;

        for (int i = 0; i < fieldNames.Count; i++)
        {
            string value = match.Groups[i + 1].Value.Trim();
            switch (fieldNames[i].ToLowerInvariant())
            {
                case "artist": artist = value; break;
                case "title": title = value; break;
                case "album": album = value; break;
            }
        }

        return new SongQuery(baseQuery)
        {
            Artist = artist ?? baseQuery.Artist,
            Title = title ?? baseQuery.Title,
            Album = album ?? baseQuery.Album,
        };
    }

    private static CachedTemplate GetOrCreateRegexAndFields(string template)
    {
        lock (cacheLock)
        {
            if (!regexCache.TryGetValue(template, out var cachedData))
            {
                while (regexCache.Count >= CacheCapacity)
                    regexCache.Remove(cacheOrder.Dequeue());

                cachedData = BuildRegexFromTemplate(template);
                regexCache[template] = cachedData;
                cacheOrder.Enqueue(template);
            }
            return cachedData;
        }
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderRegex();

    private static CachedTemplate BuildRegexFromTemplate(string template)
    {
        var fieldNames = new List<string>();
        var patternBuilder = new StringBuilder("^\\s*");
        int lastIndex = 0;

        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            if (match.Index > lastIndex)
                patternBuilder.Append(Regex.Escape(template.Substring(lastIndex, match.Index - lastIndex)));

            string fieldName = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new ArgumentException("Template contains an empty placeholder '{}'.");

            fieldNames.Add(fieldName);
            patternBuilder.Append("(.*?)");
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < template.Length)
            patternBuilder.Append(Regex.Escape(template.Substring(lastIndex)));

        patternBuilder.Append("\\s*$");

        RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
        return new CachedTemplate(
            new Regex(patternBuilder.ToString(), options),
            fieldNames);
    }

    private sealed record CachedTemplate(
        Regex Pattern,
        IReadOnlyList<string> FieldNames);
}
