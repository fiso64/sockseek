using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Planning;
using Sockseek.Core.Snapshots;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Planning;

public sealed class JobPreviewUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class JobPreviewCoordinator(
    IOptions<ServerOptions> options,
    EngineSupervisor supervisor,
    SubmissionCommitCoordinator commits,
    JobPreviewCursorCodec cursors,
    ILogger<JobPreviewCoordinator> logger,
    InputArtifactCoordinator? artifacts = null) : IHostedService, IAsyncDisposable
{
    private const int InitialPlanningBatchSize = 100;
    private const int PlanningBatchSize = 1_000;
    private readonly ServerOptions serverOptions = options.Value;
    private JobPreviewStore? store;
    private FileStream? spoolLease;
    private string? spoolDirectory;
    private Exception? initializationFailure;
    private readonly Channel<Guid> planningQueue = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private Task? maintenance;
    internal event Action<Guid>? PreviewCompleted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string dataDirectory = SockseekDataPaths.ResolveDataDirectory(
            serverOptions.Persistence.DataDirectory);
        string spoolRoot = Path.Combine(dataDirectory, "planning", "job-preview-spools");
        string candidateDirectory = Path.Combine(spoolRoot, Guid.NewGuid().ToString("N"));
        FileStream? candidateLease = null;
        JobPreviewStore? candidate = null;
        try
        {
            CleanupInactiveSpools(spoolRoot);
            Directory.CreateDirectory(candidateDirectory);
            candidate = new JobPreviewStore(Path.Combine(candidateDirectory, "preview.db"));
            candidateLease = new FileStream(
                Path.Combine(candidateDirectory, ".active"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            await candidate.InitializeAsync(cancellationToken).ConfigureAwait(false);
            store = candidate;
            spoolLease = candidateLease;
            spoolDirectory = candidateDirectory;
            await RunMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
            worker = RunWorkerAsync(lifetime.Token);
            maintenance = RunMaintenanceAsync(lifetime.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            initializationFailure = exception;
            store = null;
            spoolLease = null;
            spoolDirectory = null;
            if (candidate != null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            candidateLease?.Dispose();
            DeleteSpoolDirectory(candidateDirectory);
            ServerLogMessages.JobPreviewUnavailable(logger, exception);
        }
        catch
        {
            store = null;
            spoolLease = null;
            spoolDirectory = null;
            if (candidate != null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            candidateLease?.Dispose();
            DeleteSpoolDirectory(candidateDirectory);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        planningQueue.Writer.TryComplete();
        lifetime.Cancel();
        if (worker != null || maintenance != null)
        {
            try
            {
                await Task.WhenAll(
                        worker ?? Task.CompletedTask,
                        maintenance ?? Task.CompletedTask)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await RunMaintenanceOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ServerLogMessages.JobPreviewMaintenanceDegraded(logger, exception);
            }
        }
    }

    internal async Task<IReadOnlyList<Guid>> RunMaintenanceOnceAsync(CancellationToken cancellationToken)
    {
        JobPreviewStore repository = RequiredStore();
        foreach (Guid previewId in await repository.ExpireDueAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            ServerLogMessages.JobPreviewExpired(logger, previewId);
        }
        IReadOnlyList<Guid> cleanup = await repository
            .PruneTombstonesAsync(
                TimeSpan.FromDays(1),
                TimeSpan.Zero,
                cancellationToken).ConfigureAwait(false);
        if (artifacts != null)
            foreach (Guid previewId in cleanup)
                await ReleaseArtifactReferencesAsync(previewId, cancellationToken).ConfigureAwait(false);
        return cleanup;
    }

    public async Task<CreateJobPreviewResponseDto> CreateAsync(
        CreateJobPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JobPreviewStore repository = RequiredStore();
        if (ContainsInlineCredentials(request))
        {
            throw new ArgumentException(
                "Job Preview does not persist request credential values. Configure credential slots on the daemon and submit the preview without inline credentials.");
        }
        CreateJobPreviewRequestDto durableRequest = WithoutCredentials(request);
        string[] artifactIds = ArtifactIds(durableRequest.Job)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (artifactIds.Length > 0 && artifacts == null)
            throw new InputArtifactUnavailableException(
                "Input artifacts are unavailable; ordinary inputs remain available.");
        string requestJson = JsonSerializer.Serialize(
            durableRequest,
            SockseekApiJson.CreateSerializerOptions());
        StoredJobPreview preview = await repository.CreateAsync(
            requestJson,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            if (artifactIds.Length > 0)
                await artifacts!.RetainAsync(artifactIds, preview.Id, cancellationToken)
                    .ConfigureAwait(false);
            // Once retained, cancellation must not strand the spool outside the queue.
            await planningQueue.Writer.WriteAsync(preview.Id, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            await ReleaseArtifactReferencesAsync(preview.Id, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        ServerLogMessages.JobPreviewCreated(logger, preview.Id);
        return new(ToDto(preview));
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        await foreach (Guid previewId in planningQueue.Reader.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                StoredJobPreviewWork? work = await RequiredStore()
                    .GetPlanningWorkAsync(previewId, cancellationToken).ConfigureAwait(false);
                if (work == null)
                    continue;
                CreateJobPreviewRequestDto request = JsonSerializer.Deserialize<CreateJobPreviewRequestDto>(
                    work.RequestJson,
                    SockseekApiJson.CreateSerializerOptions())
                    ?? throw new InvalidDataException("A durable job-preview request is empty.");
                await PlanAsync(previewId, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ServerLogMessages.JobPreviewWorkLoadFailed(logger, exception, previewId);
            }
        }
    }

    private async Task PlanAsync(
        Guid previewId,
        CreateJobPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        JobPreviewStore repository = RequiredStore();
        StoredJobPreview preview = await repository.GetPreviewAsync(previewId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The job preview was not found.");
        var started = Stopwatch.StartNew();
        TimeSpan captureDuration = TimeSpan.Zero;
        TimeSpan spoolDuration = TimeSpan.Zero;
        var batch = new List<StoredJobPreviewNode>(PlanningBatchSize);
        using var capture = new PreviewCapture();
        int batchSize = InitialPlanningBatchSize;
        try
        {
            ResolvedArtifactPlan resolvedArtifacts = await ResolveArtifactsAsync(
                request.Job,
                cancellationToken).ConfigureAwait(false);
            var artifactNamesByExtractRef = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (PlannedJobNode node in supervisor.PlanDraftAsync(
                resolvedArtifacts.Draft,
                request.Options,
                resolvedArtifacts.Revisions,
                cancellationToken).ConfigureAwait(false))
            {
                string? displayName = null;
                if (node.Kind == JobSnapshotKind.Extract && node.Input is { } input
                    && resolvedArtifacts.DisplayNamesByPath.TryGetValue(input, out string? originalName))
                {
                    artifactNamesByExtractRef[node.Ref] = originalName;
                    displayName = originalName;
                }
                else if (node.Kind == JobSnapshotKind.JobList
                         && node.ParentRef != null
                         && artifactNamesByExtractRef.TryGetValue(node.ParentRef, out string? sourceName))
                {
                    displayName = Path.GetFileNameWithoutExtension(sourceName);
                }
                long captureStarted = Stopwatch.GetTimestamp();
                batch.Add(capture.ToStoredNode(previewId, node, displayName));
                captureDuration += Stopwatch.GetElapsedTime(captureStarted);
                if (batch.Count >= batchSize)
                {
                    long spoolStarted = Stopwatch.GetTimestamp();
                    await repository.AppendNodesAsync(previewId, batch, cancellationToken)
                        .ConfigureAwait(false);
                    spoolDuration += Stopwatch.GetElapsedTime(spoolStarted);
                    batch.Clear();
                    capture.Clear();
                    // Publish initial progress promptly, then amortize storage
                    // writes for large plans without retaining the whole stream.
                    batchSize = PlanningBatchSize;
                }
            }
            long finalSpoolStarted = Stopwatch.GetTimestamp();
            if (batch.Count > 0)
                await repository.AppendNodesAsync(previewId, batch, cancellationToken)
                    .ConfigureAwait(false);
            preview = await repository.CompleteAsync(previewId, cancellationToken)
                .ConfigureAwait(false);
            spoolDuration += Stopwatch.GetElapsedTime(finalSpoolStarted);
            ServerLogMessages.JobPreviewCompleted(
                logger,
                preview.Id,
                preview.State,
                started.ElapsedMilliseconds,
                preview.NodeCount,
                preview.ReadyNodeCount,
                preview.FailedNodeCount);
            ServerLogMessages.JobPreviewPlanningTiming(
                logger,
                preview.Id,
                (long)(started.Elapsed - captureDuration - spoolDuration).TotalMilliseconds,
                (long)captureDuration.TotalMilliseconds,
                (long)spoolDuration.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                if (batch.Count > 0)
                    await repository.AppendNodesAsync(previewId, batch, CancellationToken.None)
                        .ConfigureAwait(false);
                preview = await repository.CompleteAsync(previewId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The owner logs the original terminal failure once. Store
                // diagnostics remain available if the final state write worked.
            }
            ServerLogMessages.JobPreviewFailed(
                logger,
                exception,
                preview.Id,
                started.ElapsedMilliseconds,
                preview.NodeCount);
        }
        finally
        {
            InvokePreviewCompleted(previewId);
        }
    }

    private void InvokePreviewCompleted(Guid previewId)
    {
        if (PreviewCompleted == null)
            return;
        foreach (Action<Guid> observer in Delegate.EnumerateInvocationList(PreviewCompleted))
        {
            try
            {
                observer(previewId);
            }
            catch (Exception exception)
            {
                ServerLogMessages.JobPreviewObserverFailed(logger, exception, previewId);
            }
        }
    }

    public async Task<JobPreviewSummaryDto?> GetAsync(
        Guid previewId,
        CancellationToken cancellationToken,
        bool waitForCompletion = false)
    {
        if (!waitForCompletion)
        {
            StoredJobPreview? preview = await RequiredStore()
                .GetPreviewAsync(previewId, cancellationToken).ConfigureAwait(false);
            return preview == null || preview.State == "Committed" ? null : ToDto(preview);
        }

        // Subscribe before reading so completion between the read and wait cannot
        // be lost. This uses the planning owner's notification, not a second
        // polling loop or a separately retained progress resource.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(Guid id)
        {
            if (id == previewId) completed.TrySetResult();
        }
        PreviewCompleted += OnCompleted;
        try
        {
            JobPreviewSummaryDto? preview = await GetAsync(previewId, cancellationToken).ConfigureAwait(false);
            if (preview?.State != JobPreviewState.Planning)
                return preview;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            try
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(30), wait.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Slow external extractors can outlive an HTTP long poll. Return
                // current progress and let the caller continue or leave the page.
            }
            return await GetAsync(previewId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PreviewCompleted -= OnCompleted;
        }
    }

    public async Task<CursorPage<JobPreviewNodeDto>?> GetNodesAsync(
        Guid previewId,
        string? parentRef,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        JobPreviewStore repository = RequiredStore();
        if (await GetAsync(previewId, cancellationToken).ConfigureAwait(false) == null)
            return null;
        long after = cursor == null ? -1 : cursors.Decode(cursor, previewId, parentRef);
        IReadOnlyList<StoredJobPreviewNode> rows = await repository.GetNodesAsync(
            previewId,
            parentRef,
            after,
            limit,
            cancellationToken).ConfigureAwait(false);
        string? next = rows.Count == limit
            ? cursors.Encode(previewId, parentRef, rows[^1].Ordinal)
            : null;
        return new CursorPage<JobPreviewNodeDto>(rows.Select(ToDto).ToArray(), next);
    }

    public async Task<CommitJobPreviewResponseDto?> CommitAsync(
        Guid previewId,
        CommitJobPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        ValidateCommitRequest(request);
        string fingerprint = SubmissionCommitCoordinator.Fingerprint(
            "job-preview",
            previewId,
            request.Revision,
            request.Selection,
            subtreeOverrides: request.SubtreeOverrides);
        return await commits.ExecuteAsync(
            request.IdempotencyKey,
            fingerprint,
            async operationToken =>
            {
                CommitJobPreviewResponseDto receipt = await CommitCoreAsync(
                    previewId,
                    request,
                    fingerprint,
                    operationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The job preview was not found.");
                return receipt;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommitJobPreviewResponseDto?> CommitCoreAsync(
        Guid previewId,
        CommitJobPreviewRequestDto request,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        JobPreviewStore repository = RequiredStore();
        StoredPreviewCommit? resolved = await repository.ResolveCommitAsync(
            previewId,
            request.Revision,
            request.Selection.Mode.ToString(),
            refs,
            request.SubtreeOverrides?.Select(rule =>
                new StoredPreviewSelectionOverride(rule.Ref, rule.Selected)).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (resolved == null)
            return null;
        long resolveMs = started.ElapsedMilliseconds;

        int failedCount = resolved.Mode == RefSelectionMode.AllExcept.ToString()
            ? resolved.FailedNodeCount
            : 0;
        int rejectedBeforeSubmission = checked(failedCount + resolved.MissingRequestedRefCount);
        int requestedCount = checked(resolved.SelectedNodes.Count + rejectedBeforeSubmission);
        if (resolved.SelectedNodes.Count == 0)
        {
            IReadOnlyList<SubmissionReasonCountDto> reasons = RejectionReasons(
                failedCount,
                resolved.MissingRequestedRefCount,
                includeEmpty: rejectedBeforeSubmission == 0);
            return new(
                previewId,
                null,
                null,
                requestedCount,
                0,
                0,
                0,
                Math.Max(1, rejectedBeforeSubmission),
                reasons);
        }

        SubmissionSpecification acceptedSpecification = AssembleSelectedSpecification(resolved);
        var (root, rootSettings) = acceptedSpecification.Materialize(supervisor.DefaultDownloadSettings);
        JobRequestMapper.AssignWorkflowId(root, root.WorkflowId);
        long materializedMs = started.ElapsedMilliseconds;

        Guid submissionId = request.IdempotencyKey;
        if (!await repository.TryBeginCommitAsync(previewId, submissionId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("The job preview is already committed or no longer available.");
        }

        long claimedMs = started.ElapsedMilliseconds;
        var receipt = new CommitJobPreviewResponseDto(
            previewId, submissionId, root.WorkflowId, requestedCount,
            resolved.SelectedNodes.Count, resolved.SelectedNodes.Count, 0,
            rejectedBeforeSubmission, RejectionReasons(failedCount, resolved.MissingRequestedRefCount));
        try
        {
            await supervisor.QueuePlannedSubmissionAsync(
                root,
                rootSettings,
                acceptedSpecification,
                submissionId,
                previewId,
                commitFingerprint,
                receipt,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await repository.ReleaseCommitAsync(previewId, submissionId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        long queuedMs = started.ElapsedMilliseconds;
        bool published = await repository.MarkCommittedAsync(
            previewId,
            submissionId,
            CancellationToken.None).ConfigureAwait(false);
        long publishedMs = started.ElapsedMilliseconds;
        if (!published)
        {
            ServerLogMessages.JobPreviewCommitPublicationFailed(
                logger,
                previewId,
                submissionId);
        }
        // Committed plans are no longer public resources. The existing periodic
        // maintenance reclaims their temporary rows and input references; admission
        // must not synchronously delete every unselected preview node.
        ServerLogMessages.JobPreviewCommitted(
            logger,
            previewId,
            submissionId,
            started.ElapsedMilliseconds,
            requestedCount,
            resolved.SelectedNodes.Count,
            rejectedBeforeSubmission);
        ServerLogMessages.JobPreviewCommitTiming(
            logger,
            previewId,
            resolveMs,
            materializedMs - resolveMs,
            claimedMs - materializedMs,
            queuedMs - claimedMs,
            publishedMs - queuedMs);
        return receipt;
    }

    private static void ValidateCommitRequest(CommitJobPreviewRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SelectionExpressionValidator.Validate(request.Selection);
        if (request.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.");
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Preview revision cannot be negative.");
        if (request.SubtreeOverrides != null)
        {
            if (request.Selection.Refs.Count != 0)
                throw new ArgumentException("Exact refs and subtree overrides cannot be mixed.");
            if (request.SubtreeOverrides.Any(rule => rule == null || string.IsNullOrWhiteSpace(rule.Ref)))
                throw new ArgumentException("Subtree override refs cannot be empty.");
            if (request.SubtreeOverrides.Select(rule => rule.Ref).Distinct(StringComparer.Ordinal).Count()
                != request.SubtreeOverrides.Count)
                throw new ArgumentException("Subtree override refs must be unique.");
        }
    }

    private static IReadOnlyList<SubmissionReasonCountDto> RejectionReasons(
        int failedCount,
        int missingCount,
        bool includeEmpty = false)
    {
        var reasons = new List<SubmissionReasonCountDto>(2);
        if (failedCount > 0)
            reasons.Add(new("planning-failed", failedCount));
        if (missingCount > 0)
            reasons.Add(new("missing-ref", missingCount));
        if (includeEmpty)
            reasons.Add(new("empty-selection", 1));
        return reasons;
    }

    private JobPreviewStore RequiredStore()
        => store ?? throw new JobPreviewUnavailableException(
            "Job Preview is unavailable; direct Start remains available.",
            initializationFailure);

    // One persisted batch owns all capture scratch state. Compute settings-ref
    // hashes once per JSON/binding pair, avoiding combined text and UTF8 bytes
    // for every job. No cache survives a batch or preview.
    private sealed class PreviewCapture : IDisposable
    {
        private readonly Dictionary<string, DownloadSettings> validatedSettings = new(StringComparer.Ordinal);
        private readonly RetainedSettingsWriter settingsWriter = new();
        // The writer interns JSON within this batch. Use that identity so a
        // lookup need not hash a long settings string again. Equal JSON with
        // distinct bindings still needs distinct references.
        private readonly Dictionary<string, List<(string Bindings, string Ref)>> settingsRefs =
            new(ReferenceEqualityComparer.Instance);

        public StoredJobPreviewNode ToStoredNode(Guid previewId, PlannedJobNode node, string? displayName)
        {
            SubmissionSpecification? completeSpecification = node.State == PlannedJobState.Ready
                ? node.CreateSpecification(settingsWriter) : null;
            bool selectable = completeSpecification is not null
                && node.Kind is not (JobSnapshotKind.Extract or JobSnapshotKind.JobList);
            if (completeSpecification != null && displayName != null)
            {
                completeSpecification = completeSpecification with
                {
                    Command = completeSpecification.Command with
                    {
                        ItemName = displayName,
                        Input = node.Kind == JobSnapshotKind.Extract
                            ? displayName
                            : completeSpecification.Command.Input,
                    },
                };
            }
            string? bindingsJson = completeSpecification == null
                ? null
                : JsonSerializer.Serialize(completeSpecification.CredentialBindings);
            string? settingsRef = completeSpecification == null
                ? null
                : GetSettingsRef(
                    completeSpecification.EffectiveSettingsJson,
                    bindingsJson!);
            string? specification = completeSpecification == null
                ? null
                : SubmissionSpecificationCodec.Serialize(completeSpecification with
                {
                    // PlannedJobNode owns a shallow command; effective settings
                    // live in the separate spool record, never on that command.
                    EffectiveSettingsJson = "{}",
                    CredentialBindings = [],
                    // Assembly takes submission metadata from the root. Search
                    // jobs retain their own definition on the command; child
                    // wrapper metadata is discarded when the plan is assembled.
                    SourceRevision = node.ParentRef is null ? completeSpecification.SourceRevision : null,
                    Search = node.ParentRef is null ? completeSpecification.Search : null,
                }, validatedSettings);
            return new(
                previewId,
                0,
                node.Ref,
                node.ParentRef,
                node.Role.ToString(),
                node.State.ToString(),
                selectable,
                ServerSnapshotMapper.ToServerJobKind(node.Kind).ToString(),
                displayName ?? node.PreviewTitle,
                node.QueryText,
                node.DirectChildCount,
                JsonSerializer.Serialize(node.AppliedAutoProfiles),
                specification,
                settingsRef,
                completeSpecification?.EffectiveSettingsJson,
                bindingsJson,
                node.FailureCode,
                node.FailureMessage);
        }

        private string GetSettingsRef(string json, string bindings)
        {
            if (!settingsRefs.TryGetValue(json, out var matches))
                settingsRefs.Add(json, matches = []);
            foreach (var match in matches)
                if (match.Bindings == bindings) return match.Ref;
            string reference = EffectiveSettingsRef(json, bindings);
            matches.Add((bindings, reference));
            return reference;
        }

        public void Clear()
        {
            validatedSettings.Clear();
            settingsWriter.Clear();
            settingsRefs.Clear();
        }

        public void Dispose() => settingsWriter.Dispose();
    }

    private static SubmissionSpecification AssembleSelectedSpecification(StoredPreviewCommit resolved)
    {
        // These immutable prototypes live only for this assembly operation.
        // Every node is still validated; equal settings are decoded once rather
        // than once for each joined row in a large selected preview.
        var validatedSettings = new Dictionary<string, DownloadSettings>(StringComparer.Ordinal);
        var nodes = resolved.AncestorNodes.Concat(resolved.SelectedNodes)
            .OrderBy(node => node.Ordinal)
            .Select(node => (node.Ref, node.ParentRef, HydrateSpecification(node, validatedSettings)));
        return JobPlan.AssembleSpecifications(nodes);
    }

    private static SubmissionSpecification HydrateSpecification(
        StoredJobPreviewNode node, Dictionary<string, DownloadSettings> validatedSettings)
    {
        if (node.SpecificationJson == null
            || node.EffectiveSettingsJson == null
            || node.CredentialBindingsJson == null)
        {
            throw new InvalidDataException(
                "A selected preview node has no retained specification or effective settings.");
        }
        SubmissionSpecification skeleton = SubmissionSpecificationCodec.Deserialize(
            node.SpecificationJson, validatedSettings);
        string[] bindings = JsonSerializer.Deserialize<string[]>(
                node.CredentialBindingsJson)
            ?? throw new InvalidDataException(
                "A selected preview node has invalid credential bindings.");
        var hydrated = skeleton with
        {
            Command = skeleton.Command with
            {
                PlannedEffectiveSettingsJson = node.EffectiveSettingsJson,
                PlannedCredentialBindings = bindings,
                PlannedSettingsRef = null,
            },
            EffectiveSettingsJson = node.EffectiveSettingsJson,
            CredentialBindings = bindings,
        };
        hydrated.Validate(validatedSettings);
        return hydrated;
    }

    private static string EffectiveSettingsRef(string settingsJson, string bindingsJson)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            settingsJson + "\0" + bindingsJson));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static JobPreviewSummaryDto ToDto(StoredJobPreview preview)
        => new(
            preview.Id,
            preview.CreatedAtUtc,
            preview.ExpiresAtUtc,
            Enum.Parse<JobPreviewState>(preview.State),
            preview.Revision,
            preview.NodeCount,
            preview.ReadyNodeCount,
            preview.FailedNodeCount,
            preview.SelectableNodeCount,
            preview.CommittedSubmissionId);

    private static JobPreviewNodeDto ToDto(StoredJobPreviewNode node)
    {
        NormalizedJobCommand? command = node.SpecificationJson == null
            ? null
            : SubmissionSpecificationCodec.Deserialize(node.SpecificationJson).Command;
        return new(
            node.Ref,
            node.ParentRef,
            Enum.Parse<ServerJobRole>(node.Role),
            node.State == PlannedJobState.Ready.ToString(),
            node.IsSelectable,
            Enum.Parse<ServerJobKind>(node.Kind),
            node.ItemName,
            node.QueryText,
            node.DirectChildCount,
            JsonSerializer.Deserialize<string[]>(node.AppliedAutoProfilesJson) ?? [],
            command?.SongQuery is { } songQuery
                ? ServerSnapshotMapper.ToSongQueryDto(songQuery)
                : null,
            command?.AlbumQuery is { } albumQuery
                ? ServerSnapshotMapper.ToAlbumQueryDto(albumQuery)
                : null,
            node.FailureCode,
            node.FailureMessage,
            node.SelectableSubtreeCount,
            command?.SourceInfo?.PreviewDetail,
            command?.SourceInfo?.CollectionDescription(node.SelectableSubtreeCount),
            command?.ExtractorInfo);
    }

    private static CreateJobPreviewRequestDto WithoutCredentials(
        CreateJobPreviewRequestDto request)
        => request with
        {
            Job = WithoutCredentials(request.Job),
            Options = request.Options == null
                ? null
                : request.Options with
                {
                    DownloadSettings = WithoutCredentials(
                        request.Options.DownloadSettings),
                },
        };

    private static bool ContainsInlineCredentials(CreateJobPreviewRequestDto request)
        => HasCredentials(request.Options?.DownloadSettings)
            || Drafts(request.Job).Any(draft =>
                HasCredentials(JobRequestMapper.DraftDownloadSettings(draft)));

    private static IEnumerable<JobDraftDto> Drafts(JobDraftDto root)
    {
        yield return root;
        if (root is JobListJobDraftDto list)
        {
            foreach (JobDraftDto child in list.Jobs)
            foreach (JobDraftDto descendant in Drafts(child))
                yield return descendant;
        }
    }

    private static bool HasCredentials(DownloadSettingsPatchDto? patch)
        => patch?.Spotify is
            {
                ClientId: not null,
            }
            or { ClientSecret: not null }
            or { Token: not null }
            or { Refresh: not null }
            || patch?.YouTube?.ApiKey != null
            || patch?.SoundCloud?.AuthToken != null;

    private static JobDraftDto WithoutCredentials(JobDraftDto draft)
        => draft switch
        {
            ExtractJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            GenericSearchJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            TrackSearchJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AlbumSearchJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            SongJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AlbumJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AggregateJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            AlbumAggregateJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            JobListJobDraftDto value => value with
            {
                Jobs = value.Jobs.Select(WithoutCredentials).ToArray(),
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            RemoteFileJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            RemoteDirectoryJobDraftDto value => value with
            {
                DownloadSettings = WithoutCredentials(value.DownloadSettings),
            },
            _ => throw new ArgumentException(
                $"Unsupported job draft type '{draft.GetType().Name}'."),
        };

    private static DownloadSettingsPatchDto? WithoutCredentials(
        DownloadSettingsPatchDto? patch)
        => patch == null
            ? null
            : patch with
            {
                Spotify = null,
                SoundCloud = null,
                YouTube = patch.YouTube == null
                    ? null
                    : patch.YouTube with { ApiKey = null },
            };

    private async Task<IReadOnlyDictionary<string, ResolvedInputArtifact>> ResolveInputArtifactsAsync(
        JobDraftDto draft,
        CancellationToken cancellationToken)
    {
        string[] ids = ArtifactIds(draft)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, ResolvedInputArtifact>(StringComparer.Ordinal);
        if (artifacts == null)
            throw new InputArtifactUnavailableException(
                "Input artifacts are unavailable; ordinary inputs remain available.");
        var leases = new Dictionary<string, ResolvedInputArtifact>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            ResolvedInputArtifact? lease = await artifacts.ResolveAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (lease == null)
                throw new ArgumentException(
                    $"Input artifact '{id}' was not found or has expired.");
            leases.Add(id, lease);
        }
        return leases;
    }

    private async Task<ResolvedArtifactPlan> ResolveArtifactsAsync(
        JobDraftDto draft,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ResolvedInputArtifact> leases =
            await ResolveInputArtifactsAsync(draft, cancellationToken).ConfigureAwait(false);
        if (leases.Count == 0)
            return new(
                draft,
                new Dictionary<string, SubmissionSourceRevision>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal));
        var revisions = leases.ToDictionary(
            pair => pair.Key,
            pair => InputArtifactCoordinator.Revision(pair.Value),
            StringComparer.Ordinal);
        var displayNames = leases.Values
            .Where(lease => !string.IsNullOrWhiteSpace(lease.Artifact.OriginalName))
            .ToDictionary(
                lease => lease.Path,
                lease => lease.Artifact.OriginalName!,
                StringComparer.Ordinal);
        return new(ResolveArtifactDraft(draft, leases), revisions, displayNames);
    }

    /// <summary>
    /// Effective-settings reads must compose the same uploaded-source draft
    /// that Preview will plan, without creating or retaining a preview resource.
    /// </summary>
    public async Task<JobDraftDto> ResolveArtifactDraftForSettingsAsync(
        JobDraftDto draft,
        CancellationToken cancellationToken)
        => (await ResolveArtifactsAsync(draft, cancellationToken).ConfigureAwait(false)).Draft;

    private static JobDraftDto ResolveArtifactDraft(
        JobDraftDto draft,
        IReadOnlyDictionary<string, ResolvedInputArtifact> leases)
        => draft switch
        {
            ExtractJobDraftDto extract when extract.ArtifactId is { } id =>
                ResolveArtifactExtract(extract, leases[id]),
            JobListJobDraftDto list => list with
            {
                Jobs = list.Jobs.Select(child => ResolveArtifactDraft(child, leases)).ToArray(),
            },
            _ => draft,
        };

    private static ExtractJobDraftDto ResolveArtifactExtract(
        ExtractJobDraftDto draft,
        ResolvedInputArtifact lease)
    {
        var immutablePatch = new DownloadSettingsPatchDto(
            Extraction: new ExtractionSettingsPatchDto(RemoveTracksFromSource: false));
        string? inputType = draft.InputType;
        if (string.IsNullOrWhiteSpace(inputType))
        {
            inputType = string.Equals(
                Path.GetExtension(lease.Artifact.OriginalName),
                ".csv",
                StringComparison.OrdinalIgnoreCase)
                ? InputType.CSV.ToString()
                : InputType.List.ToString();
        }
        return draft with
        {
            Input = lease.Path,
            InputType = inputType,
            DownloadSettings = DownloadSettingsPatchDtoMapper.Combine(
                draft.DownloadSettings,
                immutablePatch),
            Provenance = draft.Provenance == null
                ? null
                : draft.Provenance with { SourceMutation = null },
        };
    }

    private static IEnumerable<string> ArtifactIds(JobDraftDto draft)
        => Drafts(draft)
            .OfType<ExtractJobDraftDto>()
            .Select(extract => extract.ArtifactId)
            .OfType<string>();

    private sealed record ResolvedArtifactPlan(
        JobDraftDto Draft,
        IReadOnlyDictionary<string, SubmissionSourceRevision> Revisions,
        IReadOnlyDictionary<string, string> DisplayNamesByPath);

    private async Task ReleaseArtifactReferencesAsync(
        Guid previewId,
        CancellationToken cancellationToken)
    {
        if (artifacts == null)
            return;
        try
        {
            await artifacts.ReleaseAsync(previewId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ServerLogMessages.JobPreviewArtifactCleanupDegraded(
                logger,
                exception,
                previewId);
        }
    }

    private static void CleanupInactiveSpools(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string leasePath = Path.Combine(directory, ".active");
            try
            {
                using (new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                }
                DeleteSpoolDirectory(directory);
            }
            catch (IOException)
            {
                // Another daemon session still owns this spool.
            }
            catch (UnauthorizedAccessException)
            {
                // Initialization will report a clear feature-level failure if
                // this also prevents creation of the new spool.
            }
        }
    }

    private static void DeleteSpoolDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        if (store != null)
            await store.DisposeAsync().ConfigureAwait(false);
        store = null;
        spoolLease?.Dispose();
        spoolLease = null;
        if (spoolDirectory != null)
            DeleteSpoolDirectory(spoolDirectory);
        spoolDirectory = null;
    }
}
