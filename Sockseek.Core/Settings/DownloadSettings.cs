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
/// Immutable (init-only). Profile re-evaluation in ConfigManager reconstructs from scratch
/// rather than mutating — no Copy() needed.
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
    /// Set by ConfigManager's special-case handler for --print and its shortcuts.
    public PrintOption PrintOption { get; set; } = PrintOption.None;

    /// Tracks which auto-profiles were applied by the job settings resolver.
    public HashSet<string> AppliedAutoProfiles { get; set; } = [];

    /// Runtime path expansion context. This is not a user-facing setting.
    [JsonIgnore]
    public PathVariableContext RuntimePathContext { get; set; } = PathVariableContext.Empty;

    // ── Computed properties ───────────────────────────────────────────────────

    public bool DoNotDownload  => PrintOption != PrintOption.None;
    public bool PrintJobs      => (PrintOption & PrintOption.Jobs)    != 0;
    public bool PrintResults   => (PrintOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
    public bool PrintFull      => (PrintOption & PrintOption.Full)    != 0;
    public bool NonVerbosePrint => (PrintOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;

    public bool NeedLogin      => !PrintJobs && (PrintOption & PrintOption.Index) == 0;

    public ResolvedIncompleteAlbumAction ResolveIncompleteAlbumAction()
    {
        var kind = Output.IncompleteAlbumAction.Kind ?? IncompleteAlbumActionKind.Move;
        var path = kind == IncompleteAlbumActionKind.Move
            ? Output.IncompleteAlbumAction.Path ?? Path.Join(Output.ParentDir ?? Directory.GetCurrentDirectory(), "failed")
            : null;

        return new ResolvedIncompleteAlbumAction(kind, path);
    }

    public bool HasOnComplete => Output.OnComplete?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;

    // ── Validation ────────────────────────────────────────────────────────────

    /// Returns any validation errors. Called by ConfigManager after binding.
    public IEnumerable<string> Validate()
    {
        if (Extraction.Input == null && (PrintOption & PrintOption.Index) == 0)
            yield return "No input provided";

        // IgnoreOn should be at most as lenient as DownrankOn (enforced at bind time).
        // ConfigManager.Bind() applies: IgnoreOn = Math.Min(IgnoreOn, DownrankOn).
        // Validate only reports if they somehow end up inconsistent post-bind.
        if (Search.IgnoreOn > Search.DownrankOn)
            yield return $"ignore-on ({Search.IgnoreOn}) is less strict than downrank-on ({Search.DownrankOn})";
    }
}
