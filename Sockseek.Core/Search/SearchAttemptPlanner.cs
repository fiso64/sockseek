using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;

namespace Sockseek.Core.Services;

internal sealed record SearchAttempt(SearchQuery Query, FileConditions Conditions, int Timeout);

// Each batch completes before the executor decides whether to try the next one.
internal static class SearchAttemptPlanner
{
    private sealed record Variant(string Text, FileConditions Conditions, int Timeout);

    public static IReadOnlyList<SearchAttempt[]> Create(SongQuery query, SearchSettings search)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(search.FallbackLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(search.FallbackLevel, 3);
        ArgumentOutOfRangeException.ThrowIfNegative(search.MaxFallbackAttempts);

        int level = search.DesperateSearch ? 3 : search.FallbackLevel;
        int remaining = search.MaxFallbackAttempts;
        var batches = new List<SearchAttempt[]>();
        var seen = new List<(string Key, FileConditions Conditions)>();
        bool normalSearch = true;

        foreach (var variants in GenerateBatches(query, search, level))
        {
            if (!normalSearch && remaining == 0)
                break;

            var attempts = new List<SearchAttempt>();
            foreach (var variant in variants)
            {
                if (!normalSearch && remaining == 0)
                    break;

                string text = Searcher.CleanSearchString(variant.Text, !search.NoRemoveSpecialChars);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var networkQuery = SearchQuery.FromText(text);
                string key = QueryKey(networkQuery, search.RemoveSingleCharSearchTerms);
                if (seen.Any(previous => previous.Key == key && previous.Conditions.Equals(variant.Conditions)))
                    continue;

                seen.Add((key, variant.Conditions));
                attempts.Add(new SearchAttempt(networkQuery, variant.Conditions, variant.Timeout));
                if (!normalSearch)
                    remaining--;
            }

            if (attempts.Count > 0)
                batches.Add(attempts.ToArray());
            normalSearch = false;
        }

        return batches;
    }

    private static IEnumerable<IEnumerable<Variant>> GenerateBatches(SongQuery query, SearchSettings search, int level)
    {
        string title = query.Title.Length > 0 ? query.Title : query.Album;
        yield return WithDiacritics($"{query.Artist} {title}".Trim(), search.NecessaryCond,
            search.SearchTimeout, includeAlternative: !query.ArtistMaybeWrong);

        // Existing recovery for uncertain artist metadata is available even at level 0.
        if (query.ArtistMaybeWrong && query.Title.Length > 0)
            yield return UncertainArtistSearch(query, search);

        if (level >= 1)
            foreach (var batch in ArtistFallbacks(query, title, search, level))
                yield return batch;

        if (level >= 3)
        {
            yield return AlbumAndLengthFallbacks(query, search);
            yield return BroadFallbacks(query, search);
        }
    }

    private static Variant[] WithDiacritics(string text, FileConditions conditions, int timeout, bool includeAlternative = true)
    {
        var original = new Variant(text, conditions, timeout);
        if (includeAlternative && text.RemoveDiacriticsIfExist(out string plain))
            return [original, new Variant(plain, conditions, timeout)];
        return [original];
    }

    private static Variant[] UncertainArtistSearch(SongQuery query, SearchSettings search)
    {
        var inferred = Searcher.InferSongQuery(query.Title, new SongQuery());
        var conditions = new FileConditions(search.NecessaryCond)
        {
            StrictTitle = inferred.Title == query.Title,
            StrictArtist = false,
        };
        return [new Variant($"{inferred.Artist} {inferred.Title}", conditions, Math.Min(search.SearchTimeout, 5000))];
    }

    private static IEnumerable<Variant[]> ArtistFallbacks(SongQuery query, string title, SearchSettings search, int level)
    {
        if (query.ArtistMaybeWrong || query.Artist.Length == 0)
            yield break;

        string cleanTitle = title.RemoveFt();
        if (cleanTitle.Length == 0)
            yield break;

        IEnumerable<string> artists = query.Artist.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (level == 1)
            artists = artists.Take(1);

        foreach (string artist in artists)
        {
            string cleanArtist = artist.RemoveFt();
            if (cleanArtist.Length > 0)
                yield return WithDiacritics($"{cleanArtist} {cleanTitle}", search.NecessaryCond, search.SearchTimeout);
        }
    }

    private static IEnumerable<Variant> AlbumAndLengthFallbacks(SongQuery query, SearchSettings search)
    {
        if (query.ArtistMaybeWrong || query.Artist.Length == 0 || query.Title.Length == 0)
            yield break;

        int timeout = Math.Min(search.SearchTimeout, 5000);
        if (query.Album.Length > 0)
        {
            var conditions = new FileConditions(search.NecessaryCond) { StrictTitle = true, StrictAlbum = true };
            yield return new Variant($"{query.Artist} {query.Album}", conditions, timeout);
        }

        if (query.Length != -1 && search.NecessaryCond.LengthTolerance != -1)
        {
            var conditions = new FileConditions(search.NecessaryCond)
            {
                LengthTolerance = -1,
                StrictTitle = true,
                StrictArtist = true,
            };
            yield return new Variant($"{query.Artist} {query.Title}", conditions, timeout);
        }
    }

    private static IEnumerable<Variant> BroadFallbacks(SongQuery query, SearchSettings search)
    {
        var effectiveQuery = query.ArtistMaybeWrong ? Searcher.InferSongQuery(query.Title, new SongQuery()) : query;
        var conditions = new FileConditions(search.NecessaryCond)
        {
            StrictTitle = !query.ArtistMaybeWrong,
            StrictArtist = !query.ArtistMaybeWrong,
            LengthTolerance = -1,
        };
        int timeout = Math.Min(search.SearchTimeout, 5000);

        if (query.Album.Length > 3)
            yield return new Variant(query.Album, new FileConditions(conditions) { StrictAlbum = true }, timeout);

        string title = effectiveQuery.Title.RemoveFt();
        if (title.Length > 3 && query.Artist.Length > 0)
            yield return new Variant(title, new FileConditions(conditions), timeout);

        if (effectiveQuery.Artist.Length > 3 && query.Title.Length > 0)
            yield return new Variant(effectiveQuery.Artist, new FileConditions(conditions), timeout);
    }

    private static string QueryKey(SearchQuery query, bool removeSingleCharTerms)
    {
        // Term order and repeated terms do not change network matching.
        var terms = query.Terms.Where(term => !removeSingleCharTerms || term.Length > 1);
        return string.Join('\0', terms.Distinct().Order(StringComparer.Ordinal)) + '\u0001'
            + string.Join('\0', query.Exclusions.Distinct().Order(StringComparer.Ordinal));
    }
}
