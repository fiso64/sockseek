using Sldl.Core;

namespace Sldl.Core.Settings;

/// Engine-lifetime settings — stable for the lifetime of a DownloadEngine instance.
/// Set once at construction; shared across all submissions.
/// Does NOT include display/UI concerns (see CliSettings).
public class EngineSettings
{
    // ── Soulseek connection ───────────────────────────────────────────────────

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool UseRandomLogin { get; set; }

    /// null = don't listen (set by --no-listen).
    public int? ListenPort { get; set; } = 49998;

    public int ConnectTimeout { get; set; } = 20_000;

    // ── Sharing ───────────────────────────────────────────────────────────────

    public int SharedFiles { get; set; }

    public int SharedFolders { get; set; }

    public string? UserDescription { get; set; }

    public bool NoModifyShareCount { get; set; }

    // ── Concurrency ───────────────────────────────────────────────────────────

    public int ConcurrentSearches { get; set; } = 2;

    public int ConcurrentExtractors { get; set; } = 4;

    public int SearchesPerTime { get; set; } = 34;

    public int SearchRenewTime { get; set; } = 220;

    // ── Logging ───────────────────────────────────────────────────────────────

    /// Verbosity level for the engine's own log output.
    /// The CLI reads this to configure the console logger.
    /// --verbose / -v / --debug are special cases handled by ConfigManager that set this to Debug.
    public Logger.LogLevel LogLevel { get; set; } = Logger.LogLevel.Info;

    public string? LogFilePath { get; set; }

    // ── Testing / mock ────────────────────────────────────────────────────────

    public string? MockFilesDir { get; set; }

    /// true = read audio tags from mock files (default). --mock-files-no-read-tags sets to false.
    public bool MockFilesReadTags { get; set; } = true;

    public bool MockFilesSlow { get; set; }
}
