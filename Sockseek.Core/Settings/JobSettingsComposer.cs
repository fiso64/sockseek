using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Sockseek.Core.Settings;

public enum SearchSettingsBaselineKind
{
    Generic,
    Music,
}

public enum JobSettingsInheritance
{
    None,
    SearchConstraints,
}

/// <summary>
/// Sparse per-submission layers supplied by a CLI or daemon adapter.  Keeping
/// these as patches preserves the distinction between built-in values and
/// values the operator or caller explicitly selected.
/// </summary>
public sealed record JobSettingsRequestLayers(
    IReadOnlyList<string>? ProfileNames = null,
    ProfileContext? ProfileContext = null,
    DownloadSettingsPatch? Download = null);

public sealed record JobSettingsCompositionResult(
    DownloadSettings Settings,
    SearchSettingsBaselineKind Baseline,
    IReadOnlyDictionary<string, string> Provenance);

public interface IJobSettingsRequestResolver
{
    DownloadSettings Resolve(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance,
        JobSettingsRequestLayers? request);
}

public interface IDetailedJobSettingsRequestResolver : IJobSettingsRequestResolver
{
    JobSettingsCompositionResult ResolveDetailed(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None,
        JobSettingsRequestLayers? request = null);
}

public static class SearchSettingsBaselines
{
    public static SearchSettingsBaselineKind For(JobSettingsContext job) => job switch
    {
        { Kind: JobSnapshotKind.Search, SearchProjection: SearchDefaultProjectionKind.Track or SearchDefaultProjectionKind.Album }
            or { Kind: JobSnapshotKind.Search, MusicSearchDefaults: true }
            => SearchSettingsBaselineKind.Music,
        { Kind: JobSnapshotKind.Song or JobSnapshotKind.Album or JobSnapshotKind.Aggregate or JobSnapshotKind.AlbumAggregate }
            => SearchSettingsBaselineKind.Music,
        _ => SearchSettingsBaselineKind.Generic,
    };

    public static DownloadSettings Create(SearchSettingsBaselineKind baseline)
    {
        var settings = new DownloadSettings();
        if (baseline == SearchSettingsBaselineKind.Generic)
        {
            settings.Search.NecessaryCond = new();
            settings.Search.PreferredCond = new();
            settings.Search.NecessaryFolderCond = new();
            settings.Search.PreferredFolderCond = new();
        }

        return settings;
    }
}

/// <summary>
/// Owns the settings precedence shared by local and daemon submissions.
/// Adapters supply sparse launch/request layers and a filesystem-aware finalizer.
/// </summary>
public sealed class JobSettingsComposer
{
    private readonly DownloadSettings? operatorDefaults;
    private readonly DownloadSettingsPatch? operatorDefault;
    private readonly ProfileCatalog catalog;
    private readonly IReadOnlyList<SettingsProfile> launchNamedProfiles;
    private readonly DownloadSettingsPatch? launchDownload;
    private readonly ProfileContext launchContext;
    private readonly Action<DownloadSettings>? finalize;

    public JobSettingsComposer(
        DownloadSettings? operatorDefaults,
        ProfileCatalog catalog,
        IReadOnlyList<SettingsProfile>? launchNamedProfiles = null,
        DownloadSettingsPatch? launchDownload = null,
        ProfileContext? launchContext = null,
        Action<DownloadSettings>? finalize = null,
        DownloadSettingsPatch? operatorDefault = null)
    {
        this.operatorDefaults = operatorDefaults == null
            ? null
            : SettingsCloner.Clone(operatorDefaults);
        this.catalog = catalog;
        this.operatorDefault = operatorDefault;
        this.launchNamedProfiles = launchNamedProfiles ?? [];
        this.launchDownload = launchDownload;
        this.launchContext = launchContext ?? new ProfileContext();
        this.finalize = finalize;

        foreach (var profile in catalog.AutoProfiles.Where(p => p.HasEngineSettings))
            throw new Exception($"Input error: Auto-profile '{profile.Name}' contains engine settings, which cannot be applied per job");
    }

    public DownloadSettings Compose(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None,
        JobSettingsRequestLayers? request = null)
        => ComposeCore(inherited, job, inheritance, request, provenance: null, out _);

    public JobSettingsCompositionResult ComposeDetailed(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None,
        JobSettingsRequestLayers? request = null)
    {
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
        DownloadSettings settings = ComposeCore(
            inherited,
            job,
            inheritance,
            request,
            provenance,
            out SearchSettingsBaselineKind baseline);
        return new JobSettingsCompositionResult(settings, baseline, provenance);
    }

    private DownloadSettings ComposeCore(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance,
        JobSettingsRequestLayers? request,
        Dictionary<string, string>? provenance,
        out SearchSettingsBaselineKind baseline)
    {
        var requestNamedProfiles = catalog.ResolveNamedProfiles(request?.ProfileNames);
        var allNamedProfiles = launchNamedProfiles.Concat(requestNamedProfiles).ToList();
        baseline = SearchSettingsBaselines.For(job);
        IReadOnlyList<SettingsProfile> matchingAutoProfiles = [];
        DownloadSettings? conditionSettings = null;
        if (catalog.AutoProfiles.Count > 0)
        {
            var context = MergeContext(launchContext, request?.ProfileContext);
            // Predicates see the same precedence as execution, without their
            // own patches. Evaluate each once; retain this value if none match.
            conditionSettings = ComposeLayers(inherited, baseline, inheritance,
                [], allNamedProfiles, request?.Download, provenance: null);
            matchingAutoProfiles = catalog.AutoProfiles
                .Where(p => p.Condition != null
                    && ProfileConditionEvaluator.SatisfiedForContext(p.Condition, conditionSettings, job, context))
                .ToArray();
        }

        // Detailed callers still need field provenance from the actual layers.
        // Ordinary jobs need no second composition when auto-profiles do not
        // apply, and no predicate-only settings when no auto-profiles exist.
        var settings = conditionSettings is not null && matchingAutoProfiles.Count == 0 && provenance is null
            ? conditionSettings
            : ComposeLayers(inherited, baseline, inheritance, matchingAutoProfiles,
                allNamedProfiles, request?.Download, provenance);
        settings.AppliedAutoProfiles = [.. matchingAutoProfiles.Select(p => p.Name)];
        finalize?.Invoke(settings);
        return settings;
    }

    private DownloadSettings ComposeLayers(
        DownloadSettings inherited,
        SearchSettingsBaselineKind baseline,
        JobSettingsInheritance inheritance,
        IReadOnlyList<SettingsProfile> autoProfiles,
        IReadOnlyList<SettingsProfile> namedProfiles,
        DownloadSettingsPatch? request,
        Dictionary<string, string>? provenance)
    {
        var settings = CreateBuiltInBase(baseline);
        if (provenance != null)
        {
            string source = operatorDefaults == null ? "built-in" : "operator-default";
            foreach (string field in SettingsFieldSnapshot.Capture(settings).Keys)
                provenance[field] = source;
        }
        ApplyTracked(settings, operatorDefault, "operator-default", provenance);
        ApplyTracked(settings, catalog.DefaultProfile?.Download, "profile", provenance);
        foreach (var profile in autoProfiles)
            ApplyTracked(settings, profile.Download, "profile", provenance);
        foreach (var profile in namedProfiles)
            ApplyTracked(settings, profile.Download, "profile", provenance);
        ApplyTracked(settings, launchDownload, "operator-default", provenance);

        if (inheritance == JobSettingsInheritance.SearchConstraints)
        {
            if (provenance == null)
            {
                PreserveInheritedSearchConstraints(settings, inherited);
            }
            else
            {
                var before = SettingsFieldSnapshot.Capture(settings);
                PreserveInheritedSearchConstraints(settings, inherited);
                RecordChanges(
                    before,
                    settings,
                    "inherited",
                    provenance,
                    new HashSet<string>(StringComparer.Ordinal));
            }
        }
        ApplyTracked(settings, request, "request", provenance);
        return settings;
    }

    public static void PreserveInheritedSearchConstraints(
        DownloadSettings settings,
        DownloadSettings inherited)
    {
        settings.Search.NecessaryCond = settings.Search.NecessaryCond.With(inherited.Search.NecessaryCond);
        settings.Search.PreferredCond = settings.Search.PreferredCond.With(inherited.Search.PreferredCond);
        settings.Search.NecessaryFolderCond = MergeFolderConditions(
            settings.Search.NecessaryFolderCond,
            inherited.Search.NecessaryFolderCond);
        settings.Search.PreferredFolderCond = MergeFolderConditions(
            settings.Search.PreferredFolderCond,
            inherited.Search.PreferredFolderCond);
    }

    private DownloadSettings CreateBuiltInBase(SearchSettingsBaselineKind baseline)
    {
        var settings = operatorDefaults == null
            ? SearchSettingsBaselines.Create(baseline)
            : SettingsCloner.Clone(operatorDefaults);
        return settings;
    }

    private static void ApplyTracked(
        DownloadSettings settings,
        DownloadSettingsPatch? patch,
        string source,
        Dictionary<string, string>? provenance)
    {
        if (patch == null || !patch.HasOperations)
            return;
        if (provenance == null)
        {
            patch.ApplyTo(settings);
            return;
        }
        var before = SettingsFieldSnapshot.Capture(settings);
        patch.ApplyTo(settings);
        RecordChanges(before, settings, source, provenance, patch.ExplicitFields);
    }

    private static void RecordChanges(
        IReadOnlyDictionary<string, string?> before,
        DownloadSettings settings,
        string source,
        Dictionary<string, string> provenance,
        IReadOnlySet<string> explicitFields)
    {
        var after = SettingsFieldSnapshot.Capture(settings);
        foreach (var (field, value) in after)
        {
            if (!before.TryGetValue(field, out string? previous)
                || !string.Equals(previous, value, StringComparison.Ordinal)
                || explicitFields.Contains(field))
            {
                provenance[field] = source;
            }
        }
        foreach (string field in explicitFields)
            provenance[field] = source;
    }

    private static ProfileContext MergeContext(ProfileContext baseline, ProfileContext? request)
    {
        var result = new ProfileContext();
        foreach (var (key, value) in baseline.Values)
            result.Values[key] = value;
        if (request != null)
        {
            foreach (var (key, value) in request.Values)
                result.Values[key] = value;
        }

        return result;
    }

    private static FolderConditions MergeFolderConditions(
        FolderConditions current,
        FolderConditions inherited)
    {
        var result = new FolderConditions(current)
        {
            MinTrackCount = inherited.MinTrackCount ?? current.MinTrackCount,
            MaxTrackCount = inherited.MaxTrackCount ?? current.MaxTrackCount,
        };
        result.AddRequiredTrackTitles(inherited.RequiredTrackTitles);
        return result;
    }
}

internal static class SettingsFieldSnapshot
{
    private static readonly string[] RootSections =
    [
        nameof(DownloadSettings.Output),
        nameof(DownloadSettings.Search),
        nameof(DownloadSettings.Skip),
        nameof(DownloadSettings.Preprocess),
        nameof(DownloadSettings.Extraction),
        nameof(DownloadSettings.Transfer),
        nameof(DownloadSettings.Spotify),
        nameof(DownloadSettings.YouTube),
        nameof(DownloadSettings.YtDlp),
        nameof(DownloadSettings.Csv),
        nameof(DownloadSettings.Bandcamp),
        nameof(DownloadSettings.PrintOption),
    ];

    public static Dictionary<string, string?> Capture(DownloadSettings settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        Type type = typeof(DownloadSettings);
        foreach (string section in RootSections)
        {
            PropertyInfo property = type.GetProperty(section)!;
            Walk(property.GetValue(settings), section, values);
        }
        return values;
    }

    private static void Walk(object? value, string path, Dictionary<string, string?> values)
    {
        if (value == null || IsScalar(value.GetType()) || value is IEnumerable)
        {
            values[path] = StableValue(value);
            return;
        }

        Type type = value.GetType();
        var members = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            .OrderBy(member => member.Name, StringComparer.Ordinal);
        foreach (MemberInfo member in members)
        {
            object? child = member switch
            {
                PropertyInfo property => property.GetValue(value),
                FieldInfo field => field.GetValue(value),
                _ => null,
            };
            Walk(child, $"{path}.{member.Name}", values);
        }
    }

    private static bool IsScalar(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTimeOffset)
            || Nullable.GetUnderlyingType(type) is { } underlying && IsScalar(underlying);

    private static string? StableValue(object? value)
    {
        if (value == null)
            return null;
        if (value is string text)
            return text;
        if (value is IFormattable formattable && value is not IEnumerable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(value, value.GetType());
    }
}
