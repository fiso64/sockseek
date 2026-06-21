using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Api;

public sealed record CollectionPatchDto<T>(
    IReadOnlyList<T>? Replace = null,
    IReadOnlyList<T>? Append = null);

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
    PrintOption? PrintOption = null);

public sealed record OutputSettingsPatchDto(
    string? ParentDir = null,
    string? NameFormat = null,
    string? InvalidReplaceStr = null,
    bool? WritePlaylist = null,
    bool? WriteIndex = null,
    bool? HasConfiguredIndex = null,
    string? M3uFilePath = null,
    string? IndexFilePath = null,
    IncompleteAlbumActionSettingsPatchDto? IncompleteAlbumAction = null,
    CollectionPatchDto<string>? OnComplete = null,
    bool? AlbumArtOnly = null,
    AlbumArtOption? AlbumArtOption = null);

public sealed record IncompleteAlbumActionSettingsPatchDto(
    IncompleteAlbumActionKind? Kind = null,
    string? Path = null);

public sealed record SearchSettingsPatchDto(
    FileConditionsPatchDto? NecessaryCond = null,
    FileConditionsPatchDto? PreferredCond = null,
    FolderConditionsPatchDto? NecessaryFolderCond = null,
    FolderConditionsPatchDto? PreferredFolderCond = null,
    int? SearchTimeout = null,
    int? MaxStaleTime = null,
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
    int? AggregateLengthTol = null);

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
    bool? AcceptMissingProps = null);

public sealed record FolderConditionsPatchDto(
    int? MinTrackCount = null,
    int? MaxTrackCount = null,
    CollectionPatchDto<string>? RequiredTrackTitles = null);

public sealed record SkipSettingsPatchDto(
    bool? SkipExisting = null,
    bool? SkipNotFound = null,
    SkipMode? SkipMode = null,
    string? SkipMusicDir = null,
    SkipMode? SkipModeMusicDir = null,
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
    InputType? InputType = null,
    int? MaxTracks = null,
    int? Offset = null,
    bool? Reverse = null,
    bool? RemoveTracksFromSource = null,
    // Nullable by design: null lets the input source decide. String input and string
    // lines inside list files then use the 3.0 album default; explicit Song/Album
    // only affects ambiguous string interpretation.
    ExtractionMode? RequestedMode = null,
    bool? UpgradeToAlbum = null,
    bool? SetAlbumMinTrackCount = null,
    bool? SetAlbumMaxTrackCount = null);

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

public static class DownloadSettingsPatchDtoMapper
{
    public static void ApplyTo(DownloadSettings settings, DownloadSettingsPatchDto? patch)
    {
        if (patch == null)
            return;

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

        if (patch.PrintOption is { } printOption) settings.PrintOption = printOption;
    }

    public static DownloadSettingsPatchDto? FromDifference(DownloadSettings baseline, DownloadSettings effective)
    {
        var operations = DownloadSettingsDeltaMapper.DifferenceOperations(baseline, effective);
        return FromOperations(operations);
    }

    public static DownloadSettingsPatchDto? FromOperations(IEnumerable<DownloadSettingOperationDto> operations)
    {
        var patch = new PatchBuilder();
        foreach (var operation in operations)
            patch.Add(operation);
        return patch.Build();
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
            target.IncompleteAlbumAction.Kind = incompleteAlbumAction.Kind;
            target.IncompleteAlbumAction.Path = incompleteAlbumAction.Path;
        }
        if (patch.OnComplete is { } onComplete)
        {
            ValidateOnCompletePatch(onComplete);
            target.OnComplete ??= [];
            onComplete.ApplyTo(target.OnComplete);
        }
        if (patch.AlbumArtOnly is { } albumArtOnly) target.AlbumArtOnly = albumArtOnly;
        if (patch.AlbumArtOption is { } albumArtOption) target.AlbumArtOption = albumArtOption;
    }

    private static void ApplySearch(SearchSettings target, SearchSettingsPatchDto? patch)
    {
        if (patch == null) return;
        ApplyFileConditions(target.NecessaryCond, patch.NecessaryCond);
        ApplyFileConditions(target.PreferredCond, patch.PreferredCond);
        ApplyFolderConditions(target.NecessaryFolderCond, patch.NecessaryFolderCond);
        ApplyFolderConditions(target.PreferredFolderCond, patch.PreferredFolderCond);
        if (patch.SearchTimeout is { } searchTimeout) target.SearchTimeout = searchTimeout;
        if (patch.MaxStaleTime is { } maxStaleTime) target.MaxStaleTime = maxStaleTime;
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
        if (patch.MinTrackCount is { } minTrackCount) target.MinTrackCount = minTrackCount;
        if (patch.MaxTrackCount is { } maxTrackCount) target.MaxTrackCount = maxTrackCount;
        patch.RequiredTrackTitles?.ApplyTo(target.RequiredTrackTitles);
    }

    private static void ApplySkip(SkipSettings target, SkipSettingsPatchDto? patch)
    {
        if (patch == null) return;
        if (patch.SkipExisting is { } skipExisting) target.SkipExisting = skipExisting;
        if (patch.SkipNotFound is { } skipNotFound) target.SkipNotFound = skipNotFound;
        if (patch.SkipMode is { } skipMode) target.SkipMode = skipMode;
        if (patch.SkipMusicDir is { } skipMusicDir) target.SkipMusicDir = skipMusicDir;
        if (patch.SkipModeMusicDir is { } skipModeMusicDir) target.SkipModeMusicDir = skipModeMusicDir;
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
        if (patch.InputType is { } inputType) target.InputType = inputType;
        if (patch.MaxTracks is { } maxTracks) target.MaxTracks = maxTracks;
        if (patch.Offset is { } offset) target.Offset = offset;
        if (patch.Reverse is { } reverse) target.Reverse = reverse;
        if (patch.RemoveTracksFromSource is { } removeTracksFromSource) target.RemoveTracksFromSource = removeTracksFromSource;
        if (patch.RequestedMode is { } requestedMode) target.RequestedMode = requestedMode;
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

    private sealed class PatchBuilder
    {
        private OutputBuilder? output;
        private SearchBuilder? search;
        private SkipBuilder? skip;
        private PreprocessBuilder? preprocess;
        private ExtractionBuilder? extraction;
        private TransferBuilder? transfer;
        private SpotifyBuilder? spotify;
        private YouTubeBuilder? youtube;
        private YtDlpBuilder? ytDlp;
        private CsvBuilder? csv;
        private BandcampBuilder? bandcamp;
        private PrintOption? printOption;

        public void Add(DownloadSettingOperationDto op)
        {
            switch (op.Path)
            {
                case "Output.ParentDir": Output.ParentDir = op.StringValue; break;
                case "Output.NameFormat": Output.NameFormat = op.StringValue ?? ""; break;
                case "Output.InvalidReplaceStr": Output.InvalidReplaceStr = op.StringValue ?? ""; break;
                case "Output.WritePlaylist": Output.WritePlaylist = Bool(op); break;
                case "Output.WriteIndex": Output.WriteIndex = Bool(op); break;
                case "Output.HasConfiguredIndex": Output.HasConfiguredIndex = Bool(op); break;
                case "Output.M3uFilePath": Output.M3uFilePath = op.StringValue; break;
                case "Output.IndexFilePath": Output.IndexFilePath = op.StringValue; break;
                case "Output.IncompleteAlbumAction.Kind": Output.IncompleteAlbumAction.SetKind(op.IncompleteAlbumActionKindValue); break;
                case "Output.IncompleteAlbumAction.Path": Output.IncompleteAlbumAction.SetPath(op.StringValue); break;
                case "Output.OnComplete": Output.OnComplete = Collection(Output.OnComplete, op); break;
                case "Output.AlbumArtOnly": Output.AlbumArtOnly = Bool(op); break;
                case "Output.AlbumArtOption": Output.AlbumArtOption = op.AlbumArtOptionValue; break;

                case "Search.SearchTimeout": Search.SearchTimeout = Int(op); break;
                case "Search.MaxStaleTime": Search.MaxStaleTime = Int(op); break;
                case "Search.DownrankOn": Search.DownrankOn = Int(op); break;
                case "Search.IgnoreOn": Search.IgnoreOn = Int(op); break;
                case "Search.FastSearch": Search.FastSearch = Bool(op); break;
                case "Search.FastSearchDelay": Search.FastSearchDelay = Int(op); break;
                case "Search.FastSearchMinUpSpeed": Search.FastSearchMinUpSpeed = Double(op); break;
                case "Search.DesperateSearch": Search.DesperateSearch = Bool(op); break;
                case "Search.NoRemoveSpecialChars": Search.NoRemoveSpecialChars = Bool(op); break;
                case "Search.RemoveSingleCharSearchTerms": Search.RemoveSingleCharSearchTerms = Bool(op); break;
                case "Search.NoBrowseFolder": Search.NoBrowseFolder = Bool(op); break;
                case "Search.Relax": Search.Relax = Bool(op); break;
                case "Search.StrictAlbumQuality": Search.StrictAlbumQuality = Bool(op); break;
                case "Search.ArtistMaybeWrong": Search.ArtistMaybeWrong = Bool(op); break;
                case "Search.IsAggregate": Search.IsAggregate = Bool(op); break;
                case "Search.MinSharesAggregate": Search.MinSharesAggregate = Int(op); break;
                case "Search.AggregateLengthTol": Search.AggregateLengthTol = Int(op); break;

                case "Search.NecessaryCond.LengthTolerance": Search.NecessaryCond.LengthTolerance = op.IntValue; break;
                case "Search.NecessaryCond.MinBitrate": Search.NecessaryCond.MinBitrate = op.IntValue; break;
                case "Search.NecessaryCond.MaxBitrate": Search.NecessaryCond.MaxBitrate = op.IntValue; break;
                case "Search.NecessaryCond.MinSampleRate": Search.NecessaryCond.MinSampleRate = op.IntValue; break;
                case "Search.NecessaryCond.MaxSampleRate": Search.NecessaryCond.MaxSampleRate = op.IntValue; break;
                case "Search.NecessaryCond.MinBitDepth": Search.NecessaryCond.MinBitDepth = op.IntValue; break;
                case "Search.NecessaryCond.MaxBitDepth": Search.NecessaryCond.MaxBitDepth = op.IntValue; break;
                case "Search.NecessaryCond.StrictTitle": Search.NecessaryCond.StrictTitle = op.BoolValue; break;
                case "Search.NecessaryCond.StrictArtist": Search.NecessaryCond.StrictArtist = op.BoolValue; break;
                case "Search.NecessaryCond.StrictAlbum": Search.NecessaryCond.StrictAlbum = op.BoolValue; break;
                case "Search.NecessaryCond.Formats": Search.NecessaryCond.Formats = Collection(Search.NecessaryCond.Formats, op); break;
                case "Search.NecessaryCond.BannedUsers": Search.NecessaryCond.BannedUsers = Collection(Search.NecessaryCond.BannedUsers, op); break;
                case "Search.NecessaryCond.AllowedUsers": Search.NecessaryCond.AllowedUsers = Collection(Search.NecessaryCond.AllowedUsers, op); break;
                case "Search.NecessaryCond.AcceptNoLength": Search.NecessaryCond.AcceptNoLength = op.BoolValue; break;
                case "Search.NecessaryCond.AcceptMissingProps": Search.NecessaryCond.AcceptMissingProps = op.BoolValue; break;

                case "Search.PreferredCond.LengthTolerance": Search.PreferredCond.LengthTolerance = op.IntValue; break;
                case "Search.PreferredCond.MinBitrate": Search.PreferredCond.MinBitrate = op.IntValue; break;
                case "Search.PreferredCond.MaxBitrate": Search.PreferredCond.MaxBitrate = op.IntValue; break;
                case "Search.PreferredCond.MinSampleRate": Search.PreferredCond.MinSampleRate = op.IntValue; break;
                case "Search.PreferredCond.MaxSampleRate": Search.PreferredCond.MaxSampleRate = op.IntValue; break;
                case "Search.PreferredCond.MinBitDepth": Search.PreferredCond.MinBitDepth = op.IntValue; break;
                case "Search.PreferredCond.MaxBitDepth": Search.PreferredCond.MaxBitDepth = op.IntValue; break;
                case "Search.PreferredCond.StrictTitle": Search.PreferredCond.StrictTitle = op.BoolValue; break;
                case "Search.PreferredCond.StrictArtist": Search.PreferredCond.StrictArtist = op.BoolValue; break;
                case "Search.PreferredCond.StrictAlbum": Search.PreferredCond.StrictAlbum = op.BoolValue; break;
                case "Search.PreferredCond.Formats": Search.PreferredCond.Formats = Collection(Search.PreferredCond.Formats, op); break;
                case "Search.PreferredCond.BannedUsers": Search.PreferredCond.BannedUsers = Collection(Search.PreferredCond.BannedUsers, op); break;
                case "Search.PreferredCond.AllowedUsers": Search.PreferredCond.AllowedUsers = Collection(Search.PreferredCond.AllowedUsers, op); break;
                case "Search.PreferredCond.AcceptNoLength": Search.PreferredCond.AcceptNoLength = op.BoolValue; break;
                case "Search.PreferredCond.AcceptMissingProps": Search.PreferredCond.AcceptMissingProps = op.BoolValue; break;

                case "Search.NecessaryFolderCond.MinTrackCount": Search.NecessaryFolderCond.MinTrackCount = Int(op); break;
                case "Search.NecessaryFolderCond.MaxTrackCount": Search.NecessaryFolderCond.MaxTrackCount = Int(op); break;
                case "Search.NecessaryFolderCond.RequiredTrackTitles": Search.NecessaryFolderCond.RequiredTrackTitles = Collection(Search.NecessaryFolderCond.RequiredTrackTitles, op); break;
                case "Search.PreferredFolderCond.MinTrackCount": Search.PreferredFolderCond.MinTrackCount = Int(op); break;
                case "Search.PreferredFolderCond.MaxTrackCount": Search.PreferredFolderCond.MaxTrackCount = Int(op); break;
                case "Search.PreferredFolderCond.RequiredTrackTitles": Search.PreferredFolderCond.RequiredTrackTitles = Collection(Search.PreferredFolderCond.RequiredTrackTitles, op); break;

                case "Skip.SkipExisting": Skip.SkipExisting = Bool(op); break;
                case "Skip.SkipNotFound": Skip.SkipNotFound = Bool(op); break;
                case "Skip.SkipMode": Skip.SkipMode = op.SkipModeValue; break;
                case "Skip.SkipMusicDir": Skip.SkipMusicDir = op.StringValue; break;
                case "Skip.SkipModeMusicDir": Skip.SkipModeMusicDir = op.SkipModeValue; break;
                case "Skip.SkipCheckCond": Skip.SkipCheckCond = Bool(op); break;
                case "Skip.SkipCheckPrefCond": Skip.SkipCheckPrefCond = Bool(op); break;

                case "Preprocess.RemoveFt": Preprocess.RemoveFt = Bool(op); break;
                case "Preprocess.RemoveBrackets": Preprocess.RemoveBrackets = Bool(op); break;
                case "Preprocess.ExtractArtist": Preprocess.ExtractArtist = Bool(op); break;
                case "Preprocess.ParseTitleTemplate": Preprocess.ParseTitleTemplate = op.StringValue; break;
                case "Preprocess.Regex": Preprocess.Regex = RegexCollection(Preprocess.Regex, op); break;

                case "Extraction.Input": Extraction.Input = op.StringValue; break;
                case "Extraction.InputType": Extraction.InputType = op.InputTypeValue; break;
                case "Extraction.MaxTracks": Extraction.MaxTracks = Int(op); break;
                case "Extraction.Offset": Extraction.Offset = Int(op); break;
                case "Extraction.Reverse": Extraction.Reverse = Bool(op); break;
                case "Extraction.RemoveTracksFromSource": Extraction.RemoveTracksFromSource = Bool(op); break;
                case "Extraction.RequestedMode": Extraction.RequestedMode = op.ExtractionModeValue; break;
                case "Extraction.UpgradeToAlbum": Extraction.UpgradeToAlbum = Bool(op); break;
                case "Extraction.SetAlbumMinTrackCount": Extraction.SetAlbumMinTrackCount = Bool(op); break;
                case "Extraction.SetAlbumMaxTrackCount": Extraction.SetAlbumMaxTrackCount = Bool(op); break;

                case "Transfer.MaxDownloadRetries": Transfer.MaxDownloadRetries = Int(op); break;
                case "Transfer.UnknownErrorRetries": Transfer.UnknownErrorRetries = Int(op); break;
                case "Transfer.NoIncompleteExt": Transfer.NoIncompleteExt = Bool(op); break;
                case "Transfer.AlbumTrackCountMaxRetries": Transfer.AlbumTrackCountMaxRetries = Int(op); break;

                case "Spotify.ClientId": Spotify.ClientId = op.StringValue; break;
                case "Spotify.ClientSecret": Spotify.ClientSecret = op.StringValue; break;
                case "Spotify.Token": Spotify.Token = op.StringValue; break;
                case "Spotify.Refresh": Spotify.Refresh = op.StringValue; break;
                case "YouTube.ApiKey": YouTube.ApiKey = op.StringValue; break;
                case "YouTube.GetDeleted": YouTube.GetDeleted = Bool(op); break;
                case "YouTube.DeletedOnly": YouTube.DeletedOnly = Bool(op); break;
                case "YtDlp.UseYtdlp": YtDlp.UseYtdlp = Bool(op); break;
                case "YtDlp.YtdlpArgument": YtDlp.YtdlpArgument = op.StringValue; break;
                case "Csv.ArtistCol": Csv.ArtistCol = op.StringValue ?? ""; break;
                case "Csv.AlbumCol": Csv.AlbumCol = op.StringValue ?? ""; break;
                case "Csv.TitleCol": Csv.TitleCol = op.StringValue ?? ""; break;
                case "Csv.YtIdCol": Csv.YtIdCol = op.StringValue ?? ""; break;
                case "Csv.DescCol": Csv.DescCol = op.StringValue ?? ""; break;
                case "Csv.TrackCountCol": Csv.TrackCountCol = op.StringValue ?? ""; break;
                case "Csv.LengthCol": Csv.LengthCol = op.StringValue ?? ""; break;
                case "Csv.TimeUnit": Csv.TimeUnit = op.StringValue ?? ""; break;
                case "Csv.YtParse": Csv.YtParse = Bool(op); break;
                case "Bandcamp.HtmlFromFile": Bandcamp.HtmlFromFile = op.StringValue; break;
                case "PrintOption": printOption = op.PrintOptionValue; break;
                default:
                    throw new ArgumentException($"Unknown download setting operation path '{op.Path}'.");
            }
        }

        public DownloadSettingsPatchDto? Build()
        {
            var result = new DownloadSettingsPatchDto(
                output?.Build(),
                search?.Build(),
                skip?.Build(),
                preprocess?.Build(),
                extraction?.Build(),
                transfer?.Build(),
                spotify?.Build(),
                youtube?.Build(),
                ytDlp?.Build(),
                csv?.Build(),
                bandcamp?.Build(),
                printOption);

            return result == new DownloadSettingsPatchDto() ? null : result;
        }

        private OutputBuilder Output => output ??= new();
        private SearchBuilder Search => search ??= new();
        private SkipBuilder Skip => skip ??= new();
        private PreprocessBuilder Preprocess => preprocess ??= new();
        private ExtractionBuilder Extraction => extraction ??= new();
        private TransferBuilder Transfer => transfer ??= new();
        private SpotifyBuilder Spotify => spotify ??= new();
        private YouTubeBuilder YouTube => youtube ??= new();
        private YtDlpBuilder YtDlp => ytDlp ??= new();
        private CsvBuilder Csv => csv ??= new();
        private BandcampBuilder Bandcamp => bandcamp ??= new();
    }

    private sealed class OutputBuilder
    {
        public string? ParentDir, NameFormat, InvalidReplaceStr, M3uFilePath, IndexFilePath;
        public IncompleteAlbumActionBuilder IncompleteAlbumAction { get; } = new();
        public bool? WritePlaylist, WriteIndex, HasConfiguredIndex, AlbumArtOnly;
        public AlbumArtOption? AlbumArtOption;
        public CollectionPatchDto<string>? OnComplete;
        public OutputSettingsPatchDto Build() => new(ParentDir, NameFormat, InvalidReplaceStr, WritePlaylist, WriteIndex, HasConfiguredIndex, M3uFilePath, IndexFilePath, IncompleteAlbumAction.Build(), OnComplete, AlbumArtOnly, AlbumArtOption);
    }

    private sealed class IncompleteAlbumActionBuilder
    {
        private bool touched;
        private IncompleteAlbumActionKind? kind;
        private string? path;

        public void SetKind(IncompleteAlbumActionKind? value)
        {
            kind = value;
            touched = true;
        }

        public void SetPath(string? value)
        {
            path = value;
            touched = true;
        }

        public IncompleteAlbumActionSettingsPatchDto? Build()
            => touched ? new IncompleteAlbumActionSettingsPatchDto(kind, path) : null;
    }

    private sealed class SearchBuilder
    {
        public FileConditionsBuilder NecessaryCond { get; } = new();
        public FileConditionsBuilder PreferredCond { get; } = new();
        public FolderConditionsBuilder NecessaryFolderCond { get; } = new();
        public FolderConditionsBuilder PreferredFolderCond { get; } = new();
        public int? SearchTimeout, MaxStaleTime, DownrankOn, IgnoreOn, FastSearchDelay, MinSharesAggregate, AggregateLengthTol;
        public double? FastSearchMinUpSpeed;
        public bool? FastSearch, DesperateSearch, NoRemoveSpecialChars, RemoveSingleCharSearchTerms, NoBrowseFolder, Relax, StrictAlbumQuality, ArtistMaybeWrong, IsAggregate;
        public SearchSettingsPatchDto Build() => new(NecessaryCond.Build(), PreferredCond.Build(), NecessaryFolderCond.Build(), PreferredFolderCond.Build(), SearchTimeout, MaxStaleTime, DownrankOn, IgnoreOn, FastSearch, FastSearchDelay, FastSearchMinUpSpeed, DesperateSearch, NoRemoveSpecialChars, RemoveSingleCharSearchTerms, NoBrowseFolder, Relax, StrictAlbumQuality, ArtistMaybeWrong, IsAggregate, MinSharesAggregate, AggregateLengthTol);
    }

    private sealed class FileConditionsBuilder
    {
        public int? LengthTolerance, MinBitrate, MaxBitrate, MinSampleRate, MaxSampleRate, MinBitDepth, MaxBitDepth;
        public bool? StrictTitle, StrictArtist, StrictAlbum, AcceptNoLength, AcceptMissingProps;
        public CollectionPatchDto<string>? Formats, BannedUsers, AllowedUsers;
        public FileConditionsPatchDto? Build()
        {
            var result = new FileConditionsPatchDto(LengthTolerance, MinBitrate, MaxBitrate, MinSampleRate, MaxSampleRate, MinBitDepth, MaxBitDepth, StrictTitle, StrictArtist, StrictAlbum, Formats, BannedUsers, AllowedUsers, AcceptNoLength, AcceptMissingProps);
            return result == new FileConditionsPatchDto() ? null : result;
        }
    }

    private sealed class FolderConditionsBuilder
    {
        public int? MinTrackCount, MaxTrackCount;
        public CollectionPatchDto<string>? RequiredTrackTitles;
        public FolderConditionsPatchDto? Build()
        {
            var result = new FolderConditionsPatchDto(MinTrackCount, MaxTrackCount, RequiredTrackTitles);
            return result == new FolderConditionsPatchDto() ? null : result;
        }
    }

    private sealed class SkipBuilder
    {
        public bool? SkipExisting, SkipNotFound, SkipCheckCond, SkipCheckPrefCond;
        public SkipMode? SkipMode, SkipModeMusicDir;
        public string? SkipMusicDir;
        public SkipSettingsPatchDto Build() => new(SkipExisting, SkipNotFound, SkipMode, SkipMusicDir, SkipModeMusicDir, SkipCheckCond, SkipCheckPrefCond);
    }

    private sealed class PreprocessBuilder
    {
        public bool? RemoveFt, RemoveBrackets, ExtractArtist;
        public string? ParseTitleTemplate;
        public CollectionPatchDto<RegexRuleDto>? Regex;
        public PreprocessSettingsPatchDto Build() => new(RemoveFt, RemoveBrackets, ExtractArtist, ParseTitleTemplate, Regex);
    }

    private sealed class ExtractionBuilder
    {
        public string? Input;
        public InputType? InputType;
        public int? MaxTracks, Offset;
        public ExtractionMode? RequestedMode;
        public bool? Reverse, RemoveTracksFromSource, UpgradeToAlbum, SetAlbumMinTrackCount, SetAlbumMaxTrackCount;
        public ExtractionSettingsPatchDto Build() => new(Input, InputType, MaxTracks, Offset, Reverse, RemoveTracksFromSource, RequestedMode, UpgradeToAlbum, SetAlbumMinTrackCount, SetAlbumMaxTrackCount);
    }

    private sealed class TransferBuilder
    {
        public int? MaxDownloadRetries, UnknownErrorRetries, AlbumTrackCountMaxRetries;
        public bool? NoIncompleteExt;
        public TransferSettingsPatchDto Build() => new(MaxDownloadRetries, UnknownErrorRetries, NoIncompleteExt, AlbumTrackCountMaxRetries);
    }

    private sealed class SpotifyBuilder
    {
        public string? ClientId, ClientSecret, Token, Refresh;
        public SpotifySettingsPatchDto Build() => new(ClientId, ClientSecret, Token, Refresh);
    }

    private sealed class YouTubeBuilder
    {
        public string? ApiKey;
        public bool? GetDeleted, DeletedOnly;
        public YouTubeSettingsPatchDto Build() => new(ApiKey, GetDeleted, DeletedOnly);
    }

    private sealed class YtDlpBuilder
    {
        public bool? UseYtdlp;
        public string? YtdlpArgument;
        public YtDlpSettingsPatchDto Build() => new(UseYtdlp, YtdlpArgument);
    }

    private sealed class CsvBuilder
    {
        public string? ArtistCol, AlbumCol, TitleCol, YtIdCol, DescCol, TrackCountCol, LengthCol, TimeUnit;
        public bool? YtParse;
        public CsvSettingsPatchDto Build() => new(ArtistCol, AlbumCol, TitleCol, YtIdCol, DescCol, TrackCountCol, LengthCol, TimeUnit, YtParse);
    }

    private sealed class BandcampBuilder
    {
        public string? HtmlFromFile;
        public BandcampSettingsPatchDto Build() => new(HtmlFromFile);
    }

    private static CollectionPatchDto<string> Collection(CollectionPatchDto<string>? current, DownloadSettingOperationDto op)
        => op.Operation == SettingOperationKind.Append
            ? current == null ? new CollectionPatchDto<string>(Append: op.StringListValue ?? []) : current with { Append = [.. current.Append ?? [], .. op.StringListValue ?? []] }
            : current == null ? new CollectionPatchDto<string>(Replace: op.StringListValue ?? []) : current with { Replace = op.StringListValue ?? [] };

    private static void ValidateOnCompletePatch(CollectionPatchDto<string> patch)
    {
        OnCompleteExecutor.ValidateCommands(patch.Replace);
        OnCompleteExecutor.ValidateCommands(patch.Append);
    }

    private static CollectionPatchDto<RegexRuleDto> RegexCollection(CollectionPatchDto<RegexRuleDto>? current, DownloadSettingOperationDto op)
        => op.Operation == SettingOperationKind.Append
            ? current == null ? new CollectionPatchDto<RegexRuleDto>(Append: op.RegexListValue ?? []) : current with { Append = [.. current.Append ?? [], .. op.RegexListValue ?? []] }
            : current == null ? new CollectionPatchDto<RegexRuleDto>(Replace: op.RegexListValue ?? []) : current with { Replace = op.RegexListValue ?? [] };

    private static int Int(DownloadSettingOperationDto op)
        => op.IntValue ?? throw new ArgumentException($"Operation '{op.Path}' requires an integer value.");

    private static double Double(DownloadSettingOperationDto op)
        => op.DoubleValue ?? throw new ArgumentException($"Operation '{op.Path}' requires a double value.");

    private static bool Bool(DownloadSettingOperationDto op)
        => op.BoolValue ?? throw new ArgumentException($"Operation '{op.Path}' requires a boolean value.");
}
