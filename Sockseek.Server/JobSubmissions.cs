using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Core.Snapshots;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;
using Sockseek.Server.Planning;

namespace Sockseek.Server;

/// <summary>
/// Composes submission settings, invokes the shared Core planner, and durably
/// accepts exact work before handing it to execution. Review supplies its captured
/// plan; accepting it never runs extraction again. Runtime queue ownership stays
/// with EngineSupervisor, including the handoff across engine restarts.
/// </summary>
public sealed class JobSubmissions
{
    private static readonly ActivitySource SubmissionActivities = new("Sockseek.Server.Submissions");
    private readonly ServerOptions options;
    private readonly EngineSettings engineSettings;
    private readonly DownloadSettings defaultDownloadSettings;
    private readonly ProfileCatalog profileCatalog;
    private readonly SubmissionOptionsJobSettingsResolver jobSettingsResolver;
    private readonly PersistenceCoordinator? persistence;
    private readonly EngineStateStore StateStore;
    private readonly DaemonRuntime runtime;
    private readonly Action<Job, DownloadSettings, Guid?, bool> enqueue;
    private readonly InputArtifactCoordinator? inputArtifacts;
    private readonly ILogger<JobSubmissions> logger;

    internal DownloadSettings DefaultDownloadSettings => defaultDownloadSettings;
    internal SubmissionOptionsJobSettingsResolver SettingsResolver => jobSettingsResolver;

    internal JobSubmissions(ServerOptions options, EngineSettings engineSettings,
        PersistenceCoordinator? persistence, EngineStateStore stateStore, DaemonRuntime runtime,
        Action<Job, DownloadSettings, Guid?, bool> enqueue, InputArtifactCoordinator? inputArtifacts,
        ILoggerFactory loggerFactory)
    {
        this.options = options;
        this.engineSettings = engineSettings;
        this.persistence = persistence;
        StateStore = stateStore;
        this.runtime = runtime;
        this.enqueue = enqueue;
        this.inputArtifacts = inputArtifacts;
        logger = loggerFactory.CreateLogger<JobSubmissions>();
        defaultDownloadSettings = options.DefaultDownload == null
            ? SearchSettingsBaselines.Create(SearchSettingsBaselineKind.Generic)
            : SettingsCloner.Clone(options.DefaultDownload);
        DownloadSettingsPatchDtoMapper.ApplyTo(defaultDownloadSettings, options.OperatorDownloadSettings);
        defaultDownloadSettings.DefaultMinSharesAggregate = engineSettings.DefaultMinSharesAggregate;
        var pathContext = new PathVariableContext(ConfigDir: options.ConfigDir);
        SettingsNormalizer.NormalizeDownloadPaths(defaultDownloadSettings, pathContext);
        profileCatalog = options.Profiles ?? ProfileCatalog.Empty;
        jobSettingsResolver = CreateJobSettingsResolver(options, profileCatalog, pathContext);
    }

    private static JobSummaryDto BuildSubmittedJobSummary(Job job, Guid? sourceJobId = null)
        => ServerSnapshotMapper.ToSubmittedJobSummary(job, sourceJobId);

    private static SubmissionOptionsJobSettingsResolver CreateJobSettingsResolver(
        ServerOptions options,
        ProfileCatalog profiles,
        PathVariableContext pathContext)
    {
        SettingsPatch launchPatch = DownloadSettingsPatchDtoMapper.ToCore(options.LaunchDownloadSettings);
        SettingsPatch operatorPatch = DownloadSettingsPatchDtoMapper.ToCore(options.OperatorDownloadSettings);
        var profilesResolver = new ProfileJobSettingsResolver(
            options.DefaultDownload,
            profiles,
            profiles.ResolveNamedProfiles(options.LaunchProfileNames),
            new SettingsProfile { Name = "<daemon-launch>", Download = launchPatch },
            normalize: settings =>
            {
                settings.DefaultMinSharesAggregate = options.Engine.DefaultMinSharesAggregate;
                SettingsNormalizer.NormalizeDownloadPaths(settings, pathContext);
            },
            operatorDefault: operatorPatch);
        return new SubmissionOptionsJobSettingsResolver(profilesResolver);
    }

    public IReadOnlyList<ProfileSummaryDto> GetProfiles()
        => profileCatalog.NamedProfiles
            .Select(profile => new ProfileSummaryDto(
                profile.Name,
                profile.Condition,
                profile.Condition != null,
                profile.HasEngineSettings,
                profile.HasDownloadSettings))
            .OrderBy(profile => profile.Name)
            .ToList();

    public async Task<JobSummaryDto> SubmitExtractJobAsync(
        SubmitExtractJobRequestDto request,
        CancellationToken ct)
    {
        if (request.ArtifactId == null)
        {
            var resolved = ResolveExtractInput(new ExtractJobDraftDto(request.Input, request.InputType), request.Options);
            request = request with { Input = resolved.Input, InputType = resolved.InputType };
            return await SubmitJobAsync(
                JobRequestMapper.CreateExtractJob(request),
                request.Options,
                ct).ConfigureAwait(false);
        }
        if (inputArtifacts == null)
            throw new InputArtifactUnavailableException(
                "Input artifacts are unavailable; ordinary inputs remain available.");

        Guid submissionId = Guid.NewGuid();
        IReadOnlyDictionary<string, Sockseek.Persistence.Planning.ResolvedInputArtifact> inputs =
            await inputArtifacts.RetainAsync([request.ArtifactId], submissionId, ct)
                .ConfigureAwait(false);
        try
        {
            var lease = inputs[request.ArtifactId];
            string inputType = request.InputType
                ?? (string.Equals(
                        Path.GetExtension(lease.Artifact.OriginalName),
                        ".csv",
                        StringComparison.OrdinalIgnoreCase)
                    ? InputType.CSV.ToString()
                    : InputType.List.ToString());
            var job = JobRequestMapper.CreateExtractJob(request with
            {
                Input = lease.Path,
                InputType = inputType,
            });
            job.PlannedSourceRevision = InputArtifactCoordinator.Revision(lease);
            var immutablePatch = new DownloadSettingsPatchDto(
                Extraction: new ExtractionSettingsPatchDto(RemoveTracksFromSource: false));
            SubmissionOptionsDto options = (request.Options ?? new SubmissionOptionsDto()) with
            {
                DownloadSettings = DownloadSettingsPatchDtoMapper.Combine(
                    request.Options?.DownloadSettings,
                    immutablePatch),
            };
            return await SubmitJobAsync(
                job,
                options,
                ct,
                submissionId: submissionId,
                sourceRevision: job.PlannedSourceRevision,
                artifactId: request.ArtifactId).ConfigureAwait(false);
        }
        finally
        {
            // Acceptance stores the extracted input and source revision. Execution
            // and rerun no longer need the upload; failed admission releases it too.
            try
            {
                await inputArtifacts.ReleaseAsync(submissionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ServerLogMessages.SubmissionArtifactReleaseFailed(logger, exception, submissionId);
            }
        }
    }

    public Task<JobSummaryDto> SubmitSearchJobAsync(SubmitSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitTrackSearchJobAsync(SubmitTrackSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateTrackSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumSearchJobAsync(SubmitAlbumSearchJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumSearchJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitSongJobAsync(SubmitSongJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateSongJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumJobAsync(SubmitAlbumJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAggregateJobAsync(SubmitAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitAlbumAggregateJobAsync(SubmitAlbumAggregateJobRequestDto request, CancellationToken ct)
        => SubmitJobAsync(JobRequestMapper.CreateAlbumAggregateJob(request), request.Options, ct);

    public Task<JobSummaryDto> SubmitJobListAsync(SubmitJobListRequestDto request, CancellationToken ct)
    {
        var job = JobRequestMapper.CreateJobList(request);
        JobRequestMapper.ApplyDraftDownloadSettings(
            job,
            request.Jobs,
            (item, patch) => jobSettingsResolver.SetJobOptions(
                item.Id,
                new SubmissionOptionsDto(DownloadSettings: patch)));
        return SubmitJobAsync(job, request.Options, ct, request.Jobs);
    }

    public Task<StartUserShareDownloadsResponseDto> SubmitUserShareDownloadsAsync(
        string username,
        PeerBrowseDownloadResolution resolution,
        SubmissionOptionsDto? options,
        Guid submissionId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        EnsureShareSubmissionConnection();
        JobList workflow = CreateUserShareWorkflow(username, resolution);
        return SubmitJobAsync(workflow, options, cancellationToken,
            (summary, settings) => new StartUserShareDownloadsResponseDto(summary, new(
                resolution.CanonicalDirectoryRoots, resolution.StandaloneFiles,
                resolution.TotalPublicFiles, resolution.TotalPublicBytes,
                resolution.RedundantSelectionsRemoved, resolution.LockedBranchesSkipped,
                settings.Output.ParentDir ?? throw new InvalidOperationException("The output parent was not resolved."))),
            submissionId: submissionId, commitFingerprint: fingerprint);
    }

    private static JobList CreateUserShareWorkflow(
        string username,
        PeerBrowseDownloadResolution resolution)
    {
        username = PeerUsername.Validate(username);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Plans.Count == 0)
            throw new PeerBrowseSelectionException("The selection contains no downloadable public files.");
        return new JobList(
            $"Shares from {username}",
            resolution.Plans.Select(plan =>
                (Job)new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan))));
    }

    private void EnsureShareSubmissionConnection()
    {
        if (runtime.SoulseekSession?.IsConnectedAndLoggedIn != true)
            throw new InvalidOperationException("Soulseek is not connected.");
    }

    public ResolveEffectiveSettingsResponseDto ResolveEffectiveSettings(
        ResolveEffectiveSettingsRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Job);
        JobSettingsContext job = JobRequestMapper.CreateSettingsContext(request.Job);
        bool isProvisional = request.Job is ExtractJobDraftDto
        {
            Input: var input,
            ArtifactId: null,
        } && string.IsNullOrWhiteSpace(input)
            || job.Kind == JobSnapshotKind.Search && string.IsNullOrWhiteSpace(job.QueryText);
        DownloadSettingsPatchDto? combinedPatch = DownloadSettingsPatchDtoMapper.Combine(
            request.Options?.DownloadSettings,
            JobRequestMapper.DraftDownloadSettings(request.Job));
        SubmissionOptionsDto requestOptions = (request.Options ?? new SubmissionOptionsDto()) with
        {
            DownloadSettings = combinedPatch,
        };
        JobSettingsCompositionResult result = jobSettingsResolver.ResolveDetailed(job, requestOptions);
        bool containsRemoteTransfer = ValidateDraftSubmissionSettings(
            job,
            request.Job,
            result.Settings,
            requestOptions,
            combinedPatch);
        if (containsRemoteTransfer)
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
        return EffectiveSettingsMapper.ToDto(result, requestOptions.ProfileNames) with
        {
            IsProvisional = isProvisional,
        };
    }

    private bool ValidateDraftSubmissionSettings(
        JobSettingsContext job,
        JobDraftDto draft,
        DownloadSettings effectiveSettings,
        SubmissionOptionsDto submissionOptions,
        DownloadSettingsPatchDto? explicitPatch)
    {
        bool containsRemoteTransfer = RemoteTransferSubmissionPolicy
            .IsOrdinaryRemoteTransfer(job, effectiveSettings);
        if (containsRemoteTransfer)
            RemoteTransferNameFormatPolicy.ApplyInherited(effectiveSettings.Output);

        if (draft is JobListJobDraftDto listDraft)
        {
            foreach (JobDraftDto childDraft in listDraft.Jobs)
            {
                JobSettingsContext child = JobRequestMapper.CreateSettingsContext(childDraft, allowMissingInput: false);
                DownloadSettingsPatchDto? childPatch = JobRequestMapper.DraftDownloadSettings(childDraft);
                SubmissionOptionsDto? childOptions = SubmissionOptionsStore.Merge(
                    submissionOptions,
                    childPatch == null
                        ? null
                        : new SubmissionOptionsDto(DownloadSettings: childPatch));
                DownloadSettings childSettings = jobSettingsResolver.ResolveWithOptions(
                    effectiveSettings,
                    child,
                    childOptions,
                    JobSettingsInheritance.SearchConstraints);
                containsRemoteTransfer |= ValidateDraftSubmissionSettings(
                    child,
                    childDraft,
                    childSettings,
                    submissionOptions,
                    childPatch);
            }
        }

        if (containsRemoteTransfer && explicitPatch != null)
            RemoteTransferSettingsValidator.ValidateExplicitPatch(explicitPatch);
        return containsRemoteTransfer;
    }

    private Task<JobSummaryDto> SubmitJobAsync(
        Job job,
        SubmissionOptionsDto? options,
        CancellationToken ct,
        IReadOnlyList<JobDraftDto>? childDrafts = null,
        Guid? submissionId = null,
        SubmissionSourceRevision? sourceRevision = null,
        string? artifactId = null)
        => SubmitJobAsync(job, options, ct, static (summary, _) => summary,
            childDrafts, submissionId, sourceRevision, artifactId);

    private async Task<TReceipt> SubmitJobAsync<TReceipt>(
        Job job,
        SubmissionOptionsDto? options,
        CancellationToken ct,
        Func<JobSummaryDto, DownloadSettings, TReceipt> createReceipt,
        IReadOnlyList<JobDraftDto>? childDrafts = null,
        Guid? submissionId = null,
        SubmissionSourceRevision? sourceRevision = null,
        string? artifactId = null,
        string? commitFingerprint = null)
    {
        using var activity = SubmissionActivities.StartActivity("submission.accept");
        activity?.SetTag("job.id", job.Id);
        ct.ThrowIfCancellationRequested();

        if (options?.WorkflowId is Guid workflowId)
            job.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        jobSettingsResolver.SetWorkflowOptions(job.WorkflowId, options);

        var settings = jobSettingsResolver.Resolve(defaultDownloadSettings, job);

        ValidateExplicitRemoteTransferOverrides(
            job, options?.DownloadSettings, settings, childDrafts: childDrafts);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);

        if (!CanAcceptLoginRequiredJobs() && ContainsLoginRequiredJob(job, settings, jobSettingsResolver))
            throw new ArgumentException("This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");
        activity?.AddEvent(new("prepared"));

        await new JobPlanner(jobSettingsResolver).PrepareResolvedAsync(job, settings, ct).ConfigureAwait(false);
        activity?.AddEvent(new("planned"));
        SubmissionSpecification specification = SubmissionSpecification.Create(job);
        SubmissionIdentity.AssignAccepted(
            job,
            sourceRevision is null ? specification : specification with { SourceRevision = sourceRevision },
            submissionId,
            artifactId: artifactId);
        activity?.AddEvent(new("captured"));
        job.EnsureDisplayId();
        TReceipt receipt = createReceipt(BuildSubmittedJobSummary(job), settings);
        await AcceptAsync(
            job,
            settings,
            sourceJobId: null,
            ct,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint,
            commitReceipt: commitFingerprint is null ? null : receipt).ConfigureAwait(false);
        activity?.AddEvent(new("admitted"));
        return receipt;
    }

    private ExtractJobDraftDto ResolveExtractInput(ExtractJobDraftDto draft, SubmissionOptionsDto? options)
    {
        if (!string.IsNullOrWhiteSpace(draft.Input)) return draft;
        DownloadSettings settings = jobSettingsResolver.ResolveDetailed(
            JobRequestMapper.CreateSettingsContext(draft, allowMissingInput: true),
            SubmissionOptionsStore.Merge(options, new SubmissionOptionsDto(DownloadSettings: draft.DownloadSettings))).Settings;
        string? input = settings.Extraction.Input;
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("input is required for extract jobs");
        return draft with
        {
            Input = input,
            InputType = draft.InputType ?? settings.Extraction.InputType.ToString(),
        };
    }

    internal async IAsyncEnumerable<PlannedJobNode> PlanDraftAsync(
        JobDraftDto draft,
        SubmissionOptionsDto? submissionOptions,
        IReadOnlyDictionary<string, SubmissionSourceRevision>? artifactRevisions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft is ExtractJobDraftDto { ArtifactId: null } extract && string.IsNullOrWhiteSpace(extract.Input))
            draft = ResolveExtractInput(extract, submissionOptions);
        Job root = JobRequestMapper.CreateJob(draft);
        if (submissionOptions?.WorkflowId is Guid workflowId)
            root.WorkflowId = workflowId;
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        AttachArtifactRevisions(root, artifactRevisions);
        var settingsResolver = jobSettingsResolver.CreateScope(root.WorkflowId);
        settingsResolver.SetWorkflowOptions(root.WorkflowId, submissionOptions);
        JobRequestMapper.ApplyDraftDownloadSettings(
            root,
            draft,
            (item, patch) => settingsResolver.SetJobOptions(
                item.Id, new SubmissionOptionsDto(DownloadSettings: patch)));
        DownloadSettings rootSettings = settingsResolver.Resolve(defaultDownloadSettings, root);
        if (RemoteTransferSubmissionPolicy.ValidateOverrides(
            root,
            rootSettings,
            SubmissionOptionsStore.Merge(
                submissionOptions,
                JobRequestMapper.DraftDownloadSettings(draft) is { } rootPatch
                    ? new SubmissionOptionsDto(DownloadSettings: rootPatch) : null)?.DownloadSettings,
            (parent, child, _) => settingsResolver.Resolve(parent, child, JobSettingsInheritance.SearchConstraints),
            (draft as JobListJobDraftDto)?.Jobs))
        {
            RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
        }
        RemoteTransferSubmissionPolicy.NormalizeSettings(root, rootSettings);
        if (!CanAcceptLoginRequiredJobs() && ContainsLoginRequiredJob(root, rootSettings, settingsResolver))
        {
            throw new ArgumentException(
                "This server is not configured for Soulseek login. Configure username/password, enable random login, or use a non-login submission.");
        }

        var planner = new JobPlanner(settingsResolver);
        await foreach (PlannedJobNode node in planner.PlanResolvedAsync(
            root, rootSettings, cancellationToken).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    private static void AttachArtifactRevisions(
        Job job,
        IReadOnlyDictionary<string, SubmissionSourceRevision>? revisions)
    {
        if (job.ArtifactId is { } artifactId
            && revisions?.TryGetValue(artifactId, out SubmissionSourceRevision? revision) == true)
        {
            job.PlannedSourceRevision = revision;
        }
        if (job is JobList list)
        {
            foreach (Job child in list.Jobs)
                AttachArtifactRevisions(child, revisions);
        }
    }

    internal async Task AcceptPreviewAsync(
        Job root,
        DownloadSettings rootSettings,
        SubmissionSpecification specification,
        Guid submissionId,
        Guid previewId,
        string commitFingerprint,
        CommitJobPreviewResponseDto receipt,
        CancellationToken cancellationToken)
    {
        SubmissionIdentity.AssignAccepted(
            root,
            specification,
            submissionId: submissionId,
            previewId: previewId);
        await AcceptAsync(
            root,
            rootSettings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint,
            commitReceipt: receipt).ConfigureAwait(false);
    }

    internal DownloadSettings PrepareSearchViewSelectionJob(
        Job job,
        SubmissionOptionsDto? options,
        bool allowSearchViewAlbumFolderOption = false)
    {
        ArgumentNullException.ThrowIfNull(job);
        DownloadSettings settings = jobSettingsResolver.ResolveFollowUp(job, options);
        ValidateExplicitRemoteTransferOverrides(
            job,
            options?.DownloadSettings,
            settings,
            allowSearchViewAlbumFolderOption);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);
        job.PlannedSettings = JobSettingsSnapshot.Capture(settings);
        return settings;
    }

    internal async Task AcceptSearchSelectionAsync(
        JobList root,
        Guid sourceJobId,
        Guid submissionId,
        string commitFingerprint,
        CommitSearchViewSelectionResponseDto receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Count == 0)
            throw new ArgumentException("A search-view submission requires at least one resolved job.");
        DownloadSettings rootSettings = SearchSettingsBaselines.Create(
            SearchSettingsBaselineKind.Generic);
        root.PlannedSettings = JobSettingsSnapshot.Capture(rootSettings);
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        SubmissionSpecification specification = SubmissionSpecification.Create(root);
        SubmissionIdentity.AssignAccepted(
            root,
            specification,
            submissionId: submissionId);
        await AcceptAsync(
            root,
            rootSettings,
            sourceJobId,
            cancellationToken,
            settingsAreFinal: true,
            commitFingerprint: commitFingerprint,
            commitReceipt: receipt).ConfigureAwait(false);
    }

    private void ValidateExplicitRemoteTransferOverrides(
        Job job,
        DownloadSettingsPatchDto? patch,
        DownloadSettings effectiveSettings,
        bool allowSearchViewAlbumFolderOption = false,
        IReadOnlyList<JobDraftDto>? childDrafts = null)
    {
        if (!RemoteTransferSubmissionPolicy.ValidateOverrides(
                job,
                effectiveSettings,
                patch,
                ResolveChildSettings,
                childDrafts,
                allowSearchViewAlbumFolderOption))
            return;

        RemoteTransferSettingsValidator.ValidateExplicitNameFormat(options.LaunchDownloadSettings);
    }

    private DownloadSettings ResolveChildSettings(
        DownloadSettings parentSettings,
        Job child,
        DownloadSettingsPatchDto? _)
        => jobSettingsResolver.Resolve(
            parentSettings,
            child,
            JobSettingsInheritance.SearchConstraints);

    private static bool ContainsLoginRequiredJob(
        Job job, DownloadSettings settings, IJobSettingsResolver resolver)
    {
        if (job is not JobList list) return settings.NeedLogin;
        foreach (Job child in list.Jobs)
        {
            if (ContainsLoginRequiredJob(child,
                resolver.Resolve(settings, child, JobSettingsInheritance.SearchConstraints), resolver)) return true;
        }
        return false;
    }

    public async Task<JobSummaryDto?> RerunSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null)
            throw new NotSupportedException("Submission rerun is unavailable because persistence is disabled or not started.");
        var started = System.Diagnostics.Stopwatch.StartNew();
        // Accepted intent is committed before execution starts. Replaying it
        // requires neither terminal history handoffs nor mutable job counts.
        string? retained = await persistence.Submissions
            .GetSpecificationJsonAsync(submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (retained == null)
            return null;
        long readMs = started.ElapsedMilliseconds;

        SubmissionSpecification specification =
            SubmissionSpecificationCodec.Deserialize(retained);
        var (job, settings) = specification.Materialize(defaultDownloadSettings);
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);
        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This retained submission requires Soulseek login, but the server is not configured for it.");

        SubmissionIdentity.AssignAccepted(
            job,
            specification,
            rerunOfSubmissionId: submissionId);
        long materializedMs = started.ElapsedMilliseconds;
        await AcceptAsync(
            job,
            settings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true).ConfigureAwait(false);
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SubmissionRerunAccepted(logger, submissionId, job.SubmissionId!.Value, durationMs);
        ServerLogMessages.SubmissionRerunTiming(logger, submissionId, job.SubmissionId.Value,
            readMs, materializedMs - readMs, durationMs - materializedMs);
        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    public async Task<JobSummaryDto?> RerunSearchJobAsync(
        Guid sourceJobId,
        CancellationToken cancellationToken)
    {
        if (persistence?.Submissions == null || persistence.JobHistory == null)
            throw new NotSupportedException("Search replay is unavailable because persistence is disabled or not started.");
        var started = System.Diagnostics.Stopwatch.StartNew();
        Guid submissionId;
        // A live or admitted job may not have reached job history yet. Its
        // identity is already authoritative; execution settings still come from
        // the durably accepted command below, never mutable live configuration.
        if (StateStore.GetJobSummary(sourceJobId) is { } liveSource)
        {
            if (liveSource.Kind != ServerJobKind.Search || liveSource.SubmissionId is not Guid liveSubmissionId)
                throw new ArgumentException("Only an accepted search job can be replayed.");
            submissionId = liveSubmissionId;
        }
        else
        {
            await persistence.WaitForJobHandoffAsync(sourceJobId, cancellationToken, includeSearchResults: false)
                .ConfigureAwait(false);
            PersistedJob? source = await persistence.JobHistory
                .GetJobAsync(sourceJobId, cancellationToken).ConfigureAwait(false);
            if (source == null)
                return null;
            if (source.Kind != ServerJobKind.Search.ToString() || source.SubmissionId is not Guid retainedSubmissionId)
                throw new ArgumentException("Only an accepted search job can be replayed.");
            submissionId = retainedSubmissionId;
        }

        string? retained = await persistence.Submissions
            .GetSpecificationJsonAsync(submissionId, cancellationToken).ConfigureAwait(false);
        if (retained == null)
            return null;
        long readMs = started.ElapsedMilliseconds;
        SubmissionSpecification accepted = SubmissionSpecificationCodec.Deserialize(retained);
        SubmissionSpecification replay = accepted.ForSearchJob(sourceJobId)
            ?? throw new ArgumentException(
                $"Search {sourceJobId} has no matching planned command in its retained submission.");
        var (restoredJob, settings) = replay.Materialize(defaultDownloadSettings);
        var job = (SearchJob)restoredJob;
        JobRequestMapper.AssignWorkflowId(job, job.WorkflowId);
        RemoteTransferSubmissionPolicy.NormalizeSettings(job, settings);
        if (settings.NeedLogin && !CanAcceptLoginRequiredJobs())
            throw new ArgumentException("This retained search requires Soulseek login, but the server is not configured for it.");

        SubmissionIdentity.AssignAccepted(
            job,
            replay,
            rerunOfSubmissionId: submissionId);
        long materializedMs = started.ElapsedMilliseconds;
        await AcceptAsync(
            job,
            settings,
            sourceJobId: null,
            cancellationToken,
            settingsAreFinal: true).ConfigureAwait(false);
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SubmissionRerunAccepted(logger, submissionId, job.SubmissionId!.Value, durationMs);
        ServerLogMessages.SubmissionRerunTiming(logger, submissionId, job.SubmissionId.Value,
            readMs, materializedMs - readMs, durationMs - materializedMs);
        return StateStore.GetJobSummary(job.Id) ?? BuildSubmittedJobSummary(job);
    }

    private bool CanAcceptLoginRequiredJobs()
        => !string.IsNullOrWhiteSpace(engineSettings.MockFilesDir)
        || engineSettings.UseRandomLogin
        || (!string.IsNullOrWhiteSpace(engineSettings.Username)
            && !string.IsNullOrWhiteSpace(engineSettings.Password));

    internal async Task<JobSummaryDto> SubmitFollowUpJobAsync(
        Guid sourceJobId,
        Job? sourceJob,
        Job followUpJob,
        DownloadSettings settings,
        SubmissionOptionsDto? options,
        CancellationToken ct)
    {
        if (sourceJob is not null)
        {
            followUpJob.WorkflowId = sourceJob.WorkflowId;
            JobRequestMapper.PropagateSourceMutationToFollowUp(sourceJob, followUpJob);
        }
        jobSettingsResolver.SetIsolatedJobOptions(followUpJob.Id, options);
        ValidateExplicitRemoteTransferOverrides(
            followUpJob,
            options?.DownloadSettings,
            settings);
        RemoteTransferSubmissionPolicy.NormalizeSettings(followUpJob, settings);
        await AcceptAsync(followUpJob, settings, sourceJobId, ct).ConfigureAwait(false);
        return StateStore.GetJobSummary(followUpJob.Id) ?? BuildSubmittedJobSummary(followUpJob, sourceJobId);
    }

    internal async Task AcceptAsync(
        Job job,
        DownloadSettings settings,
        Guid? sourceJobId,
        CancellationToken cancellationToken,
        bool settingsAreFinal = false,
        string? commitFingerprint = null,
        object? commitReceipt = null)
    {
        using var activity = SubmissionActivities.StartActivity("submission.queue");
        activity?.SetTag("job.id", job.Id);
        if (job.SubmissionId == null)
            SubmissionIdentity.AssignAccepted(job, settings);
        activity?.AddEvent(new("identity-retained"));
        if (persistence?.Submissions != null)
        {
            await persistence.Submissions.CreateAsync(
                new SubmissionRegistration(
                    job.SubmissionId!.Value,
                    job.CreatedAtUtc ?? DateTimeOffset.UtcNow,
                    SubmissionSpecification.CurrentSchemaVersion,
                    job.SubmissionSpecificationJson
                        ?? throw new InvalidOperationException("An accepted submission has no retained specification."),
                    job.RerunOfSubmissionId,
                    job.PreviewId,
                    job.ArtifactId,
                    commitFingerprint,
                    commitReceipt == null ? null : SubmissionCommitCoordinator.SerializeReceipt(commitReceipt)),
                cancellationToken).ConfigureAwait(false);
        }
        activity?.AddEvent(new("persisted"));
        enqueue(job, settings, sourceJobId, settingsAreFinal);
    }

}
