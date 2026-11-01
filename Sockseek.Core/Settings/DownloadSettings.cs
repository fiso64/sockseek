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
/// Section references are init-only; section values are mutable. Independent
/// composition/execution uses SettingsCloner, while planned input freezes a snapshot.
public class DownloadSettings
{
    public OutputSettings     Output     { get; init; }
    public SearchSettings     Search     { get; init; }
    public SkipSettings       Skip       { get; init; }
    public PreprocessSettings Preprocess { get; init; }
    public ExtractionSettings Extraction { get; init; }
    public TransferSettings   Transfer   { get; init; }
    public SpotifySettings    Spotify    { get; init; }
    public YouTubeSettings    YouTube    { get; init; }
    public YtDlpSettings      YtDlp      { get; init; }
    public CsvSettings        Csv        { get; init; }
    public BandcampSettings   Bandcamp   { get; init; }

    public DownloadSettings()
    {
        Output = new();
        Search = new();
        Skip = new();
        Preprocess = new();
        Extraction = new();
        Transfer = new();
        Spotify = new();
        YouTube = new();
        YtDlp = new();
        Csv = new();
        Bandcamp = new();
        AppliedAutoProfiles = [];
    }

    // Copy only the sections we keep. A default instance plus an object
    // initializer would allocate and discard every default section first.
    internal DownloadSettings(DownloadSettings source)
    {
        Output = SettingsCloner.Clone(source.Output);
        Search = SettingsCloner.Clone(source.Search);
        Skip = SettingsCloner.Clone(source.Skip);
        Preprocess = SettingsCloner.Clone(source.Preprocess);
        Extraction = SettingsCloner.Clone(source.Extraction);
        Transfer = SettingsCloner.Clone(source.Transfer);
        Spotify = SettingsCloner.Clone(source.Spotify);
        YouTube = SettingsCloner.Clone(source.YouTube);
        YtDlp = SettingsCloner.Clone(source.YtDlp);
        Csv = SettingsCloner.Clone(source.Csv);
        Bandcamp = SettingsCloner.Clone(source.Bandcamp);
        PrintOption = source.PrintOption;
        AppliedAutoProfiles = [.. source.AppliedAutoProfiles];
        RuntimePathContext = source.RuntimePathContext;
    }

    // ── Top-level mode flags ──────────────────────────────────────────────────

    /// Controls what the engine prepares for CLI/API printing instead of downloading.
    /// Set by ConfigManager's special-case handler for --print and its shortcuts.
    public PrintOption PrintOption { get; set; } = PrintOption.None;

    /// Tracks which auto-profiles were applied by the job settings resolver.
    public HashSet<string> AppliedAutoProfiles { get; set; }

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
