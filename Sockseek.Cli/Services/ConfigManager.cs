using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Extractors;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;

namespace Sockseek.Cli;

/// Owns config file loading and CLI token binding. Core owns typed profile application.
public static partial class ConfigManager
{
    // Test hosts can replace implicit discovery without changing the user's home,
    // environment, or explicitly requested config files. Null uses normal discovery.
    internal static IReadOnlyList<string>? DefaultConfigSearchPathsOverride { get; set; }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Discovers and parses the config file.
    /// Pass explicitPath = "none" to skip loading entirely.
    public static ConfigFile Load(string? explicitPath = null)
    {
        string path = ResolveConfigPath(explicitPath);
        if (path == "none" || !File.Exists(path))
            return new ConfigFile(path, new Dictionary<string, ProfileEntry>());
        return ParseConfigFile(path);
    }

    public static string ExtractConfigPath(IReadOnlyList<string> args) => GetConfigPath(ParseInvocation(args));

    public static string GetConfigPath(ParsedInvocation invocation)
        => GetConfigPath(invocation.ConfigPath);

    private static string GetConfigPath(string? configuredPath)
    {
        if (configuredPath == "none") return "none";
        if (configuredPath is { } requested)
        {
            var path = Utils.ExpandVariables(requested);
            var adjacent = Path.Join(AppDomain.CurrentDomain.BaseDirectory, path);
            return File.Exists(adjacent) ? adjacent : path;
        }
        return DefaultConfigPaths().FirstOrDefault(File.Exists) ?? "";
    }

    /// Creates fresh settings, applies the config file's [default] profile,
    /// any named profile, and finally cliArgs — in that order.
    /// Returns the top-level settings objects.
    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli)
        Bind(ConfigFile file, IReadOnlyList<string> cliArgs, string? profileName = null)
    {
        var (engine, download, cli, _, _) = BindAll(file, cliArgs, profileName);
        return (engine, download, cli);
    }

    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli, DaemonSettings Daemon, RemoteSettings Remote)
        BindAll(ConfigFile file, IReadOnlyList<string> cliArgs, string? profileName = null)
        => BindAll(file, ParseInvocation(cliArgs, file.Path), profileName);

    public static (EngineSettings Engine, DownloadSettings Download, CliSettings Cli, DaemonSettings Daemon, RemoteSettings Remote)
        BindAll(ConfigFile file, ParsedInvocation invocation, string? profileName = null)
    {
        var engine = new EngineSettings();
        var dl     = new DownloadSettings();
        var cli    = new CliSettings();
        var daemon = new DaemonSettings();
        var remote = new RemoteSettings();
        profileName ??= invocation.ProfileName;

        if (file.Profiles.TryGetValue("default", out var def))
            ApplyProfile(def, engine, dl, cli, daemon, remote);

        foreach (var prof in GetNamedProfiles(file, profileName))
            ApplyProfile(prof, engine, dl, cli, daemon, remote);

        ApplyProfile(invocation.Profile, engine, dl, cli, daemon, remote);

        PostProcess(engine, dl, file.ConfigDir);
        if (!string.IsNullOrWhiteSpace(daemon.DataDirectory))
            daemon.DataDirectory = Utils.GetFullPath(Utils.ExpandVariables(
                daemon.DataDirectory, new PathVariableContext(ConfigDir: file.ConfigDir)));

        if (daemon.WebPasswordFile is not null) daemon.WebPasswordFile = ResolveSecretPath(daemon.WebPasswordFile);
        if (remote.ApiKeyFile is not null) remote.ApiKeyFile = ResolveSecretPath(remote.ApiKeyFile);
        string ResolveSecretPath(string path) => Utils.GetFullPath(Utils.ExpandVariables(
            path, new PathVariableContext(ConfigDir: file.ConfigDir)));

        return (engine, dl, cli, daemon, remote);
    }

    public static IJobSettingsResolver CreateJobSettingsResolver(
        ConfigFile file,
        IReadOnlyList<string> cliArgs,
        CliSettings cli,
        string? profileName = null,
        EngineSettings? engine = null)
    {
        return CreateJobSettingsResolver(file, ParseInvocation(cliArgs, file.Path), cli, profileName, engine);
    }

    public static IJobSettingsResolver CreateJobSettingsResolver(
        ConfigFile file, ParsedInvocation invocation, CliSettings cli, string? profileName = null, EngineSettings? engine = null)
    {
        profileName ??= invocation.ProfileName;
        var context = CreateProfileContext(cli);

        var catalog = CreateProfileCatalog(file);
        var namedProfiles = catalog.ResolveNamedProfiles(SplitProfileNames(profileName));

        var cliProfile = invocation.Profile.Profile;

        return new ProfileJobSettingsResolver(
            null,
            catalog.DefaultProfile,
            catalog.AutoProfiles,
            namedProfiles,
            cliProfile,
            context,
            normalize: settings =>
            {
                settings.DefaultMinGroupShares = engine?.DefaultMinGroupShares ?? SearchSettings.DefaultMinShares;
                PostProcessDownload(settings, new PathVariableContext(ConfigDir: file.ConfigDir));
            });
    }

    public static DownloadSettings BindCliDownloadTokens(IReadOnlyList<string> cliArgs)
    {
        var settings = new DownloadSettings();
        ParseInvocation(cliArgs).Profile.Profile.Download.ApplyTo(settings);
        SettingsNormalizer.NormalizeDownloadPaths(settings);
        return settings;
    }

    public static SettingsPatch? CreateCliDownloadSettingsPatch(IReadOnlyList<string> cliArgs)
        => ParseInvocation(cliArgs).DownloadSettings;

    public static ProfileCatalog CreateProfileCatalog(ConfigFile file)
    {
        var defaultProfile = file.Profiles.TryGetValue("default", out var def)
            ? def.Profile
            : null;

        var autoProfiles = file.Profiles
            .Where(x => x.Key != "default" && x.Value.Condition != null)
            .Select(x => x.Value.Profile)
            .ToList();

        var namedProfiles = file.Profiles
            .Where(x => x.Key != "default")
            .Select(x => x.Value.Profile)
            .ToList();

        return new ProfileCatalog
        {
            DefaultProfile = defaultProfile,
            AutoProfiles = autoProfiles,
            NamedProfiles = namedProfiles,
        };
    }

    public static void ApplyAutoProfileCliSettings(ConfigFile file, DownloadSettings root, CliSettings cli, Job? job = null)
    {
        // Client settings can themselves affect profile context, e.g. one profile
        // enables interactive mode and another condition depends on interactive.
        // Resolve to a small fixed point so later matching sees those client-side values.
        const int maxPasses = 8;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            var before = (cli.InteractiveMode, cli.NoProgress, cli.ProgressJson);
            var context = CreateProfileContext(cli);

            foreach (var profile in file.Profiles
                         .Where(x => x.Key != "default" && x.Value.Condition != null)
                         .Select(x => x.Value)
                         .Where(p => p.Condition != null && ProfileConditionEvaluator.Satisfied(p.Condition, root, job, context)))
            {
                profile.Cli.ApplyTo(cli);
            }

            var after = (cli.InteractiveMode, cli.NoProgress, cli.ProgressJson);
            if (after.Equals(before))
                return;
        }

        Console.Error.WriteLine("Warning: Client profile settings did not stabilize after repeated auto-profile passes");
    }

    public static IReadOnlyList<string> GetProfileNames(ConfigFile file)
        => file.Profiles.Keys.Where(k => k != "default").OrderBy(k => k).ToList();

    private static string[] SplitProfileNames(string? profileName)
        => string.IsNullOrWhiteSpace(profileName)
            ? []
            : profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static ProfileContext CreateProfileContext(CliSettings cli)
    {
        var context = new ProfileContext();
        context.Values["interactive"] = cli.InteractiveMode;
        context.Values["progress-json"] = cli.ProgressJson;
        context.Values["no-progress"] = cli.NoProgress;
        return context;
    }

    private static IEnumerable<ProfileEntry> GetNamedProfiles(ConfigFile file, string? profileName)
    {
        if (string.IsNullOrEmpty(profileName))
            yield break;

        foreach (var name in profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (name == "default") continue;
            if (name.Equals("help", StringComparison.OrdinalIgnoreCase) || name.Equals("list", StringComparison.OrdinalIgnoreCase)) continue;

            if (file.Profiles.TryGetValue(name, out var prof))
                yield return prof;
            // We intentionally don't warn/throw here during initial bind. The JobSettingsResolver
            // will validate the profile name and fail the job/request if it's missing.
        }
    }

    private static void ApplyProfile(ProfileEntry profile, EngineSettings engine, DownloadSettings dl, CliSettings cli, DaemonSettings daemon, RemoteSettings remote)
    {
        profile.Profile.Engine.ApplyTo(engine);
        profile.Profile.Download.ApplyTo(dl);
        profile.Cli.ApplyTo(cli);
        profile.Daemon.ApplyTo(daemon);
        profile.Remote.ApplyTo(remote);
    }

    private static void PostProcess(EngineSettings engine, DownloadSettings dl, string? configDir)
    {
        var pathContext = new PathVariableContext(ConfigDir: configDir);
        dl.DefaultMinGroupShares = engine.DefaultMinGroupShares;
        PostProcessDownload(dl, pathContext);
        SettingsNormalizer.NormalizeEnginePaths(engine, pathContext);
    }

    private static void PostProcessDownload(DownloadSettings dl, PathVariableContext pathContext)
    {
        OnCompleteExecutor.ValidateCommands(dl.Output.OnComplete);
        SettingsNormalizer.NormalizeDownloadPaths(dl, pathContext);
    }

    private static string ResolveConfigPath(string? explicit_)
    {
        if (!string.IsNullOrEmpty(explicit_)) return explicit_;
        foreach (var p in DefaultConfigPaths())
            if (File.Exists(p)) return p;
        return "";
    }

    private static IEnumerable<string> DefaultConfigPaths()
        => DefaultConfigSearchPathsOverride is { } paths ? paths : DiscoverDefaultConfigPaths();

    private static IEnumerable<string> DiscoverDefaultConfigPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sockseek", "sockseek.conf");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sockseek", "sockseek.conf");
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
            yield return Path.Combine(xdg, "sockseek", "sockseek.conf");
        yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sockseek.conf");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static IEnumerable<SettingOption> ConfigurationOptions => OptionCatalog.ByName.Values.Distinct();

    // Kept in a nested type so config discovery/help does not initialize option metadata.
    private static class OptionCatalog
    {
        public static readonly IReadOnlyDictionary<string, SettingOption> ByName = Create();
        private static Dictionary<string, SettingOption> Create()
        {
            var result = new Dictionary<string, SettingOption>(StringComparer.Ordinal);
            Type[] roots = [typeof(EngineSettings), typeof(DownloadSettings), typeof(CliSettings), typeof(DaemonSettings), typeof(RemoteSettings)];
            foreach (var type in roots)
            foreach (var option in SettingsSchema.For(type).Options)
            foreach (var name in option.Names)
                result.Add(name, option);
            return result;
        }
    }

    public static ParsedInvocation ParseInvocation(IReadOnlyList<string> args)
        => ParseInvocation(args, null);

    private static ParsedInvocation ParseInvocation(IReadOnlyList<string> args, string? loadedConfigPath)
    {
        var builder = new ProfileBuilder("<cli>");
        string? configPath = null;
        string? profileName = null;
        bool noConfig = false;
        Func<string?> configDirectory = () => ConfigFile.DirectoryOf(loadedConfigPath ?? GetConfigPath(noConfig ? "none" : configPath));
        var tokens = NormalizeArgs(args);
        for (int i = 0; i < tokens.Count; i++)
        {
            string flag = tokens[i].Value;
            if (!flag.StartsWith('-'))
            {
                var input = OptionCatalog.ByName["--input"];
                input.Bind(builder.For(input.RootType), flag, "--input");
                continue;
            }
            switch (flag)
            {
                case "-c": case "--config": configPath = Next(tokens, ref i, flag); continue;
                case "--profile": profileName = Next(tokens, ref i, flag); continue;
                case "--nc": case "--no-config":
                    noConfig = true;
                    if (i + 1 < tokens.Count && (tokens[i + 1].IsAttachedValue || IsBoolLiteral(tokens[i + 1].Value)))
                        noConfig = (bool)SettingParser.Parse(tokens[++i].Value, typeof(bool), flag)!;
                    continue;
            }
            if (!OptionCatalog.ByName.TryGetValue(flag, out var option))
                throw UnknownArgument(flag);
            string value = "true";
            if (option.ValueKind == OptionValueKind.Required)
                value = Next(tokens, ref i, flag);
            else if (option.ValueKind == OptionValueKind.Boolean && i + 1 < tokens.Count
                     && (tokens[i + 1].IsAttachedValue || IsBoolLiteral(tokens[i + 1].Value)))
                value = tokens[++i].Value;
            option.Bind(builder.For(option.RootType), value, flag, configDirectory);
        }
        return new ParsedInvocation(builder.Build(), noConfig ? "none" : configPath, profileName);
    }

    internal static ProfileEntry ParseProfileTokens(IReadOnlyList<string> tokens) => ParseInvocation(tokens).Profile;

    private static Exception UnknownArgument(string flag) => new Exception($"Input error: Unknown argument: {flag}");

    private readonly record struct NormalizedArg(
        string Value,
        bool AllowsLeadingHyphen = false,
        bool IsAttachedValue = false);

    /// Normalize argv: expand --arg=val into --arg val, and -abc into -a -b -c.
    /// Attached values retain their origin so they may intentionally begin with '-'.
    private static List<NormalizedArg> NormalizeArgs(IReadOnlyList<string> args)
    {
        var result = new List<NormalizedArg>(args.Count);
        foreach (var arg in args)
        {
            if (arg.Length > 2 && arg[0] == '-')
            {
                if (arg[1] == '-')
                {
                    if (arg.Contains('='))
                    {
                        var eq = arg.IndexOf('=');
                        result.Add(new(arg[..eq]));
                        result.Add(new(
                            arg[(eq + 1)..],
                            AllowsLeadingHyphen: true,
                            IsAttachedValue: true));
                        continue;
                    }
                }
                else if (!arg.Contains(' '))
                {
                    foreach (char c in arg[1..])
                        result.Add(new($"-{c}"));
                    continue;
                }
            }
            result.Add(new(arg));
        }
        return result;
    }

    private static bool IsBoolLiteral(string value) =>
        value is "true" or "false" or "True" or "False";

    private static string Next(IList<NormalizedArg> tokens, ref int i, string flag)
    {
        if (i + 1 >= tokens.Count)
            throw MissingOptionParameter(flag);

        var next = tokens[++i];
        if (!next.AllowsLeadingHyphen && LooksLikeOption(next.Value))
            throw MissingOptionParameter(flag, next.Value);

        return next.Value;
    }

    private static bool LooksLikeOption(string value)
        => value.Length > 1
           && value[0] == '-'
           && !value.Any(char.IsWhiteSpace);

    private static Exception MissingOptionParameter(string flag, string? optionLikeValue = null)
    {
        string message = $"Input error: Option '{flag}' requires a parameter";
        if (optionLikeValue != null && flag.StartsWith("--", StringComparison.Ordinal))
        {
            message += $", but '{optionLikeValue}' looks like another option. " +
                       $"To use it as the value, pass '{flag}={optionLikeValue}'.";
        }

        return new Exception(message);
    }

    private static ConfigFile ParseConfigFile(string path)
        => ParseConfigText(path, File.ReadAllText(path));

    private readonly record struct ConfigLine(int Number, int Indent, string Text)
    {
        public bool IsItem => Text[0] == '-' && (Text.Length == 1 || char.IsWhiteSpace(Text[1]));
    }

    private static IEnumerable<ConfigLine> ConfigLines(string text)
    {
        using var reader = new StringReader(text);
        int number = 0;
        while (reader.ReadLine() is { } raw)
        {
            number++;
            string line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#') continue;
            yield return new(number, raw.Length - line.Length, line.TrimEnd());
        }
    }

    internal static ConfigFile ParseConfigText(string path, string text)
    {
        var profiles = new Dictionary<string, ProfileBuilder>();
        Func<string?> configDirectory = () => ConfigFile.DirectoryOf(path);
        var curProfile = "default";
        bool hasAutoProfiles = false;
        using var lines = ConfigLines(text).GetEnumerator();
        bool more = lines.MoveNext();
        while (more)
        {
            var header = lines.Current;
            more = lines.MoveNext();
            string line = header.Text;
            int lineNum = header.Number;
            Exception SyntaxError(string message) => new FormatException($"Input error: Error parsing config '{path}' at line {lineNum}: {message}");
            IEnumerable<string> ListItems(bool append)
            {
                bool HasItem() => more && lines.Current.Indent > header.Indent && lines.Current.IsItem;
                if (append && !HasItem()) throw SyntaxError("expected an indented list item.");
                while (HasItem())
                {
                    var item = lines.Current;
                    lineNum = item.Number;
                    if (item.Text.Length == 1) throw SyntaxError("expected a value after '-'.");
                    yield return item.Text[1..].TrimStart();
                    more = lines.MoveNext();
                }
                lineNum = header.Number;
            }

            try
            {
                if (header.IsItem) throw SyntaxError("list items must be indented under a list setting.");
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    curProfile = line[1..^1];
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) throw SyntaxError("expected a setting and value separated by '='.");
                string key = line[..eq].TrimEnd();
                string value = line[(eq + 1)..].TrimStart();

                if (!profiles.TryGetValue(curProfile, out var profile))
                    profiles[curProfile] = profile = new ProfileBuilder(curProfile);

                if (key == "profile-cond")
                {
                    if (value.Length == 0) throw SyntaxError("profile-cond requires a condition.");
                    if (curProfile != "default")
                    {
                        string condition = Unquote(value);
                        ProfileConditionEvaluator.Validate(condition);
                        profile.Condition = condition;
                        hasAutoProfiles = true;
                    }
                    continue;
                }

                string flag = key.Length == 1 ? $"-{key}" : $"--{key}";
                if (flag is "-c" or "--config" or "--nc" or "--no-config" or "--profile")
                {
                    if (value.Length == 0) throw SyntaxError($"'{key}' requires a value.");
                    continue;
                }
                if (!OptionCatalog.ByName.TryGetValue(flag, out var option)) throw UnknownArgument(flag);
                if (option.DefaultProfileOnly && curProfile != "default")
                    throw new ArgumentException($"Input error: Daemon setting '{flag}' is not allowed in named or automatic profile '{curProfile}'.");

                var changes = profile.For(option.RootType);
                if (value.Length == 0 || (option.IsList && value == "+"))
                {
                    bool append = value == "+";
                    option.BindList(changes, ListItems(append), flag, append);
                }
                else option.Bind(changes, Unquote(value), flag, configDirectory);
            }
            catch (Exception exception)
            {
                // Preserve the exception and its location for reload diagnostics.
                exception.Data["ConfigLine"] = lineNum;
                throw;
            }
        }

        return new ConfigFile(path, profiles.ToDictionary(pair => pair.Key, pair => pair.Value.Build()), hasAutoProfiles);

        static string Unquote(string value)
            => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }
}
