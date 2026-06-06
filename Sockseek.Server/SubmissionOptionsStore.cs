using System.Collections.Concurrent;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

public sealed class SubmissionOptionsStore
{
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> workflowOptions = [];
    private readonly ConcurrentDictionary<Guid, SubmissionOptionsDto> jobOptions = [];
    private readonly ConcurrentDictionary<Guid, string> jobOutputParentDirs = [];

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

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => jobOptions[jobId] = options ?? new SubmissionOptionsDto();

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
    {
        if (!string.IsNullOrWhiteSpace(outputParentDir))
            jobOutputParentDirs[jobId] = outputParentDir;
    }

    public SubmissionOptionsDto? GetOptions(Job job)
    {
        if (jobOptions.TryGetValue(job.Id, out var options))
            return options;

        return workflowOptions.TryGetValue(job.WorkflowId, out options)
            ? options
            : null;
    }

    public string? GetJobOutputParentDir(Guid jobId)
        => jobOutputParentDirs.TryGetValue(jobId, out var outputParentDir)
            ? outputParentDir
            : null;

    public void ApplyTo(DownloadSettings settings, SubmissionOptionsDto? options, Guid jobId)
    {
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);

        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;

        if (GetJobOutputParentDir(jobId) is { } outputParentDir)
            settings.Output.ParentDir = outputParentDir;
    }

    public static void PreserveInheritedSearchConstraints(DownloadSettings settings, DownloadSettings inherited)
    {
        settings.Search.NecessaryCond = settings.Search.NecessaryCond.With(inherited.Search.NecessaryCond);
        settings.Search.PreferredCond = settings.Search.PreferredCond.With(inherited.Search.PreferredCond);
        settings.Search.NecessaryFolderCond = MergeFolderConditions(settings.Search.NecessaryFolderCond, inherited.Search.NecessaryFolderCond);
        settings.Search.PreferredFolderCond = MergeFolderConditions(settings.Search.PreferredFolderCond, inherited.Search.PreferredFolderCond);
    }

    private static FolderConditions MergeFolderConditions(FolderConditions current, FolderConditions inherited)
    {
        var result = new FolderConditions(current)
        {
            MinTrackCount = inherited.MinTrackCount ?? current.MinTrackCount,
            MaxTrackCount = inherited.MaxTrackCount ?? current.MaxTrackCount,
        };
        result.AddRequiredTrackTitles(inherited.RequiredTrackTitles);
        return result;
    }

    private static bool IsWorkflowOnly(SubmissionOptionsDto options)
        => options.OutputParentDir == null
        && options.ProfileNames == null
        && options.ProfileContext == null
        && options.DownloadSettings == null;
}

public sealed class SubmissionOptionsJobSettingsResolver(
    IJobSettingsResolver inner,
    SubmissionOptionsStore? optionsStore = null,
    Action<DownloadSettings>? normalize = null)
    : IJobSettingsResolver
{
    public SubmissionOptionsStore Options { get; } = optionsStore ?? new SubmissionOptionsStore();

    public void SetWorkflowOptions(Guid workflowId, SubmissionOptionsDto? options)
        => Options.SetWorkflowOptions(workflowId, options);

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => Options.SetJobOptions(jobId, options);

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
        => Options.SetJobOutputParentDir(jobId, outputParentDir);

    public DownloadSettings Resolve(DownloadSettings inherited, Job job)
    {
        var settings = inner.Resolve(inherited, job);
        SubmissionOptionsStore.PreserveInheritedSearchConstraints(settings, inherited);
        Options.ApplyTo(settings, Options.GetOptions(job), job.Id);
        normalize?.Invoke(settings);
        return settings;
    }
}
