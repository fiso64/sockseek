using System.Text.RegularExpressions;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Sharing;
using Sockseek.Core.Chat;
using Sockseek.Core.Extractors;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;

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
    private readonly HashSet<string> _explicitFields = new(StringComparer.Ordinal);
    public bool HasOperations => _operations.Count > 0;
    public IReadOnlySet<string> ExplicitFields => _explicitFields;

    public void Add(Action<DownloadSettings> operation, IEnumerable<string>? explicitFields = null)
    {
        _operations.Add(operation);
        if (explicitFields != null)
            _explicitFields.UnionWith(explicitFields);
    }

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

/// <summary>Returns fresh caller-owned settings without changing the inherited value.</summary>
public interface IJobSettingsResolver
{
    DownloadSettings Resolve(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None);
}

/// <summary>
/// Optional lifetime hook for daemon resolvers that retain per-workflow or
/// per-job submission state. The version prevents an old workflow generation
/// from deleting options already supplied for a later generation with the same
/// workflow ID.
/// </summary>
public interface IWorkflowSettingsLifetime
{
    long CaptureWorkflowVersion(Guid workflowId);
    void RetireWorkflow(Guid workflowId, IReadOnlyCollection<Guid> jobIds, long expectedVersion);
}

public sealed class DefaultJobSettingsResolver : IJobSettingsResolver
{
    public static DefaultJobSettingsResolver Instance { get; } = new();

    private DefaultJobSettingsResolver() { }

    public DownloadSettings Resolve(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None) =>
        SettingsCloner.Clone(inherited);
}

public sealed class ProfileJobSettingsResolver : IJobSettingsResolver, IDetailedJobSettingsRequestResolver
{
    private readonly JobSettingsComposer composer;

    public ProfileJobSettingsResolver(
        DownloadSettings? baseDefaults,
        ProfileCatalog catalog,
        IReadOnlyList<SettingsProfile> namedProfiles,
        SettingsProfile? cliProfile,
        ProfileContext? context = null,
        Action<DownloadSettings>? normalize = null,
        DownloadSettingsPatch? operatorDefault = null)
    {
        composer = new JobSettingsComposer(
            baseDefaults,
            catalog,
            namedProfiles,
            cliProfile?.Download,
            context,
            normalize,
            operatorDefault);
    }

    public ProfileJobSettingsResolver(
        DownloadSettings? baseDefaults,
        SettingsProfile? defaultProfile,
        IReadOnlyList<SettingsProfile> autoProfiles,
        IReadOnlyList<SettingsProfile> namedProfiles,
        SettingsProfile? cliProfile,
        ProfileContext? context = null,
        Action<DownloadSettings>? normalize = null,
        DownloadSettingsPatch? operatorDefault = null)
    {
        var catalog = new ProfileCatalog
        {
            DefaultProfile = defaultProfile,
            AutoProfiles = autoProfiles,
            NamedProfiles = autoProfiles.Concat(namedProfiles)
                .DistinctBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        composer = new JobSettingsComposer(baseDefaults, catalog, namedProfiles,
            cliProfile?.Download, context, normalize, operatorDefault);
    }

    public DownloadSettings Resolve(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None)
        => composer.Compose(inherited, job, inheritance);

    public DownloadSettings Resolve(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance,
        JobSettingsRequestLayers? request)
        => composer.Compose(inherited, job, inheritance, request);

    public JobSettingsCompositionResult ResolveDetailed(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None,
        JobSettingsRequestLayers? request = null)
        => composer.ComposeDetailed(inherited, job, inheritance, request);
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
        if (engine.UserPicturePath != null)
            engine.UserPicturePath = Utils.GetFullPath(Utils.ExpandVariables(engine.UserPicturePath, pathContext));

        SharingSettingsValidator.NormalizeAndValidate(engine, pathContext);
        ChatSettingsValidator.NormalizeAndValidate(engine);
    }
}

public static partial class ProfileConditionEvaluator
{
    private const string GenericFileMode = "generic-file";
    private const string GenericDirectoryMode = "generic-directory";

    public static bool Satisfied(string cond, DownloadSettings settings, Job? job = null, ProfileContext? context = null)
        => SatisfiedForContext(cond, settings, job is null ? (JobSettingsContext?)null : (JobSettingsContext)job, context);

    internal static bool SatisfiedForContext(
        string cond, DownloadSettings settings, JobSettingsContext? job, ProfileContext? context = null)
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
                string? cur = GetVarValue(tok, settings, job, context)?.ToString()?.ToLower();
                return op == "==" ? cur == val : cur != val;
            }

            object? value = GetVarValue(tok, settings, job, context);
            if (value is bool boolean)
                return boolean;
            throw new Exception($"Input error: Profile condition variable '{tok}' requires a comparison");
        }

        var result = ParseExpression();
        if (tokens.Count > 0)
            throw new Exception($"Input error: Unexpected token in profile condition: {tokens.Peek()}");
        return result;
    }

    private static object? GetVarValue(string var, DownloadSettings settings, JobSettingsContext? job, ProfileContext? context)
    {
        InputType inputType = EffectiveInputType(settings, job);

        // download-mode describes a concrete semantic download shape. Internal orchestration
        // job names and source-decided input before extraction are deliberately not modes.
        string? mode = job switch
        {
            { Kind: JobSnapshotKind.Extract, Input: { } source } when SoulseekExtractor.InputMatches(source) =>
                SoulseekMode(
                    source,
                    job.Value.RequestedMode ?? settings.Extraction.RequestedMode),
            { Kind: JobSnapshotKind.Search, SearchProjection: SearchDefaultProjectionKind.Album } => "album",
            { Kind: JobSnapshotKind.Search, SearchProjection: SearchDefaultProjectionKind.Track } => "song",
            { Kind: JobSnapshotKind.AlbumAggregate } => "album-aggregate",
            { Kind: JobSnapshotKind.Album } => "album",
            { Kind: JobSnapshotKind.Aggregate } => "aggregate",
            { Kind: JobSnapshotKind.Song } => "song",
            { Kind: JobSnapshotKind.RemoteFile } => GenericFileMode,
            { Kind: JobSnapshotKind.RemoteDirectory } => GenericDirectoryMode,
            { Kind: JobSnapshotKind.Extract } => SettingsMode(settings, inputType, job.Value.RequestedMode),
            null when inputType == InputType.Soulseek
                && SoulseekExtractor.InputMatches(settings.Extraction.Input ?? "") =>
                SoulseekMode(settings.Extraction.Input!, settings.Extraction.RequestedMode),
            _ => SettingsMode(settings, inputType),
        };

        return var switch
        {
            "input-type" => inputType.ToString().ToLower(),
            "download-mode" => mode,
            "album" => mode is "album" or "album-aggregate",
            "aggregate" => settings.Search.IsAggregate || mode is "aggregate" or "album-aggregate",
            _ when context?.Values.TryGetValue(var, out var value) == true => value,
            _ => throw new Exception($"Input error: Unrecognized profile condition variable '{var}'"),
        };
    }

    private static InputType EffectiveInputType(DownloadSettings settings, JobSettingsContext? job)
    {
        string? input = settings.Extraction.Input;
        InputType configured = job?.SourceInputType
            ?? settings.Extraction.InputType;
        if (job is { Kind: JobSnapshotKind.Extract } extract)
        {
            input = extract.Input;
            if (extract.InputType is { } jobInputType && jobInputType != InputType.None)
                configured = jobInputType;
        }

        return ExtractorRegistry.TryResolveInputType(input, configured, out InputType resolved)
            ? resolved
            : configured;
    }

    private static string SoulseekMode(string input, ExtractionMode? requestedMode)
        => SoulseekExtractor.ClassifyLink(input, requestedMode) switch
        {
            SoulseekLinkInterpretation.RemoteFile => GenericFileMode,
            SoulseekLinkInterpretation.RemoteDirectory => GenericDirectoryMode,
            SoulseekLinkInterpretation.MusicTrack => "song",
            SoulseekLinkInterpretation.MusicAlbum => "album",
            _ => throw new ArgumentOutOfRangeException(),
        };

    private static string? SettingsMode(
        DownloadSettings settings,
        InputType inputType,
        ExtractionMode? requestedModeOverride = null)
    {
        string? mode = (requestedModeOverride ?? settings.Extraction.RequestedMode) switch
        {
            ExtractionMode.Album => "album",
            ExtractionMode.Song => "song",
            _ when settings.Extraction.UpgradeToAlbum => "album",
            _ when inputType is InputType.String or InputType.List => "album",
            _ => null,
        };

        return mode switch
        {
            "album" when settings.Search.IsAggregate => "album-aggregate",
            "song" when settings.Search.IsAggregate => "aggregate",
            _ => mode,
        };
    }

    [GeneratedRegex(@"(\s+|\(|\)|&&|\|\||==|!=|!|\"".*?\"")")]
    private static partial Regex CondTokenRegex();
}

public static class SettingsCloner
{
    public static EngineSettings Clone(EngineSettings source)
    {
        var clone = source.ShallowClone();
        clone.Sharing = source.Sharing.ShallowClone();
        clone.Sharing.Roots = [.. source.Sharing.Roots.Select(root => new ShareRootSettings
        {
            LocalPath = root.LocalPath,
            Alias = root.Alias,
            EffectiveAlias = root.EffectiveAlias,
        })];
        clone.Sharing.ExcludedDirectories = [.. source.Sharing.ExcludedDirectories];
        clone.Sharing.Filters = [.. source.Sharing.Filters];
        clone.Uploads = source.Uploads.ShallowClone();
        clone.PeerRestrictions = source.PeerRestrictions.ShallowClone();
        clone.PeerRestrictions.UploadAccess = source.PeerRestrictions.UploadAccess.ShallowClone();
        clone.PeerRestrictions.UploadAccess.BlockedUsernames =
            [.. source.PeerRestrictions.UploadAccess.BlockedUsernames];
        clone.PeerRestrictions.UploadAccess.BlockedIpAddresses =
            [.. source.PeerRestrictions.UploadAccess.BlockedIpAddresses];
        clone.PeerRestrictions.PrivateMessages = source.PeerRestrictions.PrivateMessages.ShallowClone();
        clone.PeerRestrictions.PrivateMessages.BlockedUsernames =
            [.. source.PeerRestrictions.PrivateMessages.BlockedUsernames];
        clone.Chat = source.Chat.ShallowClone();
        clone.Chat.AutoJoinRooms = [.. source.Chat.AutoJoinRooms];
        return clone;
    }

    public static DownloadSettings Clone(DownloadSettings source) => new(source);

    public static OutputSettings Clone(OutputSettings source)
    {
        var clone = source.ShallowClone();
        clone.IncompleteAlbumAction = new IncompleteAlbumActionSettings
        {
            Kind = source.IncompleteAlbumAction.Kind,
            Path = source.IncompleteAlbumAction.Path,
        };
        clone.OnComplete = source.OnComplete?.ToList();
        return clone;
    }

    public static SearchSettings Clone(SearchSettings source)
    {
        var clone = source.ShallowClone();
        clone.NecessaryCond = new FileConditions(source.NecessaryCond);
        clone.PreferredCond = new FileConditions(source.PreferredCond);
        clone.NecessaryFolderCond = new FolderConditions(source.NecessaryFolderCond);
        clone.PreferredFolderCond = new FolderConditions(source.PreferredFolderCond);
        return clone;
    }

    public static SkipSettings Clone(SkipSettings source) => source.ShallowClone();

    public static PreprocessSettings Clone(PreprocessSettings source)
    {
        var clone = source.ShallowClone();
        clone.Regex = source.Regex?.Select(x => (Clone(x.Item1), Clone(x.Item2))).ToList();
        return clone;
    }

    private static RegexFields Clone(RegexFields source) => new()
    {
        Title = source.Title,
        Artist = source.Artist,
        Album = source.Album,
    };

    public static ExtractionSettings Clone(ExtractionSettings source) => source.ShallowClone();

    public static TransferSettings Clone(TransferSettings source) => source.ShallowClone();

    public static SpotifySettings Clone(SpotifySettings source) => source.ShallowClone();

    public static YouTubeSettings Clone(YouTubeSettings source) => source.ShallowClone();

    public static YtDlpSettings Clone(YtDlpSettings source) => source.ShallowClone();

    public static CsvSettings Clone(CsvSettings source) => source.ShallowClone();

    public static BandcampSettings Clone(BandcampSettings source) => source.ShallowClone();
}
