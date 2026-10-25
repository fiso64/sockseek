using System.Text.Json.Serialization;
using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Per-submission settings. Passed to engine.EnqueueAsync(job, settings).
/// Composed of domain-oriented sub-objects so services receive only what they need:
///
///   Preprocessor.PreprocessSong(song, job.Config.Preprocess)
///   FileManager.GetDownloadPath(candidate, job.Config.Output)
///   ResultSorter.Sort(candidates, job.Config.Search)
///   TrackSkipper.Check(song, job.Config.Skip)
///
/// Each job receives its own copy before profiles and extractor conditions are applied.
public class DownloadSettings
{
    public OutputSettings     Output     { get; init; } = new();
    public SearchSettings     Search     { get; init; } = new();
    public SkipSettings       Skip       { get; init; } = new();
    public PreprocessSettings Preprocess { get; init; } = new();
    public ExtractionSettings Extraction { get; init; } = new();
    public TransferSettings   Transfer   { get; init; } = new();
    public SpotifySettings    Spotify    { get; init; } = new();
    public YouTubeSettings    YouTube    { get; init; } = new();
    public YtDlpSettings      YtDlp      { get; init; } = new();
    public CsvSettings        Csv        { get; init; } = new();
    public BandcampSettings   Bandcamp   { get; init; } = new();
    public SoundCloudSettings SoundCloud { get; init; } = new();

    // ── Top-level mode flags ──────────────────────────────────────────────────

    /// Controls what the engine prepares for CLI/API printing instead of downloading.
    [SettingOption("--print", Parser = nameof(ParsePrintOption))]
    [SettingOption("--print-jobs", FixedValue = "Jobs")]
    [SettingOption("--print-jobs-full", FixedValue = "Jobs, Full")]
    [SettingOption("--pt", "--print-tracks", FixedValue = "Jobs")]
    [SettingOption("--ptf", "--print-tracks-full", FixedValue = "Jobs, Full")]
    [SettingOption("--pr", "--print-results", FixedValue = "Results")]
    [SettingOption("--prf", "--print-results-full", FixedValue = "Results, Full")]
    [SettingOption("--pl", "--print-link", FixedValue = "Link")]
    [SettingOption("--pj", "--print-json", FixedValue = "Json")]
    [SettingOption("--pjf", "--print-json-full", FixedValue = "Json, Full")]
    public PrintOption PrintOption { get; set; } = PrintOption.None;

    private static PrintOption ParsePrintOption(string s, string flag) => s.ToLower().Trim() switch
    {
        "none"          => PrintOption.None,
        "jobs"          => PrintOption.Jobs,
        "tracks"        => PrintOption.Jobs,
        "results"       => PrintOption.Results,
        "jobs-full"     => PrintOption.Jobs | PrintOption.Full,
        "tracks-full"   => PrintOption.Jobs | PrintOption.Full,
        "results-full"  => PrintOption.Results | PrintOption.Full,
        "link"          => PrintOption.Link,
        "json"          => PrintOption.Json,
        "json-all"      => PrintOption.Json | PrintOption.Full,
        "index"         => PrintOption.Index,
        "index-failed"  => PrintOption.Index | PrintOption.IndexFailed,
        _ => throw new Exception($"Input error: Invalid print option '{s}' for '{flag}'"),
    };

    /// Tracks which auto-profiles were applied by the job settings resolver.
    [JsonIgnore]
    public HashSet<string> AppliedAutoProfiles { get; set; } = [];

    /// Runtime path expansion context. This is not a user-facing setting.
    [JsonIgnore]
    public PathVariableContext RuntimePathContext { get; set; } = PathVariableContext.Empty;

    /// Automatic aggregate threshold supplied by the host's backend.
    [JsonIgnore]
    public int DefaultMinSharesAggregate { get; set; } = SearchSettings.DefaultMinShares;

    // ── Computed properties ───────────────────────────────────────────────────

    public bool DoNotDownload  => PrintOption != PrintOption.None;
    public bool PrintJobs      => (PrintOption & PrintOption.Jobs)    != 0;
    public bool PrintResults   => (PrintOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
    public bool PrintFull      => (PrintOption & PrintOption.Full)    != 0;
    public bool NonVerbosePrint => (PrintOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;

    public bool NeedLogin      => !PrintJobs && (PrintOption & PrintOption.Index) == 0;

    /// Resolve mode-specific search behavior without rewriting configured conditions.
    public SearchSettings ResolveSearchSettings(SearchSettings? search = null)
        => (search ?? Search).Resolve(Output.AlbumArtOnly, DefaultMinSharesAggregate);

    public ResolvedIncompleteAlbumAction ResolveIncompleteAlbumAction()
    {
        var kind = Output.IncompleteAlbumAction.Kind ?? IncompleteAlbumActionKind.Move;
        var path = kind == IncompleteAlbumActionKind.Move
            ? Output.IncompleteAlbumAction.Path ?? Path.Join(Output.ParentDir ?? Directory.GetCurrentDirectory(), "failed")
            : null;

        return new ResolvedIncompleteAlbumAction(kind, path);
    }

    public bool HasOnComplete => Output.OnComplete?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;
}
