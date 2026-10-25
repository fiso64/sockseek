using Sockseek.Core;

namespace Sockseek.Core.Settings;

/// Engine-lifetime settings — stable for the lifetime of a DownloadEngine instance.
/// Set once at construction; shared across all submissions.
/// Does NOT include display/UI concerns (see CliSettings).
public class EngineSettings
{
    // ── Soulseek connection ───────────────────────────────────────────────────

    [SettingOption("--user", "--username")]
    public string? Username { get; set; }

    [SettingOption("--pass", "--password")]
    public string? Password { get; set; }

    [SettingOption("-l", "--login")]
    private static void ConfigureLogin(SettingWriter changes, string value)
    {
        var parts = value.Split(';', 2);
        changes.Set(nameof(Username), parts[0]);
        changes.Set(nameof(Password), parts.Length > 1 ? parts[1] : "");
    }

    [SettingOption("--rl", "--random-login")]
    public bool UseRandomLogin { get; set; }

    /// null = don't listen (set by --no-listen).
    [SettingOption("--lp", "--port", "--listen-port")]
    [SettingOption("--no-listen", Clear = true)]
    public int? ListenPort { get; set; } = 49998;

    [SettingOption("--cto", "--connect-timeout")]
    public int ConnectTimeout { get; set; } = 20_000;

    /// Daemons can recover from server kicks on their own; foreground CLI runs should fail
    /// clearly so they do not fight another client using the same Soulseek account.
    public bool AutoReconnectAfterKickedFromServer { get; set; }

    // ── Sharing ───────────────────────────────────────────────────────────────

    [SettingOption("--shared-files")]
    public int SharedFiles { get; set; }

    [SettingOption("--shared-folders")]
    public int SharedFolders { get; set; }

    [SettingOption("--user-description")]
    public string? UserDescription { get; set; }

    [SettingOption("--nmsc", "--no-modify-share-count")]
    public bool NoModifyShareCount { get; set; }

    // ── Concurrency ───────────────────────────────────────────────────────────

    /// Global limit for concurrent leaf work. Counts direct song jobs, album jobs,
    /// aggregate child song jobs, album-aggregate child album jobs, and standalone
    /// folder retrievals. Does not count extractor jobs or song jobs inside albums.
    [SettingOption("--cj", "--concurrent-jobs")]
    public int ConcurrentJobs { get; set; } = 20;

    [SettingOption("--cs", "--concurrent-searches")]
    public int ConcurrentSearches { get; set; } = 2;

    [SettingOption("--ce", "--concurrent-extractors")]
    public int ConcurrentExtractors { get; set; } = 4;

    [SettingOption("--spt", "--searches-per-time")]
    public int SearchesPerTime { get; set; } = 34;

    [SettingOption("--srt", "--searches-renew-time")]
    public int SearchRenewTime { get; set; } = 220;

    // ── Logging ───────────────────────────────────────────────────────────────

    /// Verbosity level for the engine's own log output.
    /// The CLI reads this to configure the console logger.
    [SettingOption("-v", "--verbose", "--debug", FixedValue = "Debug")]
    [SettingOption("-vv", "--trace", FixedValue = "Trace")]
    public Microsoft.Extensions.Logging.LogLevel LogLevel { get; set; } = LogLevel.Information;

    public bool ReportIntervalProgress { get; set; } = true;

    [SettingOption("--lf", "--log-file")]
    public string? LogFilePath { get; set; }

    // ── Testing / mock ────────────────────────────────────────────────────────

    [SettingOption("--mock-files-dir")]
    public string? MockFilesDir { get; set; }

    /// true = read audio tags from mock files (default). --mock-files-no-read-tags sets to false.
    [SettingOption("--mock-files-no-read-tags", FixedValue = "false")]
    public bool MockFilesReadTags { get; set; } = true;

    [SettingOption("--mock-files-slow")]
    public bool MockFilesSlow { get; set; }

    [SettingOption("--mock-files-fail-downloads")]
    public int MockFilesFailDownloads { get; set; }
}
