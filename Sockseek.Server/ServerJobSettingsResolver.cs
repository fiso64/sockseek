using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;
using Sockseek.Api;

namespace Sockseek.Server;

internal sealed class ServerJobSettingsResolver : IJobSettingsResolver
{
    private readonly DownloadSettings baseDefaults;
    private readonly ProfileCatalog catalog;
    private readonly DownloadSettingsPatchDto? launchDownloadSettings;
    private readonly PathVariableContext pathContext;
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> workflowOptions = new();
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> jobOptions = new();
    private readonly ConcurrentDictionary<Guid, string> jobOutputParentDirs = new();

    public ServerJobSettingsResolver(
        DownloadSettings baseDefaults,
        ProfileCatalog catalog,
        DownloadSettingsPatchDto? launchDownloadSettings = null,
        PathVariableContext? pathContext = null)
    {
        this.baseDefaults = SettingsCloner.Clone(baseDefaults);
        this.catalog = catalog;
        this.launchDownloadSettings = launchDownloadSettings;
        this.pathContext = pathContext ?? PathVariableContext.Empty;

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
        NormalizeForServer(settings, pathContext);
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
        NormalizeForServer(settings, pathContext);
        return settings;
    }

    public static void NormalizeForServer(DownloadSettings settings, PathVariableContext? pathContext = null)
    {
        SettingsNormalizer.NormalizeDownloadPaths(settings, pathContext ?? PathVariableContext.Empty);
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
