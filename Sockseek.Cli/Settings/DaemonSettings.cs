using System.ComponentModel.DataAnnotations;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

/// Settings consumed by the CLI launcher when hosting `Sockseek daemon`.
public class DaemonSettings
{
    [SettingOption("--web-username", DefaultProfileOnly = true)]
    public string WebUsername { get; set; } = "admin";
    [SettingOption("--web-password-file", DefaultProfileOnly = true)]
    public string? WebPasswordFile { get; set; }
    [SettingOption("--web-password-hash", DefaultProfileOnly = true)]
    public string? WebPasswordHash { get; set; }
    [SettingOption("--web-session-lifetime", Parser = nameof(ParseSessionLifetime), DefaultProfileOnly = true)]
    public TimeSpan WebSessionLifetime { get; set; } = TimeSpan.FromDays(7);
    [SettingOption("--trusted-proxies", DefaultProfileOnly = true)]
    public string[] TrustedProxies { get; set; } = ["127.0.0.1", "::1"];
    [SettingOption("--no-auth", Negate = true, DefaultProfileOnly = true)]
    public bool AuthenticationEnabled { get; set; } = true;
    [SettingOption("--allow-http-auth", DefaultProfileOnly = true)]
    public bool AllowHttpAuthentication { get; set; }

    public Server.Authentication.AuthenticationOptions CreateAuthenticationOptions() => new()
    {
        Enabled = AuthenticationEnabled, Username = WebUsername,
        PasswordFile = WebPasswordFile, PasswordHash = WebPasswordHash,
        SessionLifetime = WebSessionLifetime, TrustedProxies = TrustedProxies,
        AllowHttp = AllowHttpAuthentication,
    };

    private static TimeSpan ParseSessionLifetime(string value, string flag)
    {
        if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var duration)
            && duration > TimeSpan.Zero && duration <= TimeSpan.FromDays(365)) return duration;
        throw new ArgumentException($"{flag} must be a positive TimeSpan no longer than 365 days (default 7.00:00:00).");
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
    public bool RetentionEnabled { get; set; } = true;
    [SettingOption("--successful-job-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? CompletedJobRetention { get; set; }
    [SettingOption("--unsuccessful-job-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? UnsuccessfulJobRetention { get; set; }
    [SettingOption("--search-result-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? SearchResultRetention { get; set; } = TimeSpan.FromDays(30);
    [SettingOption("--transfer-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? TransferRetention { get; set; }
    [SettingOption("--private-message-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? PrivateMessageRetention { get; set; }
    [SettingOption("--room-message-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? RoomMessageRetention { get; set; } = TimeSpan.FromDays(30);
    private static TimeSpan? ParseRetentionDays(string value, string flag)
    {
        if (value.Equals("forever", StringComparison.OrdinalIgnoreCase)) return null;
        double days = (double)SettingParser.Parse(value, typeof(double), flag)!;
        if (days <= 0 || !double.IsFinite(days))
            throw new ArgumentException($"{flag} must be a positive number of days or 'forever'.");
        return TimeSpan.FromDays(days);
    }

}
