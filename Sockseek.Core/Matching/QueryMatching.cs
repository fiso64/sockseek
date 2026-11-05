using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

// Derived matching/search terms only; source metadata remains unchanged.
internal static class QueryMatching
{
    public static string[] Artists(string artist)
        => artist.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(Titles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string[] Titles(string title)
    {
        if (title.Length == 0)
            return [];
        string withoutCredits = title.RemoveFt();
        return withoutCredits.Length > 0 && withoutCredits != title
            ? [title, withoutCredits]
            : [title];
    }

    public static bool AggregateIdentityMatches(SongQuery candidate, SongQuery query)
    {
        if (query.Title.Length > 0 && !FileConditions.StrictStringAny(candidate.Title, Titles(query.Title)))
            return false;

        if (query.Artist.Length == 0)
            return true;
        var artists = Artists(query.Artist);
        if (FileConditions.StrictStringAny(candidate.Artist, artists, boundarySkipWs: false))
            return true;

        string preparedTitle = FileConditions.StrictStringPreprocess(candidate.Title);
        foreach (string artist in artists)
        {
            if (candidate.Title.ContainsInBrackets(artist, ignoreCase: true)
                && FileConditions.StrictStringPrepared(preparedTitle,
                    FileConditions.StrictStringPreprocess(artist, removeSlash: true), boundarySkipWs: false))
                return true;
        }

        return false;
    }
}
