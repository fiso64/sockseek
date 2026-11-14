using Sockseek.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sockseek.Cli;

[Flags]
internal enum ConfiguredCommandOptions
{
    None = 0,
    Remote = 1,
    DataDirectory = 2,
    Authentication = 4,
}

/// <summary>
/// Resolves standard configuration before dispatching top-level commands that
/// have their own positional grammar. Command-specific parsers never receive
/// config/profile options and never need to load configuration themselves.
/// </summary>
internal static class ConfiguredCommandDispatcher
{
    public static async Task<Program.CliExitCode?> TryRunAsync(
        string[] args,
        CliOutputController? output = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
            return null;

        string command = args[0].ToLowerInvariant();
        try
        {
            if (command == "auth")
                return await AuthenticationCommandRunner.RunAsync(ConfiguredCommandInvocation.Create(args,
                    ConfiguredCommandOptions.Remote | ConfiguredCommandOptions.DataDirectory
                    | ConfiguredCommandOptions.Authentication), cancellationToken).ConfigureAwait(false);
            if (command is "chat" or "room" or "notifications" or "notification")
            {
                if (HasHelp(args))
                    return await ChatCommandRunner.RunAsync(args, remote: null, cancellationToken)
                        .ConfigureAwait(false);
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.Remote);
                return await ChatCommandRunner.RunAsync(
                    invocation.CommandArguments,
                    invocation.Remote,
                    cancellationToken).ConfigureAwait(false);
            }

            if (command is "share" or "transfers" or "transfer")
            {
                if (HasHelp(args))
                    return await DaemonResourceCommandRunner.RunAsync(args, remote: null, cancellationToken)
                        .ConfigureAwait(false);
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.Remote);
                return await DaemonResourceCommandRunner.RunAsync(
                    invocation.CommandArguments,
                    invocation.Remote,
                    cancellationToken).ConfigureAwait(false);
            }

            if (command == "user")
            {
                if (HasHelp(args))
                    return await UserCommandRunner.RunAsync(
                        args, remote: null, profileNames: null, cancellationToken)
                        .ConfigureAwait(false);
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.Remote);
                return await UserCommandRunner.RunAsync(
                    invocation.CommandArguments,
                    invocation.Remote,
                    invocation.ProfileName,
                    cancellationToken).ConfigureAwait(false);
            }

            if (command == "database")
            {
                if (HasHelp(args))
                    return null;
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.DataDirectory);
                using ILoggerFactory loggerFactory = output is null
                    ? NullLoggerFactory.Instance
                    : output.CreateLoggerFactory(
                        invocation.Engine.LogLevel,
                        invocation.Engine.LogFilePath,
                        LogLevel.Debug);
                return await DatabaseCommandRunner.RunAsync(
                    invocation.CommandArguments.Skip(1).ToArray(),
                    invocation.ConfigFile,
                    invocation.Daemon,
                    loggerFactory.CreateLogger("Sockseek.Cli.DatabaseCommandRunner"),
                    output).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   || ex.Message.StartsWith("Input error:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                ex.Message.StartsWith("Input error:", StringComparison.Ordinal)
                    ? ex.Message
                    : $"Input error: {ex.Message}");
            return Program.CliExitCode.UsageError;
        }
    }

    private static bool HasHelp(IReadOnlyList<string> args)
        => args.Any(argument => argument is "-h" or "--help");
}

internal sealed record ConfiguredCommandInvocation(
    string[] CommandArguments,
    ConfigFile ConfigFile,
    EngineSettings Engine,
    DaemonSettings Daemon,
    RemoteSettings Remote,
    string? ProfileName)
{
    public static ConfiguredCommandInvocation Create(
        IReadOnlyList<string> args,
        ConfiguredCommandOptions options)
    {
        var commandArguments = new List<string>(args.Count);
        var bindingArguments = new List<string>();

        for (int index = 0; index < args.Count; index++)
        {
            string original = args[index];
            SplitOption(original, out string option, out string? attachedValue);
            bool valueless = option is "--nc" or "--no-config";
            bool standard = option is "-c" or "--config" or "--profile"
                || valueless
                || options.HasFlag(ConfiguredCommandOptions.Remote)
                    && option is "--remote" or "--server-url" or "--remote-api-key" or "--remote-api-key-file"
                || options.HasFlag(ConfiguredCommandOptions.DataDirectory)
                    && option == "--data-dir"
                || options.HasFlag(ConfiguredCommandOptions.Authentication)
                    && option == "--web-password-file";
            if (!standard)
            {
                commandArguments.Add(original);
                continue;
            }

            bindingArguments.Add(option);
            if (valueless)
            {
                if (attachedValue is not null)
                    bindingArguments.Add(RequireValue(option, attachedValue));
                else if (index + 1 < args.Count && bool.TryParse(args[index + 1], out _))
                    bindingArguments.Add(args[++index]);
                continue;
            }

            string value = attachedValue
                ?? (index + 1 < args.Count ? args[++index] : "");
            bindingArguments.Add(RequireValue(option, value));
        }

        var invocation = ConfigManager.ParseInvocation(bindingArguments);
        var configFile = ConfigManager.Load(ConfigManager.GetConfigPath(invocation));
        var (engine, _, _, daemon, remote) = ConfigManager.BindAll(configFile, invocation);
        return new ConfiguredCommandInvocation(
            commandArguments.ToArray(), configFile, engine, daemon, remote, invocation.ProfileName);
    }

    private static void SplitOption(
        string argument,
        out string option,
        out string? attachedValue)
    {
        int equals = argument.IndexOf('=');
        if (equals <= 0)
        {
            option = argument.ToLowerInvariant();
            attachedValue = null;
            return;
        }

        option = argument[..equals].ToLowerInvariant();
        attachedValue = argument[(equals + 1)..];
    }

    private static string RequireValue(string option, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            throw new ArgumentException($"Option '{option}' requires a value.");
        return value;
    }
}
