using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Jobs;
using Sockseek.Core.Extractors;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Api;
using Sockseek.Server;
using System.Collections.Concurrent;

namespace Sockseek.Cli;

/// Owns config file loading and CLI token binding. Core owns typed profile application.
public static partial class ConfigManager
{
    // TODO [ARCHITECTURE]: Replace this parser soup with a real option-definition model.
    // CLI aliases, value kind, valueless behavior, help text, config binding, and remote
    // patch binding should come from one declarative source instead of the current switch
    // plus probing bridge.
    
    private static readonly ConcurrentDictionary<string, bool> BoolOptionCache = new(StringComparer.Ordinal);

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

    /// Pre-scan argv for --config/-c / --no-config and return the path to use.
    /// This mirrors Config.SetConfigPath so that Load() can be called before full parsing.
    public static string ExtractConfigPath(IReadOnlyList<string> args)
    {
        int noConf = FindLastFlag(args, "--nc", "--no-config");
        if (noConf != -1 && !IsExplicitFalse(args, noConf))
            return "none";

        int conf = FindLastFlag(args, "-c", "--config");
        if (conf != -1 && conf + 1 < args.Count)
        {
            string p = Utils.ExpandVariables(args[conf + 1]);
            string adjacent = Path.Join(AppDomain.CurrentDomain.BaseDirectory, p);
            return File.Exists(adjacent) ? adjacent : p;
        }

        foreach (var candidate in DefaultConfigPaths())
            if (File.Exists(candidate))
                return candidate;

        return "";
    }

    public static string? ExtractProfileName(IReadOnlyList<string> args)
    {
        int idx = FindLastFlag(args, "--profile");
        return idx != -1 && idx + 1 < args.Count ? args[idx + 1] : null;
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
    {
        var engine = new EngineSettings();
        var dl     = new DownloadSettings();
        var cli    = new CliSettings();
        var daemon = new DaemonSettings();
        var remote = new RemoteSettings();
        profileName ??= ExtractProfileName(cliArgs);

        if (file.Profiles.TryGetValue("default", out var def))
            ApplyProfile(def, engine, dl, cli, daemon, remote);

        foreach (var prof in GetNamedProfiles(file, profileName))
            ApplyProfile(prof, engine, dl, cli, daemon, remote);

        ApplyTokens(NormalizeArgs(cliArgs), engine, dl, cli, daemon, remote);

        PostProcess(engine, dl, file.ConfigDir);

        return (engine, dl, cli, daemon, remote);
    }

    public static IJobSettingsResolver CreateJobSettingsResolver(
        ConfigFile file,
        IReadOnlyList<string> cliArgs,
        CliSettings cli,
        string? profileName = null)
    {
        profileName ??= ExtractProfileName(cliArgs);
        var context = CreateProfileContext(cli);

        var catalog = CreateProfileCatalog(file);
        var namedProfiles = catalog.ResolveNamedProfiles(SplitProfileNames(profileName));

        var cliProfile = ParseTokensAsProfile("<cli>", NormalizeArgs(cliArgs)).Profile;

        return new ProfileJobSettingsResolver(
            new DownloadSettings(),
            catalog.DefaultProfile,
            catalog.AutoProfiles,
            namedProfiles,
            cliProfile,
            context,
            normalize: settings => PostProcessDownload(settings, new PathVariableContext(ConfigDir: file.ConfigDir)),
            warn: msg => SockseekLog.Warn(msg));
    }

    public static DownloadSettings BindCliDownloadTokens(IReadOnlyList<string> cliArgs)
    {
        var engine = new EngineSettings();
        var download = new DownloadSettings();
        var cli = new CliSettings();
        var daemon = new DaemonSettings();
        var remote = new RemoteSettings();
        ApplyTokens(NormalizeArgs(cliArgs), engine, download, cli, daemon, remote);
        SettingsNormalizer.NormalizeDownloadPaths(download);
        return download;
    }

    public static DownloadSettingsPatchDto? CreateCliDownloadSettingsPatch(IReadOnlyList<string> cliArgs)
    {
        var builder = new DownloadSettingsDeltaBuilder();
        ParseTokensAsProfile("<remote-cli>", NormalizeArgs(cliArgs), builder);
        return builder.Build();
    }

    public static ProfileCatalog CreateProfileCatalog(ConfigFile file)
    {
        var defaultProfile = file.Profiles.TryGetValue("default", out var def)
            ? ToSettingsProfile(def)
            : null;

        var autoProfiles = file.Profiles
            .Where(x => x.Key != "default" && x.Value.Condition != null)
            .Select(x => ToSettingsProfile(x.Value))
            .ToList();

        var namedProfiles = file.Profiles
            .Where(x => x.Key != "default")
            .Select(x => ToSettingsProfile(x.Value))
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
                         .Select(x => ToProfileEntry(x.Value))
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
        var effective = ToProfileEntry(profile);
        SettingsPatchApplier.Apply(effective.Profile, engine, dl);
        effective.Cli.ApplyTo(cli);
        effective.Daemon.ApplyTo(daemon);
        effective.Remote.ApplyTo(remote);
    }

    private static SettingsProfile ToSettingsProfile(ProfileEntry profile)
        => ToProfileEntry(profile).Profile;

    private static ProfileEntry ToProfileEntry(ProfileEntry profile)
    {
        if (profile.Tokens.Count == 0)
            return profile;

        var parsed = ParseTokensAsProfile(profile.Profile.Name, profile.Tokens);
        return parsed with { Profile = parsed.Profile with { Condition = profile.Condition } };
    }

    // ── Token application ─────────────────────────────────────────────────────

    /// Maps one token list to a typed profile, then applies that profile.
    private static void ApplyTokens(
        IList<string> tokens,
        EngineSettings engine,
        DownloadSettings dl,
        CliSettings cli,
        DaemonSettings daemon,
        RemoteSettings remote)
    {
        ApplyProfile(ParseTokensAsProfile("<tokens>", tokens), engine, dl, cli, daemon, remote);
    }

    private static ProfileEntry ParseTokensAsProfile(
        string name,
        IList<string> tokens,
        DownloadSettingsDeltaBuilder? downloadDeltaBuilder = null)
    {
        var entry = new ProfileEntry(
            new SettingsProfile { Name = name },
            new CliSettingsPatch(),
            new DaemonSettingsPatch(),
            new RemoteSettingsPatch(),
            []);

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];

            if (!t.StartsWith('-'))
            {
                AddProfileOption(entry, "--input", t, downloadDeltaBuilder);
                continue;
            }

            switch (t)
            {
                case "-c": case "--config": case "--profile":
                    i++;
                    break;
                case "--nc": case "--no-config":
                    if (i + 1 < tokens.Count && IsBoolLiteral(tokens[i + 1]))
                        i++;
                    break;
                default:
                    if (IsValuelessOption(t))
                    {
                        AddProfileOption(entry, t, "true", downloadDeltaBuilder);
                    }
                    else if (OptionUsesBoolValue(t))
                    {
                        string value = "true";
                        if (i + 1 < tokens.Count && IsBoolLiteral(tokens[i + 1]))
                            value = tokens[++i];
                        AddProfileOption(entry, t, value, downloadDeltaBuilder);
                    }
                    else
                    {
                        AddProfileOption(entry, t, Next(tokens, ref i, t), downloadDeltaBuilder);
                    }
                    break;
            }
        }

        return entry;
    }

    // ── Post-processing ───────────────────────────────────────────────────────

    private static void PostProcess(EngineSettings engine, DownloadSettings dl, string? configDir)
    {
        var pathContext = new PathVariableContext(ConfigDir: configDir);
        PostProcessDownload(dl, pathContext);
        SettingsNormalizer.NormalizeEnginePaths(engine, pathContext);
    }

    private static void PostProcessDownload(DownloadSettings dl, PathVariableContext pathContext)
    {
        OnCompleteExecutor.ValidateCommands(dl.Output.OnComplete);
        SettingsNormalizer.NormalizeDownloadPaths(dl, pathContext);
    }

    private static (bool Append, string Command) ParseOnCompleteConfigValue(string value)
    {
        var trimmed = value.TrimStart();
        var append = trimmed.StartsWith("+ ", StringComparison.Ordinal);
        var command = append ? trimmed[2..] : value.Trim();
        OnCompleteExecutor.ValidateCommand(command);
        return (append, command);
    }

    // ── Config file parsing ───────────────────────────────────────────────────

    private static ConfigFile ParseConfigFile(string path)
    {
        var profiles = new Dictionary<string, ProfileEntry>();
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
                profiles[curProfile] = new ProfileEntry(
                    new SettingsProfile { Name = curProfile },
                    new CliSettingsPatch(),
                    new DaemonSettingsPatch(),
                    new RemoteSettingsPatch(),
                    []);

            if (key == "profile-cond")
            {
                if (curProfile != "default")
                {
                    profiles[curProfile] = profiles[curProfile] with
                    {
                        Profile = profiles[curProfile].Profile with { Condition = val }
                    };
                    hasAutoProfiles = true;
                }
            }
            else
            {
                string flag = key.Length == 1 ? $"-{key}" : $"--{key}";
                AddProfileOption(profiles[curProfile], flag, val);
            }
        }

        return new ConfigFile(path, profiles, hasAutoProfiles);
    }

    private static void AddProfileOption(
        ProfileEntry entry,
        string flag,
        string value,
        DownloadSettingsDeltaBuilder? downloadDeltaBuilder = null,
        OptionProbe? probe = null)
    {
        var tr = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;

        void Engine(Action<EngineSettings> action)
        {
            if (probe != null)
                action(new EngineSettings());
            else
                entry.Profile.Engine.Add(action);
        }
        void Download(Action<DownloadSettings> action)
        {
            if (probe != null)
            {
                action(new DownloadSettings());
            }
            else
            {
                entry.Profile.Download.Add(action);
                downloadDeltaBuilder?.Record(flag, value, action);
            }
        }
        void Cli(Action<CliSettings> action)
        {
            if (probe != null)
                action(new CliSettings());
            else
                entry.Cli.Add(action);
        }
        void Daemon(Action<DaemonSettings> action)
        {
            if (probe != null)
                action(new DaemonSettings());
            else
                entry.Daemon.Add(action);
        }
        void Remote(Action<RemoteSettings> action)
        {
            if (probe != null)
                action(new RemoteSettings());
            else
                entry.Remote.Add(action);
        }

        bool Bool()
        {
            if (probe != null)
            {
                probe.UsesBoolValue = true;
                return true;
            }
            return ParseBool(value, flag);
        }
        int Int()
        {
            if (probe != null)
                return 1;
            return ParseInt(value, flag);
        }
        double Double()
        {
            if (probe != null)
                return 1.0;
            return ParseDouble(value, flag);
        }

        switch (flag)
        {
            // ── Meta ─────────────────────────────────────────────────────────
            case "-c": case "--config":
            case "--nc": case "--no-config":
            case "--profile":
                break;

            // ── EngineSettings ───────────────────────────────────────────────
            case "--user": case "--username":
                Engine(e => e.Username = value); break;
            case "--pass": case "--password":
                Engine(e => e.Password = value); break;
            case "-l": case "--login":
                Engine(e =>
                {
                    var parts = value.Split(';', 2);
                    e.Username = parts[0];
                    e.Password = parts.Length > 1 ? parts[1] : "";
                });
                break;
            case "--rl": case "--random-login": // For testing only
                Engine(e => e.UseRandomLogin = Bool()); break;
            case "--lp": case "--port": case "--listen-port":
                Engine(e => e.ListenPort = Int()); break;
            case "--no-listen":
                Engine(e => e.ListenPort = null); break;
            case "--cj": case "--concurrent-jobs":
                Engine(e => e.ConcurrentJobs = Int()); break;
            case "--cs": case "--concurrent-searches":
                Engine(e => e.ConcurrentSearches = Int()); break;
            case "--ce": case "--concurrent-extractors":
                Engine(e => e.ConcurrentExtractors = Int()); break;
            case "--spt": case "--searches-per-time":
                Engine(e => e.SearchesPerTime = Int()); break;
            case "--srt": case "--searches-renew-time":
                Engine(e => e.SearchRenewTime = Int()); break;
            case "--nmsc": case "--no-modify-share-count":
                Engine(e => e.NoModifyShareCount = Bool()); break;
            case "-v": case "--verbose": case "--debug":
                Engine(e => e.LogLevel = LogLevel.Debug); break;
            case "-vv": case "--trace":
                Engine(e => e.LogLevel = LogLevel.Trace); break;
            case "--lf": case "--log-file":
                Engine(e => e.LogFilePath = value); break;
            case "--cto": case "--connect-timeout":
                Engine(e => e.ConnectTimeout = Int()); break;
            case "--user-description":
                Engine(e => e.UserDescription = value); break;
            case "--shared-files":
                Engine(e => e.SharedFiles = Int()); break;
            case "--shared-folders":
                Engine(e => e.SharedFolders = Int()); break;
            case "--mock-files-dir":
                Engine(e => e.MockFilesDir = value); break;
            case "--mock-files-no-read-tags":
                Engine(e => e.MockFilesReadTags = false); break;
            case "--mock-files-slow":
                Engine(e => e.MockFilesSlow = Bool()); break;
            case "--mock-files-fail-downloads":
                Engine(e => e.MockFilesFailDownloads = Int()); break;

            // ── CliSettings ──────────────────────────────────────────────────
            case "-t": case "--interactive":
                Cli(c => c.InteractiveMode = Bool()); break;
            case "--np": case "--no-progress":
                Cli(c => c.NoProgress = Bool()); break;
            case "--progress":
                Cli(c => c.NoProgress = !Bool()); break;
            case "--progress-json":
                Cli(c => c.ProgressJson = Bool()); break;
            case "--server-ip": case "--daemon-ip": case "--api-ip":
                Daemon(d => d.ListenIp = value); break;
            case "--server-port": case "--daemon-port": case "--api-port":
                Daemon(d => d.ListenPort = Int()); break;
            case "--remote": case "--server-url":
                Remote(r => r.ServerUrl = value); break;

            // ── OutputSettings ───────────────────────────────────────────────
            case "-p": case "--path": case "--parent":
                Download(d => d.Output.ParentDir = value); break;
            case "--nf": case "--name-format":
                Download(d => d.Output.NameFormat = value); break;
            case "--irs": case "--invalid-replace-str":
                Download(d => d.Output.InvalidReplaceStr = value); break;
            case "--wp": case "--write-playlist":
                Download(d => d.Output.WritePlaylist = Bool()); break;
            case "--nwp": case "--no-write-playlist":
                Download(d => d.Output.WritePlaylist = false); break;
            case "--pp": case "--playlist-path":
                Download(d => d.Output.M3uFilePath = value); break;
            case "--wi": case "--write-index":
                Download(d => { d.Output.WriteIndex = Bool(); d.Output.HasConfiguredIndex = true; }); break;
            case "--nwi": case "--no-write-index":
                Download(d => { d.Output.WriteIndex = false; d.Output.HasConfiguredIndex = true; }); break;
            case "--ip": case "--index-path":
                Download(d => { d.Output.IndexFilePath = value; d.Output.HasConfiguredIndex = true; }); break;
            case "--iaa": case "--incomplete-album-action":
                var incompleteAlbumAction = ParseIncompleteAlbumAction(value, flag);
                Download(d =>
                {
                    d.Output.IncompleteAlbumAction.Kind = incompleteAlbumAction.Kind;
                    d.Output.IncompleteAlbumAction.Path = incompleteAlbumAction.Path;
                });
                break;
            case "--oc": case "--on-complete":
                var onComplete = ParseOnCompleteConfigValue(value);
                Download(d =>
                {
                    if (onComplete.Append)
                    {
                        d.Output.OnComplete ??= [];
                        d.Output.OnComplete.Add(onComplete.Command);
                    }
                    else
                    {
                        d.Output.OnComplete = [onComplete.Command];
                    }
                });
                break;
            case "--print":
                Download(d => d.PrintOption = ParsePrintOption(value, flag)); break;
            case "--pt": case "--print-tracks":
                Download(d => d.PrintOption = PrintOption.Tracks); break;
            case "--ptf": case "--print-tracks-full":
                Download(d => d.PrintOption = PrintOption.Tracks | PrintOption.Full); break;
            case "--pr": case "--print-results":
                Download(d => d.PrintOption = PrintOption.Results); break;
            case "--prf": case "--print-results-full":
                Download(d => d.PrintOption = PrintOption.Results | PrintOption.Full); break;
            case "--pl": case "--print-link":
                Download(d => d.PrintOption = PrintOption.Link); break;
            case "--pj": case "--print-json":
                Download(d => d.PrintOption = PrintOption.Json); break;
            case "--pjf": case "--print-json-full":
                Download(d => d.PrintOption = PrintOption.Json | PrintOption.Full); break;

            // ── Extraction / album settings ──────────────────────────────────
            case "-i": case "--input":
                Download(d =>
                {
                    if (d.Extraction.Input != null)
                        throw new Exception($"Input error: Invalid argument '{value}'. Input is already set to '{d.Extraction.Input}'");
                    d.Extraction.Input = value;
                });
                break;
            case "--it": case "--input-type":
                Download(d =>
                {
                    if (!Enum.TryParse<InputType>(value.Replace("-", ""), ignoreCase: true, out var parsed))
                        throw new Exception($"Input error: Invalid input type '{value}'");
                    d.Extraction.InputType = parsed;
                });
                break;
            case "-n": case "--number":
                Download(d => d.Extraction.MaxTracks = ParseIntAtLeast(value, flag, 1)); break;
            case "-o": case "--offset":
                Download(d => d.Extraction.Offset = ParseIntAtLeast(value, flag, 0)); break;
            case "-r": case "--reverse":
                Download(d => d.Extraction.Reverse = Bool()); break;
            case "--gd": case "--get-deleted":
                Download(d => d.YouTube.GetDeleted = Bool()); break;
            case "--do": case "--deleted-only":
                Download(d => d.YouTube.DeletedOnly = Bool()); break;
            case "--rfp": case "--rfs": case "--remove-from-source": case "--remove-from-playlist":
                Download(d => d.Extraction.RemoveTracksFromSource = Bool()); break;
            case "--msa": case "--min-shares-aggregate":
                Download(d => d.Search.MinSharesAggregate = Int()); break;
            case "--alt": case "--aggregate-length-tol":
                Download(d => d.Search.AggregateLengthTol = Int()); break;
            case "-a": case "--album":
                Download(d => d.Extraction.RequestedMode = Bool() ? ExtractionMode.Album : ExtractionMode.Song); break;
            case "-s": case "--song":
                Download(d => d.Extraction.RequestedMode = Bool() ? ExtractionMode.Song : ExtractionMode.Album); break;
            case "--uta": case "--upgrade-to-album":
                Download(d => d.Extraction.UpgradeToAlbum = Bool()); break;
            case "-g": case "--aggregate":
                Download(d => d.Search.IsAggregate = Bool()); break;
            case "--aa": case "--album-art":
                Download(d => d.Output.AlbumArtOption = value.ToLower().Trim() switch
                {
                    "default" => AlbumArtOption.Default,
                    "largest" => AlbumArtOption.Largest,
                    "most" => AlbumArtOption.Most,
                    var s => throw new Exception($"Input error: Invalid album art option '{s}'"),
                });
                break;
            case "--aao": case "--aa-only": case "--album-art-only":
                Download(d =>
                {
                    d.Output.AlbumArtOnly = Bool();
                    if (d.Output.AlbumArtOnly)
                    {
                        if (d.Output.AlbumArtOption == AlbumArtOption.Default)
                            d.Output.AlbumArtOption = AlbumArtOption.Largest;
                        d.Search.PreferredCond = new FileConditions();
                        d.Search.NecessaryCond = new FileConditions();
                    }
                });
                break;
            case "--matc": case "--min-album-track-count":
                Download(d => d.Search.NecessaryFolderCond.MinTrackCount = Int()); break;
            case "--Matc": case "--max-album-track-count":
                Download(d => d.Search.NecessaryFolderCond.MaxTrackCount = Int()); break;
            case "--eMtc": case "--extract-max-track-count":
                Download(d => d.Extraction.SetAlbumMaxTrackCount = Bool()); break;
            case "--emtc": case "--extract-min-track-count":
                Download(d => d.Extraction.SetAlbumMinTrackCount = Bool()); break;
            case "--album-track-count-max-retries":
                Download(d => d.Transfer.AlbumTrackCountMaxRetries = Int()); break;
            case "--atc": case "--album-track-count":
                Download(d =>
                {
                    if (value == "-1")
                        d.Search.NecessaryFolderCond.MinTrackCount = d.Search.NecessaryFolderCond.MaxTrackCount = null;
                    else if (value.EndsWith('-'))
                        d.Search.NecessaryFolderCond.MaxTrackCount = ParseInt(value[..^1], flag);
                    else if (value.EndsWith('+'))
                        d.Search.NecessaryFolderCond.MinTrackCount = ParseInt(value[..^1], flag);
                    else
                        d.Search.NecessaryFolderCond.MinTrackCount = d.Search.NecessaryFolderCond.MaxTrackCount = Int();
                });
                break;

            // ── Preprocess / search settings ─────────────────────────────────
            case "--rft": case "--remove-ft":
                Download(d => d.Preprocess.RemoveFt = Bool()); break;
            case "--rb": case "--remove-brackets":
                Download(d => d.Preprocess.RemoveBrackets = Bool()); break;
            case "--amw": case "--artist-maybe-wrong":
                Download(d => d.Search.ArtistMaybeWrong = Bool()); break;
            case "--ea": case "--extract-artist":
                Download(d => d.Preprocess.ExtractArtist = Bool()); break;
            case "--parse-title":
                Download(d => d.Preprocess.ParseTitleTemplate = value); break;
            case "--re": case "--regex":
                Download(d => ApplyRegex(value, d.Preprocess)); break;
            case "--st": case "--search-time": case "--search-timeout":
                Download(d => d.Search.SearchTimeout = Int()); break;
            case "--Mst": case "--stale-time": case "--max-stale-time":
                Download(d => d.Search.MaxStaleTime = Int()); break;
            case "--Mr": case "--retries": case "--max-retries":
                Download(d => d.Transfer.MaxDownloadRetries = Int()); break;
            case "--uer": case "--unknown-error-retries":
                Download(d => d.Transfer.UnknownErrorRetries = Int()); break;
            case "--fs": case "--fast-search":
                Download(d => d.Search.FastSearch = Bool()); break;
            case "--fsd": case "--fast-search-delay":
                Download(d => d.Search.FastSearchDelay = Int()); break;
            case "--fsmus": case "--fast-search-min-up-speed":
                Download(d => d.Search.FastSearchMinUpSpeed = Double()); break;
            case "-d": case "--desperate":
                Download(d => d.Search.DesperateSearch = Bool()); break;
            case "--nrsc": case "--no-remove-special-chars":
                Download(d => d.Search.NoRemoveSpecialChars = Bool()); break;
            case "--rsc": case "--remove-special-chars":
                Download(d => d.Search.NoRemoveSpecialChars = false); break;
            case "--nbf": case "--no-browse-folder":
                Download(d => d.Search.NoBrowseFolder = true); break;
            case "--bf": case "--browse-folder":
                Download(d => d.Search.NoBrowseFolder = false); break;
            case "--nie": case "--no-incomplete-ext":
                Download(d => d.Transfer.NoIncompleteExt = Bool()); break;
            case "--rf": case "--relax": case "--relax-filtering":
                Download(d => d.Search.Relax = Bool()); break;
            case "--saq": case "--strict-album-quality":
                Download(d => d.Search.StrictAlbumQuality = Bool()); break;
            case "--ftd": case "--fails-to-downrank":
                Download(d => d.Search.DownrankOn = -Int()); break;
            case "--fti": case "--fails-to-ignore":
                Download(d => d.Search.IgnoreOn = -Int()); break;

            // ── Necessary condition shorthands ───────────────────────────────
            case "--af": case "--format":
                Download(d => d.Search.NecessaryCond.Formats = [.. value.ToLower().Split(',', tr).Select(x => x.TrimStart('.'))]); break;
            case "--lt": case "--tolerance": case "--length-tol": case "--length-tolerance":
                Download(d => d.Search.NecessaryCond.LengthTolerance = Int()); break;
            case "--mbr": case "--min-bitrate":
                Download(d => d.Search.NecessaryCond.MinBitrate = Int()); break;
            case "--Mbr": case "--max-bitrate":
                Download(d => d.Search.NecessaryCond.MaxBitrate = Int()); break;
            case "--msr": case "--min-samplerate":
                Download(d => d.Search.NecessaryCond.MinSampleRate = Int()); break;
            case "--Msr": case "--max-samplerate":
                Download(d => d.Search.NecessaryCond.MaxSampleRate = Int()); break;
            case "--mbd": case "--min-bitdepth":
                Download(d => d.Search.NecessaryCond.MinBitDepth = Int()); break;
            case "--Mbd": case "--max-bitdepth":
                Download(d => d.Search.NecessaryCond.MaxBitDepth = Int()); break;
            case "--stt": case "--strict-title":
                Download(d => d.Search.NecessaryCond.StrictTitle = Bool()); break;
            case "--sar": case "--strict-artist":
                Download(d => d.Search.NecessaryCond.StrictArtist = Bool()); break;
            case "--sal": case "--strict-album":
                Download(d => d.Search.NecessaryCond.StrictAlbum = Bool()); break;
            case "--anl": case "--accept-no-length":
                Download(d => d.Search.NecessaryCond.AcceptNoLength = Bool()); break;
            case "--bu": case "--banned-users":
                Download(d => d.Search.NecessaryCond.BannedUsers = value.Split(',', tr)); break;
            case "--au": case "--allowed-users":
                Download(d => d.Search.NecessaryCond.AllowedUsers = value.Split(',', tr)); break;
            case "--sc": case "--strict": case "--strict-conditions":
                Download(d =>
                {
                    bool val = Bool();
                    d.Search.NecessaryCond.AcceptMissingProps = !val;
                    d.Search.PreferredCond.AcceptMissingProps = !val;
                });
                break;
            case "--cond": case "--conditions":
                Download(d =>
                {
                    var fc = new FolderConditionPatch();
                    d.Search.NecessaryCond.AddConditions(ConditionParser.ParseFileConditions(value, fc));
                    d.Search.NecessaryFolderCond.AddConditions(fc);
                });
                break;

            // ── Preferred condition shorthands ───────────────────────────────
            case "--paf": case "--pf": case "--pref-format":
                Download(d => d.Search.PreferredCond.Formats = [.. value.ToLower().Split(',', tr).Select(x => x.TrimStart('.'))]); break;
            case "--plt": case "--pref-tolerance": case "--pref-length-tol": case "--pref-length-tolerance":
                Download(d => d.Search.PreferredCond.LengthTolerance = Int()); break;
            case "--pmbr": case "--pref-min-bitrate":
                Download(d => d.Search.PreferredCond.MinBitrate = Int()); break;
            case "--pMbr": case "--pref-max-bitrate":
                Download(d => d.Search.PreferredCond.MaxBitrate = Int()); break;
            case "--pmsr": case "--pref-min-samplerate":
                Download(d => d.Search.PreferredCond.MinSampleRate = Int()); break;
            case "--pMsr": case "--pref-max-samplerate":
                Download(d => d.Search.PreferredCond.MaxSampleRate = Int()); break;
            case "--pmbd": case "--pref-min-bitdepth":
                Download(d => d.Search.PreferredCond.MinBitDepth = Int()); break;
            case "--pMbd": case "--pref-max-bitdepth":
                Download(d => d.Search.PreferredCond.MaxBitDepth = Int()); break;
            case "--pst": case "--pstt": case "--pref-strict-title":
                Download(d => d.Search.PreferredCond.StrictTitle = Bool()); break;
            case "--psar": case "--pref-strict-artist":
                Download(d => d.Search.PreferredCond.StrictArtist = Bool()); break;
            case "--psal": case "--pref-strict-album":
                Download(d => d.Search.PreferredCond.StrictAlbum = Bool()); break;
            case "--panl": case "--pref-accept-no-length":
                Download(d => d.Search.PreferredCond.AcceptNoLength = Bool()); break;
            case "--pbu": case "--pref-banned-users":
                Download(d => d.Search.PreferredCond.BannedUsers = value.Split(',', tr)); break;
            case "--pau": case "--pref-allowed-users":
                Download(d => d.Search.PreferredCond.AllowedUsers = value.Split(',', tr)); break;
            case "--pc": case "--pref": case "--preferred-conditions":
                Download(d =>
                {
                    var fc = new FolderConditionPatch();
                    d.Search.PreferredCond.AddConditions(ConditionParser.ParseFileConditions(value, fc));
                    d.Search.PreferredFolderCond.AddConditions(fc);
                });
                break;

            // ── Skip and provider settings ───────────────────────────────────
            case "--se": case "--skip-existing":
                Download(d => d.Skip.SkipExisting = Bool()); break;
            case "--nse": case "--no-skip-existing":
                Download(d => d.Skip.SkipExisting = false); break;
            case "--snf": case "--skip-not-found":
                Download(d => d.Skip.SkipNotFound = Bool()); break;
            case "--smd": case "--skip-music-dir":
                Download(d => d.Skip.SkipMusicDir = value); break;
            case "--smod": case "--skip-mode-output-dir":
                Download(d => d.Skip.SkipMode = ParseSkipMode(value, flag, allowIndex: true)); break;
            case "--smmd": case "--skip-mode-music-dir":
                Download(d => d.Skip.SkipModeMusicDir = ParseSkipMode(value, flag, allowIndex: false)); break;
            case "--scc": case "--skip-check-cond":
                Download(d => d.Skip.SkipCheckCond = Bool()); break;
            case "--scpc": case "--skip-check-pref-cond":
                Download(d => d.Skip.SkipCheckPrefCond = Bool()); break;
            case "--si": case "--spotify-id":
                Download(d => d.Spotify.ClientId = value); break;
            case "--ss": case "--spotify-secret":
                Download(d => d.Spotify.ClientSecret = value); break;
            case "--stk": case "--spotify-token":
                Download(d => d.Spotify.Token = value); break;
            case "--str": case "--spotify-refresh":
                Download(d => d.Spotify.Refresh = value); break;
            case "--yk": case "--youtube-key":
                Download(d => d.YouTube.ApiKey = value); break;
            case "--yp": case "--yt-parse":
                Download(d => d.Csv.YtParse = Bool()); break;
            case "--yd": case "--yt-dlp":
                Download(d => d.YtDlp.UseYtdlp = Bool()); break;
            case "--yda": case "--yt-dlp-argument":
                Download(d => d.YtDlp.YtdlpArgument = value); break;
            case "--ac": case "--artist-col":
                Download(d => d.Csv.ArtistCol = value); break;
            case "--tc": case "--track-col": case "--title-col":
                Download(d => d.Csv.TitleCol = value); break;
            case "--alc": case "--album-col":
                Download(d => d.Csv.AlbumCol = value); break;
            case "--ydc": case "--yt-desc-col":
                Download(d => d.Csv.DescCol = value); break;
            case "--atcc": case "--album-track-count-col":
                Download(d => d.Csv.TrackCountCol = value); break;
            case "--yic": case "--yt-id-col":
                Download(d => d.Csv.YtIdCol = value); break;
            case "--lc": case "--length-col":
                Download(d => d.Csv.LengthCol = value); break;
            case "--tf": case "--time-format":
                Download(d =>
                {
                    CsvExtractor.ValidateTimeFormat(value);
                    d.Csv.TimeUnit = value;
                });
                break;
            case "--from-html":
                Download(d => d.Bandcamp.HtmlFromFile = value); break;

            default:
                throw new Exception($"Input error: Unknown argument: {flag}");
        }
    }

    private sealed class DownloadSettingsDeltaBuilder
    {
        private readonly List<DownloadSettingOperationDto> operations = [];

        public DownloadSettingsPatchDto? Build()
            => DownloadSettingsPatchDtoMapper.FromOperations(operations);

        public void Record(string flag, string value, Action<DownloadSettings> action)
        {
            if (TryRecordSpecial(flag, value))
                return;

            AddDiffOperations(action, CreateSentinelSettings(
                boolSeed: false,
                intSeed: -987654321,
                doubleSeed: -987654321.5,
                stringSeed: "<<Sockseek-sentinel-a>>",
                printSeed: PrintOption.IndexFailed,
                inputSeed: InputType.CSV,
                skipSeed: SkipMode.Name,
                albumArtSeed: AlbumArtOption.Default));

            AddDiffOperations(action, CreateSentinelSettings(
                boolSeed: true,
                intSeed: -987654320,
                doubleSeed: -987654320.5,
                stringSeed: "<<Sockseek-sentinel-b>>",
                printSeed: PrintOption.Tracks,
                inputSeed: InputType.Spotify,
                skipSeed: SkipMode.Tag,
                albumArtSeed: AlbumArtOption.Most));
        }

        private bool TryRecordSpecial(string flag, string value)
        {
            switch (flag)
            {
                case "-i":
                case "--input":
                    Add(DownloadSettingsDeltaMapper.Set("Extraction.Input", value));
                    return true;

                case "--oc":
                case "--on-complete":
                    var onComplete = ParseOnCompleteConfigValue(value);
                    if (onComplete.Append)
                    {
                        Add(DownloadSettingsDeltaMapper.Append(
                            "Output.OnComplete",
                            [onComplete.Command]));
                        return true;
                    }
                    return false;

                case "--re":
                case "--regex":
                    if (value.TrimStart().StartsWith("+ "))
                    {
                        var preprocess = new PreprocessSettings();
                        ApplyRegex(value, preprocess);
                        Add(DownloadSettingsDeltaMapper.AppendRegex(
                            "Preprocess.Regex",
                            preprocess.Regex?.Select(ToRegexRuleDto).ToList() ?? []));
                        return true;
                    }
                    return false;

                case "--cond":
                case "--conditions":
                    AddConditionOperations("Search.NecessaryCond", "Search.NecessaryFolderCond", value);
                    return true;

                case "--pc":
                case "--pref":
                case "--preferred-conditions":
                    AddConditionOperations("Search.PreferredCond", "Search.PreferredFolderCond", value);
                    return true;

                default:
                    return false;
            }
        }

        private void AddConditionOperations(string filePrefix, string folderPrefix, string value)
        {
            var folder = new FolderConditionPatch();
            var file = ConditionParser.ParseFileConditions(value, folder);
            AddFileConditionOperations(filePrefix, file);

            if (folder.MinTrackCount != null)
                Add(DownloadSettingsDeltaMapper.Set($"{folderPrefix}.MinTrackCount", folder.MinTrackCount));
            if (folder.MaxTrackCount != null)
                Add(DownloadSettingsDeltaMapper.Set($"{folderPrefix}.MaxTrackCount", folder.MaxTrackCount));
            if (folder.RequiredTrackTitles?.Count > 0)
                Add(DownloadSettingsDeltaMapper.Append($"{folderPrefix}.RequiredTrackTitles", folder.RequiredTrackTitles));
        }

        private void AddFileConditionOperations(string prefix, FileConditionPatch file)
        {
            if (file.LengthTolerance != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.LengthTolerance", file.LengthTolerance));
            if (file.MinBitrate != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.MinBitrate", file.MinBitrate));
            if (file.MaxBitrate != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.MaxBitrate", file.MaxBitrate));
            if (file.MinSampleRate != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.MinSampleRate", file.MinSampleRate));
            if (file.MaxSampleRate != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.MaxSampleRate", file.MaxSampleRate));
            if (file.MinBitDepth != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.MinBitDepth", file.MinBitDepth));
            if (file.MaxBitDepth != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.MaxBitDepth", file.MaxBitDepth));
            if (file.StrictTitle != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.StrictTitle", file.StrictTitle));
            if (file.StrictArtist != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.StrictArtist", file.StrictArtist));
            if (file.StrictAlbum != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.StrictAlbum", file.StrictAlbum));
            if (file.Formats != null) Add(DownloadSettingsDeltaMapper.Replace($"{prefix}.Formats", file.Formats));
            if (file.BannedUsers != null) Add(DownloadSettingsDeltaMapper.Replace($"{prefix}.BannedUsers", file.BannedUsers));
            if (file.AllowedUsers != null) Add(DownloadSettingsDeltaMapper.Replace($"{prefix}.AllowedUsers", file.AllowedUsers));
            if (file.AcceptNoLength != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.AcceptNoLength", file.AcceptNoLength));
            if (file.AcceptMissingProps != null) Add(DownloadSettingsDeltaMapper.Set($"{prefix}.AcceptMissingProps", file.AcceptMissingProps));
        }

        private void AddDiffOperations(Action<DownloadSettings> action, DownloadSettings before)
        {
            var after = SettingsCloner.Clone(before);
            action(after);

            foreach (var operation in DownloadSettingsDeltaMapper.DifferenceOperations(before, after))
                Add(operation);
        }

        private void Add(DownloadSettingOperationDto operation)
        {
            if (operations.Any(existing => SameOperation(existing, operation)))
                return;

            operations.Add(operation);
        }

        private static bool SameOperation(DownloadSettingOperationDto left, DownloadSettingOperationDto right)
            => left.Path == right.Path
            && left.Operation == right.Operation
            && left.StringValue == right.StringValue
            && left.IntValue == right.IntValue
            && left.DoubleValue == right.DoubleValue
            && left.BoolValue == right.BoolValue
            && left.PrintOptionValue == right.PrintOptionValue
            && left.InputTypeValue == right.InputTypeValue
            && left.ExtractionModeValue == right.ExtractionModeValue
            && left.SkipModeValue == right.SkipModeValue
            && left.AlbumArtOptionValue == right.AlbumArtOptionValue
            && left.IncompleteAlbumActionKindValue == right.IncompleteAlbumActionKindValue
            && ListEqual(left.StringListValue, right.StringListValue)
            && RegexListEqual(left.RegexListValue, right.RegexListValue);

        private static bool ListEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
            => left == null && right == null
            || left != null && right != null && left.SequenceEqual(right);

        private static bool RegexListEqual(IReadOnlyList<RegexRuleDto>? left, IReadOnlyList<RegexRuleDto>? right)
            => left == null && right == null
            || left != null && right != null && left.SequenceEqual(right);

        private static DownloadSettings CreateSentinelSettings(
            bool boolSeed,
            int intSeed,
            double doubleSeed,
            string stringSeed,
            PrintOption printSeed,
            InputType inputSeed,
            SkipMode skipSeed,
            AlbumArtOption albumArtSeed)
        {
            var settings = new DownloadSettings
            {
                PrintOption = printSeed,
            };

            settings.Output.ParentDir = stringSeed;
            settings.Output.NameFormat = stringSeed;
            settings.Output.InvalidReplaceStr = stringSeed;
            settings.Output.WritePlaylist = boolSeed;
            settings.Output.WriteIndex = boolSeed;
            settings.Output.HasConfiguredIndex = boolSeed;
            settings.Output.M3uFilePath = stringSeed;
            settings.Output.IndexFilePath = stringSeed;
            settings.Output.IncompleteAlbumAction.Kind = null;
            settings.Output.IncompleteAlbumAction.Path = stringSeed;
            settings.Output.OnComplete = [stringSeed];
            settings.Output.AlbumArtOnly = boolSeed;
            settings.Output.AlbumArtOption = albumArtSeed;

            settings.Search.NecessaryCond = SentinelFileConditions(boolSeed, intSeed, stringSeed);
            settings.Search.PreferredCond = SentinelFileConditions(boolSeed, intSeed, stringSeed);
            settings.Search.NecessaryFolderCond = SentinelFolderConditions(intSeed, stringSeed);
            settings.Search.PreferredFolderCond = SentinelFolderConditions(intSeed, stringSeed);
            settings.Search.SearchTimeout = intSeed;
            settings.Search.MaxStaleTime = intSeed;
            settings.Search.DownrankOn = intSeed;
            settings.Search.IgnoreOn = intSeed;
            settings.Search.FastSearch = boolSeed;
            settings.Search.FastSearchDelay = intSeed;
            settings.Search.FastSearchMinUpSpeed = doubleSeed;
            settings.Search.DesperateSearch = boolSeed;
            settings.Search.NoRemoveSpecialChars = boolSeed;
            settings.Search.RemoveSingleCharSearchTerms = boolSeed;
            settings.Search.NoBrowseFolder = boolSeed;
            settings.Search.Relax = boolSeed;
            settings.Search.StrictAlbumQuality = boolSeed;
            settings.Search.ArtistMaybeWrong = boolSeed;
            settings.Search.IsAggregate = boolSeed;
            settings.Search.MinSharesAggregate = intSeed;
            settings.Search.AggregateLengthTol = intSeed;

            settings.Skip.SkipExisting = boolSeed;
            settings.Skip.SkipNotFound = boolSeed;
            settings.Skip.SkipMode = skipSeed;
            settings.Skip.SkipMusicDir = stringSeed;
            settings.Skip.SkipModeMusicDir = skipSeed;
            settings.Skip.SkipCheckCond = boolSeed;
            settings.Skip.SkipCheckPrefCond = boolSeed;

            settings.Preprocess.RemoveFt = boolSeed;
            settings.Preprocess.RemoveBrackets = boolSeed;
            settings.Preprocess.ExtractArtist = boolSeed;
            settings.Preprocess.ParseTitleTemplate = stringSeed;
            settings.Preprocess.Regex = [(SentinelRegexFields(stringSeed), SentinelRegexFields(stringSeed + "-replace"))];

            settings.Extraction.Input = stringSeed;
            settings.Extraction.InputType = inputSeed;
            settings.Extraction.MaxTracks = intSeed;
            settings.Extraction.Offset = intSeed;
            settings.Extraction.Reverse = boolSeed;
            settings.Extraction.RemoveTracksFromSource = boolSeed;
            settings.Extraction.RequestedMode = boolSeed ? ExtractionMode.Album : ExtractionMode.Song;
            settings.Extraction.UpgradeToAlbum = boolSeed;
            settings.Extraction.SetAlbumMinTrackCount = boolSeed;
            settings.Extraction.SetAlbumMaxTrackCount = boolSeed;

            settings.Transfer.MaxDownloadRetries = intSeed;
            settings.Transfer.UnknownErrorRetries = intSeed;
            settings.Transfer.NoIncompleteExt = boolSeed;
            settings.Transfer.AlbumTrackCountMaxRetries = intSeed;

            settings.Spotify.ClientId = stringSeed;
            settings.Spotify.ClientSecret = stringSeed;
            settings.Spotify.Token = stringSeed;
            settings.Spotify.Refresh = stringSeed;
            settings.YouTube.ApiKey = stringSeed;
            settings.YouTube.GetDeleted = boolSeed;
            settings.YouTube.DeletedOnly = boolSeed;
            settings.YtDlp.UseYtdlp = boolSeed;
            settings.YtDlp.YtdlpArgument = stringSeed;
            settings.Csv.ArtistCol = stringSeed;
            settings.Csv.AlbumCol = stringSeed;
            settings.Csv.TitleCol = stringSeed;
            settings.Csv.YtIdCol = stringSeed;
            settings.Csv.DescCol = stringSeed;
            settings.Csv.TrackCountCol = stringSeed;
            settings.Csv.LengthCol = stringSeed;
            settings.Csv.TimeUnit = stringSeed;
            settings.Csv.YtParse = boolSeed;
            settings.Bandcamp.HtmlFromFile = stringSeed;

            return settings;
        }

        private static FileConditions SentinelFileConditions(bool boolSeed, int intSeed, string stringSeed) => new()
        {
            LengthTolerance = intSeed,
            MinBitrate = intSeed,
            MaxBitrate = intSeed,
            MinSampleRate = intSeed,
            MaxSampleRate = intSeed,
            MinBitDepth = intSeed,
            MaxBitDepth = intSeed,
            StrictTitle = boolSeed,
            StrictArtist = boolSeed,
            StrictAlbum = boolSeed,
            Formats = [stringSeed],
            BannedUsers = [stringSeed],
            AllowedUsers = [stringSeed],
            AcceptNoLength = boolSeed,
            AcceptMissingProps = boolSeed,
        };

        private static FolderConditions SentinelFolderConditions(int intSeed, string stringSeed) => new()
        {
            MinTrackCount = intSeed,
            MaxTrackCount = intSeed,
            RequiredTrackTitles = [stringSeed],
        };

        private static RegexFields SentinelRegexFields(string value) => new()
        {
            Title = value,
            Artist = value,
            Album = value,
        };

        private static RegexRuleDto ToRegexRuleDto((RegexFields, RegexFields) rule)
            => new(ToDto(rule.Item1), ToDto(rule.Item2));

        private static RegexFieldsDto ToDto(RegexFields fields)
            => new(fields.Title, fields.Artist, fields.Album);
    }

    private static string ResolveConfigPath(string? explicit_)
    {
        if (!string.IsNullOrEmpty(explicit_)) return explicit_;
        foreach (var p in DefaultConfigPaths())
            if (File.Exists(p)) return p;
        return "";
    }

    private static IEnumerable<string> DefaultConfigPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sockseek", "sockseek.conf");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sockseek", "sockseek.conf");
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(xdg))
            yield return Path.Combine(xdg, "sockseek", "sockseek.conf");
        yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sockseek.conf");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Normalize argv: expand --arg=val into --arg val, and -abc into -a -b -c.
    private static List<string> NormalizeArgs(IReadOnlyList<string> args)
    {
        var result = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (arg.Length > 2 && arg[0] == '-')
            {
                if (arg[1] == '-')
                {
                    if (arg.Contains('='))
                    {
                        var eq = arg.IndexOf('=');
                        result.Add(arg[..eq]);
                        result.Add(arg[(eq + 1)..]);
                        continue;
                    }
                }
                else if (!arg.Contains(' '))
                {
                    foreach (char c in arg[1..])
                        result.Add($"-{c}");
                    continue;
                }
            }
            result.Add(arg);
        }
        return result;
    }

    private static bool IsBoolLiteral(string value) =>
        value is "true" or "false" or "True" or "False";

    private sealed class OptionProbe
    {
        public bool UsesBoolValue { get; set; }
    }

    private static bool OptionUsesBoolValue(string flag)
        => BoolOptionCache.GetOrAdd(flag, static key =>
        {
            var entry = new ProfileEntry(
                new SettingsProfile { Name = "<probe>" },
                new CliSettingsPatch(),
                new DaemonSettingsPatch(),
                new RemoteSettingsPatch(),
                []);
            var probe = new OptionProbe();
            try
            {
                AddProfileOption(entry, key, "true", probe: probe);
            }
            catch
            {
                return false;
            }

            return probe.UsesBoolValue;
        });

    private static bool IsValuelessOption(string flag) => flag switch
    {
        "--no-listen"
        or "-v" or "--verbose" or "--debug" or "-vv" or "--trace"
        or "--mock-files-no-read-tags"
        or "--np" or "--no-progress"
        or "--progress"
        or "--nwp" or "--no-write-playlist"
        or "--nwi" or "--no-write-index"
        or "--pt" or "--print-tracks"
        or "--ptf" or "--print-tracks-full"
        or "--pr" or "--print-results"
        or "--prf" or "--print-results-full"
        or "--pl" or "--print-link"
        or "--pj" or "--print-json"
        or "--pjf" or "--print-json-full"
        or "--rsc" or "--remove-special-chars"
        or "--nbf" or "--no-browse-folder"
        or "--bf" or "--browse-folder"
        or "--nse" or "--no-skip-existing" => true,
        _ => false,
    };

    private static void ApplyRegex(string raw, PreprocessSettings pre)
    {
        string s = raw.Replace("\\;", "<<semicol>>");
        bool append = s.TrimStart().StartsWith("+ ");
        if (append) s = s.TrimStart()[2..];

        string applyTo = "TAL";
        if (s.Length > 2 && s[1] == ':' && s[0] is 'T' or 'A' or 'L')
        {
            applyTo = s[0].ToString();
            s = s[2..];
        }

        var parts  = s.Split(';');
        string pat = parts[0].Replace("<<semicol>>", ";");
        string rep = (parts.Length > 1 ? parts[1] : "").Replace("<<semicol>>", ";");

        var toReplace = new RegexFields
        {
            Title  = applyTo.Contains('T') ? pat : "",
            Artist = applyTo.Contains('A') ? pat : "",
            Album  = applyTo.Contains('L') ? pat : "",
        };
        var replaceBy = new RegexFields
        {
            Title  = applyTo.Contains('T') ? rep : "",
            Artist = applyTo.Contains('A') ? rep : "",
            Album  = applyTo.Contains('L') ? rep : "",
        };

        if (!append) pre.Regex = null;
        pre.Regex ??= [];
        pre.Regex.Add((toReplace, replaceBy));
    }

    private static PrintOption ParsePrintOption(string s, string flag) => s.ToLower().Trim() switch
    {
        "none"          => PrintOption.None,
        "tracks"        => PrintOption.Tracks,
        "results"       => PrintOption.Results,
        "tracks-full"   => PrintOption.Tracks | PrintOption.Full,
        "results-full"  => PrintOption.Results | PrintOption.Full,
        "link"          => PrintOption.Link,
        "json"          => PrintOption.Json,
        "json-all"      => PrintOption.Json | PrintOption.Full,
        "index"         => PrintOption.Index,
        "index-failed"  => PrintOption.Index | PrintOption.IndexFailed,
        _ => throw new Exception($"Input error: Invalid print option '{s}' for '{flag}'"),
    };

    private static string Next(IList<string> tokens, ref int i, string flag)
    {
        if (++i >= tokens.Count)
            throw new Exception($"Input error: Option '{flag}' requires a parameter");
        return tokens[i];
    }

    private static double ParseDouble(string s, string flag)
    {
        if (!double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
            throw new Exception($"Input error: Option '{flag}' requires a numeric parameter, got '{s}'");
        return v;
    }

    private static bool ParseBool(string s, string flag)
    {
        if (bool.TryParse(s, out var value))
            return value;

        throw new Exception($"Input error: Option '{flag}' requires a boolean parameter, got '{s}'");
    }

    private static SkipMode ParseSkipMode(string s, string flag, bool allowIndex)
    {
        return s.ToLower().Trim() switch
        {
            "name"  => SkipMode.Name,
            "tag"   => SkipMode.Tag,
            "index" when allowIndex => SkipMode.Index,
            _ => throw new Exception($"Input error: Invalid skip mode '{s}' for '{flag}'"),
        };
    }

    private static IncompleteAlbumActionSettings ParseIncompleteAlbumAction(string s, string flag)
    {
        var value = s.Trim();
        var lower = value.ToLowerInvariant();

        if (lower == "move")
            return new IncompleteAlbumActionSettings { Kind = IncompleteAlbumActionKind.Move };
        if (lower.StartsWith("move:", StringComparison.Ordinal))
        {
            var path = value["move:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(path))
                throw new Exception($"Input error: Option '{flag}' requires a path for move:<path>");
            return new IncompleteAlbumActionSettings { Kind = IncompleteAlbumActionKind.Move, Path = path };
        }
        if (lower == "delete")
            return new IncompleteAlbumActionSettings { Kind = IncompleteAlbumActionKind.Delete };
        if (lower == "keep")
            return new IncompleteAlbumActionSettings { Kind = IncompleteAlbumActionKind.Keep };

        throw new Exception($"Input error: Invalid incomplete album action '{s}' for '{flag}'");
    }

    private static int ParseInt(string s, string flag)
    {
        if (!int.TryParse(s.Replace("_", ""), out int v))
            throw new Exception($"Input error: Option '{flag}' requires an integer parameter, got '{s}'");
        return v;
    }

    private static int ParseIntAtLeast(string s, string flag, int min)
    {
        var value = ParseInt(s, flag);
        if (value < min)
            throw new Exception($"Input error: Option '{flag}' must be at least {min}, got '{s}'");
        return value;
    }

    private static int FindLastFlag(IReadOnlyList<string> args, params string[] names)
    {
        for (int i = args.Count - 1; i >= 0; i--)
            if (names.Contains(args[i])) return i;
        return -1;
    }

    private static bool IsExplicitFalse(IReadOnlyList<string> args, int idx)
        => idx + 1 < args.Count && args[idx + 1] == "false";

}
