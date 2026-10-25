using System.ComponentModel.DataAnnotations;
using Sockseek.Core.Services;
using Sockseek.Core.Models;

namespace Sockseek.Core.Settings;

/// Controls how files are searched for and ranked.
public class SearchSettings
{
    public const int DefaultMinShares = 2;

    // ── Conditions ────────────────────────────────────────────────────────────

    [SettingOption("--af", "--format", Member = "Formats", Parser = nameof(ParseFormats))]
    [SettingOption("--lt", "--tolerance", "--length-tol", "--length-tolerance", Member = "LengthTolerance")]
    [SettingOption("--mbr", "--min-bitrate", Member = "MinBitrate")]
    [SettingOption("--Mbr", "--max-bitrate", Member = "MaxBitrate")]
    [SettingOption("--msr", "--min-samplerate", Member = "MinSampleRate")]
    [SettingOption("--Msr", "--max-samplerate", Member = "MaxSampleRate")]
    [SettingOption("--mbd", "--min-bitdepth", Member = "MinBitDepth")]
    [SettingOption("--Mbd", "--max-bitdepth", Member = "MaxBitDepth")]
    [SettingOption("--stt", "--strict-title", Member = "StrictTitle")]
    [SettingOption("--sar", "--strict-artist", Member = "StrictArtist")]
    [SettingOption("--sal", "--strict-album", Member = "StrictAlbum")]
    [SettingOption("--anl", "--accept-no-length", Member = "AcceptNoLength")]
    [SettingOption("--bu", "--banned-users", Member = "BannedUsers")]
    [SettingOption("--au", "--allowed-users", Member = "AllowedUsers")]
    public FileConditions NecessaryCond { get; set; } = new()
    {
        Formats = [.. SearchDefaults.Formats],
        LengthTolerance = 3,
    };

    [SettingOption("--paf", "--pf", "--pref-format", Member = "Formats", Parser = nameof(ParseFormats))]
    [SettingOption("--plt", "--pref-tolerance", "--pref-length-tol", "--pref-length-tolerance", Member = "LengthTolerance")]
    [SettingOption("--pmbr", "--pref-min-bitrate", Member = "MinBitrate")]
    [SettingOption("--pMbr", "--pref-max-bitrate", Member = "MaxBitrate")]
    [SettingOption("--pmsr", "--pref-min-samplerate", Member = "MinSampleRate")]
    [SettingOption("--pMsr", "--pref-max-samplerate", Member = "MaxSampleRate")]
    [SettingOption("--pmbd", "--pref-min-bitdepth", Member = "MinBitDepth")]
    [SettingOption("--pMbd", "--pref-max-bitdepth", Member = "MaxBitDepth")]
    [SettingOption("--pst", "--pstt", "--pref-strict-title", Member = "StrictTitle")]
    [SettingOption("--psar", "--pref-strict-artist", Member = "StrictArtist")]
    [SettingOption("--psal", "--pref-strict-album", Member = "StrictAlbum")]
    [SettingOption("--panl", "--pref-accept-no-length", Member = "AcceptNoLength")]
    [SettingOption("--pbu", "--pref-banned-users", Member = "BannedUsers")]
    [SettingOption("--pau", "--pref-allowed-users", Member = "AllowedUsers")]
    public FileConditions PreferredCond { get; set; } = new()
    {
        Formats          = ["mp3"],
        LengthTolerance  = 3,
        MinBitrate       = 200,
        MaxBitrate       = 2500,
        MaxSampleRate    = 48000,
        StrictTitle      = true,
        StrictAlbum      = true,
    };

    [SettingOption("--matc", "--min-album-track-count", Member = "MinTrackCount")]
    [SettingOption("--Matc", "--max-album-track-count", Member = "MaxTrackCount")]
    public FolderConditions NecessaryFolderCond { get; set; } = new();
    public FolderConditions PreferredFolderCond { get; set; } = new();

    private static string[] ParseFormats(string value, string flag)
        => value.ToLowerInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(format => format.TrimStart('.')).ToArray();

    [SettingOption("--atc", "--album-track-count")]
    private static void ConfigureAlbumTrackCount(SettingWriter changes, string value)
    {
        if (value == "-1")
        {
            changes.Set("NecessaryFolderCond.MinTrackCount", null);
            changes.Set("NecessaryFolderCond.MaxTrackCount", null);
        }
        else if (value.EndsWith('-'))
            changes.Set("NecessaryFolderCond.MaxTrackCount", SettingParser.Integer(value[..^1], changes.Flag));
        else if (value.EndsWith('+'))
            changes.Set("NecessaryFolderCond.MinTrackCount", SettingParser.Integer(value[..^1], changes.Flag));
        else
        {
            int count = SettingParser.Integer(value, changes.Flag);
            changes.Set("NecessaryFolderCond.MinTrackCount", count);
            changes.Set("NecessaryFolderCond.MaxTrackCount", count);
        }
    }

    [SettingOption("--sc", "--strict", "--strict-conditions")]
    private static void ConfigureStrictConditions(SettingWriter changes, bool value)
    {
        changes.Set("NecessaryCond.AcceptMissingProps", !value);
        changes.Set("PreferredCond.AcceptMissingProps", !value);
    }

    [SettingOption("--cond", "--conditions")]
    private static void ConfigureConditions(SettingWriter changes, string value)
    {
        ConfigureConditions(changes, value, preferred: false);
    }

    [SettingOption("--pc", "--pref", "--preferred-conditions")]
    private static void ConfigurePreferredConditions(SettingWriter changes, string value)
    {
        ConfigureConditions(changes, value, preferred: true);
    }

    private static void ConfigureConditions(SettingWriter changes, string value, bool preferred)
    {
        var folder = new FolderConditionPatch();
        var file = ConditionParser.ParseFileConditions(value, folder);
        changes.Merge(preferred ? nameof(PreferredCond) : nameof(NecessaryCond), file);
        string path = preferred ? nameof(PreferredFolderCond) : nameof(NecessaryFolderCond);
        if (folder.MinTrackCount != null) changes.Set(path + ".MinTrackCount", folder.MinTrackCount);
        if (folder.MaxTrackCount != null) changes.Set(path + ".MaxTrackCount", folder.MaxTrackCount);
        if (folder.RequiredTrackTitles is { Count: > 0 })
            changes.Append(path + ".RequiredTrackTitles", folder.RequiredTrackTitles, distinct: true);
    }

    /// When required album-track conditions are set (format, bitrate, sample
    /// rate, or bit depth), require every visible audio file in the
    /// candidate folder to satisfy them. When false, mixed-quality album folders
    /// are allowed and ranked by quality coverage.
    [SettingOption("--saq", "--strict-album-quality")]
    public bool StrictAlbumQuality { get; set; }

    // ── Timing ────────────────────────────────────────────────────────────────

    [SettingOption("--st", "--search-time", "--search-timeout")]
    public int SearchTimeout { get; set; } = 5_000;

    [SettingOption("--Mst", "--stale-time", "--max-stale-time")]
    public int MaxStaleTime { get; set; } = 30_000;

    // ── Ranking ───────────────────────────────────────────────────────────────

    /// After this many failures, downrank the candidate. Stored as negative: -N means N failures.
    [SettingOption("--ftd", "--fails-to-downrank", Negate = true)]
    public int DownrankOn { get; set; } = -1;

    /// After this many failures, ignore the candidate entirely. Stored as negative.
    [SettingOption("--fti", "--fails-to-ignore", Negate = true)]
    public int IgnoreOn { get; set; } = -2;

    public int EffectiveIgnoreOn => Math.Min(IgnoreOn, DownrankOn);

    // ── Fast search ───────────────────────────────────────────────────────────

    [SettingOption("--fs", "--fast-search")]
    public bool FastSearch { get; set; }

    [SettingOption("--fsd", "--fast-search-delay")]
    public int FastSearchDelay { get; set; } = 300;

    [SettingOption("--fsmus", "--fast-search-min-up-speed")]
    public double FastSearchMinUpSpeed { get; set; } = 1.0;

    // ── Search strategy ───────────────────────────────────────────────────────

    [SettingOption("-d", "--desperate")]
    public bool DesperateSearch { get; set; }

    /// Fallbacks: 0 normal, 1 first artist without credits, 2 other artists,
    /// 3 legacy desperate searches. DesperateSearch also enables level 3.
    [Range(0, 3)]
    public int FallbackLevel { get; set; } = 2;

    [SettingOption("--fallback-level")]
    private static void ConfigureFallbackLevel(SettingWriter changes, string value)
    {
        int level = SettingParser.Integer(value, changes.Flag);
        if (level is < 0 or > 3)
            throw new Exception($"Input error: Option '{changes.Flag}' must be between 0 and 3");
        changes.Set(nameof(FallbackLevel), level);
        changes.Set(nameof(DesperateSearch), false);
    }

    /// Maximum additional searches after deduplication; normal searches do not count.
    [SettingOption("--max-fallback-attempts")]
    [Range(0, int.MaxValue)]
    public int MaxFallbackAttempts { get; set; } = 8;

    [SettingOption("--nrsc", "--no-remove-special-chars")]
    [SettingOption("--rsc", "--remove-special-chars", FixedValue = "false")]
    public bool NoRemoveSpecialChars { get; set; }

    public bool RemoveSingleCharSearchTerms { get; set; }

    [SettingOption("--nbf", "--no-browse-folder", FixedValue = "true")]
    [SettingOption("--bf", "--browse-folder", FixedValue = "false")]
    public bool NoBrowseFolder { get; set; }

    [SettingOption("--rf", "--relax", "--relax-filtering")]
    public bool Relax { get; set; }

    // ── Artist handling ───────────────────────────────────────────────────────

    /// When true, the artist field of a query is treated as potentially incorrect.
    /// Affects how the Searcher relaxes matching and builds fallback queries.
    [SettingOption("--amw", "--artist-maybe-wrong")]
    public bool ArtistMaybeWrong { get; set; }

    // ── Aggregate search ──────────────────────────────────────────────────────

    /// When true, search all matching sources and download from each.
    /// Applies to both songs and albums.
    [SettingOption("-g", "--aggregate")]
    public bool IsAggregate { get; set; }

    /// Minimum distinct sharers for aggregate results. Null uses the backend default.
    [SettingOption("--msa", "--min-shares-aggregate")]
    public int? MinSharesAggregate { get; set; }

    private int defaultMinSharesAggregate = DefaultMinShares;
    public int EffectiveMinSharesAggregate => MinSharesAggregate ?? defaultMinSharesAggregate;

    /// Length tolerance (seconds) when grouping candidate songs into aggregate buckets.
    [SettingOption("--alt", "--aggregate-length-tol")]
    public int AggregateLengthTol { get; set; } = 3;

    internal bool AudioConditionsIgnored { get; private set; }

    /// Resolve execution context without rewriting configured conditions or defaults.
    internal SearchSettings Resolve(bool artworkOnly, int defaultMinShares)
    {
        if (AudioConditionsIgnored == artworkOnly && defaultMinSharesAggregate == defaultMinShares)
            return this;
        var result = (SearchSettings)MemberwiseClone();
        result.defaultMinSharesAggregate = defaultMinShares;
        if (artworkOnly)
        {
            result.NecessaryCond = WithoutAudioConditions(NecessaryCond);
            result.PreferredCond = WithoutAudioConditions(PreferredCond);
            result.NecessaryFolderCond = new();
            result.PreferredFolderCond = new();
            result.StrictAlbumQuality = false;
            result.AudioConditionsIgnored = true;
        }
        return result;
    }

    private static FileConditions WithoutAudioConditions(FileConditions conditions)
    {
        var result = conditions.WithoutAlbumTrackConditions();
        result.LengthTolerance = null;
        result.AcceptNoLength = true;
        return result;
    }
}
