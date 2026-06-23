using System.Text.RegularExpressions;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;

namespace Sockseek.Core.Settings;

public sealed record SettingsProfile
{
    public string Name { get; init; } = "";
    public string? Condition { get; init; }
    public EngineSettingsPatch Engine { get; init; } = new();
    public DownloadSettingsPatch Download { get; init; } = new();

    public bool HasEngineSettings => Engine.HasOperations;
    public bool HasDownloadSettings => Download.HasOperations;
}

public sealed class ProfileCatalog
{
    public SettingsProfile? DefaultProfile { get; init; }
    public IReadOnlyList<SettingsProfile> AutoProfiles { get; init; } = [];
    public IReadOnlyList<SettingsProfile> NamedProfiles { get; init; } = [];

    public static ProfileCatalog Empty { get; } = new();

    public IReadOnlyList<string> ProfileNames =>
        NamedProfiles.Select(p => p.Name).OrderBy(x => x).ToList();

    public IReadOnlyList<SettingsProfile> ResolveNamedProfiles(IEnumerable<string>? names)
    {
        if (names == null)
            return [];

        var byName = NamedProfiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<SettingsProfile>();

        foreach (var name in names.SelectMany(x => x.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(name, "help", StringComparison.OrdinalIgnoreCase))
                continue;

            if (byName.TryGetValue(name, out var profile))
                resolved.Add(profile);
            else
                throw new ArgumentException($"Input error: No profile '{name}' found in config");
        }

        return resolved;
    }
}

public sealed class EngineSettingsPatch
{
    private readonly List<Action<EngineSettings>> _operations = [];
    public bool HasOperations => _operations.Count > 0;

    public void Add(Action<EngineSettings> operation) => _operations.Add(operation);

    public void ApplyTo(EngineSettings settings)
    {
        foreach (var operation in _operations)
            operation(settings);
    }
}

public sealed class DownloadSettingsPatch
{
    private readonly List<Action<DownloadSettings>> _operations = [];
    public bool HasOperations => _operations.Count > 0;

    public void Add(Action<DownloadSettings> operation) => _operations.Add(operation);

    public void ApplyTo(DownloadSettings settings)
    {
        foreach (var operation in _operations)
            operation(settings);
    }
}

public sealed class ProfileContext
{
    public Dictionary<string, object> Values { get; } = new();
}

public interface IJobSettingsResolver
{
    DownloadSettings Resolve(DownloadSettings inherited, Job job);
}

public sealed class DefaultJobSettingsResolver : IJobSettingsResolver
{
    public static DefaultJobSettingsResolver Instance { get; } = new();

    private DefaultJobSettingsResolver() { }

    public DownloadSettings Resolve(DownloadSettings inherited, Job job) =>
        SettingsCloner.Clone(inherited);
}

public sealed class ProfileJobSettingsResolver : IJobSettingsResolver
{
    private readonly DownloadSettings _baseDefaults;
    private readonly SettingsProfile? _defaultProfile;
    private readonly IReadOnlyList<SettingsProfile> _autoProfiles;
    private readonly IReadOnlyList<SettingsProfile> _namedProfiles;
    private readonly SettingsProfile? _cliProfile;
    private readonly ProfileContext _context;
    private readonly Action<string>? _warn;
    private readonly Action<DownloadSettings>? _normalize;

    public ProfileJobSettingsResolver(
        DownloadSettings baseDefaults,
        SettingsProfile? defaultProfile,
        IReadOnlyList<SettingsProfile> autoProfiles,
        IReadOnlyList<SettingsProfile> namedProfiles,
        SettingsProfile? cliProfile,
        ProfileContext? context = null,
        Action<DownloadSettings>? normalize = null,
        Action<string>? warn = null)
    {
        _baseDefaults = SettingsCloner.Clone(baseDefaults);
        _defaultProfile = defaultProfile;
        _autoProfiles = autoProfiles;
        _namedProfiles = namedProfiles;
        _cliProfile = cliProfile;
        _context = context ?? new ProfileContext();
        _normalize = normalize;
        _warn = warn;

        foreach (var profile in _autoProfiles.Where(p => p.HasEngineSettings))
            throw new Exception($"Input error: Auto-profile '{profile.Name}' contains engine settings, which cannot be applied per job");
    }

    public DownloadSettings Resolve(DownloadSettings inherited, Job job)
    {
        if (inherited.PrintOption != PrintOption.None)
            return SettingsCloner.Clone(inherited);

        var matchingAutoProfiles = _autoProfiles
            .Where(p => p.Condition != null && ProfileConditionEvaluator.Satisfied(p.Condition, inherited, job, _context))
            .ToList();

        var settings = SettingsCloner.Clone(_baseDefaults);

        _defaultProfile?.Download.ApplyTo(settings);

        foreach (var profile in matchingAutoProfiles)
            profile.Download.ApplyTo(settings);

        foreach (var profile in _namedProfiles)
            profile.Download.ApplyTo(settings);

        _cliProfile?.Download.ApplyTo(settings);

        settings.AppliedAutoProfiles = [.. matchingAutoProfiles.Select(p => p.Name)];
        _normalize?.Invoke(settings);

        return settings;
    }
}

public static class SettingsPatchApplier
{
    public static void Apply(SettingsProfile profile, EngineSettings engine, DownloadSettings download)
    {
        profile.Engine.ApplyTo(engine);
        profile.Download.ApplyTo(download);
    }

    public static void ApplyDownload(SettingsProfile profile, DownloadSettings download) =>
        profile.Download.ApplyTo(download);

    public static void ApplyEngine(SettingsProfile profile, EngineSettings engine) =>
        profile.Engine.ApplyTo(engine);
}

public static class SettingsNormalizer
{
    public static void Normalize(DownloadSettings dl)
    {
        dl.Search.IgnoreOn = Math.Min(dl.Search.IgnoreOn, dl.Search.DownrankOn);

        if (dl.YouTube.DeletedOnly)
            dl.YouTube.GetDeleted = true;

        if (dl.Output.AlbumArtOnly && dl.Output.AlbumArtOption == AlbumArtOption.Default)
            dl.Output.AlbumArtOption = AlbumArtOption.Largest;

        dl.Output.NameFormat = dl.Output.NameFormat.Trim();
    }

    public static void NormalizeDownloadPaths(DownloadSettings dl)
        => NormalizeDownloadPaths(dl, PathVariableContext.Empty);

    public static void NormalizeDownloadPaths(DownloadSettings dl, PathVariableContext pathContext)
    {
        Normalize(dl);
        dl.RuntimePathContext = pathContext;

        if (string.IsNullOrWhiteSpace(dl.Output.ParentDir))
            dl.Output.ParentDir = Directory.GetCurrentDirectory();

        dl.Output.ParentDir = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.ParentDir, pathContext));
        dl.Output.NameFormat = dl.Output.NameFormat.Trim();

        if (dl.Output.M3uFilePath != null)
            dl.Output.M3uFilePath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.M3uFilePath, pathContext));
        if (dl.Output.IndexFilePath != null)
            dl.Output.IndexFilePath = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.IndexFilePath, pathContext));
        if (dl.Skip.SkipMusicDir != null)
            dl.Skip.SkipMusicDir = Utils.GetFullPath(Utils.ExpandVariables(dl.Skip.SkipMusicDir, pathContext));

        if (dl.Output.IncompleteAlbumAction.Path != null)
            dl.Output.IncompleteAlbumAction.Path = Utils.GetFullPath(Utils.ExpandVariables(dl.Output.IncompleteAlbumAction.Path, pathContext));
    }

    public static void NormalizeEnginePaths(EngineSettings engine)
        => NormalizeEnginePaths(engine, PathVariableContext.Empty);

    public static void NormalizeEnginePaths(EngineSettings engine, PathVariableContext pathContext)
    {
        if (engine.LogFilePath != null)
            engine.LogFilePath = Utils.GetFullPath(Utils.ExpandVariables(engine.LogFilePath, pathContext));
        if (engine.MockFilesDir != null)
            engine.MockFilesDir = Utils.GetFullPath(Utils.ExpandVariables(engine.MockFilesDir, pathContext));
    }
}

public static partial class ProfileConditionEvaluator
{
    public static bool Satisfied(string cond, DownloadSettings settings, Job? job = null, ProfileContext? context = null)
    {
        var tokens = new Queue<string>(CondTokenRegex().Split(cond).Where(t => !string.IsNullOrWhiteSpace(t)));

        bool ParseExpression()
        {
            bool left = ParseAndExpression();
            while (tokens.Count > 0 && tokens.Peek() == "||")
            {
                tokens.Dequeue();
                bool right = ParseAndExpression();
                left = left || right;
            }
            return left;
        }

        bool ParseAndExpression()
        {
            bool left = ParsePrimary();
            while (tokens.Count > 0 && tokens.Peek() == "&&")
            {
                tokens.Dequeue();
                bool right = ParsePrimary();
                left = left && right;
            }
            return left;
        }

        bool ParsePrimary()
        {
            if (tokens.Count == 0)
                throw new Exception("Input error: Unexpected end of profile condition");

            string tok = tokens.Dequeue();
            if (tok == "(")
            {
                var r = ParseExpression();
                if (tokens.Count == 0 || tokens.Dequeue() != ")")
                    throw new Exception("Input error: Missing ')' in profile condition");
                return r;
            }
            if (tok == "!") return !ParsePrimary();
            if (tok.StartsWith('"')) throw new Exception($"Input error: Invalid token at this position: {tok}");

            if (tokens.Count > 0 && (tokens.Peek() == "==" || tokens.Peek() == "!="))
            {
                string op = tokens.Dequeue();
                if (tokens.Count == 0)
                    throw new Exception($"Input error: Missing comparison value after '{op}'");
                string val = tokens.Dequeue().Trim('"').ToLower();
                string cur = GetVarValue(tok, settings, job, context).ToString()!.ToLower();
                return op == "==" ? cur == val : cur != val;
            }

            return (bool)GetVarValue(tok, settings, job, context);
        }

        var result = ParseExpression();
        if (tokens.Count > 0)
            throw new Exception($"Input error: Unexpected token in profile condition: {tokens.Peek()}");
        return result;
    }

    private static object GetVarValue(string var, DownloadSettings settings, Job? job, ProfileContext? context)
    {
        static string ToKebab(string s) =>
            string.Concat(s.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLower(c) : char.ToLower(c).ToString()));

        string mode = job switch
        {
            SearchJob { DefaultFolderProjection: not null } => "album",
            SearchJob { DefaultFileProjection: not null } => "song",
            AlbumAggregateJob => "album-aggregate",
            AlbumJob => "album",
            AggregateJob => "aggregate",
            SongJob => "song",
            _ when job != null => ToKebab(job.GetType().Name.Replace("Job", "")),
            _ when SettingsMode(settings) == "album" && settings.Search.IsAggregate => "album-aggregate",
            _ when SettingsMode(settings) == "song" && settings.Search.IsAggregate => "aggregate",
            _ when SettingsMode(settings) == "album" => "album",
            _ when SettingsMode(settings) == "song" => "song",
            _ when settings.Search.IsAggregate => "aggregate",
            _ => "normal",
        };

        return var switch
        {
            "input-type" => settings.Extraction.InputType.ToString().ToLower(),
            "download-mode" => mode,
            "album" => mode is "album" or "album-aggregate",
            "aggregate" => settings.Search.IsAggregate || mode is "aggregate" or "album-aggregate",
            _ when context?.Values.TryGetValue(var, out var value) == true => value,
            _ => throw new Exception($"Input error: Unrecognized profile condition variable '{var}'"),
        };
    }

    private static string SettingsMode(DownloadSettings settings)
        => settings.Extraction.RequestedMode switch
        {
            ExtractionMode.Album => "album",
            ExtractionMode.Song => "song",
            _ when settings.Extraction.InputType is InputType.String or InputType.List or InputType.None => "album",
            _ => "normal",
        };

    [GeneratedRegex(@"(\s+|\(|\)|&&|\|\||==|!=|!|\"".*?\"")")]
    private static partial Regex CondTokenRegex();
}

public static class SettingsCloner
{
    public static EngineSettings Clone(EngineSettings source) => new()
    {
        Username = source.Username,
        Password = source.Password,
        UseRandomLogin = source.UseRandomLogin,
        ListenPort = source.ListenPort,
        ConnectTimeout = source.ConnectTimeout,
        AutoReconnectAfterKickedFromServer = source.AutoReconnectAfterKickedFromServer,
        SharedFiles = source.SharedFiles,
        SharedFolders = source.SharedFolders,
        UserDescription = source.UserDescription,
        NoModifyShareCount = source.NoModifyShareCount,
        ConcurrentJobs = source.ConcurrentJobs,
        ConcurrentSearches = source.ConcurrentSearches,
        ConcurrentExtractors = source.ConcurrentExtractors,
        SearchesPerTime = source.SearchesPerTime,
        SearchRenewTime = source.SearchRenewTime,
        LogLevel = source.LogLevel,
        ReportIntervalProgress = source.ReportIntervalProgress,
        LogFilePath = source.LogFilePath,
        MockFilesDir = source.MockFilesDir,
        MockFilesReadTags = source.MockFilesReadTags,
        MockFilesSlow = source.MockFilesSlow,
        MockFilesFailDownloads = source.MockFilesFailDownloads,
    };

    public static DownloadSettings Clone(DownloadSettings source) => new()
    {
        Output = Clone(source.Output),
        Search = Clone(source.Search),
        Skip = Clone(source.Skip),
        Preprocess = Clone(source.Preprocess),
        Extraction = Clone(source.Extraction),
        Transfer = Clone(source.Transfer),
        Spotify = Clone(source.Spotify),
        YouTube = Clone(source.YouTube),
        YtDlp = Clone(source.YtDlp),
        Csv = Clone(source.Csv),
        Bandcamp = Clone(source.Bandcamp),
        PrintOption = source.PrintOption,
        AppliedAutoProfiles = [.. source.AppliedAutoProfiles],
        RuntimePathContext = source.RuntimePathContext,
    };

    public static OutputSettings Clone(OutputSettings source) => new()
    {
        ParentDir = source.ParentDir,
        NameFormat = source.NameFormat,
        InvalidReplaceStr = source.InvalidReplaceStr,
        WritePlaylist = source.WritePlaylist,
        WriteIndex = source.WriteIndex,
        HasConfiguredIndex = source.HasConfiguredIndex,
        M3uFilePath = source.M3uFilePath,
        IndexFilePath = source.IndexFilePath,
        IncompleteAlbumAction = new IncompleteAlbumActionSettings
        {
            Kind = source.IncompleteAlbumAction.Kind,
            Path = source.IncompleteAlbumAction.Path,
        },
        OnComplete = source.OnComplete?.ToList(),
        AlbumArtOnly = source.AlbumArtOnly,
        AlbumArtOption = source.AlbumArtOption,
    };

    public static SearchSettings Clone(SearchSettings source) => new()
    {
        NecessaryCond = new FileConditions(source.NecessaryCond),
        PreferredCond = new FileConditions(source.PreferredCond),
        NecessaryFolderCond = new FolderConditions(source.NecessaryFolderCond),
        PreferredFolderCond = new FolderConditions(source.PreferredFolderCond),
        StrictAlbumQuality = source.StrictAlbumQuality,
        SearchTimeout = source.SearchTimeout,
        MaxStaleTime = source.MaxStaleTime,
        DownrankOn = source.DownrankOn,
        IgnoreOn = source.IgnoreOn,
        FastSearch = source.FastSearch,
        FastSearchDelay = source.FastSearchDelay,
        FastSearchMinUpSpeed = source.FastSearchMinUpSpeed,
        DesperateSearch = source.DesperateSearch,
        NoRemoveSpecialChars = source.NoRemoveSpecialChars,
        RemoveSingleCharSearchTerms = source.RemoveSingleCharSearchTerms,
        NoBrowseFolder = source.NoBrowseFolder,
        Relax = source.Relax,
        ArtistMaybeWrong = source.ArtistMaybeWrong,
        IsAggregate = source.IsAggregate,
        MinSharesAggregate = source.MinSharesAggregate,
        AggregateLengthTol = source.AggregateLengthTol,
    };

    public static SkipSettings Clone(SkipSettings source) => new()
    {
        SkipExisting = source.SkipExisting,
        SkipNotFound = source.SkipNotFound,
        SkipMode = source.SkipMode,
        SkipMusicDir = source.SkipMusicDir,
        SkipModeMusicDir = source.SkipModeMusicDir,
        SkipCheckCond = source.SkipCheckCond,
        SkipCheckPrefCond = source.SkipCheckPrefCond,
    };

    public static PreprocessSettings Clone(PreprocessSettings source) => new()
    {
        RemoveFt = source.RemoveFt,
        RemoveBrackets = source.RemoveBrackets,
        ExtractArtist = source.ExtractArtist,
        ParseTitleTemplate = source.ParseTitleTemplate,
        Regex = source.Regex?.Select(x => (Clone(x.Item1), Clone(x.Item2))).ToList(),
    };

    private static RegexFields Clone(RegexFields source) => new()
    {
        Title = source.Title,
        Artist = source.Artist,
        Album = source.Album,
    };

    public static ExtractionSettings Clone(ExtractionSettings source) => new()
    {
        Input = source.Input,
        InputType = source.InputType,
        MaxTracks = source.MaxTracks,
        Offset = source.Offset,
        Reverse = source.Reverse,
        RemoveTracksFromSource = source.RemoveTracksFromSource,
        RequestedMode = source.RequestedMode,
        UpgradeToAlbum = source.UpgradeToAlbum,
        SetAlbumMinTrackCount = source.SetAlbumMinTrackCount,
        SetAlbumMaxTrackCount = source.SetAlbumMaxTrackCount,
    };

    public static TransferSettings Clone(TransferSettings source) => new()
    {
        MaxDownloadRetries = source.MaxDownloadRetries,
        UnknownErrorRetries = source.UnknownErrorRetries,
        NoIncompleteExt = source.NoIncompleteExt,
        AlbumTrackCountMaxRetries = source.AlbumTrackCountMaxRetries,
    };

    public static SpotifySettings Clone(SpotifySettings source) => new()
    {
        ClientId = source.ClientId,
        ClientSecret = source.ClientSecret,
        Token = source.Token,
        Refresh = source.Refresh,
    };

    public static YouTubeSettings Clone(YouTubeSettings source) => new()
    {
        ApiKey = source.ApiKey,
        GetDeleted = source.GetDeleted,
        DeletedOnly = source.DeletedOnly,
    };

    public static YtDlpSettings Clone(YtDlpSettings source) => new()
    {
        UseYtdlp = source.UseYtdlp,
        YtdlpArgument = source.YtdlpArgument,
    };

    public static CsvSettings Clone(CsvSettings source) => new()
    {
        ArtistCol = source.ArtistCol,
        AlbumCol = source.AlbumCol,
        TitleCol = source.TitleCol,
        YtIdCol = source.YtIdCol,
        DescCol = source.DescCol,
        TrackCountCol = source.TrackCountCol,
        LengthCol = source.LengthCol,
        TimeUnit = source.TimeUnit,
        YtParse = source.YtParse,
    };

    public static BandcampSettings Clone(BandcampSettings source) => new()
    {
        HtmlFromFile = source.HtmlFromFile,
    };
}
