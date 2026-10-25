using Sockseek.Core.Settings;

namespace Sockseek.Cli;

/// CLI-only settings — control presentation and UI behavior.
/// Not passed to the engine; consumed by the CLI layer before/when constructing the engine.
///
/// The engine interacts with UI through Core event surfaces such as DownloadEvents and SearchEvents.
/// These settings tell the CLI which presentation/orchestration layers to wire up.
///
public class CliSettings
{
    /// When true, the CLI uses interactive search/select orchestration for album jobs.
    [SettingOption("-t", "--interactive")]
    public bool InteractiveMode { get; set; }

    /// When true, the CLI skips progress event subscribers.
    [SettingOption("--np", "--no-progress", ValueKind = OptionValueKind.None)]
    [SettingOption("--progress", Negate = true, ValueKind = OptionValueKind.None)]
    public bool NoProgress { get; set; }

    /// When true, the CLI uses JsonStreamProgressReporter instead of the default CLI reporter.
    [SettingOption("--progress-json")]
    public bool ProgressJson { get; set; }
}
