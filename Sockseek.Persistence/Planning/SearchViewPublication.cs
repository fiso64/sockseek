using Microsoft.Data.Sqlite;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Persistence.Planning;

public sealed partial class SearchViewStore
{
    /// <summary>Keep source transactions small while preparing the first publication.</summary>
    public IDisposable PrioritizeInitialPublication() => writes.BeginInteractivePreparation();

    public Task<StoredSearchView> PublishAsync(
        Guid viewId, SearchViewKernelUpdate update, string retentionState,
        CancellationToken cancellationToken = default,
        bool interactive = false)
        => WithWriteAsync((connection, transaction, ct) => PublishInTransactionAsync(
            connection, transaction, viewId, update, retentionState, ct), cancellationToken, background: !interactive);

    private static async Task WriteProjectionRowsAsync(
        SqliteConnection connection, SqliteTransaction transaction, StoredSearchView current,
        long revision, long revisionId, SearchViewKernelUpdate effectiveUpdate,
        Dictionary<(string Username, string Filename, SearchResultVisibility Visibility), FileIdentity> fileRefs,
        CancellationToken ct)
    {
        Guid viewId = current.Id;
        using (StartActivity("publish-file-refs", viewId, revision))
            await ResolveFileIdsAsync(connection, transaction, current.StorageId,
                current.SourceJobId, fileRefs, effectiveUpdate, ct).ConfigureAwait(false);

        // Membership statements repeat across every root/option in this
        // publication. Keep their prepared commands scoped to the transaction.
        using var directoryChildWriter = CreateDirectoryChildInsert(connection, transaction, revision);
        using var directoryWriter = CreateDirectoryInsert(connection, transaction, current.StorageId);
        using var directoryVersionWriter = CreateDirectoryVersionInsert(connection, transaction, current.StorageId, revision, revisionId);
        Dictionary<PeerDirectoryIdentity, DirectoryIdentity> directories;
        using (StartActivity("publish-directories", viewId, revision))
        {
            directories = await PreparePublicationDirectoriesAsync(
                connection, transaction, directoryWriter, current, effectiveUpdate, ct).ConfigureAwait(false);
            foreach (SearchViewProjectedDirectory directory in
                effectiveUpdate.ChangedDirectories ?? [])
            {
                DirectoryIdentity identity = RequireDirectory(directories, directory.Directory);
                string directoryRef = identity.Ref;
                long directoryId = identity.Id;
                long bestId = ResolveFileId(directory.BestChild.Input, fileRefs);
                foreach (PeerDirectoryIdentity absorbed in directory.AbsorbedDirectories ?? [])
                {
                    string absorbedRef = RequireDirectory(directories, absorbed).Ref;
                    await CopyDirectoryChildrenAsync(
                        connection,
                        transaction,
                        viewId,
                        directoryId,
                        absorbedRef,
                        revision,
                        RelativeDirectoryPrefix(
                            directory.Directory.FolderPath,
                            absorbed.FolderPath),
                        ct).ConfigureAwait(false);
                }
                foreach (ProjectedFileCandidate child in directory.NewChildren)
                {
                    long childId = fileRefs[(child.Input.Username, child.Input.Filename, child.Input.Visibility)].Id;
                    InsertDirectoryChild(
                        directoryChildWriter,
                        directoryId,
                        childId,
                        RelativePath(directory.Directory.FolderPath, child.Input.Filename),
                        ct);
                }
                SearchViewProjectedDirectory publishedDirectory = directory;
                Guid? retrievalObservation = identity.RetrievalObservation;
                if (retrievalObservation != null
                    || (directory.AbsorbedDirectories?.Count ?? 0) > 0)
                {
                    (long fileCount, long? bytes) = await ReadVisibleDirectoryTotalsAsync(
                        connection, transaction, directoryId,
                        revision, retrievalObservation, ct).ConfigureAwait(false);
                    bool hasAdditionalObservedFiles = fileCount >
                        directory.PublicMatchingFileCount + directory.LockedMatchingFileCount;
                    bool wasComplete = retrievalObservation != null
                        && directory.NewChildren.Count == 0
                        && (directory.AbsorbedDirectories?.Count ?? 0) == 0
                        && await ReadLatestDirectoryRetrievedAsync(
                            connection, transaction, viewId, directoryRef, ct)
                            .ConfigureAwait(false);
                    if (retrievalObservation != null || hasAdditionalObservedFiles)
                    {
                        publishedDirectory = directory with
                        {
                            IsFullyRetrieved = wasComplete,
                            RetrievedFileCount = fileCount,
                            RetrievedBytes = bytes,
                        };
                    }
                }
                InsertDirectoryVersion(directoryVersionWriter, directoryId, bestId, publishedDirectory, ct);
            }
            if (effectiveUpdate.RemovedDirectories is { Count: > 0 } removed)
            {
                using var removalWriter = CreateDirectoryRemoval(connection, transaction, revision, revisionId);
                foreach (PeerDirectoryIdentity directory in removed)
                {
                    removalWriter.Bind("$directory", RequireDirectory(directories, directory).Id);
                    if (removalWriter.Execute(ct) != 1)
                        throw new InvalidDataException("A removed projected directory has no prior version.");
                }
            }
        }
        using (StartActivity("publish-tracks", viewId, revision))
            await PublishAggregateTracksAsync(connection, transaction, current, revision,
                effectiveUpdate.ChangedAggregateTrackGroups ?? [], fileRefs, ct).ConfigureAwait(false);
        if ((effectiveUpdate.ChangedAggregateAlbumGroups?.Count ?? 0) > 0
            || (effectiveUpdate.RemovedAggregateAlbumGroups?.Count ?? 0) > 0)
        {
            using var albumsActivity = StartActivity("publish-albums", viewId, revision);
            await PublishAggregateAlbumsAsync(connection, transaction, current, revision, revisionId,
                effectiveUpdate, directories, ct).ConfigureAwait(false);
        }
    }

}
