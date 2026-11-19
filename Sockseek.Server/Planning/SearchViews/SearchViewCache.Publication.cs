using System.Collections.Immutable;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Server.Planning;

public sealed partial class SearchViewCache
{
    public SearchViewDirectoryPublishResult PublishRetrievedDirectory(Guid viewId, string directoryRef,
        PeerDirectorySnapshot snapshot, IReadOnlyList<ProjectedFileCandidate> files, SearchProjectionInput peerObservation,
        CancellationToken cancellationToken = default, Guid? existingObservationId = null)
    {
        if (!StringComparer.Ordinal.Equals(peerObservation.Username, snapshot.Identity.Username))
            throw new ArgumentException("The peer observation does not match the retrieved directory peer.", nameof(peerObservation));
        var retrieved = snapshot.Files.ToDictionary(file => (file.Username, file.Filename));
        if (files.Any(file => file.Input.Visibility != SearchResultVisibility.Public
            || !retrieved.ContainsKey((file.Input.Username, file.Input.Filename))))
            throw new ArgumentException("Projected directory files must belong to the retrieved snapshot.", nameof(files));
        var view = RequireView(viewId);
        lock (view.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view.Summary.ExpiresAtUtc <= clock.GetUtcNow())
                throw new KeyNotFoundException("The Search View expired; refresh it before selecting results.");
            var current = view.Summary;
            var before = view.Revisions[current.Revision];
            var known = before.Directories.GetValueOrDefault(directoryRef) ?? before.HiddenDirectories.GetValueOrDefault(directoryRef)
                ?? throw new KeyNotFoundException("The search-view directory was not found.");
            var identity = new PeerDirectoryIdentity(known.Value.Username, known.Value.FolderPath);
            if (identity != snapshot.Identity)
                throw new InvalidOperationException("The retrieved directory identity does not match the search-view ref.");
            before.Directories.TryGetValue(directoryRef, out var directory);
            var observationKey = (current.SourceJobId, identity);
            SearchDirectoryObservation observation;
            if (existingObservationId is { } retainedId)
            {
                if (directory?.ObservationId == retainedId) return new SearchViewDirectoryPublishResult(current, 0);
                if (!observations.TryGetValue(observationKey, out observation!) || observation.Id != retainedId
                    || observation.ExpiresAtUtc <= clock.GetUtcNow())
                    throw new InvalidOperationException("The retrieved directory observation is no longer available.");
            }
            else
            {
                var now = clock.GetUtcNow();
                observation = new(Guid.NewGuid(), current.SourceJobId, current.SourceRevision, peerObservation,
                    snapshot, now, now + DefaultRetention);
                observations[observationKey] = observation;
            }
            // An arriving root can absorb a directory while its browse is in flight.
            // Reuse its observation in refinements without restoring removed membership.
            if (directory == null) return new SearchViewDirectoryPublishResult(current, 0);
            var summary = current with { Revision = checked(current.Revision + 1) };
            var next = Copy(before, summary);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            AddFiles(next, summary, files, changed, cancellationToken);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                directory = AddChild(next, directory, RequireFile(next, summary, file.Input).Value);
            }
            var sizes = snapshot.Files.ToImmutableDictionary(file => file.Filename, file => file.Size, StringComparer.Ordinal);
            long bytes = 0, unknown = 0;
            foreach (var child in directory.Children)
            {
                var input = next.Files[child.Ref].Value.Input;
                long? size = ObservedSize(input, sizes);
                if (size.HasValue) bytes = checked(bytes + size.Value); else unknown++;
            }
            directory = directory with { ObservationId = observation.Id, RetrievedSizes = sizes, KnownBytes = bytes, UnknownSizes = unknown,
                Value = directory.Value with { IsFullyRetrieved = snapshot.IsComplete,
                    RetrievedFileCount = directory.PublicCount + directory.LockedCount, RetrievedBytes = unknown == 0 ? bytes : null } };
            next.Directories = next.Directories.SetItem(directoryRef, directory);
            FinishPublication(view, before, next, summary, changed, new HashSet<string> { directoryRef }, new HashSet<string>(), []);
            return new SearchViewDirectoryPublishResult(summary, changed.Count);
        }
    }

    public SearchDirectoryObservation? GetDirectoryObservation(Guid sourceJobId, PeerDirectoryIdentity identity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return observations.TryGetValue((sourceJobId, identity), out var observation)
            && observation.ExpiresAtUtc > clock.GetUtcNow() ? observation : null;
    }

    public IEnumerable<SearchDirectoryObservationTarget> ReadUnappliedDirectoryObservations(Guid viewId,
        CancellationToken cancellationToken = default)
    {
        var view = Get(viewId, cancellationToken);
        if (view == null) yield break;
        var snapshot = GetSnapshot(viewId, view.Revision, cancellationToken);
        if (snapshot == null) yield break;
        foreach (var directory in snapshot.Directories.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = new PeerDirectoryIdentity(directory.Value.Username, directory.Value.FolderPath);
            if (observations.TryGetValue((view.SourceJobId, identity), out var observation)
                && observation.ExpiresAtUtc > clock.GetUtcNow() && observation.Id != directory.ObservationId)
                yield return new(directory.Value.Ref, identity, observation.Id);
        }
    }


}
