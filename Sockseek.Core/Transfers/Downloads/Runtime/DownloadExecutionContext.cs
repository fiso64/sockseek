using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Soulseek;
using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.Reporting;
using Sockseek.Core.Transfers.Downloads.Runtime;
using Sockseek.Core.Transfers.Downloads.Skipping;
using Sockseek.Core.Transfers.Downloads.SourceMutations;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Extractors;
using Sockseek.Core.Planning;

namespace Sockseek.Core.Transfers.Downloads.Runtime;

internal sealed record PendingTerminalTransfer(
    Guid TransferId,
    int AttemptCount,
    PeerFileTarget? Target,
    string SourceReference,
    string InitialOutputPath);

internal sealed class DownloadExecutionContext
{
    private readonly ConcurrentDictionary<Guid, byte> musicDirectoryIndexBuildLoggedByWorkflow = new();

    public DownloadExecutionContext(
        EngineSettings engineSettings,
        SoulseekClientManager clientManager,
        IJobSettingsResolver jobSettingsResolver,
        ISongDownloadFallback songDownloadFallback,
        StaleDownloadCoordinator staleDownloadCoordinator,
        DownloadEvents events,
        JobList queue,
        PeerFailureTracker peerFailures,
        OutputFinalizer outputFinalizer,
        DownloadJobContextStore contexts,
        SourceMutationCoordinator sourceMutations,
        DownloadJobTracker jobs,
        AutoProfileWorkflowReporter autoProfiles,
        SkipEvaluationCoordinator skipEvaluation,
        DownloadRunScope runtime,
        ILoggerFactory loggerFactory,
        ISensitiveOutput sensitiveOutput)
    {
        EngineSettings = engineSettings;
        ClientManager = clientManager;
        JobSettingsResolver = jobSettingsResolver;
        SongDownloadFallback = songDownloadFallback;
        StaleDownloadCoordinator = staleDownloadCoordinator;
        Events = events;
        Queue = queue;
        PeerFailures = peerFailures;
        OutputFinalizer = outputFinalizer;
        Contexts = contexts;
        SourceMutations = sourceMutations;
        Jobs = jobs;
        AutoProfiles = autoProfiles;
        SkipEvaluation = skipEvaluation;
        Runtime = runtime;
        LoggerFactory = loggerFactory;
        SensitiveOutput = sensitiveOutput;
    }

    public EngineSettings EngineSettings { get; }
    public SoulseekClientManager ClientManager { get; }
    public IJobSettingsResolver JobSettingsResolver { get; }
    public ISongDownloadFallback SongDownloadFallback { get; }
    public StaleDownloadCoordinator StaleDownloadCoordinator { get; }
    public DownloadEvents Events { get; }
    public JobList Queue { get; }
    public PeerFailureTracker PeerFailures { get; }
    public OutputFinalizer OutputFinalizer { get; }
    public DownloadJobContextStore Contexts { get; }
    public SourceMutationCoordinator SourceMutations { get; }
    public DownloadJobTracker Jobs { get; }
    public AutoProfileWorkflowReporter AutoProfiles { get; }
    public SkipEvaluationCoordinator SkipEvaluation { get; }
    public DownloadRunScope Runtime { get; }
    public ILoggerFactory LoggerFactory { get; }
    public ISensitiveOutput SensitiveOutput { get; }
    public ConcurrentDictionary<Guid, PendingTerminalTransfer> PendingTerminalTransfers { get; } = new();

    public JobContext Ctx(Job job) => Contexts.Get(job);

    public void UpdateOutputs(Job job, JobContext? prepared, bool indexOnly = false)
    {
        if (prepared?.IndexEditor is { IsWritable: true } index)
        {
            index.Observe(job);
            Runtime.Files.RequestOutputUpdate(index);
        }
        if (!indexOnly && prepared?.PlaylistEditor is { IsWritable: true } playlist)
        {
            playlist.Observe(job);
            Runtime.Files.RequestOutputUpdate(playlist);
        }
    }

    public async Task FlushOutputsAsync(Job root)
    {
        // Root execution, resume and cancellation all settle here before their
        // workflow lease can retire. File-operation disposal alone is too late
        // in a daemon whose engine keeps accepting other workflows.
        foreach (var document in Contexts.GetOutputFiles(root.WorkflowId))
        {
            Exception? failure = await Runtime.Files.FlushOutputAsync(document).ConfigureAwait(false);
            if (failure is null) continue;
            DownloadLogMessages.OutputPublicationFailed(LoggerFactory.CreateLogger<DownloadExecutionContext>(), failure,
                document.OperationId, root.Id);
            Events.RaiseJobMessage(root, LogLevel.Error, "output-files",
                document.Kind == OutputFileKind.Playlist ? "The playlist could not be written. Downloaded files remain available."
                    : "The download index could not be written. Downloaded files remain available.");
        }
    }

    public JobList PrepareGroupChildren(Job owner, IEnumerable<Job> children, JobContext inherited)
    {
        var group = new JobList(owner.ItemName, children)
        {
            Config = owner.Config,
            WorkflowId = owner.WorkflowId,
            DownloadBehaviorPolicy = owner.DownloadBehaviorPolicy,
        };
        inherited.IndexEditor?.RegisterChildren(owner);
        inherited.PlaylistEditor?.RegisterChildren(owner);
        SubmissionIdentity.AssignExecutionChild(owner, group);
        Contexts.Set(group, ChildContext());
        foreach (var child in group.Jobs)
        {
            // Bind ownership before storing context or publishing registration.
            child.WorkflowId = owner.WorkflowId;
            child.Config = owner.Config;
            Contexts.Set(child, ChildContext());
        }
        return group;

        JobContext ChildContext() => new()
        {
            PreparationOwner = inherited.PreparationOwner,
            IndexEditor = inherited.IndexEditor,
            PlaylistEditor = inherited.PlaylistEditor,
            OutputDirSkipper = inherited.OutputDirSkipper,
            MusicDirSkipper = inherited.MusicDirSkipper,
            OutputScope = inherited.OutputScope,
            EnablesIndexByDefault = inherited.EnablesIndexByDefault,
            PreprocessTracks = false,
        };
    }

    public void RegisterJob(Job job, Job? parent, Guid? sourceJobId = null) => Jobs.Register(job, parent, sourceJobId);

    // Start an execution generation before publication, so even queued work can
    // be cancelled. Admission must keep this token rather than replacing it.
    // The cancellation parent can differ from the displayed parent (extraction
    // results and grouping lists have their own hierarchy semantics).
    public void RegisterForExecution(Job job, CancellationToken parentToken, Job? parent, DirectoryTransferEntry? directoryEntry = null)
    {
        job.Cts?.Dispose();
        job.Cts = CancellationTokenSource.CreateLinkedTokenSource(Runtime.Token, parentToken);
        // Cancellation may arrive after queue admission but before this token
        // replaces discovery's token. Preserve the request across that boundary.
        if (job.CancellationSource != JobCancellationSource.None)
            job.Cts.Cancel();
        Jobs.Register(job, parent, directoryEntry: directoryEntry);
    }

    public void ObservePreparedAutoProfiles(Job preparedRoot) => AutoProfiles.ObservePreparedRoot(preparedRoot);

    public void EmitAutoProfileFinalSummary(Job rootJob) => AutoProfiles.EmitFinalSummary(rootJob);

    public void EmitAutoProfileFinalSummary(Guid workflowId) => AutoProfiles.EmitFinalSummary(workflowId);

    public void RetireWorkflow(Guid workflowId, IReadOnlyCollection<Guid> jobIds)
    {
        musicDirectoryIndexBuildLoggedByWorkflow.TryRemove(workflowId, out _);
        foreach (Guid jobId in jobIds)
            PendingTerminalTransfers.TryRemove(jobId, out _);
        AutoProfiles.Retire(workflowId);
    }

    internal (int WorkflowDiagnostics, int PendingTerminalTransfers, int AutoProfileWorkflows) RetainedStateCounts
        => (musicDirectoryIndexBuildLoggedByWorkflow.Count,
            PendingTerminalTransfers.Count,
            AutoProfiles.RetainedWorkflowCount);

    public void RaiseBuildingMusicDirectoryIndex(Job job)
    {
        if (musicDirectoryIndexBuildLoggedByWorkflow.TryAdd(job.WorkflowId, 0))
            Events.RaiseWorkflowMessage(job.WorkflowId, LogLevel.Information, null, "Building music directory index..");
    }
}
