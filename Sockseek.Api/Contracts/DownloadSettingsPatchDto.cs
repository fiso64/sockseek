namespace Sockseek.Api;

public sealed record CollectionPatchDto<T>(
    IReadOnlyList<T>? Replace = null,
    IReadOnlyList<T>? Append = null);

public sealed record RegexRuleDto(
    RegexFieldsDto Match,
    RegexFieldsDto Replace);

public sealed record RegexFieldsDto(
    string Title,
    string Artist,
    string Album);

public sealed record DownloadSettingsPatchDto(
    OutputSettingsPatchDto? Output = null,
    SearchSettingsPatchDto? Search = null,
    SkipSettingsPatchDto? Skip = null,
    PreprocessSettingsPatchDto? Preprocess = null,
    ExtractionSettingsPatchDto? Extraction = null,
    TransferSettingsPatchDto? Transfer = null,
    SpotifySettingsPatchDto? Spotify = null,
    YouTubeSettingsPatchDto? YouTube = null,
    YtDlpSettingsPatchDto? YtDlp = null,
    CsvSettingsPatchDto? Csv = null,
    BandcampSettingsPatchDto? Bandcamp = null,
    ServerPrintOption? PrintOption = null,
    SoundCloudSettingsPatchDto? SoundCloud = null);

public sealed record OutputSettingsPatchDto(
    string? ParentDir = null,
    string? NameFormat = null,
    string? InvalidReplaceStr = null,
    bool? WritePlaylist = null,
    bool? WriteIndex = null,
    string? M3uFilePath = null,
    string? IndexFilePath = null,
    IncompleteAlbumActionSettingsPatchDto? IncompleteAlbumAction = null,
    CollectionPatchDto<string>? OnComplete = null,
    bool? AlbumArtOnly = null,
    ServerAlbumArtOption? AlbumArtOption = null,
    bool? ClearWriteIndex = null);

public sealed record IncompleteAlbumActionSettingsPatchDto(
    ServerIncompleteAlbumActionKind? Kind = null,
    string? Path = null,
    bool? ClearPath = null,
    bool? ClearKind = null);

public sealed record SearchSettingsPatchDto(
    FileConditionsPatchDto? NecessaryCond = null,
    FileConditionsPatchDto? PreferredCond = null,
    FolderConditionsPatchDto? NecessaryFolderCond = null,
    FolderConditionsPatchDto? PreferredFolderCond = null,
    int? SearchTimeout = null,
    int? DownrankOn = null,
    int? IgnoreOn = null,
    bool? FastSearch = null,
    int? FastSearchDelay = null,
    double? FastSearchMinUpSpeed = null,
    bool? DesperateSearch = null,
    bool? NoRemoveSpecialChars = null,
    bool? RemoveSingleCharSearchTerms = null,
    bool? NoBrowseFolder = null,
    bool? Relax = null,
    bool? StrictAlbumQuality = null,
    bool? ArtistMaybeWrong = null,
    bool? IsAggregate = null,
    int? MinSharesAggregate = null,
    int? AggregateLengthTol = null,
    int? FallbackLevel = null,
    int? MaxFallbackAttempts = null,
    int? MaxStaleTime = null,
    bool? ClearMinSharesAggregate = null);

public sealed record FileConditionsPatchDto(
    int? LengthTolerance = null,
    int? MinBitrate = null,
    int? MaxBitrate = null,
    int? MinSampleRate = null,
    int? MaxSampleRate = null,
    int? MinBitDepth = null,
    int? MaxBitDepth = null,
    bool? StrictTitle = null,
    bool? StrictArtist = null,
    bool? StrictAlbum = null,
    CollectionPatchDto<string>? Formats = null,
    CollectionPatchDto<string>? BannedUsers = null,
    CollectionPatchDto<string>? AllowedUsers = null,
    bool? AcceptNoLength = null,
    bool? AcceptMissingProps = null,
    // Omitted means inherit; true explicitly removes an inherited bound.
    bool? ClearMinBitrate = null,
    bool? ClearMaxBitrate = null,
    bool? ClearMinSampleRate = null,
    bool? ClearMaxSampleRate = null,
    bool? ClearMinBitDepth = null,
    bool? ClearMaxBitDepth = null,
    bool? ClearLengthTolerance = null,
    bool? ExcludeLocked = null);

public sealed record FolderConditionsPatchDto(
    int? MinTrackCount = null,
    int? MaxTrackCount = null,
    CollectionPatchDto<string>? RequiredTrackTitles = null,
    bool? ClearMinTrackCount = null,
    bool? ClearMaxTrackCount = null);

public sealed record SkipSettingsPatchDto(
    bool? SkipExisting = null,
    bool? SkipNotFound = null,
    ServerSkipMode? SkipMode = null,
    string? SkipMusicDir = null,
    ServerSkipMode? SkipModeMusicDir = null,
    bool? SkipCheckCond = null,
    bool? SkipCheckPrefCond = null);

public sealed record PreprocessSettingsPatchDto(
    bool? RemoveFt = null,
    bool? RemoveBrackets = null,
    bool? ExtractArtist = null,
    string? ParseTitleTemplate = null,
    CollectionPatchDto<RegexRuleDto>? Regex = null);

public sealed record ExtractionSettingsPatchDto(
    string? Input = null,
    ServerInputType? InputType = null,
    int? MaxTracks = null,
    int? Offset = null,
    bool? Reverse = null,
    bool? RemoveTracksFromSource = null,
    // Nullable by design: null lets the input source decide. String input and string
    // lines inside list files then use the 3.0 album default; explicit Song/Album
    // only affects ambiguous string interpretation.
    ServerExtractionMode? RequestedMode = null,
    bool? UpgradeToAlbum = null,
    bool? SetAlbumMinTrackCount = null,
    bool? SetAlbumMaxTrackCount = null,
    bool? ClearRequestedMode = null);

public sealed record TransferSettingsPatchDto(
    int? MaxDownloadRetries = null,
    int? UnknownErrorRetries = null,
    bool? NoIncompleteExt = null,
    int? AlbumTrackCountMaxRetries = null);

public sealed record SpotifySettingsPatchDto(
    string? ClientId = null,
    string? ClientSecret = null,
    string? Token = null,
    string? Refresh = null);

public sealed record YouTubeSettingsPatchDto(
    string? ApiKey = null,
    bool? GetDeleted = null,
    bool? DeletedOnly = null);

public sealed record YtDlpSettingsPatchDto(
    bool? UseYtdlp = null,
    string? YtdlpArgument = null);

public sealed record CsvSettingsPatchDto(
    string? ArtistCol = null,
    string? AlbumCol = null,
    string? TitleCol = null,
    string? YtIdCol = null,
    string? DescCol = null,
    string? TrackCountCol = null,
    string? LengthCol = null,
    string? TimeUnit = null,
    bool? YtParse = null);

public sealed record BandcampSettingsPatchDto(
    string? HtmlFromFile = null);

public sealed record SoundCloudSettingsPatchDto(string? AuthToken = null);
