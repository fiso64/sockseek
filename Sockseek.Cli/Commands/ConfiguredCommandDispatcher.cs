using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Core.UserProfiles;
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
            {
                return await AuthenticationCommands.RunAsync(ConfiguredCommandInvocation.Create(args,
                    ConfiguredCommandOptions.Remote | ConfiguredCommandOptions.DataDirectory
                    | ConfiguredCommandOptions.Authentication), cancellationToken).ConfigureAwait(false);
            }
            if (command is "chat" or "room" or "notifications" or "notification")
                return await RunRemoteCommandAsync(args, ChatCommands.RunAsync, cancellationToken).ConfigureAwait(false);
            if (command is "share" or "transfers" or "transfer" or "job")
                return await RunRemoteCommandAsync(args, SharingAndTransferCommands.RunAsync, cancellationToken).ConfigureAwait(false);
            if (command == "user")
                return await RunRemoteCommandAsync(args, UserCommands.RunAsync, cancellationToken).ConfigureAwait(false);

            if (command == "database")
            {
                ConfiguredCommandInvocation invocation = ConfiguredCommandInvocation.Create(
                    args, ConfiguredCommandOptions.DataDirectory);
                using ILoggerFactory loggerFactory = output is null
                    ? NullLoggerFactory.Instance
                    : output.CreateLoggerFactory(
                        invocation.Engine.LogLevel,
                        invocation.Engine.LogFilePath,
                        LogLevel.Debug);
                return await DatabaseCommands.RunAsync(
                    invocation.CommandArguments.Skip(1).ToArray(),
                    invocation.ConfigFile,
                    invocation.Daemon,
                    loggerFactory.CreateLogger("Sockseek.Cli.DatabaseCommands"),
                    output).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   || ex.Message.StartsWith("Input error:", StringComparison.Ordinal))
        {
            return Usage(ex.Message);
        }
    }

    private static async Task<Program.CliExitCode> RunRemoteCommandAsync(
        string[] args,
        Func<SockseekApiClient, ConfiguredCommandInvocation, CancellationToken, Task<Program.CliExitCode>> run,
        CancellationToken cancellationToken)
    {
        var invocation = ConfiguredCommandInvocation.Create(args, ConfiguredCommandOptions.Remote);
        if (!invocation.Remote.IsEnabled)
            return Usage("This command requires a configured remote URL (remote = <url> or --remote <url>).");

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cancellation.Cancel();
        }
        Console.CancelKeyPress += OnCancel;
        try
        {
            using var http = invocation.Remote.CreateHttpClient();
            return await run(new SockseekApiClient(http), invocation, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return Program.CliExitCode.Cancelled;
        }
        catch (SockseekApiRequestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Program.CliExitCode.WorkFailed;
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Daemon command failed: {UserProfileText.NormalizeDescription(ex.Message).Replace('\n', ' ')}");
            return Program.CliExitCode.WorkFailed;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    internal static Program.CliExitCode Usage(string message)
    {
        Console.Error.WriteLine(message.StartsWith("Input error:", StringComparison.Ordinal)
            ? message : $"Input error: {message}");
        return Program.CliExitCode.UsageError;
    }
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

/// <summary>
/// The one reading of flags, options and JSON output shared by the commands
/// that have their own positional grammar.
/// </summary>
internal static class CommandArguments
{
    public static bool Flag(IReadOnlyList<string> args, string name)
        => args.Any(argument => string.Equals(OptionName(argument), name, StringComparison.OrdinalIgnoreCase));

    public static bool Json(IReadOnlyList<string> args) => Flag(args, "--json");

    /// <summary>The value of <c>--name value</c> or <c>--name=value</c>.</summary>
    public static string? Option(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string token = args[index];
            if (!string.Equals(OptionName(token), name, StringComparison.OrdinalIgnoreCase))
                continue;
            int equals = token.IndexOf('=');
            if (equals >= 0) return token[(equals + 1)..];
            return index + 1 < args.Count ? args[index + 1] : null;
        }
        return null;
    }

    public static string OptionName(string token)
    {
        int equals = token.IndexOf('=');
        return (equals < 0 ? token : token[..equals]).ToLowerInvariant();
    }

    public static void Write(object value, bool json)
    {
        if (json) WriteJson(value);
        else Console.WriteLine(value);
    }

    public static void WriteJson(object value)
    {
        System.Text.Json.JsonSerializerOptions options = SockseekApiJson.CreateSerializerOptions();
        options.WriteIndented = true;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(value, value.GetType(), options));
    }
}
