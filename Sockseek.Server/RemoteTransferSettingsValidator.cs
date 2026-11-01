using Sockseek.Api;
using Sockseek.Core.Services;

namespace Sockseek.Server;

/// <summary>
/// Validates settings explicitly attached to an ordinary remote transfer. Global
/// music defaults may coexist with these jobs, but callers must not submit
/// music-only overrides that would otherwise be silently ignored.
/// </summary>
public static class RemoteTransferSettingsValidator
{
    public static void ValidateExplicitPatch(
        DownloadSettingsPatchDto patch,
        bool allowSearchViewAlbumFolderOption = false)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ValidateExplicitNameFormat(patch);

        var invalid = new List<string>();
        if (patch.Search is { } search
            && (!allowSearchViewAlbumFolderOption
                || search.NoBrowseFolder is null
                || search with { NoBrowseFolder = null } != new SearchSettingsPatchDto()))
            invalid.Add("Search");
        if (patch.Skip is { } skip)
        {
            // Exact remote jobs support only path-based skip-existing. The
            // remaining options depend on music indexes, search, or tags.
            if (skip.SkipNotFound != null) invalid.Add("Skip.SkipNotFound");
            if (skip.SkipMode != null) invalid.Add("Skip.SkipMode");
            if (skip.SkipMusicDir != null) invalid.Add("Skip.SkipMusicDir");
            if (skip.SkipModeMusicDir != null) invalid.Add("Skip.SkipModeMusicDir");
            if (skip.SkipCheckCond != null) invalid.Add("Skip.SkipCheckCond");
            if (skip.SkipCheckPrefCond != null) invalid.Add("Skip.SkipCheckPrefCond");
        }
        if (patch.Preprocess != null) invalid.Add("Preprocess");
        if (patch.Spotify != null) invalid.Add("Spotify");
        if (patch.YouTube != null) invalid.Add("YouTube");
        if (patch.YtDlp != null) invalid.Add("YtDlp");
        if (patch.Csv != null) invalid.Add("Csv");
        if (patch.Bandcamp != null) invalid.Add("Bandcamp");
        if (patch.Extraction is { } extraction
            && (extraction.Input != null
                || extraction.InputType != null
                || extraction.MaxTracks != null
                || extraction.Offset != null
                || extraction.Reverse != null
                || extraction.RemoveTracksFromSource != null
                || extraction.RequestedMode is ServerExtractionMode.Song or ServerExtractionMode.Album
                || extraction.UpgradeToAlbum != null
                || extraction.SetAlbumMinTrackCount != null
                || extraction.SetAlbumMaxTrackCount != null))
        {
            invalid.Add("Extraction");
        }

        if (patch.Transfer is { MaxDownloadRetries: not null }) invalid.Add("Transfer.MaxDownloadRetries");
        if (patch.Transfer is { AlbumTrackCountMaxRetries: not null }) invalid.Add("Transfer.AlbumTrackCountMaxRetries");
        if (patch.Output is { } output)
        {
            if (output.WritePlaylist != null) invalid.Add("Output.WritePlaylist");
            if (output.WriteIndex != null) invalid.Add("Output.WriteIndex");
            if (output.HasConfiguredIndex != null) invalid.Add("Output.HasConfiguredIndex");
            if (output.M3uFilePath != null) invalid.Add("Output.M3uFilePath");
            if (output.IndexFilePath != null) invalid.Add("Output.IndexFilePath");
            if (output.IncompleteAlbumAction != null) invalid.Add("Output.IncompleteAlbumAction");
            if (output.AlbumArtOnly != null) invalid.Add("Output.AlbumArtOnly");
            if (output.AlbumArtOption != null) invalid.Add("Output.AlbumArtOption");
        }

        if (invalid.Count > 0)
        {
            throw new ArgumentException(
                $"The following settings do not apply to an ordinary remote transfer: {string.Join(", ", invalid.Distinct())}.",
                nameof(patch));
        }
    }

    public static void ValidateExplicitNameFormat(DownloadSettingsPatchDto? patch)
    {
        if (patch?.Output?.NameFormat is not { } format)
            return;

        NameFormatRenderer.ValidateVariables(format, NameFormatVariableProvider.Supported);
    }
}
