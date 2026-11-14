using System.Collections.Concurrent;
using System.Text.Json;
using Sockseek.Api;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Persistence.Read;
using Sockseek.Server.Persistence;

namespace Sockseek.Server;

/// <summary>
/// Resolves exact candidate identities from current execution or retained observations.
/// It does not create jobs, change settings or admit work. Those belong to execution.
/// </summary>
internal sealed class SearchResultReader(
    PersistenceCoordinator? persistence,
    Func<Guid, Job?> currentJob,
    Func<Guid, ConcurrentDictionary<string, int>> peerFailureCounts)
{
    internal sealed record Source(Guid Id, Guid WorkflowId, string? ItemName, Job? RuntimeJob, PersistedJob? RetainedJob);

    public async Task<Source?> GetSourceAsync(Guid id, CancellationToken ct)
    {
        if (currentJob(id) is { Config: not null } job)
            return new(id, job.WorkflowId, job.ItemName, job, null);
        if (persistence?.JobHistory is null) return null;
        await persistence.WaitForJobHandoffAsync(id, ct).ConfigureAwait(false);
        var retained = await persistence.JobHistory.GetJobAsync(id, ct).ConfigureAwait(false);
        return retained is null ? null : new(id, retained.WorkflowId, retained.ItemName, null, retained);
    }

    public async Task<(FileCandidate Candidate, string? SearchQuery)> GetFileAsync(Source source, FileCandidateRefDto file, CancellationToken ct)
    {
        if (source.RuntimeJob is { } current)
            return (JobRequestMapper.FindFileCandidate(current, file, peerFailureCounts(source.WorkflowId))
                ?? throw new ArgumentException("Requested file was not found in this job's file candidates."), null);
        if (persistence?.SearchHistory is null)
            throw new NotSupportedException("Retained search results are unavailable.");
        PersistedSearchResultLookup? lookup;
        try
        {
            lookup = await persistence.SearchHistory.GetResultAsync(source.Id, file.Username, file.Filename, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The retained result has invalid file-attribute data and cannot be downloaded safely.", exception);
        }
        if (lookup is null)
            throw new ArgumentException("The historical job has no retained search-result history.");
        if (lookup.Result is null)
        {
            string detail = lookup.Metadata.ResultPersistenceState switch
            {
                "Pruned" => "Its raw results were pruned by retention.",
                "Incomplete" => "Its persisted raw results are incomplete.",
                "Interrupted" => "The search was interrupted and the requested result was not committed.",
                "NotPersisted" => "Its raw results were not persisted.",
                _ => "The requested result was not found.",
            };
            throw new ArgumentException($"Cannot start a download from this historical search. {detail}");
        }
        return (lookup.Result.ToFileCandidate(), lookup.Metadata.Query);
    }

    public async Task<(AlbumFolder Folder, AlbumQuery? Query)> GetFolderAsync(
        Source source, AlbumFolderRefDto folderRef, AlbumQueryDto? requestedQuery,
        bool forRetrieval, CancellationToken ct)
    {
        if (source.RuntimeJob is { } current)
        {
            var counts = peerFailureCounts(source.WorkflowId);
            var currentFolder = forRetrieval
                ? JobRequestMapper.FindAlbumFolderForRetrieval(current, folderRef, counts, requestedQuery)
                : JobRequestMapper.FindAlbumFolder(current, folderRef, counts, requestedQuery);
            if (currentFolder is null)
                throw new ArgumentException("Requested folder was not found in this job's album candidates.");
            return (currentFolder, JobRequestMapper.ResolveFolderSelectionQuery(current, requestedQuery));
        }
        if (persistence?.SearchHistory is null)
            throw new NotSupportedException("Retained search results are unavailable.");
        var metadata = await persistence.SearchHistory.GetMetadataAsync(source.Id, ct).ConfigureAwait(false);
        if (metadata is null)
            throw new ArgumentException("The historical job has no retained search-result history.");
        if (metadata.ResultPersistenceState is "Pruned" or "NotPersisted")
            throw new ArgumentException($"Cannot use this historical folder because its result data is {metadata.ResultPersistenceState.ToLowerInvariant()}.");
        var retainedPayload = RetainedJobPayloadCodec.Read(source.RetainedJob!);
        var defaultProjection = HistoricalJobDtoMapper.DefaultFolderProjection(retainedPayload);
        var query = requestedQuery != null
            ? JobRequestMapper.ToAlbumQuery(requestedQuery)
            : defaultProjection?.Query
                ?? throw new ArgumentException("Historical folder operations require an album query.");
        // Album folders combine only directories belonging to this exact peer.
        // Match live/local execution eligibility: locked observations belong to
        // read-only Views, not a resolved download target.
        IReadOnlyList<SearchProjectionInput> inputs = await persistence.SearchHistory
            .GetFolderResultsAsync(source.Id, new(folderRef.Username, folderRef.FolderPath),
                query, SearchResultVisibility.Public, ct).ConfigureAwait(false);
        SearchDefinition definition = await HistoricalJobDtoMapper.SearchDefinitionAsync(
            persistence.Submissions,
            source.RetainedJob!,
            retainedPayload,
            ct).ConfigureAwait(false);
        var folder = SearchResultProjector.AlbumFolders(
                inputs,
                query,
                definition.ProjectionSettings.ToSettings(),
                new ConcurrentDictionary<string, int>(StringComparer.Ordinal))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Username, folderRef.Username, StringComparison.Ordinal)
                && string.Equals(candidate.FolderPath, folderRef.FolderPath, StringComparison.Ordinal));
        if (folder == null)
        {
            string detail = metadata.ResultPersistenceState is "Incomplete" or "Interrupted"
                ? $" Persisted results are {metadata.ResultPersistenceState.ToLowerInvariant()}."
                : "";
            throw new ArgumentException("Requested folder was not found in retained search results." + detail);
        }
        return (folder, query);
    }
}
