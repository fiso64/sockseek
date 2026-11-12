using System.ComponentModel.DataAnnotations;
using Sockseek.Core.Settings;
using Sockseek.Server;
using Microsoft.Extensions.Logging;

namespace Sockseek.Cli;

/// Settings consumed by the CLI launcher when hosting `Sockseek daemon`.
public class DaemonSettings
{
    // Embedding and CLI startup use the same defaults. Never expose these mutable
    // defaults or share their collections with a bound settings instance.
    private static readonly ServerOptions Defaults = new();

    [SettingOption("--web-theme", DefaultProfileOnly = true), SettingReloadable]
    [Required, RegularExpression("^(dark|light|auto)$")]
    public string WebTheme { get; set; } = Defaults.WebUi.Theme;

    [SettingOption("--web-default-search-mode", DefaultProfileOnly = true), SettingReloadable]
    [Required, RegularExpression("^(generic|track|album|song-groups|album-groups)$")]
    public string WebDefaultSearchMode { get; set; } = Defaults.WebUi.DefaultSearchMode;

    [SettingOption("--no-config-watch", DefaultProfileOnly = true)]
    public bool NoConfigWatch { get; set; }

    [SettingOption("--web-username", DefaultProfileOnly = true)]
    public string WebUsername { get; set; } = Defaults.Authentication.Username;
    [SettingOption("--web-password-file", DefaultProfileOnly = true)]
    public string? WebPasswordFile { get; set; }
    [SettingOption("--web-password-hash", DefaultProfileOnly = true)]
    public string? WebPasswordHash { get; set; }
    [SettingOption("--web-session-lifetime", Parser = nameof(ParseSessionLifetime), DefaultProfileOnly = true)]
    public TimeSpan WebSessionLifetime { get; set; } = Defaults.Authentication.SessionLifetime;
    [SettingOption("--trusted-proxies", DefaultProfileOnly = true)]
    public string[] TrustedProxies { get; set; } = [.. Defaults.Authentication.TrustedProxies];
    [SettingOption("--no-auth", Negate = true, DefaultProfileOnly = true)]
    public bool AuthenticationEnabled { get; set; } = Defaults.Authentication.Enabled;
    [SettingOption("--allow-http-auth", DefaultProfileOnly = true)]
    public bool AllowHttpAuthentication { get; set; } = Defaults.Authentication.AllowHttp;

    private static TimeSpan ParseSessionLifetime(string value, string flag)
    {
        if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var duration)
            && duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(365)) return duration;
        throw new ArgumentException($"{flag} must be a positive TimeSpan no longer than 365 days.");
    }

    /// IP/interface used by `Sockseek daemon` for the HTTP/SignalR API.
    [SettingOption("--server-ip", "--daemon-ip", "--api-ip")]
    public string ListenIp { get; set; } = "127.0.0.1";

    /// Port used by `Sockseek daemon` for the HTTP/SignalR API.
    [SettingOption("--server-port", "--daemon-port", "--api-port")]
    [Range(1, 65535, ErrorMessage = "{0} must be a TCP port between 1 and 65535.")]
    public int ListenPort { get; set; } = 5030;
    [SettingOption("--data-dir")]
    public string? DataDirectory { get; set; }
    [SettingOption("--no-retention", FixedValue = "false")]
    public bool RetentionEnabled { get; set; } = Defaults.Persistence.RetentionEnabled;
    [SettingOption("--successful-job-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? CompletedJobRetention { get; set; } = Defaults.Persistence.CompletedJobHistoryAge;
    [SettingOption("--unsuccessful-job-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? UnsuccessfulJobRetention { get; set; } = Defaults.Persistence.UnsuccessfulJobHistoryAge;
    [SettingOption("--search-result-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? SearchResultRetention { get; set; } = Defaults.Persistence.SearchResultAge;
    [SettingOption("--transfer-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? TransferRetention { get; set; } = Defaults.Persistence.TransferHistoryAge;
    [SettingOption("--private-message-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? PrivateMessageRetention { get; set; } = Defaults.Persistence.PrivateMessageHistoryAge;
    [SettingOption("--room-message-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? RoomMessageRetention { get; set; } = Defaults.Persistence.RoomMessageHistoryAge;
    private static TimeSpan? ParseRetentionDays(string value, string flag)
    {
        if (value.Equals("forever", StringComparison.OrdinalIgnoreCase)) return null;
        double days = (double)SettingParser.Parse(value, typeof(double), flag)!;
        if (days <= 0 || !double.IsFinite(days))
            throw new ArgumentException($"{flag} must be a positive number of days or 'forever'.");
        return TimeSpan.FromDays(days);
    }

    /// <summary>The single translation from bound CLI/config settings to host options.</summary>
    public ServerOptions CreateServerOptions(EngineSettings engine, ConfigFile file, ParsedInvocation invocation)
    {
        var daemonEngine = SettingsCloner.Clone(engine);
        if (daemonEngine.LogLevel == LogLevel.Information)
            daemonEngine.LogLevel = LogLevel.Debug;
        return new ServerOptions
        {
            Authentication = CreateAuthenticationOptions(),
            WebUi = CreateWebUiOptions(),
            Engine = daemonEngine,
            LaunchDownloadSettings = DownloadSettingsPatchDtoMapper.FromCore(invocation.DownloadSettings),
            LaunchProfileNames = invocation.ProfileName?
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [],
            Profiles = ConfigManager.CreateProfileCatalog(file),
            ConfigDir = file.ConfigDir,
            Persistence = new ServerPersistenceOptions
            {
                Enabled = true, DataDirectory = DataDirectory,
                RetentionEnabled = RetentionEnabled,
                CompletedJobHistoryAge = CompletedJobRetention,
                UnsuccessfulJobHistoryAge = UnsuccessfulJobRetention,
                SearchResultAge = SearchResultRetention,
                TransferHistoryAge = TransferRetention,
                PrivateMessageHistoryAge = PrivateMessageRetention,
                RoomMessageHistoryAge = RoomMessageRetention,
            },
        };
    }

    public Server.WebUiOptions CreateWebUiOptions() => new(WebTheme, WebDefaultSearchMode);

    private Server.Authentication.AuthenticationOptions CreateAuthenticationOptions() => new()
    {
        Enabled = AuthenticationEnabled, Username = WebUsername,
        PasswordFile = WebPasswordFile, PasswordHash = WebPasswordHash,
        SessionLifetime = WebSessionLifetime, TrustedProxies = [.. TrustedProxies],
        AllowHttp = AllowHttpAuthentication,
    };
}
