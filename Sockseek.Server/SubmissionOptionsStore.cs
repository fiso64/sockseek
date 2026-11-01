using System.Collections.Concurrent;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Server;

public sealed class SubmissionOptionsStore
{
    private readonly ConcurrentDictionary<Guid, WorkflowOptionsEntry> workflowOptions = [];
    private readonly ConcurrentDictionary<Guid, OptionsEntry> jobOptions = [];
    private readonly ConcurrentDictionary<Guid, byte> isolatedJobOptions = [];
    private readonly ConcurrentDictionary<Guid, string> jobOutputParentDirs = [];

    internal SubmissionOptionsStore CreateScope(Guid workflowId)
    {
        var scope = new SubmissionOptionsStore();
        if (workflowOptions.TryGetValue(workflowId, out var entry))
            scope.workflowOptions[workflowId] = entry;
        return scope;
    }

    public void SetWorkflowOptions(Guid workflowId, SubmissionOptionsDto? options)
    {
        if (options == null)
        {
            workflowOptions.TryAdd(
                workflowId,
                new WorkflowOptionsEntry(new OptionsEntry(new SubmissionOptionsDto()), Version: 1));
            return;
        }

        var entry = new OptionsEntry(options);
        workflowOptions.AddOrUpdate(
            workflowId,
            _ => new WorkflowOptionsEntry(entry, Version: 1),
            (_, current) => !HasSettingsOverrides(options)
                ? current
                : new WorkflowOptionsEntry(entry, checked(current.Version + 1)));
    }

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
    {
        isolatedJobOptions.TryRemove(jobId, out _);
        jobOptions[jobId] = new OptionsEntry(options ?? new SubmissionOptionsDto());
    }

    public void SetIsolatedJobOptions(Guid jobId, SubmissionOptionsDto? options)
    {
        jobOptions[jobId] = new OptionsEntry(options ?? new SubmissionOptionsDto());
        isolatedJobOptions[jobId] = 0;
    }

    public void RemoveWorkflowOptions(Guid workflowId)
        => workflowOptions.TryRemove(workflowId, out _);

    public long CaptureWorkflowVersion(Guid workflowId)
        => workflowOptions.TryGetValue(workflowId, out var entry)
            ? entry.Version
            : 0;

    public void RetireWorkflow(
        Guid workflowId,
        IReadOnlyCollection<Guid> jobIds,
        long expectedVersion)
    {
        foreach (Guid jobId in jobIds)
        {
            jobOptions.TryRemove(jobId, out _);
            isolatedJobOptions.TryRemove(jobId, out _);
            jobOutputParentDirs.TryRemove(jobId, out _);
        }

        if (workflowOptions.TryGetValue(workflowId, out var entry)
            && entry.Version == expectedVersion)
        {
            workflowOptions.TryRemove(
                new KeyValuePair<Guid, WorkflowOptionsEntry>(workflowId, entry));
        }
    }

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
    {
        if (!string.IsNullOrWhiteSpace(outputParentDir))
            jobOutputParentDirs[jobId] = outputParentDir;
    }

    public SubmissionOptionsDto? GetOptions(JobSettingsContext job)
    {
        var (workflow, item) = GetEntries(job);
        return Merge(workflow?.Options, item?.Options);
    }

    internal JobSettingsRequestLayers? GetRequestLayers(JobSettingsContext job)
    {
        var (workflow, item) = GetEntries(job);
        string? outputParentDir = GetJobOutputParentDir(job.Id);
        if (outputParentDir != null || (workflow != null && item != null))
            return SubmissionOptionsJobSettingsResolver.CreateRequestLayers(
                Merge(workflow?.Options, item?.Options), outputParentDir);
        return (item ?? workflow)?.RequestLayers.Value;
    }

    private (OptionsEntry? Workflow, OptionsEntry? Item) GetEntries(JobSettingsContext job)
    {
        workflowOptions.TryGetValue(job.WorkflowId, out var workflowEntry);
        jobOptions.TryGetValue(job.Id, out var itemOptions);
        if (isolatedJobOptions.ContainsKey(job.Id))
            return (null, itemOptions);
        return (workflowEntry?.Entry, itemOptions);
    }

    public string? GetJobOutputParentDir(Guid jobId)
        => jobOutputParentDirs.TryGetValue(jobId, out var outputParentDir)
            ? outputParentDir
            : null;

    internal (int Workflows, int Jobs, int OutputPaths) RetainedStateCounts
        => (workflowOptions.Count, jobOptions.Count, jobOutputParentDirs.Count);

    internal static SubmissionOptionsDto? Merge(
        SubmissionOptionsDto? submission,
        SubmissionOptionsDto? item)
    {
        if (submission == null)
            return item;
        if (item == null)
            return submission;

        IReadOnlyDictionary<string, bool>? context = submission.ProfileContext;
        if (item.ProfileContext != null)
        {
            var merged = submission.ProfileContext == null
                ? new Dictionary<string, bool>(StringComparer.Ordinal)
                : new Dictionary<string, bool>(submission.ProfileContext, StringComparer.Ordinal);
            foreach (var (key, value) in item.ProfileContext)
                merged[key] = value;
            context = merged;
        }

        return new SubmissionOptionsDto(
            item.WorkflowId ?? submission.WorkflowId,
            item.OutputParentDir ?? submission.OutputParentDir,
            item.ProfileNames ?? submission.ProfileNames,
            context,
            DownloadSettingsPatchDtoMapper.Combine(
                submission.DownloadSettings,
                item.DownloadSettings));
    }

    public static bool HasSettingsOverrides(SubmissionOptionsDto options)
        => options.OutputParentDir != null
        || options.ProfileNames != null
        || options.ProfileContext != null
        || options.DownloadSettings != null;

    // Request layers are read-only during composition. Keep their conversion with
    // the options' existing lifetime so ordinary children reuse one workflow patch,
    // including its reflected provenance fields and profile context. A replacement
    // gets a new entry; planning scopes retain the exact generation they captured.
    private sealed class OptionsEntry(SubmissionOptionsDto options)
    {
        public SubmissionOptionsDto Options { get; } = options;
        public Lazy<JobSettingsRequestLayers> RequestLayers { get; } = new(
            () => SubmissionOptionsJobSettingsResolver.CreateRequestLayers(options));
    }

    private sealed record WorkflowOptionsEntry(OptionsEntry Entry, long Version);
}

public sealed class SubmissionOptionsJobSettingsResolver(
    IJobSettingsResolver inner,
    SubmissionOptionsStore? optionsStore = null,
    Action<DownloadSettings>? normalize = null)
    : IJobSettingsResolver, IWorkflowSettingsLifetime
{
    public SubmissionOptionsStore Options { get; } = optionsStore ?? new SubmissionOptionsStore();

    // Planning owns temporary request options, not execution's workflow state.
    // Reuse the composer/normalizer with an independent options store so every
    // exit path releases the request without retiring another workflow's options.
    public SubmissionOptionsJobSettingsResolver CreateScope(Guid workflowId)
        => new(inner, Options.CreateScope(workflowId), normalize);

    public void SetWorkflowOptions(Guid workflowId, SubmissionOptionsDto? options)
        => Options.SetWorkflowOptions(workflowId, options);

    public void SetJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => Options.SetJobOptions(jobId, options);

    public void SetIsolatedJobOptions(Guid jobId, SubmissionOptionsDto? options)
        => Options.SetIsolatedJobOptions(jobId, options);

    public void SetJobOutputParentDir(Guid jobId, string? outputParentDir)
        => Options.SetJobOutputParentDir(jobId, outputParentDir);

    public void RemoveWorkflowOptions(Guid workflowId)
        => Options.RemoveWorkflowOptions(workflowId);

    public long CaptureWorkflowVersion(Guid workflowId)
        => Options.CaptureWorkflowVersion(workflowId);

    public void RetireWorkflow(Guid workflowId, IReadOnlyCollection<Guid> jobIds, long expectedVersion)
        => Options.RetireWorkflow(workflowId, jobIds, expectedVersion);

    public DownloadSettings Resolve(
        DownloadSettings inherited,
        JobSettingsContext job,
        JobSettingsInheritance inheritance = JobSettingsInheritance.None)
    {
        JobSettingsRequestLayers? request = Options.GetRequestLayers(job);
        if (inner is IJobSettingsRequestResolver requestResolver)
        {
            return requestResolver.Resolve(
                inherited,
                job,
                inheritance,
                request);
        }

        var settings = inner.Resolve(inherited, job, inheritance);
        if (inheritance == JobSettingsInheritance.SearchConstraints)
            JobSettingsComposer.PreserveInheritedSearchConstraints(settings, inherited);
        request?.Download?.ApplyTo(settings);
        normalize?.Invoke(settings);
        return settings;
    }

    public DownloadSettings ResolveFollowUp(Job job, SubmissionOptionsDto? options)
        => ResolveWithOptions(
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic),
            job,
            options,
            JobSettingsInheritance.None);

    public void ApplyManualSelectionOptions(Job source, Job selected, SubmissionOptionsDto? options)
    {
        if (options == null || !SubmissionOptionsStore.HasSettingsOverrides(options))
            return;
        selected.PlannedSettings = JobSettingsSnapshot.Capture(ResolveWithOptions(
            selected.Config,
            selected,
            SubmissionOptionsStore.Merge(Options.GetOptions(source), options),
            JobSettingsInheritance.SearchConstraints));
    }

    public JobSettingsCompositionResult ResolveDetailed(
        JobSettingsContext job,
        SubmissionOptionsDto? options)
        => ResolveDetailed(
            SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic),
            job,
            options,
            JobSettingsInheritance.None);

    public JobSettingsCompositionResult ResolveDetailed(
        DownloadSettings inherited,
        JobSettingsContext job,
        SubmissionOptionsDto? options,
        JobSettingsInheritance inheritance)
    {
        if (inner is not IDetailedJobSettingsRequestResolver detailed)
        {
            throw new NotSupportedException(
                $"{inner.GetType().Name} does not expose detailed settings composition.");
        }

        return detailed.ResolveDetailed(
            inherited,
            job,
            inheritance,
            CreateRequestLayers(options));
    }

    internal DownloadSettings ResolveWithOptions(
        DownloadSettings inherited,
        JobSettingsContext job,
        SubmissionOptionsDto? options,
        JobSettingsInheritance inheritance)
    {
        if (inner is IJobSettingsRequestResolver requestResolver)
        {
            return requestResolver.Resolve(
                inherited,
                job,
                inheritance,
                CreateRequestLayers(options));
        }

        DownloadSettings settings = inner.Resolve(inherited, job, inheritance);
        ApplyTo(settings, options, outputParentDir: null);
        normalize?.Invoke(settings);
        return settings;
    }

    internal static JobSettingsRequestLayers CreateRequestLayers(
        SubmissionOptionsDto? options,
        string? outputParentDir = null)
    {
        var operations = new List<SettingChange>(DownloadSettingsPatchDtoMapper.ToCore(options?.DownloadSettings).Operations);
        string? parent = outputParentDir ?? options?.OutputParentDir;
        if (!string.IsNullOrWhiteSpace(parent)) operations.Add(new("Output.ParentDir", parent));
        var requestPatch = new SettingsPatch(operations);
        return new JobSettingsRequestLayers(
            options?.ProfileNames,
            ToProfileContext(options?.ProfileContext),
            requestPatch);
    }

    private static void ApplyTo(
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        string? outputParentDir)
    {
        DownloadSettingsPatchDtoMapper.ApplyTo(settings, options?.DownloadSettings);
        if (!string.IsNullOrWhiteSpace(options?.OutputParentDir))
            settings.Output.ParentDir = options.OutputParentDir;
        if (outputParentDir != null)
            settings.Output.ParentDir = outputParentDir;
    }

    private static ProfileContext ToProfileContext(IReadOnlyDictionary<string, bool>? values)
    {
        var context = new ProfileContext();
        if (values != null)
        {
            foreach (var (key, value) in values)
                context.Values[key] = value;
        }

        return context;
    }
}
