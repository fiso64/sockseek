using System.ComponentModel.DataAnnotations;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

/// Settings consumed by the CLI launcher when hosting `Sockseek daemon`.
public class DaemonSettings
{
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
    public TimeSpan? CompletedJobRetention { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan? UnsuccessfulJobRetention { get; set; } = TimeSpan.FromDays(180);
    [SettingOption("--search-result-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? SearchResultRetention { get; set; } = TimeSpan.FromDays(30);
    [SettingOption("--transfer-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? TransferRetention { get; set; } = TimeSpan.FromDays(90);
    [SettingOption("--private-message-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? PrivateMessageRetention { get; set; }
    [SettingOption("--room-message-retention-days", Parser = nameof(ParseRetentionDays))]
    public TimeSpan? RoomMessageRetention { get; set; } = TimeSpan.FromDays(30);
    public int? MaximumRetainedJobs { get; set; } = 100_000;
    [SettingOption("--successful-job-retention-days")]
    private static void ConfigureCompletedRetention(SettingWriter changes, string value)
        => ConfigureJobRetention(changes, nameof(CompletedJobRetention), value);

    [SettingOption("--unsuccessful-job-retention-days")]
    private static void ConfigureUnsuccessfulRetention(SettingWriter changes, string value)
        => ConfigureJobRetention(changes, nameof(UnsuccessfulJobRetention), value);

    private static void ConfigureJobRetention(SettingWriter changes, string member, string value)
    {
        TimeSpan? duration = ParseRetentionDays(value, changes.Flag);
        changes.Set(member, duration);
        if (duration == null) changes.Set(nameof(MaximumRetainedJobs), null);
    }

    private static TimeSpan? ParseRetentionDays(string value, string flag)
    {
        if (value.Equals("forever", StringComparison.OrdinalIgnoreCase)) return null;
        double days = (double)SettingParser.Parse(value, typeof(double), flag)!;
        if (days <= 0 || !double.IsFinite(days))
            throw new ArgumentException($"{flag} must be a positive number of days or 'forever'.");
        return TimeSpan.FromDays(days);
    }

}
