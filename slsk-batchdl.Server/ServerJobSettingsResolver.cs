using System.Collections.Concurrent;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Settings;

namespace Sldl.Server;

internal sealed class ServerJobSettingsResolver : IJobSettingsResolver
{
    private readonly DownloadSettings baseDefaults;
    private readonly ProfileCatalog catalog;
    private readonly DownloadSettingsPatchDto? launchDownloadSettings;
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> workflowOptions = new();
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> jobOptions = new();
    private readonly ConcurrentDictionary<Guid, string> jobOutputParentDirs = new();

    public ServerJobSettingsResolver(
        DownloadSettings baseDefaults,
        ProfileCatalog catalog,
        DownloadSettingsPatchDto? launchDownloadSettings = null)
    {
        this.baseDefaults = SettingsCloner.Clone(baseDefaults);
        this.catalog = catalog;
        this.launchDownloadSettings = launchDownloadSettings;

        foreach (var profile in catalog.AutoProfiles.Where(p => p.HasEngineSettings))
            throw new Exception($"Input error: Auto-profile '{profile.Name}' contains engine settings, which cannot be applied per job");
    }

    public void SetWorkflowOptions(Guid workflowId, SubmissionOptionsDto? options)
    {
        if (options == null)
        {
            workflowOptions.TryAdd(workflowId, new SubmissionOptionsDto());
            return;
        }

        if (IsWorkflowOnly(options) && workflowOptions.ContainsKey(workflowId))
            return;

        workflowOptions[workflowId] = options;
    }

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
    {
        if (!string.IsNullOrWhiteSpace(outputParentDir))
            jobOutputParentDirs[jobId] = outputParentDir;
    }

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => jobOptions[jobId] = options ?? new SubmissionOptionsDto();

    public DownloadSettings Resolve(DownloadSettings inherited, Job job)
    {
        if (inherited.PrintOption != PrintOption.None)
            return SettingsCloner.Clone(inherited);

        if (!jobOptions.TryGetValue(job.Id, out var options))
            workflowOptions.TryGetValue(job.WorkflowId, out options);
        var context = ToProfileContext(options?.ProfileContext);

        var matchingAutoProfiles = catalog.AutoProfiles
            .Where(p => p.Condition != null && ProfileConditionEvaluator.Satisfied(p.Condition, inherited, job, context))
            .ToList();

        var namedProfiles = catalog.ResolveNamedProfiles(options?.ProfileNames);

        var settings = SettingsCloner.Clone(baseDefaults);
        catalog.DefaultProfile?.Download.ApplyTo(settings);

        foreach (var profile in matchingAutoProfiles)
            profile.Download.ApplyTo(settings);

        foreach (var profile in namedProfiles)
            profile.Download.ApplyTo(settings);

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, launchDownloadSettings);
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);

        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;

        if (jobOutputParentDirs.TryGetValue(job.Id, out var outputParentDir))
            settings.Output.ParentDir = outputParentDir;

        settings.AppliedAutoProfiles = [.. matchingAutoProfiles.Select(p => p.Name)];
        NormalizeForServer(settings);
        return settings;
    }

    public DownloadSettings ResolveFollowUp(Job job, SubmissionOptionsDto? options)
    {
        var context = ToProfileContext(options?.ProfileContext);
        var matchingAutoProfiles = catalog.AutoProfiles
            .Where(p => p.Condition != null && ProfileConditionEvaluator.Satisfied(p.Condition, baseDefaults, job, context))
            .ToList();

        var namedProfiles = catalog.ResolveNamedProfiles(options?.ProfileNames);

        var settings = SettingsCloner.Clone(baseDefaults);
        catalog.DefaultProfile?.Download.ApplyTo(settings);

        foreach (var profile in matchingAutoProfiles)
            profile.Download.ApplyTo(settings);

        foreach (var profile in namedProfiles)
            profile.Download.ApplyTo(settings);

        DownloadSettingsPatchDtoMapper.ApplyTo(settings, launchDownloadSettings);
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);

        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;

        if (jobOutputParentDirs.TryGetValue(job.Id, out var outputParentDir))
            settings.Output.ParentDir = outputParentDir;

        settings.AppliedAutoProfiles = [.. matchingAutoProfiles.Select(p => p.Name)];
        NormalizeForServer(settings);
        return settings;
    }

    public static void NormalizeForServer(DownloadSettings settings)
    {
        SettingsNormalizer.Normalize(settings);

        if (string.IsNullOrWhiteSpace(settings.Output.ParentDir))
            settings.Output.ParentDir = Directory.GetCurrentDirectory();

        settings.Output.ParentDir = Utils.GetFullPath(Utils.ExpandVariables(settings.Output.ParentDir));
        settings.Output.NameFormat = settings.Output.NameFormat.Trim();

        if (settings.Output.M3uFilePath != null)
            settings.Output.M3uFilePath = Utils.GetFullPath(Utils.ExpandVariables(settings.Output.M3uFilePath));
        if (settings.Output.IndexFilePath != null)
            settings.Output.IndexFilePath = Utils.GetFullPath(Utils.ExpandVariables(settings.Output.IndexFilePath));
        if (settings.Skip.SkipMusicDir != null)
            settings.Skip.SkipMusicDir = Utils.GetFullPath(Utils.ExpandVariables(settings.Skip.SkipMusicDir));

        if (settings.Output.FailedAlbumPath == null)
            settings.Output.FailedAlbumPath = Path.Join(settings.Output.ParentDir, "failed");
        else if (settings.Output.FailedAlbumPath is not ("disable" or "delete"))
            settings.Output.FailedAlbumPath = Utils.GetFullPath(Utils.ExpandVariables(settings.Output.FailedAlbumPath));
    }

    private static ProfileContext ToProfileContext(IReadOnlyDictionary<string, bool>? values)
    {
        var context = new ProfileContext();
        if (values == null)
            return context;

        foreach (var (key, value) in values)
            context.Values[key] = value;

        return context;
    }

    private static bool IsWorkflowOnly(SubmissionOptionsDto options)
        => options.OutputParentDir == null
        && options.ProfileNames == null
        && options.ProfileContext == null
        && options.DownloadSettings == null;
}
