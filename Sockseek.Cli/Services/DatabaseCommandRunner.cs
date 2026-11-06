using Sockseek.Core;
using Sockseek.Persistence.Sqlite;
using Sockseek.Server;
using Microsoft.Extensions.Logging;
using Sockseek.Core.Diagnostics;

namespace Sockseek.Cli;

internal static class DatabaseCommandRunner
{
    private const string Usage =
        "Usage: sockseek database <migrate|integrity|backup|restore> " +
        "[--data-dir <path>] [--backup <path>] [--config <path>|--no-config]";

    public static async Task<Program.CliExitCode> RunAsync(
        string[] args,
        ConfigFile configFile,
        DaemonSettings daemonSettings,
        ILogger logger,
        CliOutputController? output = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            var commandOptions = ParseOptions(args);
            string databasePath = SockseekDataPaths.ResolveDatabasePath(daemonSettings.DataDirectory);

            Write(output, LogLevel.Information, $"Using Sockseek database {databasePath}.");

            if (commandOptions.Command == "restore")
            {
                string backupPath = ResolveCommandPath(commandOptions.BackupPath!, configFile);
                var restored = await SqliteMaintenanceService.RestoreOfflineAsync(backupPath, databasePath);
                Write(output, LogLevel.Information, $"Restored {restored.DatabasePath} ({restored.SizeBytes} bytes); integrity={restored.Integrity.Result}");
                return Program.CliExitCode.Success;
            }

            if (commandOptions.Command == "migrate")
            {
                var initialized = await PersistenceOfflineOperations.MigrateAsync(databasePath);
                Write(output, LogLevel.Information, $"Migrated {databasePath}; schema={initialized.SchemaVersion}");
                return Program.CliExitCode.Success;
            }

            if (commandOptions.Command == "integrity")
            {
                var integrity = await PersistenceOfflineOperations.CheckIntegrityAsync(databasePath);
                if (integrity.IsHealthy)
                    Write(output, LogLevel.Information, $"Database integrity check passed: {integrity.Result}");
                else
                    Write(output, LogLevel.Error, $"Database integrity check failed: {integrity.Result}");
                return integrity.IsHealthy
                    ? Program.CliExitCode.Success
                    : Program.CliExitCode.WorkFailed;
            }

            if (commandOptions.Command == "backup")
            {
                string backupPath = ResolveCommandPath(commandOptions.BackupPath!, configFile);
                var backup = await PersistenceOfflineOperations.BackupAsync(databasePath, backupPath);
                Write(output, LogLevel.Information, $"Backed up {backup.BackupPath} ({backup.SizeBytes} bytes); integrity={backup.Integrity.Result}");
                return Program.CliExitCode.Success;
            }

            throw new InvalidOperationException("Unreachable database command.");
        }
        catch (ArgumentException ex)
        {
            Write(output, LogLevel.Error, ex.Message);
            return Program.CliExitCode.UsageError;
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
                CliLogMessages.OperationFailed(logger, ex, "database");
            else
                Write(output, LogLevel.Error, $"Database command failed: {ExceptionText.Summary(ex)}");
            return Program.CliExitCode.WorkFailed;
        }
    }

    private static void Write(
        CliOutputController? output,
        LogLevel level,
        string message)
        => CliProcessOutput.Write(
            output,
            level,
            message,
            presentation: CliProcessLogPresentation.Plain);

    private static DatabaseCommandOptions ParseOptions(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            throw new ArgumentException(Usage);

        string command = args[0].ToLowerInvariant();
        if (command is not ("migrate" or "integrity" or "backup" or "restore"))
            throw new ArgumentException($"Unknown database command '{args[0]}'.\n{Usage}");

        string? backupPath = null;

        for (int i = 1; i < args.Count; i++)
        {
            string option = args[i];
            string? attachedValue = null;
            int equals = option.IndexOf('=');
            if (equals > 0)
            {
                attachedValue = option[(equals + 1)..];
                option = option[..equals];
            }

            string Value()
            {
                string value = attachedValue
                    ?? (i + 1 < args.Count ? args[++i] : "");
                if (string.IsNullOrWhiteSpace(value)
                    || (attachedValue == null && value.StartsWith('-')))
                {
                    throw new ArgumentException($"Missing value for {option}.");
                }
                return value;
            }

            switch (option.ToLowerInvariant())
            {
                case "--backup":
                    backupPath = Value();
                    break;
                default:
                    throw new ArgumentException($"Unknown database option '{option}'.\n{Usage}");
            }
        }

        if (command is "backup" or "restore")
        {
            if (string.IsNullOrWhiteSpace(backupPath))
                throw new ArgumentException($"Missing required option --backup for database {command}.");
        }
        else if (backupPath != null)
        {
            throw new ArgumentException($"Option --backup is not valid for database {command}.");
        }

        return new DatabaseCommandOptions(command, backupPath);
    }

    private static string ResolveCommandPath(string path, ConfigFile configFile)
        => Path.GetFullPath(
            Utils.ExpandVariables(path, new PathVariableContext(ConfigDir: configFile.ConfigDir)));

    private sealed record DatabaseCommandOptions(
        string Command,
        string? BackupPath);
}
