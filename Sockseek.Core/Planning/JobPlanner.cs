using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Sockseek.Core.Extractors;
using Sockseek.Core.Jobs;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Planning;

public enum PlannedJobRole
{
    UserRoot,
    SemanticResult,
    Orchestration,
    ExecutionChild,
}

public enum PlannedJobState
{
    Ready,
    Failed,
}

/// <summary>
/// Storage-agnostic recursive planner shared by local presentation and daemon
/// preview/submission adapters. It performs extraction but never Soulseek
/// discovery or download, and streams records as each independent node resolves.
/// </summary>
public sealed class JobPlanner(
    IJobSettingsResolver settingsResolver,
    ExtractorContext? extractorContext = null)
{
    public IAsyncEnumerable<PlannedJobNode> PlanAsync(
        Job root,
        DownloadSettings inherited,
        CancellationToken cancellationToken = default)
        => PlanNodeAsync(
            root,
            inherited,
            "0",
            parentRef: null,
            PlannedJobRole.UserRoot,
            JobSettingsInheritance.None,
            sourceRevision: null,
            cancellationToken);

    // Submission validation has already composed the root's settings. Copy that
    // value for planning ownership; resolving again repeats profiles and can
    // discard adapter normalization. Descendants resolve against their parent.
    public IAsyncEnumerable<PlannedJobNode> PlanResolvedAsync(
        Job root,
        DownloadSettings rootSettings,
        CancellationToken cancellationToken = default)
        => PlanNodeAsync(root, rootSettings, "0", parentRef: null,
            PlannedJobRole.UserRoot, JobSettingsInheritance.None,
            sourceRevision: null, cancellationToken, rootSettings);

    /// <summary>
    /// Prepare the original Jobs for direct Start without creating detached Review
    /// nodes. Capture retained input once, at acceptance, after this traversal.
    /// </summary>
    public async Task PrepareResolvedAsync(Job root, DownloadSettings rootSettings,
        CancellationToken cancellationToken = default)
    {
        await foreach (var _ in PlanNodeAsync(root, rootSettings, "0", parentRef: null,
            PlannedJobRole.UserRoot, JobSettingsInheritance.None, sourceRevision: null,
            cancellationToken, rootSettings, captureNodes: false).ConfigureAwait(false)) { }
    }

    private async IAsyncEnumerable<PlannedJobNode> PlanNodeAsync(
        Job job,
        DownloadSettings inherited,
        string nodeRef,
        string? parentRef,
        PlannedJobRole role,
        JobSettingsInheritance inheritance,
        SubmissionSourceRevision? sourceRevision,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        DownloadSettings? resolvedSettings = null,
        bool captureNodes = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DownloadSettings? settings = null;
        Exception? settingsFailure = null;
        try
        {
            settings = resolvedSettings is null
                ? JobPreparer.ResolveSemanticSettings(job, inherited, settingsResolver, inheritance)
                : JobPreparer.ApplySemanticSettings(job, SettingsCloner.Clone(resolvedSettings), consumeExtractorHints: false);
            // Extraction still uses mutable settings and can refine its input type.
            // Other nodes transfer the fresh resolved value to their frozen binding.
            if (job is ExtractJob) job.Config = settings;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            settingsFailure = exception;
        }
        if (settingsFailure != null)
        {
            if (captureNodes) yield return Failed(nodeRef, parentRef, role, job, "settings", settingsFailure);
            yield break;
        }
        DownloadSettings effectiveSettings = settings!;

        if (job is ExtractJob extract)
        {
            JobExtractionResult? extraction = null;
            SubmissionSourceRevision? extractedSourceRevision = null;
            Exception? extractionFailure = null;
            try
            {
                extraction = await JobExtraction.ExtractAsync(
                    extract,
                    extractorContext,
                    cancellationToken).ConfigureAwait(false);
                // Source retention is part of this entry's preparation. A
                // failed revision read must not discard later healthy siblings.
                extractedSourceRevision = await CaptureSourceRevisionAsync(extract, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                extractionFailure = exception;
            }
            if (extractionFailure != null)
            {
                extract.HasPlannedExtraction = true;
                extract.PlannedExtractionFailure = extractionFailure.Message;
                effectiveSettings = SettingsCloner.Clone(effectiveSettings);
                job.BindPlannedSettings(effectiveSettings);
                if (captureNodes) yield return Failed(
                    nodeRef,
                    parentRef,
                    PlannedJobRole.Orchestration,
                    job,
                    "extraction",
                    extractionFailure);
                yield break;
            }

            extract.HasPlannedExtraction = true;
            extract.PlannedExtractionFailure = null;
            extract.PlannedSourceRevision = extractedSourceRevision;

            // Extraction used a mutable Config. Detach it before yielding: a
            // caller editing the parent's Config must not change the settings
            // that the frozen parent and its pending children share.
            effectiveSettings = SettingsCloner.Clone(effectiveSettings);
            job.BindPlannedSettings(effectiveSettings);
            if (captureNodes) yield return new PlannedJobNode(
                nodeRef,
                parentRef,
                PlannedJobRole.Orchestration,
                PlannedJobState.Ready,
                job,
                1,
                sourceRevision: extractedSourceRevision);
            await foreach (PlannedJobNode node in PlanNodeAsync(
                extraction!.Result,
                effectiveSettings,
                captureNodes ? nodeRef + "/result" : "",
                nodeRef,
                PlannedJobRole.SemanticResult,
                JobSettingsInheritance.SearchConstraints,
                extractedSourceRevision,
                cancellationToken, captureNodes: captureNodes).ConfigureAwait(false))
            {
                yield return node;
            }
            yield break;
        }

        job.BindPlannedSettings(effectiveSettings);
        if (job is JobList list)
        {
            if (captureNodes) yield return new PlannedJobNode(
                nodeRef,
                parentRef,
                role == PlannedJobRole.UserRoot ? role : PlannedJobRole.Orchestration,
                PlannedJobState.Ready,
                job,
                list.Jobs.Count,
                sourceRevision: sourceRevision);
            for (int index = 0; index < list.Jobs.Count; index++)
            {
                await foreach (PlannedJobNode node in PlanNodeAsync(
                    list.Jobs[index],
                    effectiveSettings,
                    captureNodes ? $"{nodeRef}/{index}" : "",
                    nodeRef,
                    PlannedJobRole.ExecutionChild,
                    JobSettingsInheritance.SearchConstraints,
                    sourceRevision,
                    cancellationToken, captureNodes: captureNodes).ConfigureAwait(false))
                {
                    yield return node;
                }
            }
            yield break;
        }

        if (captureNodes) yield return new PlannedJobNode(
            nodeRef,
            parentRef,
            role,
            PlannedJobState.Ready,
            job,
            DirectChildCount(job),
            sourceRevision: sourceRevision);
    }

    private static PlannedJobNode Failed(
        string nodeRef,
        string? parentRef,
        PlannedJobRole role,
        Job job,
        string code,
        Exception exception)
        => new(
            nodeRef,
            parentRef,
            role,
            PlannedJobState.Failed,
            job,
            0,
            code,
            exception.Message);

    private static int DirectChildCount(Job job) => job switch
    {
        SongGroupsJob group => group.Songs.Count,
        AlbumGroupsJob group => group.Albums.Count,
        AlbumJob album => album.TrackJobs.Count,
        RemoteDirectoryJob directory => directory.FileJobs.Count,
        _ => 0,
    };

    private static async Task<SubmissionSourceRevision?> CaptureSourceRevisionAsync(
        ExtractJob job,
        CancellationToken cancellationToken)
    {
        if (job.PlannedSourceRevision != null)
            return job.PlannedSourceRevision;
        if (!File.Exists(job.Input))
            return null;

        string path = Path.GetFullPath(job.Input);
        var info = new FileInfo(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new SubmissionSourceRevision(
            "local-file",
            path,
            Convert.ToHexString(digest).ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc);
    }
}
