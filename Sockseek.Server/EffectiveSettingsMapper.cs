using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

internal static class EffectiveSettingsMapper
{
    private static readonly string[] RedactedProvenancePrefixes =
    [
        "Output.OnComplete",
        "Preprocess.Regex",
        "Extraction.Input",
        "Spotify.",
        "SoundCloud.",
        "YouTube.ApiKey",
        "YtDlp.YtdlpArgument",
        "Bandcamp.",
    ];

    public static ResolveEffectiveSettingsResponseDto ToDto(
        JobSettingsCompositionResult result)
    {
        DownloadSettings settings = result.Settings;
        var provenance = result.Provenance
            .Where(pair => !RedactedProvenancePrefixes.Any(prefix =>
                pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new ResolveEffectiveSettingsResponseDto(
            result.Baseline.ToServer(),
            ToSafeSettings(settings),
            settings.AppliedProfiles.Select(profile => new AppliedProfileDto(profile.Name, profile.Source)).ToArray(),
            provenance);
    }

    public static EffectiveDownloadSettingsDto ToSafeSettings(DownloadSettings settings)
        => new(
                SafeValues(settings),
                settings.Output.OnComplete?.Count ?? 0,
                settings.Preprocess.Regex?.Count ?? 0,
                !string.IsNullOrEmpty(settings.Spotify.ClientId),
                !string.IsNullOrEmpty(settings.Spotify.ClientSecret),
                !string.IsNullOrEmpty(settings.Spotify.Token),
                !string.IsNullOrEmpty(settings.Spotify.Refresh),
                !string.IsNullOrEmpty(settings.YouTube.ApiKey),
                !string.IsNullOrEmpty(settings.YtDlp.YtdlpArgument),
                !string.IsNullOrEmpty(settings.Bandcamp.HtmlFromFile),
                !string.IsNullOrEmpty(settings.SoundCloud.AuthToken));

    private static DownloadSettingsPatchDto SafeValues(DownloadSettings settings)
        => new(
            Output: new OutputSettingsPatchDto(
                settings.Output.ParentDir,
                settings.Output.NameFormat,
                settings.Output.InvalidReplaceStr,
                settings.Output.WritePlaylist,
                settings.Output.WriteIndex ?? true,
                settings.Output.M3uFilePath,
                settings.Output.IndexFilePath,
                new IncompleteAlbumActionSettingsPatchDto(
                    settings.Output.IncompleteAlbumAction.Kind?.ToServer(),
                    settings.Output.IncompleteAlbumAction.Path),
                OnComplete: null,
                settings.Output.AlbumArtOnly,
                settings.Output.AlbumArtOption.ToServer()),
            Search: ToSearchSettings(settings.ResolveSearchSettings()),
            Skip: new SkipSettingsPatchDto(
                settings.Skip.SkipExisting,
                settings.Skip.SkipNotFound,
                settings.Skip.SkipMode.ToServer(),
                settings.Skip.SkipMusicDir,
                settings.Skip.SkipModeMusicDir.ToServer(),
                settings.Skip.SkipCheckCond,
                settings.Skip.SkipCheckPrefCond),
            Preprocess: new PreprocessSettingsPatchDto(
                settings.Preprocess.RemoveFt,
                settings.Preprocess.RemoveBrackets,
                settings.Preprocess.ExtractArtist,
                settings.Preprocess.ParseTitleTemplate,
                Regex: null),
            Extraction: new ExtractionSettingsPatchDto(
                Input: null,
                settings.Extraction.InputType.ToServer(),
                settings.Extraction.MaxTracks,
                settings.Extraction.Offset,
                settings.Extraction.Reverse,
                settings.Extraction.RemoveTracksFromSource,
                settings.Extraction.RequestedMode?.ToServer(),
                settings.Extraction.UpgradeToAlbum,
                settings.Extraction.SetAlbumMinTrackCount,
                settings.Extraction.SetAlbumMaxTrackCount),
            Transfer: new TransferSettingsPatchDto(
                settings.Transfer.MaxDownloadRetries,
                settings.Transfer.UnknownErrorRetries,
                settings.Transfer.NoIncompleteExt,
                settings.Transfer.AlbumTrackCountMaxRetries),
            Spotify: null,
            YouTube: new YouTubeSettingsPatchDto(
                ApiKey: null,
                settings.YouTube.GetDeleted,
                settings.YouTube.DeletedOnly),
            YtDlp: new YtDlpSettingsPatchDto(
                settings.YtDlp.UseYtdlp,
                YtdlpArgument: null),
            Csv: new CsvSettingsPatchDto(
                settings.Csv.ArtistCol,
                settings.Csv.AlbumCol,
                settings.Csv.TitleCol,
                settings.Csv.YtIdCol,
                settings.Csv.DescCol,
                settings.Csv.TrackCountCol,
                settings.Csv.LengthCol,
                settings.Csv.TimeUnit,
                settings.Csv.YtParse),
            Bandcamp: null,
            settings.PrintOption.ToServer());

    public static SearchSettingsPatchDto ToSearchSettings(SearchSettings settings)
        => new(
            FileConditions(settings.NecessaryCond),
            FileConditions(settings.PreferredCond),
            FolderConditions(settings.NecessaryFolderCond),
            FolderConditions(settings.PreferredFolderCond),
            settings.SearchTimeout,
            settings.DownrankOn,
            settings.IgnoreOn,
            settings.FastSearch,
            settings.FastSearchDelay,
            settings.FastSearchMinUpSpeed,
            settings.DesperateSearch,
            settings.NoRemoveSpecialChars,
            settings.RemoveSingleCharSearchTerms,
            settings.NoBrowseFolder,
            settings.Relax,
            settings.StrictAlbumQuality,
            settings.ArtistMaybeWrong,
            settings.GroupResults,
            settings.EffectiveMinGroupShares,
            settings.GroupLengthTol,
            settings.FallbackLevel,
            settings.MaxFallbackAttempts,
            settings.MaxStaleTime);

    private static FileConditionsPatchDto FileConditions(FileConditions conditions)
        => new(
            conditions.LengthTolerance,
            conditions.MinBitrate,
            conditions.MaxBitrate,
            conditions.MinSampleRate,
            conditions.MaxSampleRate,
            conditions.MinBitDepth,
            conditions.MaxBitDepth,
            conditions.StrictTitle,
            conditions.StrictArtist,
            conditions.StrictAlbum,
            new CollectionPatchDto<string>(Replace: conditions.Formats),
            new CollectionPatchDto<string>(Replace: conditions.BannedUsers),
            new CollectionPatchDto<string>(Replace: conditions.AllowedUsers),
            conditions.AcceptNoLength,
            conditions.AcceptMissingProps,
            ExcludeLocked: conditions.ExcludeLocked);

    private static FolderConditionsPatchDto FolderConditions(FolderConditions conditions)
        => new(
            conditions.MinTrackCount,
            conditions.MaxTrackCount,
            new CollectionPatchDto<string>(Replace: conditions.RequiredTrackTitles.ToArray()));
}
