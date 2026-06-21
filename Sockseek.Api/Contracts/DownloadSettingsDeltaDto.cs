using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Api;

public sealed record DownloadSettingsDeltaDto(
    IReadOnlyList<DownloadSettingOperationDto> Operations);

public sealed record DownloadSettingOperationDto(
    string Path,
    SettingOperationKind Operation,
    string? StringValue = null,
    int? IntValue = null,
    double? DoubleValue = null,
    bool? BoolValue = null,
    PrintOption? PrintOptionValue = null,
    InputType? InputTypeValue = null,
    // Used with Path = "Extraction.RequestedMode". Null means unset/source-decided mode.
    ExtractionMode? ExtractionModeValue = null,
    SkipMode? SkipModeValue = null,
    AlbumArtOption? AlbumArtOptionValue = null,
    IncompleteAlbumActionKind? IncompleteAlbumActionKindValue = null,
    IReadOnlyList<string>? StringListValue = null,
    IReadOnlyList<RegexRuleDto>? RegexListValue = null);

public enum SettingOperationKind
{
    Set,
    Replace,
    Append,
}

public sealed record RegexRuleDto(
    RegexFieldsDto Match,
    RegexFieldsDto Replace);

public sealed record RegexFieldsDto(
    string Title,
    string Artist,
    string Album);

public static class DownloadSettingsDeltaMapper
{
    public static DownloadSettingsDeltaDto? FromOperations(IEnumerable<DownloadSettingOperationDto> operations)
    {
        var list = operations.ToList();
        return list.Count == 0 ? null : new DownloadSettingsDeltaDto(list);
    }

    public static DownloadSettingsDeltaDto? FromDifference(DownloadSettings baseline, DownloadSettings effective)
        => FromOperations(DifferenceOperations(baseline, effective));

    public static IReadOnlyList<DownloadSettingOperationDto> DifferenceOperations(DownloadSettings baseline, DownloadSettings effective)
    {
        var operations = new List<DownloadSettingOperationDto>();
        AddOutputDiffs(operations, baseline.Output, effective.Output);
        AddSearchDiffs(operations, baseline.Search, effective.Search);
        AddSkipDiffs(operations, baseline.Skip, effective.Skip);
        AddPreprocessDiffs(operations, baseline.Preprocess, effective.Preprocess);
        AddExtractionDiffs(operations, baseline.Extraction, effective.Extraction);
        AddTransferDiffs(operations, baseline.Transfer, effective.Transfer);
        AddSpotifyDiffs(operations, baseline.Spotify, effective.Spotify);
        AddYouTubeDiffs(operations, baseline.YouTube, effective.YouTube);
        AddYtDlpDiffs(operations, baseline.YtDlp, effective.YtDlp);
        AddCsvDiffs(operations, baseline.Csv, effective.Csv);
        AddBandcampDiffs(operations, baseline.Bandcamp, effective.Bandcamp);

        if (baseline.PrintOption != effective.PrintOption)
            operations.Add(Set("PrintOption", effective.PrintOption));

        return operations;
    }

    public static void ApplyTo(DownloadSettings settings, DownloadSettingsDeltaDto? delta)
    {
        if (delta?.Operations == null)
            return;

        foreach (var operation in delta.Operations)
            ApplyOperation(settings, operation);
    }

    private static void ApplyOperation(DownloadSettings settings, DownloadSettingOperationDto op)
    {
        switch (op.Path)
        {
            case "Output.ParentDir": settings.Output.ParentDir = op.StringValue; break;
            case "Output.NameFormat": settings.Output.NameFormat = op.StringValue ?? ""; break;
            case "Output.InvalidReplaceStr": settings.Output.InvalidReplaceStr = op.StringValue ?? ""; break;
            case "Output.WritePlaylist": settings.Output.WritePlaylist = Bool(op); break;
            case "Output.WriteIndex": settings.Output.WriteIndex = Bool(op); break;
            case "Output.HasConfiguredIndex": settings.Output.HasConfiguredIndex = Bool(op); break;
            case "Output.M3uFilePath": settings.Output.M3uFilePath = op.StringValue; break;
            case "Output.IndexFilePath": settings.Output.IndexFilePath = op.StringValue; break;
            case "Output.IncompleteAlbumAction.Kind": settings.Output.IncompleteAlbumAction.Kind = op.IncompleteAlbumActionKindValue; break;
            case "Output.IncompleteAlbumAction.Path": settings.Output.IncompleteAlbumAction.Path = op.StringValue; break;
            case "Output.OnComplete":
                ValidateOnCompleteOperation(op);
                var onComplete = settings.Output.OnComplete;
                ApplyStringList(ref onComplete, op);
                settings.Output.OnComplete = onComplete;
                break;
            case "Output.AlbumArtOnly": settings.Output.AlbumArtOnly = Bool(op); break;
            case "Output.AlbumArtOption": settings.Output.AlbumArtOption = op.AlbumArtOptionValue ?? settings.Output.AlbumArtOption; break;

            case "Search.SearchTimeout": settings.Search.SearchTimeout = Int(op); break;
            case "Search.MaxStaleTime": settings.Search.MaxStaleTime = Int(op); break;
            case "Search.DownrankOn": settings.Search.DownrankOn = Int(op); break;
            case "Search.IgnoreOn": settings.Search.IgnoreOn = Int(op); break;
            case "Search.FastSearch": settings.Search.FastSearch = Bool(op); break;
            case "Search.FastSearchDelay": settings.Search.FastSearchDelay = Int(op); break;
            case "Search.FastSearchMinUpSpeed": settings.Search.FastSearchMinUpSpeed = Double(op); break;
            case "Search.DesperateSearch": settings.Search.DesperateSearch = Bool(op); break;
            case "Search.NoRemoveSpecialChars": settings.Search.NoRemoveSpecialChars = Bool(op); break;
            case "Search.RemoveSingleCharSearchTerms": settings.Search.RemoveSingleCharSearchTerms = Bool(op); break;
            case "Search.NoBrowseFolder": settings.Search.NoBrowseFolder = Bool(op); break;
            case "Search.Relax": settings.Search.Relax = Bool(op); break;
            case "Search.StrictAlbumQuality": settings.Search.StrictAlbumQuality = Bool(op); break;
            case "Search.ArtistMaybeWrong": settings.Search.ArtistMaybeWrong = Bool(op); break;
            case "Search.IsAggregate": settings.Search.IsAggregate = Bool(op); break;
            case "Search.MinSharesAggregate": settings.Search.MinSharesAggregate = Int(op); break;
            case "Search.AggregateLengthTol": settings.Search.AggregateLengthTol = Int(op); break;

            case "Search.NecessaryCond.LengthTolerance": settings.Search.NecessaryCond.LengthTolerance = op.IntValue; break;
            case "Search.NecessaryCond.MinBitrate": settings.Search.NecessaryCond.MinBitrate = op.IntValue; break;
            case "Search.NecessaryCond.MaxBitrate": settings.Search.NecessaryCond.MaxBitrate = op.IntValue; break;
            case "Search.NecessaryCond.MinSampleRate": settings.Search.NecessaryCond.MinSampleRate = op.IntValue; break;
            case "Search.NecessaryCond.MaxSampleRate": settings.Search.NecessaryCond.MaxSampleRate = op.IntValue; break;
            case "Search.NecessaryCond.MinBitDepth": settings.Search.NecessaryCond.MinBitDepth = op.IntValue; break;
            case "Search.NecessaryCond.MaxBitDepth": settings.Search.NecessaryCond.MaxBitDepth = op.IntValue; break;
            case "Search.NecessaryCond.StrictTitle": settings.Search.NecessaryCond.StrictTitle = Bool(op); break;
            case "Search.NecessaryCond.StrictArtist": settings.Search.NecessaryCond.StrictArtist = Bool(op); break;
            case "Search.NecessaryCond.StrictAlbum": settings.Search.NecessaryCond.StrictAlbum = Bool(op); break;
            case "Search.NecessaryCond.Formats": settings.Search.NecessaryCond.Formats = op.StringListValue?.ToArray() ?? []; break;
            case "Search.NecessaryCond.BannedUsers": settings.Search.NecessaryCond.BannedUsers = op.StringListValue?.ToArray() ?? []; break;
            case "Search.NecessaryCond.AllowedUsers": settings.Search.NecessaryCond.AllowedUsers = op.StringListValue?.ToArray() ?? []; break;
            case "Search.NecessaryCond.AcceptNoLength": settings.Search.NecessaryCond.AcceptNoLength = Bool(op); break;
            case "Search.NecessaryCond.AcceptMissingProps": settings.Search.NecessaryCond.AcceptMissingProps = Bool(op); break;

            case "Search.PreferredCond.LengthTolerance": settings.Search.PreferredCond.LengthTolerance = op.IntValue; break;
            case "Search.PreferredCond.MinBitrate": settings.Search.PreferredCond.MinBitrate = op.IntValue; break;
            case "Search.PreferredCond.MaxBitrate": settings.Search.PreferredCond.MaxBitrate = op.IntValue; break;
            case "Search.PreferredCond.MinSampleRate": settings.Search.PreferredCond.MinSampleRate = op.IntValue; break;
            case "Search.PreferredCond.MaxSampleRate": settings.Search.PreferredCond.MaxSampleRate = op.IntValue; break;
            case "Search.PreferredCond.MinBitDepth": settings.Search.PreferredCond.MinBitDepth = op.IntValue; break;
            case "Search.PreferredCond.MaxBitDepth": settings.Search.PreferredCond.MaxBitDepth = op.IntValue; break;
            case "Search.PreferredCond.StrictTitle": settings.Search.PreferredCond.StrictTitle = Bool(op); break;
            case "Search.PreferredCond.StrictArtist": settings.Search.PreferredCond.StrictArtist = Bool(op); break;
            case "Search.PreferredCond.StrictAlbum": settings.Search.PreferredCond.StrictAlbum = Bool(op); break;
            case "Search.PreferredCond.Formats": settings.Search.PreferredCond.Formats = op.StringListValue?.ToArray() ?? []; break;
            case "Search.PreferredCond.BannedUsers": settings.Search.PreferredCond.BannedUsers = op.StringListValue?.ToArray() ?? []; break;
            case "Search.PreferredCond.AllowedUsers": settings.Search.PreferredCond.AllowedUsers = op.StringListValue?.ToArray() ?? []; break;
            case "Search.PreferredCond.AcceptNoLength": settings.Search.PreferredCond.AcceptNoLength = Bool(op); break;
            case "Search.PreferredCond.AcceptMissingProps": settings.Search.PreferredCond.AcceptMissingProps = Bool(op); break;

            case "Search.NecessaryFolderCond.MinTrackCount": settings.Search.NecessaryFolderCond.MinTrackCount = op.IntValue; break;
            case "Search.NecessaryFolderCond.MaxTrackCount": settings.Search.NecessaryFolderCond.MaxTrackCount = op.IntValue; break;
            case "Search.NecessaryFolderCond.RequiredTrackTitles": ApplyStringList(settings.Search.NecessaryFolderCond.RequiredTrackTitles, op); break;
            case "Search.PreferredFolderCond.MinTrackCount": settings.Search.PreferredFolderCond.MinTrackCount = op.IntValue; break;
            case "Search.PreferredFolderCond.MaxTrackCount": settings.Search.PreferredFolderCond.MaxTrackCount = op.IntValue; break;
            case "Search.PreferredFolderCond.RequiredTrackTitles": ApplyStringList(settings.Search.PreferredFolderCond.RequiredTrackTitles, op); break;

            case "Skip.SkipExisting": settings.Skip.SkipExisting = Bool(op); break;
            case "Skip.SkipNotFound": settings.Skip.SkipNotFound = Bool(op); break;
            case "Skip.SkipMode": settings.Skip.SkipMode = op.SkipModeValue ?? settings.Skip.SkipMode; break;
            case "Skip.SkipMusicDir": settings.Skip.SkipMusicDir = op.StringValue; break;
            case "Skip.SkipModeMusicDir": settings.Skip.SkipModeMusicDir = op.SkipModeValue ?? settings.Skip.SkipModeMusicDir; break;
            case "Skip.SkipCheckCond": settings.Skip.SkipCheckCond = Bool(op); break;
            case "Skip.SkipCheckPrefCond": settings.Skip.SkipCheckPrefCond = Bool(op); break;

            case "Preprocess.RemoveFt": settings.Preprocess.RemoveFt = Bool(op); break;
            case "Preprocess.RemoveBrackets": settings.Preprocess.RemoveBrackets = Bool(op); break;
            case "Preprocess.ExtractArtist": settings.Preprocess.ExtractArtist = Bool(op); break;
            case "Preprocess.ParseTitleTemplate": settings.Preprocess.ParseTitleTemplate = op.StringValue; break;
            case "Preprocess.Regex": ApplyRegex(settings.Preprocess, op); break;

            case "Extraction.Input": settings.Extraction.Input = op.StringValue; break;
            case "Extraction.InputType": settings.Extraction.InputType = op.InputTypeValue ?? settings.Extraction.InputType; break;
            case "Extraction.MaxTracks": settings.Extraction.MaxTracks = Int(op); break;
            case "Extraction.Offset": settings.Extraction.Offset = Int(op); break;
            case "Extraction.Reverse": settings.Extraction.Reverse = Bool(op); break;
            case "Extraction.RemoveTracksFromSource": settings.Extraction.RemoveTracksFromSource = Bool(op); break;
            case "Extraction.RequestedMode": settings.Extraction.RequestedMode = op.ExtractionModeValue; break;
            case "Extraction.UpgradeToAlbum": settings.Extraction.UpgradeToAlbum = Bool(op); break;
            case "Extraction.SetAlbumMinTrackCount": settings.Extraction.SetAlbumMinTrackCount = Bool(op); break;
            case "Extraction.SetAlbumMaxTrackCount": settings.Extraction.SetAlbumMaxTrackCount = Bool(op); break;

            case "Transfer.MaxDownloadRetries": settings.Transfer.MaxDownloadRetries = Int(op); break;
            case "Transfer.UnknownErrorRetries": settings.Transfer.UnknownErrorRetries = Int(op); break;
            case "Transfer.NoIncompleteExt": settings.Transfer.NoIncompleteExt = Bool(op); break;
            case "Transfer.AlbumTrackCountMaxRetries": settings.Transfer.AlbumTrackCountMaxRetries = Int(op); break;

            case "Spotify.ClientId": settings.Spotify.ClientId = op.StringValue; break;
            case "Spotify.ClientSecret": settings.Spotify.ClientSecret = op.StringValue; break;
            case "Spotify.Token": settings.Spotify.Token = op.StringValue; break;
            case "Spotify.Refresh": settings.Spotify.Refresh = op.StringValue; break;
            case "YouTube.ApiKey": settings.YouTube.ApiKey = op.StringValue; break;
            case "YouTube.GetDeleted": settings.YouTube.GetDeleted = Bool(op); break;
            case "YouTube.DeletedOnly": settings.YouTube.DeletedOnly = Bool(op); break;
            case "YtDlp.UseYtdlp": settings.YtDlp.UseYtdlp = Bool(op); break;
            case "YtDlp.YtdlpArgument": settings.YtDlp.YtdlpArgument = op.StringValue; break;
            case "Csv.ArtistCol": settings.Csv.ArtistCol = op.StringValue ?? ""; break;
            case "Csv.AlbumCol": settings.Csv.AlbumCol = op.StringValue ?? ""; break;
            case "Csv.TitleCol": settings.Csv.TitleCol = op.StringValue ?? ""; break;
            case "Csv.YtIdCol": settings.Csv.YtIdCol = op.StringValue ?? ""; break;
            case "Csv.DescCol": settings.Csv.DescCol = op.StringValue ?? ""; break;
            case "Csv.TrackCountCol": settings.Csv.TrackCountCol = op.StringValue ?? ""; break;
            case "Csv.LengthCol": settings.Csv.LengthCol = op.StringValue ?? ""; break;
            case "Csv.TimeUnit": settings.Csv.TimeUnit = op.StringValue ?? ""; break;
            case "Csv.YtParse": settings.Csv.YtParse = Bool(op); break;
            case "Bandcamp.HtmlFromFile": settings.Bandcamp.HtmlFromFile = op.StringValue; break;
            case "PrintOption": settings.PrintOption = op.PrintOptionValue ?? settings.PrintOption; break;
            default:
                throw new ArgumentException($"Unknown download setting operation path '{op.Path}'.");
        }
    }

    private static void AddOutputDiffs(List<DownloadSettingOperationDto> operations, OutputSettings before, OutputSettings after)
    {
        AddStringDiff(operations, "Output.ParentDir", before.ParentDir, after.ParentDir);
        AddStringDiff(operations, "Output.NameFormat", before.NameFormat, after.NameFormat);
        AddStringDiff(operations, "Output.InvalidReplaceStr", before.InvalidReplaceStr, after.InvalidReplaceStr);
        AddBoolDiff(operations, "Output.WritePlaylist", before.WritePlaylist, after.WritePlaylist);
        AddBoolDiff(operations, "Output.WriteIndex", before.WriteIndex, after.WriteIndex);
        AddBoolDiff(operations, "Output.HasConfiguredIndex", before.HasConfiguredIndex, after.HasConfiguredIndex);
        AddStringDiff(operations, "Output.M3uFilePath", before.M3uFilePath, after.M3uFilePath);
        AddStringDiff(operations, "Output.IndexFilePath", before.IndexFilePath, after.IndexFilePath);
        if (before.IncompleteAlbumAction.Kind != after.IncompleteAlbumAction.Kind)
            operations.Add(Set("Output.IncompleteAlbumAction.Kind", after.IncompleteAlbumAction.Kind));
        AddStringDiff(operations, "Output.IncompleteAlbumAction.Path", before.IncompleteAlbumAction.Path, after.IncompleteAlbumAction.Path);
        AddStringListDiff(operations, "Output.OnComplete", before.OnComplete, after.OnComplete);
        AddBoolDiff(operations, "Output.AlbumArtOnly", before.AlbumArtOnly, after.AlbumArtOnly);
        if (before.AlbumArtOption != after.AlbumArtOption) operations.Add(Set("Output.AlbumArtOption", after.AlbumArtOption));
    }

    private static void AddSearchDiffs(List<DownloadSettingOperationDto> operations, SearchSettings before, SearchSettings after)
    {
        AddFileConditionDiffs(operations, "Search.NecessaryCond", before.NecessaryCond, after.NecessaryCond);
        AddFileConditionDiffs(operations, "Search.PreferredCond", before.PreferredCond, after.PreferredCond);
        AddFolderConditionDiffs(operations, "Search.NecessaryFolderCond", before.NecessaryFolderCond, after.NecessaryFolderCond);
        AddFolderConditionDiffs(operations, "Search.PreferredFolderCond", before.PreferredFolderCond, after.PreferredFolderCond);
        AddIntDiff(operations, "Search.SearchTimeout", before.SearchTimeout, after.SearchTimeout);
        AddIntDiff(operations, "Search.MaxStaleTime", before.MaxStaleTime, after.MaxStaleTime);
        AddIntDiff(operations, "Search.DownrankOn", before.DownrankOn, after.DownrankOn);
        AddIntDiff(operations, "Search.IgnoreOn", before.IgnoreOn, after.IgnoreOn);
        AddBoolDiff(operations, "Search.FastSearch", before.FastSearch, after.FastSearch);
        AddIntDiff(operations, "Search.FastSearchDelay", before.FastSearchDelay, after.FastSearchDelay);
        AddDoubleDiff(operations, "Search.FastSearchMinUpSpeed", before.FastSearchMinUpSpeed, after.FastSearchMinUpSpeed);
        AddBoolDiff(operations, "Search.DesperateSearch", before.DesperateSearch, after.DesperateSearch);
        AddBoolDiff(operations, "Search.NoRemoveSpecialChars", before.NoRemoveSpecialChars, after.NoRemoveSpecialChars);
        AddBoolDiff(operations, "Search.RemoveSingleCharSearchTerms", before.RemoveSingleCharSearchTerms, after.RemoveSingleCharSearchTerms);
        AddBoolDiff(operations, "Search.NoBrowseFolder", before.NoBrowseFolder, after.NoBrowseFolder);
        AddBoolDiff(operations, "Search.Relax", before.Relax, after.Relax);
        AddBoolDiff(operations, "Search.StrictAlbumQuality", before.StrictAlbumQuality, after.StrictAlbumQuality);
        AddBoolDiff(operations, "Search.ArtistMaybeWrong", before.ArtistMaybeWrong, after.ArtistMaybeWrong);
        AddBoolDiff(operations, "Search.IsAggregate", before.IsAggregate, after.IsAggregate);
        AddIntDiff(operations, "Search.MinSharesAggregate", before.MinSharesAggregate, after.MinSharesAggregate);
        AddIntDiff(operations, "Search.AggregateLengthTol", before.AggregateLengthTol, after.AggregateLengthTol);
    }

    private static void AddFileConditionDiffs(List<DownloadSettingOperationDto> operations, string prefix, FileConditions before, FileConditions after)
    {
        AddNullableIntDiff(operations, $"{prefix}.LengthTolerance", before.LengthTolerance, after.LengthTolerance);
        AddNullableIntDiff(operations, $"{prefix}.MinBitrate", before.MinBitrate, after.MinBitrate);
        AddNullableIntDiff(operations, $"{prefix}.MaxBitrate", before.MaxBitrate, after.MaxBitrate);
        AddNullableIntDiff(operations, $"{prefix}.MinSampleRate", before.MinSampleRate, after.MinSampleRate);
        AddNullableIntDiff(operations, $"{prefix}.MaxSampleRate", before.MaxSampleRate, after.MaxSampleRate);
        AddNullableIntDiff(operations, $"{prefix}.MinBitDepth", before.MinBitDepth, after.MinBitDepth);
        AddNullableIntDiff(operations, $"{prefix}.MaxBitDepth", before.MaxBitDepth, after.MaxBitDepth);
        AddBoolDiff(operations, $"{prefix}.StrictTitle", before.StrictTitle, after.StrictTitle);
        AddBoolDiff(operations, $"{prefix}.StrictArtist", before.StrictArtist, after.StrictArtist);
        AddBoolDiff(operations, $"{prefix}.StrictAlbum", before.StrictAlbum, after.StrictAlbum);
        AddStringListDiff(operations, $"{prefix}.Formats", before.Formats, after.Formats);
        AddStringListDiff(operations, $"{prefix}.BannedUsers", before.BannedUsers, after.BannedUsers);
        AddStringListDiff(operations, $"{prefix}.AllowedUsers", before.AllowedUsers, after.AllowedUsers);
        AddBoolDiff(operations, $"{prefix}.AcceptNoLength", before.AcceptNoLength, after.AcceptNoLength);
        AddBoolDiff(operations, $"{prefix}.AcceptMissingProps", before.AcceptMissingProps, after.AcceptMissingProps);
    }

    private static void AddFolderConditionDiffs(List<DownloadSettingOperationDto> operations, string prefix, FolderConditions before, FolderConditions after)
    {
        AddNullableIntDiff(operations, $"{prefix}.MinTrackCount", before.MinTrackCount, after.MinTrackCount);
        AddNullableIntDiff(operations, $"{prefix}.MaxTrackCount", before.MaxTrackCount, after.MaxTrackCount);
        AddStringListDiff(operations, $"{prefix}.RequiredTrackTitles", before.RequiredTrackTitles, after.RequiredTrackTitles);
    }

    private static void AddSkipDiffs(List<DownloadSettingOperationDto> operations, SkipSettings before, SkipSettings after)
    {
        AddBoolDiff(operations, "Skip.SkipExisting", before.SkipExisting, after.SkipExisting);
        AddBoolDiff(operations, "Skip.SkipNotFound", before.SkipNotFound, after.SkipNotFound);
        if (before.SkipMode != after.SkipMode) operations.Add(Set("Skip.SkipMode", after.SkipMode));
        AddStringDiff(operations, "Skip.SkipMusicDir", before.SkipMusicDir, after.SkipMusicDir);
        if (before.SkipModeMusicDir != after.SkipModeMusicDir) operations.Add(Set("Skip.SkipModeMusicDir", after.SkipModeMusicDir));
        AddBoolDiff(operations, "Skip.SkipCheckCond", before.SkipCheckCond, after.SkipCheckCond);
        AddBoolDiff(operations, "Skip.SkipCheckPrefCond", before.SkipCheckPrefCond, after.SkipCheckPrefCond);
    }

    private static void AddPreprocessDiffs(List<DownloadSettingOperationDto> operations, PreprocessSettings before, PreprocessSettings after)
    {
        AddBoolDiff(operations, "Preprocess.RemoveFt", before.RemoveFt, after.RemoveFt);
        AddBoolDiff(operations, "Preprocess.RemoveBrackets", before.RemoveBrackets, after.RemoveBrackets);
        AddBoolDiff(operations, "Preprocess.ExtractArtist", before.ExtractArtist, after.ExtractArtist);
        AddStringDiff(operations, "Preprocess.ParseTitleTemplate", before.ParseTitleTemplate, after.ParseTitleTemplate);
        if (!RegexRulesEqual(before.Regex, after.Regex))
            operations.Add(ReplaceRegex("Preprocess.Regex", after.Regex?.Select(ToRegexRuleDto).ToList() ?? []));
    }

    private static void AddExtractionDiffs(List<DownloadSettingOperationDto> operations, ExtractionSettings before, ExtractionSettings after)
    {
        AddStringDiff(operations, "Extraction.Input", before.Input, after.Input);
        if (before.InputType != after.InputType) operations.Add(Set("Extraction.InputType", after.InputType));
        AddIntDiff(operations, "Extraction.MaxTracks", before.MaxTracks, after.MaxTracks);
        AddIntDiff(operations, "Extraction.Offset", before.Offset, after.Offset);
        AddBoolDiff(operations, "Extraction.Reverse", before.Reverse, after.Reverse);
        AddBoolDiff(operations, "Extraction.RemoveTracksFromSource", before.RemoveTracksFromSource, after.RemoveTracksFromSource);
        if (before.RequestedMode != after.RequestedMode) operations.Add(Set("Extraction.RequestedMode", after.RequestedMode));
        AddBoolDiff(operations, "Extraction.UpgradeToAlbum", before.UpgradeToAlbum, after.UpgradeToAlbum);
        AddBoolDiff(operations, "Extraction.SetAlbumMinTrackCount", before.SetAlbumMinTrackCount, after.SetAlbumMinTrackCount);
        AddBoolDiff(operations, "Extraction.SetAlbumMaxTrackCount", before.SetAlbumMaxTrackCount, after.SetAlbumMaxTrackCount);
    }

    private static void AddTransferDiffs(List<DownloadSettingOperationDto> operations, TransferSettings before, TransferSettings after)
    {
        AddIntDiff(operations, "Transfer.MaxDownloadRetries", before.MaxDownloadRetries, after.MaxDownloadRetries);
        AddIntDiff(operations, "Transfer.UnknownErrorRetries", before.UnknownErrorRetries, after.UnknownErrorRetries);
        AddBoolDiff(operations, "Transfer.NoIncompleteExt", before.NoIncompleteExt, after.NoIncompleteExt);
        AddIntDiff(operations, "Transfer.AlbumTrackCountMaxRetries", before.AlbumTrackCountMaxRetries, after.AlbumTrackCountMaxRetries);
    }

    private static void AddSpotifyDiffs(List<DownloadSettingOperationDto> operations, SpotifySettings before, SpotifySettings after)
    {
        AddStringDiff(operations, "Spotify.ClientId", before.ClientId, after.ClientId);
        AddStringDiff(operations, "Spotify.ClientSecret", before.ClientSecret, after.ClientSecret);
        AddStringDiff(operations, "Spotify.Token", before.Token, after.Token);
        AddStringDiff(operations, "Spotify.Refresh", before.Refresh, after.Refresh);
    }

    private static void AddYouTubeDiffs(List<DownloadSettingOperationDto> operations, YouTubeSettings before, YouTubeSettings after)
    {
        AddStringDiff(operations, "YouTube.ApiKey", before.ApiKey, after.ApiKey);
        AddBoolDiff(operations, "YouTube.GetDeleted", before.GetDeleted, after.GetDeleted);
        AddBoolDiff(operations, "YouTube.DeletedOnly", before.DeletedOnly, after.DeletedOnly);
    }

    private static void AddYtDlpDiffs(List<DownloadSettingOperationDto> operations, YtDlpSettings before, YtDlpSettings after)
    {
        AddBoolDiff(operations, "YtDlp.UseYtdlp", before.UseYtdlp, after.UseYtdlp);
        AddStringDiff(operations, "YtDlp.YtdlpArgument", before.YtdlpArgument, after.YtdlpArgument);
    }

    private static void AddCsvDiffs(List<DownloadSettingOperationDto> operations, CsvSettings before, CsvSettings after)
    {
        AddStringDiff(operations, "Csv.ArtistCol", before.ArtistCol, after.ArtistCol);
        AddStringDiff(operations, "Csv.AlbumCol", before.AlbumCol, after.AlbumCol);
        AddStringDiff(operations, "Csv.TitleCol", before.TitleCol, after.TitleCol);
        AddStringDiff(operations, "Csv.YtIdCol", before.YtIdCol, after.YtIdCol);
        AddStringDiff(operations, "Csv.DescCol", before.DescCol, after.DescCol);
        AddStringDiff(operations, "Csv.TrackCountCol", before.TrackCountCol, after.TrackCountCol);
        AddStringDiff(operations, "Csv.LengthCol", before.LengthCol, after.LengthCol);
        AddStringDiff(operations, "Csv.TimeUnit", before.TimeUnit, after.TimeUnit);
        AddBoolDiff(operations, "Csv.YtParse", before.YtParse, after.YtParse);
    }

    private static void AddBandcampDiffs(List<DownloadSettingOperationDto> operations, BandcampSettings before, BandcampSettings after)
        => AddStringDiff(operations, "Bandcamp.HtmlFromFile", before.HtmlFromFile, after.HtmlFromFile);

    private static void AddStringDiff(List<DownloadSettingOperationDto> operations, string path, string? before, string? after)
    {
        if (before != after) operations.Add(Set(path, after));
    }

    private static void AddIntDiff(List<DownloadSettingOperationDto> operations, string path, int before, int after)
    {
        if (before != after) operations.Add(Set(path, after));
    }

    private static void AddNullableIntDiff(List<DownloadSettingOperationDto> operations, string path, int? before, int? after)
    {
        if (before != after) operations.Add(Set(path, after));
    }

    private static void AddDoubleDiff(List<DownloadSettingOperationDto> operations, string path, double before, double after)
    {
        if (Math.Abs(before - after) > double.Epsilon) operations.Add(Set(path, after));
    }

    private static void AddBoolDiff(List<DownloadSettingOperationDto> operations, string path, bool before, bool after)
    {
        if (before != after) operations.Add(Set(path, after));
    }

    private static void AddStringListDiff(List<DownloadSettingOperationDto> operations, string path, IEnumerable<string>? before, IEnumerable<string>? after)
    {
        if (before == null && after == null) return;
        if (before != null && after != null && before.SequenceEqual(after)) return;
        operations.Add(Replace(path, after?.ToList() ?? []));
    }

    private static void ApplyStringList(ref List<string>? target, DownloadSettingOperationDto op)
    {
        if (op.Operation == SettingOperationKind.Append)
        {
            target ??= [];
            target.AddRange(op.StringListValue ?? []);
        }
        else
        {
            target = op.StringListValue?.ToList();
        }
    }

    private static void ValidateOnCompleteOperation(DownloadSettingOperationDto op)
        => OnCompleteExecutor.ValidateCommands(op.StringListValue);

    private static void ApplyStringList(List<string> target, DownloadSettingOperationDto op)
    {
        if (op.Operation == SettingOperationKind.Append)
            target.AddRange(op.StringListValue ?? []);
        else
        {
            target.Clear();
            target.AddRange(op.StringListValue ?? []);
        }
    }

    private static void ApplyRegex(PreprocessSettings settings, DownloadSettingOperationDto op)
    {
        var values = op.RegexListValue?
            .Select(rule => (ToRegexFields(rule.Match), ToRegexFields(rule.Replace)))
            .ToList() ?? [];

        if (op.Operation == SettingOperationKind.Append)
        {
            settings.Regex ??= [];
            settings.Regex.AddRange(values);
        }
        else
        {
            settings.Regex = values;
        }
    }

    private static bool RegexRulesEqual(List<(RegexFields, RegexFields)>? before, List<(RegexFields, RegexFields)>? after)
    {
        if (before == null && after == null) return true;
        if (before == null || after == null || before.Count != after.Count) return false;
        return before.Zip(after).All(pair =>
            RegexFieldsEqual(pair.First.Item1, pair.Second.Item1)
            && RegexFieldsEqual(pair.First.Item2, pair.Second.Item2));
    }

    private static RegexRuleDto ToRegexRuleDto((RegexFields, RegexFields) rule)
        => new(ToDto(rule.Item1), ToDto(rule.Item2));

    private static RegexFieldsDto ToDto(RegexFields fields)
        => new(fields.Title, fields.Artist, fields.Album);

    private static RegexFields ToRegexFields(RegexFieldsDto fields)
        => new() { Title = fields.Title, Artist = fields.Artist, Album = fields.Album };

    private static bool RegexFieldsEqual(RegexFields left, RegexFields right)
        => left.Title == right.Title && left.Artist == right.Artist && left.Album == right.Album;

    public static DownloadSettingOperationDto Set(string path, string? value)
        => new(path, SettingOperationKind.Set, StringValue: value);

    public static DownloadSettingOperationDto Set(string path, int? value)
        => new(path, SettingOperationKind.Set, IntValue: value);

    public static DownloadSettingOperationDto Set(string path, int value)
        => new(path, SettingOperationKind.Set, IntValue: value);

    public static DownloadSettingOperationDto Set(string path, double value)
        => new(path, SettingOperationKind.Set, DoubleValue: value);

    public static DownloadSettingOperationDto Set(string path, bool? value)
        => new(path, SettingOperationKind.Set, BoolValue: value);

    public static DownloadSettingOperationDto Set(string path, bool value)
        => new(path, SettingOperationKind.Set, BoolValue: value);

    public static DownloadSettingOperationDto Set(string path, PrintOption value)
        => new(path, SettingOperationKind.Set, PrintOptionValue: value);

    public static DownloadSettingOperationDto Set(string path, InputType value)
        => new(path, SettingOperationKind.Set, InputTypeValue: value);

    public static DownloadSettingOperationDto Set(string path, ExtractionMode? value)
        => new(path, SettingOperationKind.Set, ExtractionModeValue: value);

    public static DownloadSettingOperationDto Set(string path, SkipMode value)
        => new(path, SettingOperationKind.Set, SkipModeValue: value);

    public static DownloadSettingOperationDto Set(string path, AlbumArtOption value)
        => new(path, SettingOperationKind.Set, AlbumArtOptionValue: value);

    public static DownloadSettingOperationDto Set(string path, IncompleteAlbumActionKind? value)
        => new(path, SettingOperationKind.Set, IncompleteAlbumActionKindValue: value);

    public static DownloadSettingOperationDto Replace(string path, IReadOnlyList<string> values)
        => new(path, SettingOperationKind.Replace, StringListValue: values);

    public static DownloadSettingOperationDto Append(string path, IReadOnlyList<string> values)
        => new(path, SettingOperationKind.Append, StringListValue: values);

    public static DownloadSettingOperationDto ReplaceRegex(string path, IReadOnlyList<RegexRuleDto> values)
        => new(path, SettingOperationKind.Replace, RegexListValue: values);

    public static DownloadSettingOperationDto AppendRegex(string path, IReadOnlyList<RegexRuleDto> values)
        => new(path, SettingOperationKind.Append, RegexListValue: values);

    private static int Int(DownloadSettingOperationDto op)
        => op.IntValue ?? throw new ArgumentException($"Setting operation '{op.Path}' is missing an integer value.");

    private static double Double(DownloadSettingOperationDto op)
        => op.DoubleValue ?? throw new ArgumentException($"Setting operation '{op.Path}' is missing a double value.");

    private static bool Bool(DownloadSettingOperationDto op)
        => op.BoolValue ?? throw new ArgumentException($"Setting operation '{op.Path}' is missing a boolean value.");
}
