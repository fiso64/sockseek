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
    {
        if (invocation.ConfigPath == "none") return "none";
        if (invocation.ConfigPath is { } requested)
        {
            var path = Utils.ExpandVariables(requested);
            var adjacent = Path.Join(AppDomain.CurrentDomain.BaseDirectory, path);
            return File.Exists(adjacent) ? adjacent : path;
        }
        return DefaultConfigPaths().FirstOrDefault(File.Exists) ?? "";
    }

    public static string? ExtractProfileName(IReadOnlyList<string> args) => ParseInvocation(args).ProfileName;

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
        => BindAll(file, ParseInvocation(cliArgs), profileName);

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

        return (engine, dl, cli, daemon, remote);
    }

    public static IJobSettingsResolver CreateJobSettingsResolver(
        ConfigFile file,
        IReadOnlyList<string> cliArgs,
        CliSettings cli,
        string? profileName = null,
        EngineSettings? engine = null)
    {
        return CreateJobSettingsResolver(file, ParseInvocation(cliArgs), cli, profileName, engine);
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
            new DownloadSettings { DefaultMinSharesAggregate = engine?.DefaultMinSharesAggregate ?? SearchSettings.DefaultMinShares },
            catalog.DefaultProfile,
            catalog.AutoProfiles,
            namedProfiles,
            cliProfile,
            context,
            normalize: settings => PostProcessDownload(settings, new PathVariableContext(ConfigDir: file.ConfigDir)));
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

        SockseekLog.Warn("Warning: Client profile settings did not stabilize after repeated auto-profile passes");
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
        dl.DefaultMinSharesAggregate = engine.DefaultMinSharesAggregate;
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
    {
        var builder = new ProfileBuilder("<cli>");
        string? configPath = null;
        string? profileName = null;
        bool noConfig = false;
        var tokens = NormalizeArgs(args);
        for (int i = 0; i < tokens.Count; i++)
        {
            string flag = tokens[i].Value;
            if (!flag.StartsWith('-'))
            {
                BindOption(builder, "--input", flag);
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
            option.Bind(builder.For(option.RootType), value, flag);
        }
        return new ParsedInvocation(builder.Build(), noConfig ? "none" : configPath, profileName);
    }

    internal static ProfileEntry ParseProfileTokens(IReadOnlyList<string> tokens) => ParseInvocation(tokens).Profile;

    private static void BindOption(ProfileBuilder builder, string flag, string value)
    {
        if (flag is "-c" or "--config" or "--nc" or "--no-config" or "--profile") return;
        if (!OptionCatalog.ByName.TryGetValue(flag, out var option)) throw UnknownArgument(flag);
        option.Bind(builder.For(option.RootType), value, flag);
    }

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
    {
        var profiles = new Dictionary<string, ProfileBuilder>();
        var curProfile = "default";
        bool hasAutoProfiles = false;

        foreach (var (line, lineNum) in File.ReadAllLines(path).Select((l, n) => (l.Trim(), n)))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                curProfile = line[1..^1];
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
                throw new Exception($"Input error: Error parsing config '{path}' at line {lineNum}");

            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            string key = parts[0];
            string val = parts[1];
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = val[1..^1];

            if (!profiles.ContainsKey(curProfile))
                profiles[curProfile] = new ProfileBuilder(curProfile);

            if (key == "profile-cond")
            {
                if (curProfile != "default")
                {
                    profiles[curProfile].Condition = val;
                    hasAutoProfiles = true;
                }
            }
            else
            {
                string flag = key.Length == 1 ? $"-{key}" : $"--{key}";
                BindOption(profiles[curProfile], flag, val);
            }
        }

        return new ConfigFile(path, profiles.ToDictionary(pair => pair.Key, pair => pair.Value.Build()), hasAutoProfiles);
    }

}
