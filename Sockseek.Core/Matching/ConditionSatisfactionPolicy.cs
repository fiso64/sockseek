using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;
using Soulseek;

namespace Sockseek.Core.Services;

// Production condition checks should enter here. FileConditions owns the
// primitive predicate implementation; this class owns the context-specific
// policy choices that otherwise drift between search, projection, and skipping.
internal static class ConditionSatisfactionPolicy
{
    internal readonly record struct AlbumTrackCountCheck(
        int AudioFileCount,
        int? Minimum,
        int? Maximum,
        bool EnforceMinimum)
    {
        public bool FailedAboveMaximum => Maximum is { } maximum and > 0 && AudioFileCount > maximum;
        public bool FailedBelowMinimum => EnforceMinimum && Minimum is { } minimum and > 0 && AudioFileCount < minimum;
        public bool Satisfied => !FailedAboveMaximum && !FailedBelowMinimum;
    }

    internal static bool SearchFileSatisfies(
        FileConditions conditions,
        SearchResponse? response,
        Soulseek.File file,
        SongQuery? query)
        => conditions.FileSatisfies(ConditionFile.From(file), query, response, filenameChecks: true, checkUser: true);

    internal static bool SearchFileSatisfies(
        FileConditions conditions,
        SearchProjectionInput input,
        SongQuery? query)
        => conditions.VisibilitySatisfies(input.Visibility) && conditions.ProjectionFileSatisfies(
            new ConditionFile(input.Filename, input.Length, input.BitRate, input.SampleRate, input.BitDepth),
            query,
            input.Username,
            filenameChecks: true,
            checkUser: true);

    internal static bool LocalFileSatisfies(
        FileConditions conditions,
        SimpleFile file,
        SongQuery? query,
        bool filenameChecks = false)
        => conditions.FileSatisfies(ConditionFile.From(file), query, response: null, filenameChecks: filenameChecks, checkUser: false);

    internal static bool LocalFileSatisfies(
        FileConditions conditions,
        TagLib.File file,
        SongQuery? query,
        bool filenameChecks = false)
        => conditions.FileSatisfies(ConditionFile.From(file), query, response: null, filenameChecks: filenameChecks, checkUser: false);

    internal static AlbumSearchFilter CreateAlbumSearchFilter(AlbumQuery query, SearchSettings search)
        => new(query, search);

    internal static bool SearchAlbumFolderSatisfies(
        FolderConditions conditions,
        int visibleAudioFileCount,
        IEnumerable<string> visibleFilenames,
        AlbumQuery query,
        SearchSettings search,
        bool enforceMinimumTrackCount)
    {
        // User-facing filters describe the files observed so far. Automatic
        // discovery may defer a minimum until browsing the complete candidate.
        bool enforceMinimum = enforceMinimumTrackCount || SearchResultsLikelyContainCompleteAlbumFolders(query, search);
        return AlbumFolderSatisfies(conditions, visibleAudioFileCount, visibleFilenames, enforceMinimum);
    }

    internal static bool LocalAlbumFolderSatisfies(
        FolderConditions? conditions,
        int audioFileCount,
        IEnumerable<string> audioFilenames)
        => conditions == null
            || AlbumFolderSatisfies(conditions, audioFileCount, audioFilenames, enforceMinimum: true);

    internal static bool HasAlbumFolderConditions(FolderConditions conditions)
        => HasAlbumTrackCountConditions(conditions)
            || conditions.RequiredTrackTitles.Count > 0;

    internal static bool HasAlbumTrackCountConditions(FolderConditions conditions)
        => conditions.MaxTrackCount is > 0
            || conditions.MinTrackCount is > 0;

    internal static bool ShouldRetrieveFullAlbumForTrackCount(
        FolderConditions conditions,
        int knownAudioFileCount,
        bool isFullyRetrieved)
    {
        if (isFullyRetrieved || !HasAlbumTrackCountConditions(conditions))
            return false;

        return conditions.MaxTrackCount is int maxTrackCount && maxTrackCount > 0 && knownAudioFileCount <= maxTrackCount
            || conditions.MinTrackCount is int minTrackCount && minTrackCount > 0 && knownAudioFileCount < minTrackCount;
    }

    internal static AlbumTrackCountCheck CheckAlbumTrackCount(
        FolderConditions conditions,
        int audioFileCount)
        => CheckAlbumTrackCount(conditions, audioFileCount, enforceMinimum: true);

    internal static bool LocalAlbumDirectorySatisfies(
        FolderConditions? conditions,
        string? directory)
    {
        if (conditions == null || !HasAlbumFolderConditions(conditions))
            return true;

        if (string.IsNullOrEmpty(directory) || !System.IO.Directory.Exists(directory))
            return false;

        var audioFilenames = System.IO.Directory
            .GetFiles(directory, "*", System.IO.SearchOption.AllDirectories)
            .Where(Utils.IsMusicFile)
            .ToList();
        return LocalAlbumFolderSatisfies(conditions, audioFilenames.Count, audioFilenames);
    }

    internal readonly struct AlbumSearchFilter
    {
        private readonly FileConditions nonQualityFileConditions;
        private readonly bool checkUser;
        private readonly bool checkFile;

        public SongQuery SortQuery { get; }

        public AlbumSearchFilter(AlbumQuery query, SearchSettings search)
        {
            SortQuery = SearchResultProjector.AlbumFileMatchQuery(query);
            // Album track format/properties are checked as folder coverage after grouping;
            // per-file filtering only applies conditions outside that album-track policy.
            nonQualityFileConditions = search.NecessaryCond.WithoutAlbumTrackConditions();
            checkUser =
                nonQualityFileConditions.BannedUsers.Length > 0
                || nonQualityFileConditions.AllowedUsers.Length > 0;
            checkFile =
                nonQualityFileConditions.LengthTolerance is >= 0 && SortQuery.Length >= 0
                || nonQualityFileConditions.StrictTitle && SortQuery.Title.Length > 0
                || nonQualityFileConditions.StrictArtist && SortQuery.Artist.Length > 0
                || nonQualityFileConditions.StrictAlbum && SortQuery.Album.Length > 0;
        }

        public bool Satisfies((SearchResponse Response, Soulseek.File File) result)
            => Satisfies(result.Response, result.File);

        public bool Satisfies(SearchResponse response, Soulseek.File file)
        {
            if (checkUser && !nonQualityFileConditions.UserSatisfies(response))
                return false;

            if (!checkFile || !Utils.IsMusicFile(file.Filename))
                return true;

            return nonQualityFileConditions.FileSatisfies(
                ConditionFile.From(file),
                SortQuery,
                response,
                filenameChecks: true,
                checkUser: false);
        }

        public bool Satisfies(SearchProjectionInput input)
        {
            if (!nonQualityFileConditions.VisibilitySatisfies(input.Visibility)) return false;
            if (checkUser && !nonQualityFileConditions.UsernameSatisfies(input.Username))
                return false;
            if (!checkFile || !Utils.IsMusicFile(input.Filename))
                return true;
            return nonQualityFileConditions.ProjectionFileSatisfies(
                new ConditionFile(input.Filename, input.Length, input.BitRate, input.SampleRate, input.BitDepth),
                SortQuery,
                input.Username,
                filenameChecks: true,
                checkUser: false);
        }
    }

    internal static bool LocalAlbumSatisfies(
        IEnumerable<SimpleFile> audioFiles,
        FileConditions conditions,
        SearchSettings? search,
        AlbumQuery query)
        => AlbumFilesSatisfy(
            audioFiles.Select(ConditionFile.From),
            conditions,
            search?.StrictAlbumQuality ?? false,
            SearchResultProjector.AlbumFileMatchQuery(query));

    private static bool AlbumFilesSatisfy(
        IEnumerable<ConditionFile> audioFiles,
        FileConditions conditions,
        bool strictAlbumQuality,
        SongQuery? perFileQuery)
    {
        var files = audioFiles.ToList();
        var nonQualityConditions = conditions.WithoutAlbumTrackConditions();
        foreach (var file in files)
        {
            if (!nonQualityConditions.FileSatisfies(file, perFileQuery, response: null, filenameChecks: true, checkUser: false))
                return false;
        }

        var qualityCoverage = EvaluateAlbumQuality(files, conditions);
        return AlbumQualityIsAcceptable(qualityCoverage, strictAlbumQuality);
    }

    private static AlbumAudioQualityCoverage EvaluateAlbumQuality(
        IEnumerable<ConditionFile> audioFiles,
        FileConditions conditions)
        => AlbumQualityPolicy.Evaluate(audioFiles, conditions, AlbumQualityPolicy.ActiveConditions(conditions));

    internal static bool AlbumQualityIsAcceptable(
        AlbumAudioQualityCoverage qualityCoverage,
        SearchSettings search)
        => AlbumQualityIsAcceptable(qualityCoverage, search.StrictAlbumQuality);

    internal static bool AlbumQualityIsAcceptable(
        AlbumAudioQualityCoverage qualityCoverage,
        bool strictAlbumQuality)
        => qualityCoverage.IsAcceptable(strictAlbumQuality);

    private static AlbumTrackCountCheck CheckAlbumTrackCount(
        FolderConditions conditions,
        int audioFileCount,
        bool enforceMinimum)
        => new(
            audioFileCount,
            conditions.MinTrackCount,
            conditions.MaxTrackCount,
            enforceMinimum);

    private static bool AlbumFolderSatisfies(
        FolderConditions conditions,
        int audioFileCount,
        IEnumerable<string> filenames,
        bool enforceMinimum)
    {
        var trackCountCheck = CheckAlbumTrackCount(conditions, audioFileCount, enforceMinimum);
        return trackCountCheck.Satisfied
            && RequiredTrackTitlesSatisfy(conditions.RequiredTrackTitles, filenames);
    }

    private static bool RequiredTrackTitlesSatisfy(
        IReadOnlyCollection<string> requiredTrackTitles,
        IEnumerable<string> filenames)
    {
        if (requiredTrackTitles.Count == 0)
            return true;

        var fileList = filenames.ToList();
        var cond = new FileConditions { StrictTitle = true };
        foreach (string title in requiredTrackTitles)
        {
            bool found = false;
            foreach (string filename in fileList)
            {
                if (cond.StrictTitleSatisfies(filename, title))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }

    private static bool SearchResultsLikelyContainCompleteAlbumFolders(AlbumQuery query, SearchSettings search)
    {
        if (query.SearchHint.Length == 0)
            return true;

        // If Album is empty, SearchHint becomes the network query, so Soulseek may only
        // return tracks matching that hint rather than the whole album folder.
        if (query.Album.Length == 0)
            return false;

        // SearchHint can also become a file-level title filter when title conditions apply,
        // which means non-hint tracks may be filtered before folder grouping.
        if (search.NecessaryCond.StrictTitle || search.PreferredCond.StrictTitle)
            return false;

        return true;
    }
}
