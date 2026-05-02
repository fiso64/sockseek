namespace Sldl.Cli;

/// CLI-only settings — control presentation and UI behavior.
/// Not passed to the engine; consumed by the CLI layer before/when constructing the engine.
///
/// The engine interacts with UI through EngineEvents only.
/// These settings tell the CLI which presentation/orchestration layers to wire up.
///
public class CliSettings
{
    /// When true, the CLI uses interactive search/select orchestration for album jobs.
    public bool InteractiveMode { get; set; }

    /// When true, the CLI skips progress event subscribers.
    public bool NoProgress { get; set; }

    /// When true, the CLI uses JsonStreamProgressReporter instead of the default CLI reporter.
    public bool ProgressJson { get; set; }

    /// When true, album downloads show a compact progress (two lines total).
    public bool AlbumCompactProgress { get; set; }
}
