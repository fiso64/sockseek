using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Server;

namespace Sockseek.Cli;

/// <summary>
/// Reuses startup binding (including CLI precedence), validates a whole candidate,
/// then updates the existing runtime owners and future-work profiles. Other
/// engine/host settings stay fixed until restart. No file values or parser errors containing secrets go into logs.
/// </summary>
internal sealed partial class DaemonConfigWatcher : BackgroundService
{
    private readonly string path;
    private readonly ParsedInvocation invocation;
    private readonly bool disabled;
    private readonly EngineSupervisor supervisor;
    private readonly ILogger logger;
    private readonly EngineSettings startupEngine;
    private readonly DaemonSettings startupDaemon;
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest,
    });
    private string? appliedText;
    private string? rejectedText;
    private bool watching;
    private bool restartRequired;

    internal DaemonConfigWatcher(ConfigFile initial, ParsedInvocation invocation, bool disabled,
        EngineSupervisor supervisor, ILogger<DaemonConfigWatcher> logger)
    {
        path = string.IsNullOrEmpty(initial.Path) || initial.Path == "none" ? "" : Path.GetFullPath(initial.Path);
        this.invocation = invocation;
        this.disabled = disabled;
        this.supervisor = supervisor;
        this.logger = logger;
        (startupEngine, _, _, startupDaemon, _) = ConfigManager.BindAll(initial, invocation);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        supervisor.StateStore.UpdateConfigurationStatus(new(false, false, false));
        if (disabled || path.Length == 0) return;
        try
        {
            // Watch the directory, not the inode: editors commonly save by rename.
            using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Deleted += Changed;
            watcher.Renamed += (_, _) => changes.Writer.TryWrite(true);
            watcher.Error += (_, error) => changes.Writer.TryComplete(error.GetException());
            watcher.EnableRaisingEvents = true;
            watching = true;
            supervisor.StateStore.UpdateConfigurationStatus(new(true, false, false));
            // Close the startup-read/watch-registration gap too.
            changes.Writer.TryWrite(true);
            while (await changes.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Coalesce truncate/write and rename bursts. Only one reload runs
                // at a time; edits arriving during binding are processed next.
                do
                {
                    while (changes.Reader.TryRead(out _)) { }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
                } while (changes.Reader.TryPeek(out _));
                await ReloadAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            watching = false;
            ReportError(exception);
            WatchFailed(logger, exception.GetType().Name);
        }
    }

    private void Changed(object sender, FileSystemEventArgs args) => changes.Writer.TryWrite(true);

    internal async Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
    {
        bool applying = false;
        try
        {
            // Missing/unreadable files must not silently reset defaults. Empty
            // files are valid, intentional removal of all configured overrides.
            string text = File.ReadAllText(path);
            if (text == appliedText)
            {
                rejectedText = null;
                supervisor.StateStore.UpdateConfigurationStatus(new(watching, restartRequired, false));
                return true;
            }
            if (text == rejectedText) return false;
            rejectedText = text;
            ConfigFile candidate = ConfigManager.ParseConfigText(path, text);
            var (engine, _, _, daemon, _) = ConfigManager.BindAll(candidate, invocation);
            var profiles = ConfigManager.CreateProfileCatalog(candidate);
            profiles.ResolveNamedProfiles(invocation.ProfileName?.Split(','));
            foreach (string name in candidate.Profiles.Keys)
                ConfigManager.BindAll(candidate, invocation, name);
            // Compare only effective settings: overridden file values do not
            // require restart. Existing schema metadata avoids a second option list.
            SettingsSchema schema = SettingsSchema.For(typeof(EngineSettings));
            SettingsSchema daemonSchema = SettingsSchema.For(typeof(DaemonSettings));
            var restartChanges = (SettingsPatch.Difference(startupEngine, engine)?.Operations ?? [])
                .Where(change => !schema.GetMember(change.Path).Reloadable)
                .Select(change => "Engine." + change.Path)
                .Concat((SettingsPatch.Difference(startupDaemon, daemon)?.Operations ?? [])
                    .Where(change => !daemonSchema.GetMember(change.Path).Reloadable)
                    .Select(change => "Daemon." + change.Path)).ToArray();

            applying = true;
            await supervisor.Runtime.ReloadConfigurationAsync(engine, daemon.CreateWebUiOptions(), cancellationToken).ConfigureAwait(false);
            supervisor.Submissions.ReloadProfiles(profiles);
            restartRequired = restartChanges.Length != 0;
            appliedText = text;
            rejectedText = null;
            supervisor.StateStore.UpdateConfigurationStatus(new(watching, restartRequired, false));
            Reloaded(logger, profiles.NamedProfiles.Count, restartRequired);
            if (restartRequired) RestartNeeded(logger, string.Join(", ", restartChanges));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Config parser exception messages can embed a supplied password/token.
            // Report the category, never the raw exception or candidate values.
            if (applying) appliedText = null;
            if (applying || exception is IOException or UnauthorizedAccessException) rejectedText = null;
            ReportError(exception);
            ReloadRejected(logger, exception.GetType().Name, exception.Data["ConfigLine"] is int line ? $" at line {line}" : "");
            return false;
        }
    }

    private void ReportError(Exception exception)
    {
        // Details go only to the authenticated operator state, never to logs.
        string error = exception.Message;
        const string prefix = "Input error: ";
        if (error.StartsWith(prefix, StringComparison.Ordinal)) error = error[prefix.Length..];
        supervisor.StateStore.UpdateConfigurationStatus(new(watching, restartRequired, true,
            error, exception.Data["ConfigLine"] as int?));
    }

    [LoggerMessage(5010, LogLevel.Information, "Configuration reloaded: {ProfileCount} profiles; restart required: {RestartRequired}")]
    private static partial void Reloaded(ILogger logger, int profileCount, bool restartRequired);
    [LoggerMessage(5011, LogLevel.Warning, "Configuration reload failed ({ErrorType}){Location}; check the configuration file")]
    private static partial void ReloadRejected(ILogger logger, string errorType, string location);
    [LoggerMessage(5012, LogLevel.Information, "Configuration changes require daemon restart: {Settings}")]
    private static partial void RestartNeeded(ILogger logger, string settings);
    [LoggerMessage(5013, LogLevel.Warning, "Configuration watching stopped ({ErrorType}); restart the daemon after correcting the configuration path")]
    private static partial void WatchFailed(ILogger logger, string errorType);
}
