using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

public static class DownloadSettingsPatchDtoMapper
{
    public static IReadOnlySet<string> ExplicitFields(DownloadSettingsPatchDto? patch)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (patch != null)
            CollectExplicitFields(patch, "", fields);
        return fields;
    }

    public static void ApplyTo(DownloadSettings settings, DownloadSettingsPatchDto? patch)
    {
        if (patch == null)
            return;

        ValidateSearchClears(patch.Search);

        ApplyOutput(settings.Output, patch.Output);
        ApplySearch(settings.Search, patch.Search);
        ApplySkip(settings.Skip, patch.Skip);
        ApplyPreprocess(settings.Preprocess, patch.Preprocess);
        ApplyExtraction(settings.Extraction, patch.Extraction);
        ApplyTransfer(settings.Transfer, patch.Transfer);
        ApplySpotify(settings.Spotify, patch.Spotify);
        ApplyYouTube(settings.YouTube, patch.YouTube);
        ApplyYtDlp(settings.YtDlp, patch.YtDlp);
        ApplyCsv(settings.Csv, patch.Csv);
        ApplyBandcamp(settings.Bandcamp, patch.Bandcamp);

        if (patch.PrintOption is { } printOption) settings.PrintOption = printOption.ToCore();
    }

    public static void ApplyTo(SearchSettings settings, SearchSettingsPatchDto? patch)
    {
        ValidateSearchClears(patch);
        ApplySearch(settings, patch);
    }

    public static DownloadSettingsPatchDto? FromDifference(DownloadSettings baseline, DownloadSettings effective)
    {
        var patch = new DownloadSettingsPatchDto(
            Difference(baseline.Output, effective.Output),
            Difference(baseline.Search, effective.Search),
            Difference(baseline.Skip, effective.Skip),
            Difference(baseline.Preprocess, effective.Preprocess),
            Difference(baseline.Extraction, effective.Extraction),
            Difference(baseline.Transfer, effective.Transfer),
            Difference(baseline.Spotify, effective.Spotify),
            Difference(baseline.YouTube, effective.YouTube),
            Difference(baseline.YtDlp, effective.YtDlp),
            Difference(baseline.Csv, effective.Csv),
            Difference(baseline.Bandcamp, effective.Bandcamp),
            Changed(baseline.PrintOption, effective.PrintOption)?.ToServer());

        return NullIfEmpty(patch, new DownloadSettingsPatchDto());
    }

    public static DownloadSettingsPatchDto? Combine(
        DownloadSettingsPatchDto? first,
        DownloadSettingsPatchDto? second)
    {
        ValidateSearchClears(first?.Search);
        ValidateSearchClears(second?.Search);
        if (first == null) return second;
        if (second == null) return first;

        return new DownloadSettingsPatchDto(
            Combine(first.Output, second.Output),
            Combine(first.Search, second.Search),
            Combine(first.Skip, second.Skip),
            Combine(first.Preprocess, second.Preprocess),
            Combine(first.Extraction, second.Extraction),
            Combine(first.Transfer, second.Transfer),
            Combine(first.Spotify, second.Spotify),
            Combine(first.YouTube, second.YouTube),
            Combine(first.YtDlp, second.YtDlp),
            Combine(first.Csv, second.Csv),
            Combine(first.Bandcamp, second.Bandcamp),
            second.PrintOption ?? first.PrintOption);
    }

    private static OutputSettingsPatchDto? Difference(OutputSettings before, OutputSettings after)
    {
        var incompleteAlbumAction = before.IncompleteAlbumAction.Kind != after.IncompleteAlbumAction.Kind
            || before.IncompleteAlbumAction.Path != after.IncompleteAlbumAction.Path
                ? new IncompleteAlbumActionSettingsPatchDto(
                    after.IncompleteAlbumAction.Kind?.ToServer(),
                    after.IncompleteAlbumAction.Path)
                : null;
        var patch = new OutputSettingsPatchDto(
            Changed(before.ParentDir, after.ParentDir),
            Changed(before.NameFormat, after.NameFormat),
            Changed(before.InvalidReplaceStr, after.InvalidReplaceStr),
            Changed(before.WritePlaylist, after.WritePlaylist),
            Changed(before.WriteIndex, after.WriteIndex),
            Changed(before.HasConfiguredIndex, after.HasConfiguredIndex),
            Changed(before.M3uFilePath, after.M3uFilePath),
            Changed(before.IndexFilePath, after.IndexFilePath),
            incompleteAlbumAction,
            Changed(before.OnComplete, after.OnComplete),
            Changed(before.AlbumArtOnly, after.AlbumArtOnly),
            Changed(before.AlbumArtOption, after.AlbumArtOption)?.ToServer());
        return NullIfEmpty(patch, new OutputSettingsPatchDto());
    }

    private static SearchSettingsPatchDto? Difference(SearchSettings before, SearchSettings after)
    {
        var patch = new SearchSettingsPatchDto(
            Difference(before.NecessaryCond, after.NecessaryCond),
            Difference(before.PreferredCond, after.PreferredCond),
            Difference(before.NecessaryFolderCond, after.NecessaryFolderCond),
            Difference(before.PreferredFolderCond, after.PreferredFolderCond),
            Changed(before.SearchTimeout, after.SearchTimeout),
            Changed(before.DownrankOn, after.DownrankOn),
            Changed(before.IgnoreOn, after.IgnoreOn),
            Changed(before.FastSearch, after.FastSearch),
            Changed(before.FastSearchDelay, after.FastSearchDelay),
            Changed(before.FastSearchMinUpSpeed, after.FastSearchMinUpSpeed),
            Changed(before.DesperateSearch, after.DesperateSearch),
            Changed(before.NoRemoveSpecialChars, after.NoRemoveSpecialChars),
            Changed(before.RemoveSingleCharSearchTerms, after.RemoveSingleCharSearchTerms),
            Changed(before.NoBrowseFolder, after.NoBrowseFolder),
            Changed(before.Relax, after.Relax),
            Changed(before.StrictAlbumQuality, after.StrictAlbumQuality),
            Changed(before.ArtistMaybeWrong, after.ArtistMaybeWrong),
            Changed(before.IsAggregate, after.IsAggregate),
            Changed(before.MinSharesAggregate, after.MinSharesAggregate),
            Changed(before.AggregateLengthTol, after.AggregateLengthTol));
        return NullIfEmpty(patch, new SearchSettingsPatchDto());
    }

    private static FileConditionsPatchDto? Difference(FileConditions before, FileConditions after)
    {
        var patch = new FileConditionsPatchDto(
            ChangedNullable(before.LengthTolerance, after.LengthTolerance),
            ChangedNullable(before.MinBitrate, after.MinBitrate),
            ChangedNullable(before.MaxBitrate, after.MaxBitrate),
            ChangedNullable(before.MinSampleRate, after.MinSampleRate),
            ChangedNullable(before.MaxSampleRate, after.MaxSampleRate),
            ChangedNullable(before.MinBitDepth, after.MinBitDepth),
            ChangedNullable(before.MaxBitDepth, after.MaxBitDepth),
            Changed(before.StrictTitle, after.StrictTitle),
            Changed(before.StrictArtist, after.StrictArtist),
            Changed(before.StrictAlbum, after.StrictAlbum),
            Changed(before.Formats, after.Formats),
            Changed(before.BannedUsers, after.BannedUsers),
            Changed(before.AllowedUsers, after.AllowedUsers),
            Changed(before.AcceptNoLength, after.AcceptNoLength),
            Changed(before.AcceptMissingProps, after.AcceptMissingProps),
            ClearIfRemoved(before.MinBitrate, after.MinBitrate),
            ClearIfRemoved(before.MaxBitrate, after.MaxBitrate),
            ClearIfRemoved(before.MinSampleRate, after.MinSampleRate),
            ClearIfRemoved(before.MaxSampleRate, after.MaxSampleRate),
            ClearIfRemoved(before.MinBitDepth, after.MinBitDepth),
            ClearIfRemoved(before.MaxBitDepth, after.MaxBitDepth));
        return NullIfEmpty(patch, new FileConditionsPatchDto());
    }

    private static FolderConditionsPatchDto? Difference(FolderConditions before, FolderConditions after)
    {
        var patch = new FolderConditionsPatchDto(
            ChangedNullable(before.MinTrackCount, after.MinTrackCount),
            ChangedNullable(before.MaxTrackCount, after.MaxTrackCount),
            Changed(before.RequiredTrackTitles, after.RequiredTrackTitles),
            ClearIfRemoved(before.MinTrackCount, after.MinTrackCount),
            ClearIfRemoved(before.MaxTrackCount, after.MaxTrackCount));
        return NullIfEmpty(patch, new FolderConditionsPatchDto());
    }

    private static SkipSettingsPatchDto? Difference(SkipSettings before, SkipSettings after)
    {
        var patch = new SkipSettingsPatchDto(
            Changed(before.SkipExisting, after.SkipExisting),
            Changed(before.SkipNotFound, after.SkipNotFound),
            Changed(before.SkipMode, after.SkipMode)?.ToServer(),
            Changed(before.SkipMusicDir, after.SkipMusicDir),
            Changed(before.SkipModeMusicDir, after.SkipModeMusicDir)?.ToServer(),
            Changed(before.SkipCheckCond, after.SkipCheckCond),
            Changed(before.SkipCheckPrefCond, after.SkipCheckPrefCond));
        return NullIfEmpty(patch, new SkipSettingsPatchDto());
    }

    private static PreprocessSettingsPatchDto? Difference(PreprocessSettings before, PreprocessSettings after)
    {
        var beforeRegex = before.Regex?.Select(ToRegexRuleDto);
        var afterRegex = after.Regex?.Select(ToRegexRuleDto);
        var patch = new PreprocessSettingsPatchDto(
            Changed(before.RemoveFt, after.RemoveFt),
            Changed(before.RemoveBrackets, after.RemoveBrackets),
            Changed(before.ExtractArtist, after.ExtractArtist),
            Changed(before.ParseTitleTemplate, after.ParseTitleTemplate),
            Changed(beforeRegex, afterRegex));
        return NullIfEmpty(patch, new PreprocessSettingsPatchDto());
    }

    private static ExtractionSettingsPatchDto? Difference(ExtractionSettings before, ExtractionSettings after)
    {
        var patch = new ExtractionSettingsPatchDto(
            Changed(before.Input, after.Input),
            Changed(before.InputType, after.InputType)?.ToServer(),
            Changed(before.MaxTracks, after.MaxTracks),
            Changed(before.Offset, after.Offset),
            Changed(before.Reverse, after.Reverse),
            Changed(before.RemoveTracksFromSource, after.RemoveTracksFromSource),
            ChangedNullable(before.RequestedMode, after.RequestedMode)?.ToServer(),
            Changed(before.UpgradeToAlbum, after.UpgradeToAlbum),
            Changed(before.SetAlbumMinTrackCount, after.SetAlbumMinTrackCount),
            Changed(before.SetAlbumMaxTrackCount, after.SetAlbumMaxTrackCount));
        return NullIfEmpty(patch, new ExtractionSettingsPatchDto());
    }

    private static TransferSettingsPatchDto? Difference(TransferSettings before, TransferSettings after)
    {
        var patch = new TransferSettingsPatchDto(
            Changed(before.MaxDownloadRetries, after.MaxDownloadRetries),
            Changed(before.UnknownErrorRetries, after.UnknownErrorRetries),
            Changed(before.NoIncompleteExt, after.NoIncompleteExt),
            Changed(before.AlbumTrackCountMaxRetries, after.AlbumTrackCountMaxRetries),
            Changed(before.MaxStaleTime, after.MaxStaleTime));
        return NullIfEmpty(patch, new TransferSettingsPatchDto());
    }

    private static SpotifySettingsPatchDto? Difference(SpotifySettings before, SpotifySettings after)
    {
        var patch = new SpotifySettingsPatchDto(
            Changed(before.ClientId, after.ClientId),
            Changed(before.ClientSecret, after.ClientSecret),
            Changed(before.Token, after.Token),
            Changed(before.Refresh, after.Refresh));
        return NullIfEmpty(patch, new SpotifySettingsPatchDto());
    }

    private static YouTubeSettingsPatchDto? Difference(YouTubeSettings before, YouTubeSettings after)
    {
        var patch = new YouTubeSettingsPatchDto(
            Changed(before.ApiKey, after.ApiKey),
            Changed(before.GetDeleted, after.GetDeleted),
            Changed(before.DeletedOnly, after.DeletedOnly));
        return NullIfEmpty(patch, new YouTubeSettingsPatchDto());
    }

    private static YtDlpSettingsPatchDto? Difference(YtDlpSettings before, YtDlpSettings after)
    {
        var patch = new YtDlpSettingsPatchDto(
            Changed(before.UseYtdlp, after.UseYtdlp),
            Changed(before.YtdlpArgument, after.YtdlpArgument));
        return NullIfEmpty(patch, new YtDlpSettingsPatchDto());
    }

    private static CsvSettingsPatchDto? Difference(CsvSettings before, CsvSettings after)
    {
        var patch = new CsvSettingsPatchDto(
            Changed(before.ArtistCol, after.ArtistCol),
            Changed(before.AlbumCol, after.AlbumCol),
            Changed(before.TitleCol, after.TitleCol),
            Changed(before.YtIdCol, after.YtIdCol),
            Changed(before.DescCol, after.DescCol),
            Changed(before.TrackCountCol, after.TrackCountCol),
            Changed(before.LengthCol, after.LengthCol),
            Changed(before.TimeUnit, after.TimeUnit),
            Changed(before.YtParse, after.YtParse));
        return NullIfEmpty(patch, new CsvSettingsPatchDto());
    }

    private static BandcampSettingsPatchDto? Difference(BandcampSettings before, BandcampSettings after)
    {
        var patch = new BandcampSettingsPatchDto(Changed(before.HtmlFromFile, after.HtmlFromFile));
        return NullIfEmpty(patch, new BandcampSettingsPatchDto());
    }

    private static OutputSettingsPatchDto? Combine(OutputSettingsPatchDto? first, OutputSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new OutputSettingsPatchDto(
            second.ParentDir ?? first.ParentDir,
            second.NameFormat ?? first.NameFormat,
            second.InvalidReplaceStr ?? first.InvalidReplaceStr,
            second.WritePlaylist ?? first.WritePlaylist,
            second.WriteIndex ?? first.WriteIndex,
            second.HasConfiguredIndex ?? first.HasConfiguredIndex,
            second.M3uFilePath ?? first.M3uFilePath,
            second.IndexFilePath ?? first.IndexFilePath,
            second.IncompleteAlbumAction ?? first.IncompleteAlbumAction,
            Combine(first.OnComplete, second.OnComplete),
            second.AlbumArtOnly ?? first.AlbumArtOnly,
            second.AlbumArtOption ?? first.AlbumArtOption);
    }

    private static SearchSettingsPatchDto? Combine(SearchSettingsPatchDto? first, SearchSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new SearchSettingsPatchDto(
            Combine(first.NecessaryCond, second.NecessaryCond),
            Combine(first.PreferredCond, second.PreferredCond),
            Combine(first.NecessaryFolderCond, second.NecessaryFolderCond),
            Combine(first.PreferredFolderCond, second.PreferredFolderCond),
            second.SearchTimeout ?? first.SearchTimeout,
            second.DownrankOn ?? first.DownrankOn,
            second.IgnoreOn ?? first.IgnoreOn,
            second.FastSearch ?? first.FastSearch,
            second.FastSearchDelay ?? first.FastSearchDelay,
            second.FastSearchMinUpSpeed ?? first.FastSearchMinUpSpeed,
            second.DesperateSearch ?? first.DesperateSearch,
            second.NoRemoveSpecialChars ?? first.NoRemoveSpecialChars,
            second.RemoveSingleCharSearchTerms ?? first.RemoveSingleCharSearchTerms,
            second.NoBrowseFolder ?? first.NoBrowseFolder,
            second.Relax ?? first.Relax,
            second.StrictAlbumQuality ?? first.StrictAlbumQuality,
            second.ArtistMaybeWrong ?? first.ArtistMaybeWrong,
            second.IsAggregate ?? first.IsAggregate,
            second.MinSharesAggregate ?? first.MinSharesAggregate,
            second.AggregateLengthTol ?? first.AggregateLengthTol);
    }

    private static FileConditionsPatchDto? Combine(FileConditionsPatchDto? first, FileConditionsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new FileConditionsPatchDto(
            second.LengthTolerance ?? first.LengthTolerance,
            CombineBound(first.MinBitrate, second.MinBitrate, second.ClearMinBitrate),
            CombineBound(first.MaxBitrate, second.MaxBitrate, second.ClearMaxBitrate),
            CombineBound(first.MinSampleRate, second.MinSampleRate, second.ClearMinSampleRate),
            CombineBound(first.MaxSampleRate, second.MaxSampleRate, second.ClearMaxSampleRate),
            CombineBound(first.MinBitDepth, second.MinBitDepth, second.ClearMinBitDepth),
            CombineBound(first.MaxBitDepth, second.MaxBitDepth, second.ClearMaxBitDepth),
            second.StrictTitle ?? first.StrictTitle,
            second.StrictArtist ?? first.StrictArtist,
            second.StrictAlbum ?? first.StrictAlbum,
            Combine(first.Formats, second.Formats),
            Combine(first.BannedUsers, second.BannedUsers),
            Combine(first.AllowedUsers, second.AllowedUsers),
            second.AcceptNoLength ?? first.AcceptNoLength,
            second.AcceptMissingProps ?? first.AcceptMissingProps,
            CombineClear(first.ClearMinBitrate, second.MinBitrate, second.ClearMinBitrate),
            CombineClear(first.ClearMaxBitrate, second.MaxBitrate, second.ClearMaxBitrate),
            CombineClear(first.ClearMinSampleRate, second.MinSampleRate, second.ClearMinSampleRate),
            CombineClear(first.ClearMaxSampleRate, second.MaxSampleRate, second.ClearMaxSampleRate),
            CombineClear(first.ClearMinBitDepth, second.MinBitDepth, second.ClearMinBitDepth),
            CombineClear(first.ClearMaxBitDepth, second.MaxBitDepth, second.ClearMaxBitDepth));
    }

    private static FolderConditionsPatchDto? Combine(FolderConditionsPatchDto? first, FolderConditionsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new FolderConditionsPatchDto(
            CombineBound(first.MinTrackCount, second.MinTrackCount, second.ClearMinTrackCount),
            CombineBound(first.MaxTrackCount, second.MaxTrackCount, second.ClearMaxTrackCount),
            Combine(first.RequiredTrackTitles, second.RequiredTrackTitles),
            CombineClear(first.ClearMinTrackCount, second.MinTrackCount, second.ClearMinTrackCount),
            CombineClear(first.ClearMaxTrackCount, second.MaxTrackCount, second.ClearMaxTrackCount));
    }

    private static int? CombineBound(int? first, int? second, bool? secondClear)
        => secondClear == true ? null : second ?? first;

    private static bool? CombineClear(bool? first, int? second, bool? secondClear)
        => second != null ? null : secondClear == true ? true : first;

    private static void ValidateSearchClears(SearchSettingsPatchDto? search)
    {
        static void Exclusive(int? value, bool? clear, string name)
        {
            if (value != null && clear == true)
                throw new ArgumentException($"{name} cannot be set and cleared in the same settings patch.");
        }

        if (search == null) return;
        foreach (FileConditionsPatchDto? file in new[] { search.NecessaryCond, search.PreferredCond })
        {
            if (file == null) continue;
            Exclusive(file.MinBitrate, file.ClearMinBitrate, nameof(file.MinBitrate));
            Exclusive(file.MaxBitrate, file.ClearMaxBitrate, nameof(file.MaxBitrate));
            Exclusive(file.MinSampleRate, file.ClearMinSampleRate, nameof(file.MinSampleRate));
            Exclusive(file.MaxSampleRate, file.ClearMaxSampleRate, nameof(file.MaxSampleRate));
            Exclusive(file.MinBitDepth, file.ClearMinBitDepth, nameof(file.MinBitDepth));
            Exclusive(file.MaxBitDepth, file.ClearMaxBitDepth, nameof(file.MaxBitDepth));
        }
        foreach (FolderConditionsPatchDto? folder in new[] { search.NecessaryFolderCond, search.PreferredFolderCond })
        {
            if (folder == null) continue;
            Exclusive(folder.MinTrackCount, folder.ClearMinTrackCount, nameof(folder.MinTrackCount));
            Exclusive(folder.MaxTrackCount, folder.ClearMaxTrackCount, nameof(folder.MaxTrackCount));
        }
    }

    private static SkipSettingsPatchDto? Combine(SkipSettingsPatchDto? first, SkipSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new SkipSettingsPatchDto(
            second.SkipExisting ?? first.SkipExisting,
            second.SkipNotFound ?? first.SkipNotFound,
            second.SkipMode ?? first.SkipMode,
            second.SkipMusicDir ?? first.SkipMusicDir,
            second.SkipModeMusicDir ?? first.SkipModeMusicDir,
            second.SkipCheckCond ?? first.SkipCheckCond,
            second.SkipCheckPrefCond ?? first.SkipCheckPrefCond);
    }

    private static PreprocessSettingsPatchDto? Combine(PreprocessSettingsPatchDto? first, PreprocessSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new PreprocessSettingsPatchDto(
            second.RemoveFt ?? first.RemoveFt,
            second.RemoveBrackets ?? first.RemoveBrackets,
            second.ExtractArtist ?? first.ExtractArtist,
            second.ParseTitleTemplate ?? first.ParseTitleTemplate,
            Combine(first.Regex, second.Regex));
    }

    private static ExtractionSettingsPatchDto? Combine(ExtractionSettingsPatchDto? first, ExtractionSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new ExtractionSettingsPatchDto(
            second.Input ?? first.Input,
            second.InputType ?? first.InputType,
            second.MaxTracks ?? first.MaxTracks,
            second.Offset ?? first.Offset,
            second.Reverse ?? first.Reverse,
            second.RemoveTracksFromSource ?? first.RemoveTracksFromSource,
            second.RequestedMode ?? first.RequestedMode,
            second.UpgradeToAlbum ?? first.UpgradeToAlbum,
            second.SetAlbumMinTrackCount ?? first.SetAlbumMinTrackCount,
            second.SetAlbumMaxTrackCount ?? first.SetAlbumMaxTrackCount);
    }

    private static TransferSettingsPatchDto? Combine(TransferSettingsPatchDto? first, TransferSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new TransferSettingsPatchDto(
            second.MaxDownloadRetries ?? first.MaxDownloadRetries,
            second.UnknownErrorRetries ?? first.UnknownErrorRetries,
            second.NoIncompleteExt ?? first.NoIncompleteExt,
            second.AlbumTrackCountMaxRetries ?? first.AlbumTrackCountMaxRetries,
            second.MaxStaleTime ?? first.MaxStaleTime);
    }

    private static SpotifySettingsPatchDto? Combine(SpotifySettingsPatchDto? first, SpotifySettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new SpotifySettingsPatchDto(
            second.ClientId ?? first.ClientId,
            second.ClientSecret ?? first.ClientSecret,
            second.Token ?? first.Token,
            second.Refresh ?? first.Refresh);
    }

    private static YouTubeSettingsPatchDto? Combine(YouTubeSettingsPatchDto? first, YouTubeSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new YouTubeSettingsPatchDto(
            second.ApiKey ?? first.ApiKey,
            second.GetDeleted ?? first.GetDeleted,
            second.DeletedOnly ?? first.DeletedOnly);
    }

    private static YtDlpSettingsPatchDto? Combine(YtDlpSettingsPatchDto? first, YtDlpSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new YtDlpSettingsPatchDto(
            second.UseYtdlp ?? first.UseYtdlp,
            second.YtdlpArgument ?? first.YtdlpArgument);
    }

    private static CsvSettingsPatchDto? Combine(CsvSettingsPatchDto? first, CsvSettingsPatchDto? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new CsvSettingsPatchDto(
            second.ArtistCol ?? first.ArtistCol,
            second.AlbumCol ?? first.AlbumCol,
            second.TitleCol ?? first.TitleCol,
            second.YtIdCol ?? first.YtIdCol,
            second.DescCol ?? first.DescCol,
            second.TrackCountCol ?? first.TrackCountCol,
            second.LengthCol ?? first.LengthCol,
            second.TimeUnit ?? first.TimeUnit,
            second.YtParse ?? first.YtParse);
    }

    private static BandcampSettingsPatchDto? Combine(BandcampSettingsPatchDto? first, BandcampSettingsPatchDto? second)
        => second ?? first;

    private static CollectionPatchDto<T>? Combine<T>(CollectionPatchDto<T>? first, CollectionPatchDto<T>? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        IReadOnlyList<T>? append = first.Append == null && second.Append == null
            ? null
            : [.. first.Append ?? [], .. second.Append ?? []];
        return new CollectionPatchDto<T>(
            second.Replace ?? first.Replace,
            append);
    }

    private static T? Changed<T>(T before, T after) where T : struct
        => EqualityComparer<T>.Default.Equals(before, after) ? null : after;

    private static T? ChangedNullable<T>(T? before, T? after) where T : struct
        => EqualityComparer<T?>.Default.Equals(before, after) ? null : after;

    private static bool? ClearIfRemoved(int? before, int? after)
        => before != null && after == null ? true : null;

    private static string? Changed(string? before, string? after)
        => before == after ? null : after;

    private static CollectionPatchDto<T>? Changed<T>(IEnumerable<T>? before, IEnumerable<T>? after)
    {
        var beforeItems = before?.ToArray() ?? [];
        var afterItems = after?.ToArray() ?? [];
        return beforeItems.SequenceEqual(afterItems)
            ? null
            : new CollectionPatchDto<T>(Replace: afterItems);
    }

    private static T? NullIfEmpty<T>(T value, T empty) where T : class
        => EqualityComparer<T>.Default.Equals(value, empty) ? null : value;

    private static void CollectExplicitFields(object value, string prefix, HashSet<string> fields)
    {
        Type type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CollectionPatchDto<>))
        {
            if (prefix.Length > 0)
                fields.Add(prefix);
            return;
        }

        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            object? child = property.GetValue(value);
            if (child == null)
                continue;
            if (property.Name.StartsWith("Clear", StringComparison.Ordinal)
                && child is bool clear
                && property.PropertyType == typeof(bool?))
            {
                if (clear)
                    fields.Add(prefix.Length == 0
                        ? property.Name[5..]
                        : $"{prefix}.{property.Name[5..]}");
                continue;
            }
            string path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            Type childType = child.GetType();
            if (childType.IsPrimitive || childType.IsEnum || child is string || child is decimal)
                fields.Add(path);
            else
                CollectExplicitFields(child, path, fields);
        }
    }

    private static void ApplyOutput(OutputSettings target, OutputSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ParentDir is { } parentDir) target.ParentDir = parentDir;
        if (patch.NameFormat is { } nameFormat) target.NameFormat = nameFormat;
        if (patch.InvalidReplaceStr is { } invalidReplaceStr) target.InvalidReplaceStr = invalidReplaceStr;
        if (patch.WritePlaylist is { } writePlaylist) target.WritePlaylist = writePlaylist;
        if (patch.WriteIndex is { } writeIndex) target.WriteIndex = writeIndex;
        if (patch.HasConfiguredIndex is { } hasConfiguredIndex) target.HasConfiguredIndex = hasConfiguredIndex;
        if (patch.M3uFilePath is { } m3uFilePath) target.M3uFilePath = m3uFilePath;
        if (patch.IndexFilePath is { } indexFilePath) target.IndexFilePath = indexFilePath;
        if (patch.IncompleteAlbumAction is { } incompleteAlbumAction)
        {
            target.IncompleteAlbumAction.Kind = incompleteAlbumAction.Kind?.ToCore();
            target.IncompleteAlbumAction.Path = incompleteAlbumAction.Path;
        }
        if (patch.OnComplete is { } onComplete)
        {
            ValidateOnCompletePatch(onComplete);
            target.OnComplete ??= [];
            onComplete.ApplyTo(target.OnComplete);
        }
        if (patch.AlbumArtOnly is { } albumArtOnly) target.AlbumArtOnly = albumArtOnly;
        if (patch.AlbumArtOption is { } albumArtOption) target.AlbumArtOption = albumArtOption.ToCore();
    }

    private static void ApplySearch(SearchSettings target, SearchSettingsPatchDto? patch)
    {
        if (patch == null) return;
        ApplyFileConditions(target.NecessaryCond, patch.NecessaryCond);
        ApplyFileConditions(target.PreferredCond, patch.PreferredCond);
        ApplyFolderConditions(target.NecessaryFolderCond, patch.NecessaryFolderCond);
        ApplyFolderConditions(target.PreferredFolderCond, patch.PreferredFolderCond);
        if (patch.SearchTimeout is { } searchTimeout) target.SearchTimeout = searchTimeout;
        if (patch.DownrankOn is { } downrankOn) target.DownrankOn = downrankOn;
        if (patch.IgnoreOn is { } ignoreOn) target.IgnoreOn = ignoreOn;
        if (patch.FastSearch is { } fastSearch) target.FastSearch = fastSearch;
        if (patch.FastSearchDelay is { } fastSearchDelay) target.FastSearchDelay = fastSearchDelay;
        if (patch.FastSearchMinUpSpeed is { } fastSearchMinUpSpeed) target.FastSearchMinUpSpeed = fastSearchMinUpSpeed;
        if (patch.DesperateSearch is { } desperateSearch) target.DesperateSearch = desperateSearch;
        if (patch.NoRemoveSpecialChars is { } noRemoveSpecialChars) target.NoRemoveSpecialChars = noRemoveSpecialChars;
        if (patch.RemoveSingleCharSearchTerms is { } removeSingleCharSearchTerms) target.RemoveSingleCharSearchTerms = removeSingleCharSearchTerms;
        if (patch.NoBrowseFolder is { } noBrowseFolder) target.NoBrowseFolder = noBrowseFolder;
        if (patch.Relax is { } relax) target.Relax = relax;
        if (patch.StrictAlbumQuality is { } strictAlbumQuality) target.StrictAlbumQuality = strictAlbumQuality;
        if (patch.ArtistMaybeWrong is { } artistMaybeWrong) target.ArtistMaybeWrong = artistMaybeWrong;
        if (patch.IsAggregate is { } isAggregate) target.IsAggregate = isAggregate;
        if (patch.MinSharesAggregate is { } minSharesAggregate) target.MinSharesAggregate = minSharesAggregate;
        if (patch.AggregateLengthTol is { } aggregateLengthTol) target.AggregateLengthTol = aggregateLengthTol;
    }

    private static void ApplyFileConditions(FileConditions target, FileConditionsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ClearMinBitrate == true) target.MinBitrate = null;
        if (patch.ClearMaxBitrate == true) target.MaxBitrate = null;
        if (patch.ClearMinSampleRate == true) target.MinSampleRate = null;
        if (patch.ClearMaxSampleRate == true) target.MaxSampleRate = null;
        if (patch.ClearMinBitDepth == true) target.MinBitDepth = null;
        if (patch.ClearMaxBitDepth == true) target.MaxBitDepth = null;
        if (patch.LengthTolerance is { } lengthTolerance) target.LengthTolerance = lengthTolerance;
        if (patch.MinBitrate is { } minBitrate) target.MinBitrate = minBitrate;
        if (patch.MaxBitrate is { } maxBitrate) target.MaxBitrate = maxBitrate;
        if (patch.MinSampleRate is { } minSampleRate) target.MinSampleRate = minSampleRate;
        if (patch.MaxSampleRate is { } maxSampleRate) target.MaxSampleRate = maxSampleRate;
        if (patch.MinBitDepth is { } minBitDepth) target.MinBitDepth = minBitDepth;
        if (patch.MaxBitDepth is { } maxBitDepth) target.MaxBitDepth = maxBitDepth;
        if (patch.StrictTitle is { } strictTitle) target.StrictTitle = strictTitle;
        if (patch.StrictArtist is { } strictArtist) target.StrictArtist = strictArtist;
        if (patch.StrictAlbum is { } strictAlbum) target.StrictAlbum = strictAlbum;
        if (patch.Formats is { } formats) target.Formats = formats.ApplyTo(target.Formats.ToList()).ToArray();
        if (patch.BannedUsers is { } bannedUsers) target.BannedUsers = bannedUsers.ApplyTo(target.BannedUsers.ToList()).ToArray();
        if (patch.AllowedUsers is { } allowedUsers) target.AllowedUsers = allowedUsers.ApplyTo(target.AllowedUsers.ToList()).ToArray();
        if (patch.AcceptNoLength is { } acceptNoLength) target.AcceptNoLength = acceptNoLength;
        if (patch.AcceptMissingProps is { } acceptMissingProps) target.AcceptMissingProps = acceptMissingProps;
    }

    private static void ApplyFolderConditions(FolderConditions target, FolderConditionsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ClearMinTrackCount == true) target.MinTrackCount = null;
        if (patch.ClearMaxTrackCount == true) target.MaxTrackCount = null;
        if (patch.MinTrackCount is { } minTrackCount) target.MinTrackCount = minTrackCount;
        if (patch.MaxTrackCount is { } maxTrackCount) target.MaxTrackCount = maxTrackCount;
        patch.RequiredTrackTitles?.ApplyTo(target.RequiredTrackTitles);
    }

    private static void ApplySkip(SkipSettings target, SkipSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.SkipExisting is { } skipExisting) target.SkipExisting = skipExisting;
        if (patch.SkipNotFound is { } skipNotFound) target.SkipNotFound = skipNotFound;
        if (patch.SkipMode is { } skipMode) target.SkipMode = skipMode.ToCore();
        if (patch.SkipMusicDir is { } skipMusicDir) target.SkipMusicDir = skipMusicDir;
        if (patch.SkipModeMusicDir is { } skipModeMusicDir) target.SkipModeMusicDir = skipModeMusicDir.ToCore();
        if (patch.SkipCheckCond is { } skipCheckCond) target.SkipCheckCond = skipCheckCond;
        if (patch.SkipCheckPrefCond is { } skipCheckPrefCond) target.SkipCheckPrefCond = skipCheckPrefCond;
    }

    private static void ApplyPreprocess(PreprocessSettings target, PreprocessSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.RemoveFt is { } removeFt) target.RemoveFt = removeFt;
        if (patch.RemoveBrackets is { } removeBrackets) target.RemoveBrackets = removeBrackets;
        if (patch.ExtractArtist is { } extractArtist) target.ExtractArtist = extractArtist;
        if (patch.ParseTitleTemplate is { } parseTitleTemplate) target.ParseTitleTemplate = parseTitleTemplate;
        if (patch.Regex is { } regex)
        {
            var current = target.Regex?.Select(ToRegexRuleDto).ToList();
            var updated = regex.ApplyTo(current);
            target.Regex = updated.Select(ToRegexRule).ToList();
        }
    }

    private static void ApplyExtraction(ExtractionSettings target, ExtractionSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.Input is { } input) target.Input = input;
        if (patch.InputType is { } inputType) target.InputType = inputType.ToCore();
        if (patch.MaxTracks is { } maxTracks) target.MaxTracks = maxTracks;
        if (patch.Offset is { } offset) target.Offset = offset;
        if (patch.Reverse is { } reverse) target.Reverse = reverse;
        if (patch.RemoveTracksFromSource is { } removeTracksFromSource) target.RemoveTracksFromSource = removeTracksFromSource;
        if (patch.RequestedMode is { } requestedMode) target.RequestedMode = requestedMode.ToCore();
        if (patch.UpgradeToAlbum is { } upgradeToAlbum) target.UpgradeToAlbum = upgradeToAlbum;
        if (patch.SetAlbumMinTrackCount is { } setAlbumMinTrackCount) target.SetAlbumMinTrackCount = setAlbumMinTrackCount;
        if (patch.SetAlbumMaxTrackCount is { } setAlbumMaxTrackCount) target.SetAlbumMaxTrackCount = setAlbumMaxTrackCount;
    }

    private static void ApplyTransfer(TransferSettings target, TransferSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.MaxDownloadRetries is { } maxDownloadRetries) target.MaxDownloadRetries = maxDownloadRetries;
        if (patch.UnknownErrorRetries is { } unknownErrorRetries) target.UnknownErrorRetries = unknownErrorRetries;
        if (patch.NoIncompleteExt is { } noIncompleteExt) target.NoIncompleteExt = noIncompleteExt;
        if (patch.AlbumTrackCountMaxRetries is { } albumTrackCountMaxRetries) target.AlbumTrackCountMaxRetries = albumTrackCountMaxRetries;
        if (patch.MaxStaleTime is { } maxStaleTime) target.MaxStaleTime = maxStaleTime;
    }

    private static void ApplySpotify(SpotifySettings target, SpotifySettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ClientId is { } clientId) target.ClientId = clientId;
        if (patch.ClientSecret is { } clientSecret) target.ClientSecret = clientSecret;
        if (patch.Token is { } token) target.Token = token;
        if (patch.Refresh is { } refresh) target.Refresh = refresh;
    }

    private static void ApplyYouTube(YouTubeSettings target, YouTubeSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ApiKey is { } apiKey) target.ApiKey = apiKey;
        if (patch.GetDeleted is { } getDeleted) target.GetDeleted = getDeleted;
        if (patch.DeletedOnly is { } deletedOnly) target.DeletedOnly = deletedOnly;
    }

    private static void ApplyYtDlp(YtDlpSettings target, YtDlpSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.UseYtdlp is { } useYtdlp) target.UseYtdlp = useYtdlp;
        if (patch.YtdlpArgument is { } ytdlpArgument) target.YtdlpArgument = ytdlpArgument;
    }

    private static void ApplyCsv(CsvSettings target, CsvSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.ArtistCol is { } artistCol) target.ArtistCol = artistCol;
        if (patch.AlbumCol is { } albumCol) target.AlbumCol = albumCol;
        if (patch.TitleCol is { } titleCol) target.TitleCol = titleCol;
        if (patch.YtIdCol is { } ytIdCol) target.YtIdCol = ytIdCol;
        if (patch.DescCol is { } descCol) target.DescCol = descCol;
        if (patch.TrackCountCol is { } trackCountCol) target.TrackCountCol = trackCountCol;
        if (patch.LengthCol is { } lengthCol) target.LengthCol = lengthCol;
        if (patch.TimeUnit is { } timeUnit) target.TimeUnit = timeUnit;
        if (patch.YtParse is { } ytParse) target.YtParse = ytParse;
    }

    private static void ApplyBandcamp(BandcampSettings target, BandcampSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.HtmlFromFile is { } htmlFromFile) target.HtmlFromFile = htmlFromFile;
    }

    private static List<T> ApplyTo<T>(this CollectionPatchDto<T> patch, List<T>? target)
    {
        target ??= [];
        if (patch.Replace != null)
        {
            target.Clear();
            target.AddRange(patch.Replace);
        }
        if (patch.Append != null)
            target.AddRange(patch.Append);
        return target;
    }

    private static RegexRuleDto ToRegexRuleDto((RegexFields Match, RegexFields Replace) rule)
        => new(ToRegexFieldsDto(rule.Match), ToRegexFieldsDto(rule.Replace));

    private static RegexFieldsDto ToRegexFieldsDto(RegexFields fields)
        => new(fields.Title, fields.Artist, fields.Album);

    private static (RegexFields, RegexFields) ToRegexRule(RegexRuleDto rule)
        => (ToRegexFields(rule.Match), ToRegexFields(rule.Replace));

    private static RegexFields ToRegexFields(RegexFieldsDto fields)
        => new() { Title = fields.Title, Artist = fields.Artist, Album = fields.Album };

    private static void ValidateOnCompletePatch(CollectionPatchDto<string> patch)
    {
        OnCompleteExecutor.ValidateCommands(patch.Replace);
        OnCompleteExecutor.ValidateCommands(patch.Append);
    }

}
