using Sldl.Core.Models;

namespace Sldl.Core.Settings;

/// Controls how files are searched for and ranked.
///
/// FileConditions and FolderConditions are NOT decorated with [Option] — they are
/// populated via special handling in the binder for --cond/--pref and the individual
/// --format/--min-bitrate/etc. flags.
public class SearchSettings
{
    // ── Conditions ────────────────────────────────────────────────────────────

    public FileConditions NecessaryCond { get; set; } = new()
    {
        Formats = ["mp3", "flac", "ogg", "m4a", "opus", "wav", "aac", "alac"],
        LengthTolerance = 3,
    };

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

    public FolderConditions NecessaryFolderCond { get; set; } = new();
    public FolderConditions PreferredFolderCond { get; set; } = new();

    // ── Timing ────────────────────────────────────────────────────────────────

    public int SearchTimeout { get; set; } = 6_000;

    public int MaxStaleTime { get; set; } = 30_000;

    // ── Ranking ───────────────────────────────────────────────────────────────

    /// After this many failures, downrank the candidate. Stored as negative: -N means N failures.
    /// Set by ConfigManager special case for --fails-to-downrank/--ftd (which negates the input).
    public int DownrankOn { get; set; } = -1;

    /// After this many failures, ignore the candidate entirely. Stored as negative.
    /// Set by ConfigManager special case for --fails-to-ignore/--fti (which negates the input).
    public int IgnoreOn { get; set; } = -2;

    // ── Fast search ───────────────────────────────────────────────────────────

    public bool FastSearch { get; set; }

    public int FastSearchDelay { get; set; } = 300;

    public double FastSearchMinUpSpeed { get; set; } = 1.0;

    // ── Search strategy ───────────────────────────────────────────────────────

    public bool DesperateSearch { get; set; }

    public bool NoRemoveSpecialChars { get; set; }

    public bool RemoveSingleCharSearchTerms { get; set; }

    public bool NoBrowseFolder { get; set; }

    public bool Relax { get; set; }

    // ── Artist handling ───────────────────────────────────────────────────────

    /// When true, the artist field of a query is treated as potentially incorrect.
    /// Affects how the Searcher relaxes matching and builds fallback queries.
    public bool ArtistMaybeWrong { get; set; }

    // ── Aggregate search ──────────────────────────────────────────────────────

    /// When true, search all matching sources and download from each.
    /// Applies to both songs and albums.
    public bool IsAggregate { get; set; }

    /// Minimum number of distinct sharers required for a song to be included in aggregate results.
    public int MinSharesAggregate { get; set; } = 2;

    /// Length tolerance (seconds) when grouping candidate songs into aggregate buckets.
    public int AggregateLengthTol { get; set; } = 3;
}
