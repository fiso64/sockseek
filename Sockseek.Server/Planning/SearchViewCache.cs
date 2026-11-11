using static Sockseek.Server.Planning.SearchViewSnapshot;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Server.Planning;

public sealed record SearchViewDefinition(
    SearchDefinition Source,
    SearchViewProjectionDefinition Projection,
    SearchSettingsPatchDto? RefinementSettings = null);

public sealed record SearchViewInfo(
    Guid Id,
    Guid SourceJobId,
    SearchViewDefinition Definition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    string RetentionState,
    SearchViewCounters Counters)
{
    public SearchViewProjectionKind ProjectionKind => Definition.Projection.Kind;
    public SearchViewSortOrder SortOrder => Definition.Projection.SortOrder;
}

public sealed record SearchViewRevision(
    Guid ViewId,
    long Revision,
    int SourceRevision,
    long ConsumedSequence,
    bool IsComplete,
    string RetentionState,
    SearchViewCounters Counters);

public sealed record SearchViewFile(
    string Ref,
    long AdmittedRevision,
    SearchProjectionInput Input,
    SearchPreferenceTier PreferenceTier,
    bool NecessaryConditionsSatisfied,
    IReadOnlyList<SearchPreferenceCondition> SatisfiedPreferredConditions,
    IReadOnlyList<SearchPreferenceCondition> ConfiguredPreferredConditions);

public sealed record SearchViewFilePosition(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker,
    long Sequence,
    string Ref,
    long? PrimaryNumber = null,
    long? SecondaryNumber = null);

public sealed record SearchViewFilePage(
    SearchViewRevision Revision,
    IReadOnlyList<SearchViewFile> Items,
    SearchViewFilePosition? NextPosition);

public sealed record SearchViewDirectory(
    string Ref,
    string Username,
    string FolderPath,
    long PublicMatchingFileCount,
    long LockedMatchingFileCount,
    long PublicMatchingBytes,
    long LockedMatchingBytes,
    bool IsFullyRetrieved,
    long? RetrievedFileCount,
    long? RetrievedBytes,
    SearchViewFile BestChild);

public sealed record SearchViewDirectoryPosition(
    uint HighFlags,
    int UploadSpeedFast,
    uint MidFlags,
    int InferredTrackCount,
    int UploadSpeedMedium,
    int BitRate,
    int StableTieBreaker,
    long Sequence,
    string Username,
    string FolderPath,
    string Ref,
    long? PrimaryNumber = null,
    long? SecondaryNumber = null,
    string? PrimaryText = null);

public sealed record SearchViewDirectoryPage(
    SearchViewRevision Revision,
    IReadOnlyList<SearchViewDirectory> Items,
    SearchViewDirectoryPosition? NextPosition,
    IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record SearchViewDirectoryFile(
    string Ref,
    string RelativePath,
    SearchViewFile File);

public sealed record SearchViewDirectoryFilePosition(
    string RelativePath,
    string Ref);

public sealed record SearchViewDirectoryFilePage(
    SearchViewRevision Revision,
    string DirectoryRef,
    string Username,
    string FolderPath,
    IReadOnlyList<SearchViewDirectoryFile> Items,
    SearchViewDirectoryFilePosition? NextPosition,
    SearchViewDirectory DirectorySummary);

public sealed record SearchViewDirectoryFilePreview(
    IReadOnlyList<SearchViewDirectoryFile> Items,
    SearchViewDirectoryFilePosition? NextPosition);

public sealed record SearchViewDirectoryPublishResult(
    SearchViewInfo View,
    int NewFileCount);

public sealed record SearchDirectoryObservation(
    Guid Id,
    Guid SourceJobId,
    int SourceRevision,
    SearchProjectionInput PeerObservation,
    PeerDirectorySnapshot Snapshot,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record SearchDirectoryObservationTarget(
    string DirectoryRef,
    PeerDirectoryIdentity Identity,
    Guid ObservationId);

public sealed record SearchViewTrackGroup(
    string Ref,
    int Index,
    SongQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    SearchViewFile Representative);

public sealed record SearchViewTrackGroupPosition(
    int ShareCount,
    int Index,
    string Ref);

public sealed record SearchViewTrackGroupPage(
    SearchViewRevision Revision,
    IReadOnlyList<SearchViewTrackGroup> Items,
    SearchViewTrackGroupPosition? NextPosition);

public sealed record SearchViewTrackGroupOptionPage(
    SearchViewRevision Revision,
    string GroupRef,
    IReadOnlyList<SearchViewFile> Items,
    SearchViewFilePosition? NextPosition);

public sealed record SearchViewAlbumGroup(
    string Ref,
    int Index,
    AlbumQuery Query,
    int ShareCount,
    long SelectableOptionCount,
    SearchViewDirectory Representative);

public sealed record SearchViewAlbumGroupPosition(
    int ShareCount,
    int Index,
    string Ref);

public sealed record SearchViewAlbumGroupPage(
    SearchViewRevision Revision,
    IReadOnlyList<SearchViewAlbumGroup> Items,
    SearchViewAlbumGroupPosition? NextPosition,
    IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record SearchViewAlbumGroupOptionPage(
    SearchViewRevision Revision,
    string GroupRef,
    IReadOnlyList<SearchViewDirectory> Items,
    SearchViewDirectoryPosition? NextPosition,
    IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record SearchViewChosenOptions(
    SearchViewRevision Revision,
    IReadOnlyDictionary<string, SearchViewFile> Tracks,
    IReadOnlyDictionary<string, SearchViewDirectory> Albums,
    IReadOnlyDictionary<string, SearchViewDirectoryFilePreview> DirectoryFilePreviews);

public sealed record SearchViewCommitItem(
    string Ref,
    SearchViewFile? File,
    SearchViewDirectory? Directory,
    SearchViewTrackGroup? TrackGroup = null,
    SearchViewAlbumGroup? AlbumGroup = null,
    string? ParentRef = null,
    IReadOnlyList<string>? ContainerRefs = null);

public sealed record SearchViewSelectionCounts(long PublicFileCount, long LockedFileCount, long SelectedItemCount = 0);

public sealed record SearchViewFileRefInfo(
    string Ref,
    string DirectoryRef,
    SearchResultVisibility Visibility);

public sealed record SearchViewDirectoryFileMembership(
    string Ref,
    string RelativePath,
    SearchResultVisibility Visibility);

/// <summary>
/// Temporary, immutable read snapshots. Publications share unchanged rows and tree
/// nodes; only requested sort orders have indexes. No projection writes enter SQLite.
/// A View and its older snapshots expire after fifteen idle minutes. Its current
/// snapshot lives with the View. Reads renew the requested snapshot; they never
/// substitute the latest one. Restart loses all Views.
/// Accepted submissions retain their own inputs and do not depend on this cache.
/// </summary>
public sealed partial class SearchViewCache(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(15);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, View> views = new();
    private readonly ConcurrentDictionary<(Guid Source, PeerDirectoryIdentity Directory), SearchDirectoryObservation> observations = new();
    internal static readonly SearchViewCounters EmptyCounters = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    internal static readonly ActivitySource Activities = new("Sockseek.Server.SearchViewCache");

    private sealed class View(SearchViewInfo summary, TimeSpan retention)
    {
        internal readonly Lock Gate = new();
        internal readonly TimeSpan Retention = retention;
        internal SearchViewInfo Summary = summary;
        internal readonly Dictionary<long, SearchViewSnapshot> Revisions = [];
    }

    internal sealed record FileRow(SearchViewFile Value, SearchProjectionSortKey Key);
    internal sealed record DirectoryRow(SearchViewDirectory Value, SearchProjectionSortKey Key,
        ImmutableSortedSet<SearchViewDirectoryFilePosition> Children,
        long PublicCount, long LockedCount, long KnownBytes, long UnknownSizes,
        Guid? ObservationId = null, ImmutableDictionary<string, long?>? RetrievedSizes = null);
    internal sealed record TrackRow(SearchViewTrackGroup Value,
        ImmutableSortedSet<SearchViewFilePosition> Options);
    internal sealed record AlbumRow(string Ref, int Index, AlbumQuery Query, int ShareCount,
        long OptionCount, string Representative, ImmutableHashSet<string> Options);


    public SearchViewInfo Create(Guid sourceJobId, SearchViewDefinition definition, TimeSpan? retention = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan lifetime = retention ?? DefaultRetention;
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var projectionKind = definition.Projection.Kind;
        var summary = new SearchViewInfo(Guid.NewGuid(), sourceJobId, definition,
            now, now + lifetime, 0, 0, 0, false, "Live", EmptyCounters);
        var view = new View(summary, lifetime);
        var snapshot = new SearchViewSnapshot(summary);
        if (projectionKind == SearchViewProjectionKind.Files) snapshot.FileIndexes.Add(summary.SortOrder, FileSet(summary.SortOrder));
        if (projectionKind is SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories)
            snapshot.DirectoryIndexes.Add(summary.SortOrder, DirectorySet(summary.SortOrder));
        view.Revisions.Add(0, snapshot);
        views.TryAdd(summary.Id, view);
        return summary;
    }

    public bool IsActive(Guid viewId)
    {
        if (!views.TryGetValue(viewId, out var view)) return false;
        lock (view.Gate) return view.Summary.ExpiresAtUtc > clock.GetUtcNow();
    }

    public SearchViewInfo? Get(Guid viewId, CancellationToken cancellationToken = default)
        => ReadSummary(viewId, renew: true, cancellationToken);

    // Worker recovery inspects metadata without extending a reader's lifetime.
    public SearchViewInfo? Peek(Guid viewId, CancellationToken cancellationToken = default)
        => ReadSummary(viewId, renew: false, cancellationToken);

    private SearchViewInfo? ReadSummary(Guid viewId, bool renew, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!views.TryGetValue(viewId, out var view)) return null;
        lock (view.Gate)
        {
            if (view.Summary.ExpiresAtUtc <= clock.GetUtcNow()) return null;
            if (renew) Renew(view, view.Revisions[view.Summary.Revision]);
            return view.Summary;
        }
    }

    public SearchViewSnapshot? GetSnapshot(Guid viewId, long revision, CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity("snapshot", viewId, revision);
        cancellationToken.ThrowIfCancellationRequested();
        if (!views.TryGetValue(viewId, out var view)) return null;
        lock (view.Gate)
        {
            var now = clock.GetUtcNow();
            if (view.Summary.ExpiresAtUtc <= now || !view.Revisions.TryGetValue(revision, out var snapshot)
                || revision != view.Summary.Revision && snapshot.Expires <= now) return null;
            Renew(view, snapshot);
            return snapshot;
        }
    }

    private void Renew(View view, SearchViewSnapshot snapshot)
    {
        var expires = clock.GetUtcNow() + view.Retention;
        snapshot.Expires = expires;
        view.Summary = view.Summary with { ExpiresAtUtc = expires };
    }

    private View RequireView(Guid id)
        => views.TryGetValue(id, out var view) && view.Summary.ExpiresAtUtc > clock.GetUtcNow()
            ? view : throw new KeyNotFoundException("The Search View expired or was not found; refresh it before selecting results.");

    public int PruneExpired(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int removed = 0;
        var now = clock.GetUtcNow();
        foreach (var (id, view) in views)
            lock (view.Gate)
            {
                if (view.Summary.ExpiresAtUtc <= now)
                {
                    if (views.TryRemove(id, out _)) removed++;
                    continue;
                }
                foreach (var revision in view.Revisions.Where(pair => pair.Key != view.Summary.Revision && pair.Value.Expires <= now)
                    .Select(pair => pair.Key).ToArray()) view.Revisions.Remove(revision);
            }
        foreach (var pair in observations)
            if (pair.Value.ExpiresAtUtc <= now)
                ((ICollection<KeyValuePair<(Guid Source, PeerDirectoryIdentity Directory), SearchDirectoryObservation>>)observations).Remove(pair);
        return removed;
    }

    public SearchViewInfo Publish(Guid viewId, SearchViewKernelUpdate update, string retentionState,
        CancellationToken cancellationToken = default)
    {
        var view = RequireView(viewId);
        lock (view.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (view.Summary.ExpiresAtUtc <= clock.GetUtcNow())
                throw new KeyNotFoundException("The Search View expired; refresh it before selecting results.");
            var current = view.Summary;
            var before = view.Revisions[current.Revision];
            if (update.SourceRevision < current.SourceRevision || update.ConsumedSequence < current.ConsumedSequence)
                throw new InvalidOperationException("A search view publication cannot regress.");
            var peers = before.Peers.Union(update.ObservedUsernames ?? []);
            var counters = update.Counters with { ObservedPeerCount = peers.Count };
            if (!update.HasProjectionChanges && update.SourceRevision == current.SourceRevision
                && update.ConsumedSequence == current.ConsumedSequence && update.IsComplete == current.IsComplete
                && counters == current.Counters && retentionState == current.RetentionState) return current;
            var summary = current with { Revision = checked(current.Revision + 1), SourceRevision = update.SourceRevision,
                ConsumedSequence = update.ConsumedSequence, IsComplete = update.IsComplete, RetentionState = retentionState,
                Counters = counters };
            var next = Copy(before, summary);
            next.Peers = peers;
            var changedFiles = new HashSet<string>(StringComparer.Ordinal);
            var changedDirectories = new HashSet<string>(StringComparer.Ordinal);
            var changedTracks = new HashSet<string>(StringComparer.Ordinal);
            var changedAlbums = new HashSet<string>(StringComparer.Ordinal);
            AddFiles(next, summary, update.ChangedFiles, changedFiles, cancellationToken);
            foreach (var directory in update.ChangedDirectories ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = DirectoryRef(summary.SourceJobId, directory.Directory);
                var old = next.Directories.GetValueOrDefault(key) ?? next.HiddenDirectories.GetValueOrDefault(key);
                next.HiddenDirectories = next.HiddenDirectories.Remove(key);
                // A folder's preference can differ from its best file (one locked
                // member makes the folder locked). Preserve the projection's facts
                // and rank without changing the individual file row.
                var bestFile = RequireFile(next, summary, directory.BestChild.Input).Value;
                var best = ProjectedFile(bestFile.Ref, bestFile.AdmittedRevision, directory.BestChild);
                var row = old ?? new DirectoryRow(new(key, directory.Directory.Username, directory.Directory.FolderPath,
                    0, 0, 0, 0, false, null, null, best), directory.BestChild.SortKey, ChildSet(), 0, 0, 0, 0);
                foreach (var absorbed in directory.AbsorbedDirectories ?? [])
                    if (before.Directories.TryGetValue(DirectoryRef(summary.SourceJobId, absorbed), out var absorbedRow))
                        foreach (var child in absorbedRow.Children)
                            row = AddChild(next, row, next.Files[child.Ref].Value);
                foreach (var child in directory.NewChildren)
                    row = AddChild(next, row, RequireFile(next, summary, child.Input).Value);
                var visible = new SearchViewDirectory(key, directory.Directory.Username, directory.Directory.FolderPath,
                    directory.PublicMatchingFileCount, directory.LockedMatchingFileCount,
                    directory.PublicMatchingBytes, directory.LockedMatchingBytes,
                    directory.IsFullyRetrieved, directory.RetrievedFileCount, directory.RetrievedBytes,
                    best);
                bool hasRetrievedChildren = old?.ObservationId != null
                    || row.PublicCount + row.LockedCount > directory.PublicMatchingFileCount + directory.LockedMatchingFileCount;
                if (hasRetrievedChildren)
                    visible = visible with
                    {
                        IsFullyRetrieved = old?.Value.IsFullyRetrieved == true
                            && directory.NewChildren.Count == 0 && (directory.AbsorbedDirectories?.Count ?? 0) == 0,
                        RetrievedFileCount = row.PublicCount + row.LockedCount,
                        RetrievedBytes = row.UnknownSizes == 0 ? row.KnownBytes : null,
                    };
                row = row with { Value = visible, Key = directory.BestChild.SortKey };
                next.Directories = next.Directories.SetItem(key, row);
                changedDirectories.Add(key);
            }
            foreach (var identity in update.RemovedDirectories ?? [])
            {
                string key = DirectoryRef(summary.SourceJobId, identity);
                if (next.Directories.TryGetValue(key, out var removed))
                    next.HiddenDirectories = next.HiddenDirectories.SetItem(key, removed);
                next.Directories = next.Directories.Remove(key);
                changedDirectories.Add(key);
            }
            foreach (var group in update.ChangedTrackGroups ?? [])
            {
                SongQuery identity = group.StableQuery ?? group.Query;
                string key = StableRef(summary.SourceJobId, "track-group", identity.Artist, identity.Title,
                    identity.Album, identity.URI, identity.Length.ToString(CultureInfo.InvariantCulture), identity.ArtistMaybeWrong.ToString());
                next.Tracks.TryGetValue(key, out var old);
                var options = old?.Options ?? FileSet(SearchViewSortOrder.Relevance);
                foreach (var option in group.NewOptions)
                {
                    var file = RequireFile(next, summary, option.Input);
                    options = options.Add(Position(file.Value, file.Key));
                    AddParent(next, file.Value.Ref, key);
                }
                next.Tracks = next.Tracks.SetItem(key, new(new(key, group.Index,
                    old?.Value.Query ?? group.Query, group.ShareCount,
                    group.SelectableOptionCount, RequireFile(next, summary, group.Representative.Input).Value), options));
                changedTracks.Add(key);
            }
            foreach (var group in update.ChangedAlbumGroups ?? [])
            {
                string key = AlbumRef(summary.SourceJobId, group.StableIdentity);
                var old = next.Albums.GetValueOrDefault(key) ?? next.HiddenAlbums.GetValueOrDefault(key);
                next.HiddenAlbums = next.HiddenAlbums.Remove(key);
                var options = group.Options.Select(folder => DirectoryRef(summary.SourceJobId, folder.DirectoryIdentity)).ToImmutableHashSet();
                foreach (string option in options) AddParent(next, option, key);
                next.Albums = next.Albums.SetItem(key, new(key, group.Index,
                    old?.Query ?? group.Query, group.ShareCount, group.SelectableOptionCount,
                    DirectoryRef(summary.SourceJobId, group.Representative.DirectoryIdentity), options));
                changedAlbums.Add(key);
            }
            foreach (var identity in update.RemovedAlbumGroups ?? [])
            {
                string key = AlbumRef(summary.SourceJobId, identity);
                if (next.Albums.TryGetValue(key, out var removed))
                    next.HiddenAlbums = next.HiddenAlbums.SetItem(key, removed);
                next.Albums = next.Albums.Remove(key);
                changedAlbums.Add(key);
            }
            FinishPublication(view, before, next, summary, changedFiles, changedDirectories, changedTracks, changedAlbums);
            return summary;
        }
    }

    internal static SearchViewSnapshot Copy(SearchViewSnapshot before, SearchViewInfo summary) => new(summary)
    {
        Files = before.Files, Directories = before.Directories, Tracks = before.Tracks, Albums = before.Albums,
        Parents = before.Parents, Peers = before.Peers, Counts = before.Counts,
        HiddenDirectories = before.HiddenDirectories, HiddenAlbums = before.HiddenAlbums,
        TrackIndex = before.TrackIndex, AlbumIndex = before.AlbumIndex,
    };

    internal static void AddFiles(SearchViewSnapshot next, SearchViewInfo summary,
        IReadOnlyList<ProjectedFileCandidate> files, HashSet<string> changed, CancellationToken cancellationToken)
    {
        // A builder changes only this publication's tree nodes. Freezing once
        // avoids allocating a new root/path for each row while sharing the prior snapshot.
        var rows = next.Files.ToBuilder();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = FileRef(summary.SourceJobId, file.Input);
            if (rows.ContainsKey(key)) continue;
            var row = ProjectedFile(key, summary.Revision, file);
            rows.Add(key, new(row, file.SortKey));
            changed.Add(key);
        }
        next.Files = rows.ToImmutable();
    }

    private static SearchViewFile ProjectedFile(string key, long admittedRevision, ProjectedFileCandidate file)
    {
        var facts = file.ConditionFacts;
        return new(key, admittedRevision, file.Input, facts.PreferenceTier,
            facts.NecessaryConditionsSatisfied, facts.SatisfiedPreferredConditions ?? [], facts.ConfiguredPreferredConditions ?? []);
    }

    internal static FileRow RequireFile(SearchViewSnapshot snapshot, SearchViewInfo summary, SearchProjectionInput input)
        => snapshot.Files.GetValueOrDefault(FileRef(summary.SourceJobId, input))
            ?? throw new InvalidDataException("A projected membership references an unpublished file.");

    internal static DirectoryRow AddChild(SearchViewSnapshot snapshot, DirectoryRow directory, SearchViewFile file)
    {
        var position = new SearchViewDirectoryFilePosition(RelativePath(directory.Value.FolderPath, file.Input.Filename), file.Ref);
        if (directory.Children.Contains(position)) return directory;
        AddParent(snapshot, file.Ref, directory.Value.Ref);
        bool locked = file.Input.Visibility == SearchResultVisibility.Locked;
        long? size = ObservedSize(file.Input, directory.RetrievedSizes);
        return directory with { Children = directory.Children.Add(position),
            PublicCount = directory.PublicCount + (locked ? 0 : 1), LockedCount = directory.LockedCount + (locked ? 1 : 0),
            KnownBytes = checked(directory.KnownBytes + (size ?? 0)),
            UnknownSizes = directory.UnknownSizes + (size.HasValue ? 0 : 1) };
    }

    internal static long? ObservedSize(SearchProjectionInput input, ImmutableDictionary<string, long?>? retrieved)
    {
        // A browse can explicitly report an unknown size, overriding an earlier
        // search hit. Null and zero are different facts.
        if (retrieved != null && retrieved.TryGetValue(input.Filename, out var size)) return size;
        return input.Size >= 0 ? input.Size : null;
    }

    internal static void AddParent(SearchViewSnapshot snapshot, string child, string parent)
        => snapshot.Parents = snapshot.Parents.SetItem(child,
            (snapshot.Parents.GetValueOrDefault(child) ?? ImmutableHashSet<string>.Empty).Add(parent));

    private static void FinishPublication(View view, SearchViewSnapshot before, SearchViewSnapshot next, SearchViewInfo summary,
        IReadOnlySet<string> files, IReadOnlySet<string> directories, IReadOnlySet<string> tracks, HashSet<string> albums)
    {
        // Existing sort indexes advance by changed keys. An alternative ordering is
        // built only when requested, then shared/updated by subsequent publications.
        lock (before.IndexGate)
        {
            foreach (var (order, index) in before.FileIndexes)
            {
                var updated = index.ToBuilder();
                foreach (string key in files)
                {
                    if (before.Files.TryGetValue(key, out var old)) updated.Remove(Position(old.Value, old.Key, order));
                    if (next.Files.TryGetValue(key, out var row)) updated.Add(Position(row.Value, row.Key, order));
                }
                next.FileIndexes.Add(order, updated.ToImmutable());
            }
            foreach (var (order, index) in before.DirectoryIndexes)
            {
                var updated = index.ToBuilder();
                foreach (string key in directories)
                {
                    if (before.Directories.TryGetValue(key, out var old)) updated.Remove(DirectoryPosition(old.Value, old.Key, order));
                    if (next.Directories.TryGetValue(key, out var row)) updated.Add(DirectoryPosition(row.Value, row.Key, order));
                }
                next.DirectoryIndexes.Add(order, updated.ToImmutable());
            }
        }
        foreach (string key in tracks)
        {
            if (before.Tracks.TryGetValue(key, out var old)) next.TrackIndex = next.TrackIndex.Remove(new(old.Value.ShareCount, old.Value.Index, key));
            if (next.Tracks.TryGetValue(key, out var row)) next.TrackIndex = next.TrackIndex.Add(new(row.Value.ShareCount, row.Value.Index, key));
        }
        foreach (string key in directories)
            albums.UnionWith(next.Parents.GetValueOrDefault(key) ?? []);
        lock (before.IndexGate)
            foreach (var (key, index) in before.AlbumOptionIndexes)
            {
                if (!next.Albums.TryGetValue(key, out var album)) continue;
                var updated = index.ToBuilder();
                var old = before.Albums[key];
                foreach (string option in old.Options.Except(album.Options).Concat(directories.Where(old.Options.Contains)))
                    updated.Remove(DirectoryPosition(before.Directories[option].Value,
                        before.Directories[option].Key));
                foreach (string option in album.Options.Except(old.Options).Concat(directories.Where(album.Options.Contains)))
                    updated.Add(DirectoryPosition(next.Directories[option].Value,
                        next.Directories[option].Key));
                next.AlbumOptionIndexes.Add(key, updated.ToImmutable());
            }
        foreach (string key in albums)
        {
            if (before.Albums.TryGetValue(key, out var old)) next.AlbumIndex = next.AlbumIndex.Remove(new(old.ShareCount, old.Index, key));
            if (next.Albums.TryGetValue(key, out var row)) next.AlbumIndex = next.AlbumIndex.Add(new(row.ShareCount, row.Index, key));
        }
        IEnumerable<string> changedTop = summary.ProjectionKind switch
        {
            SearchViewProjectionKind.Files => files, SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories => directories,
            SearchViewProjectionKind.TrackGroups => tracks, SearchViewProjectionKind.AlbumGroups => albums, _ => [],
        };
        foreach (string key in changedTop)
        {
            var old = ItemCounts(TopItem(before, summary.ProjectionKind, key), before);
            var current = ItemCounts(TopItem(next, summary.ProjectionKind, key), next);
            next.Counts = new(checked(next.Counts.PublicFileCount + current.PublicFileCount - old.PublicFileCount),
                checked(next.Counts.LockedFileCount + current.LockedFileCount - old.LockedFileCount),
                checked(next.Counts.SelectedItemCount + current.SelectedItemCount - old.SelectedItemCount));
        }
        view.Revisions.Add(summary.Revision, next);
        view.Summary = summary;
    }

    internal static SearchViewRevision Revision(SearchViewInfo view)
        => new(view.Id, view.Revision, view.SourceRevision, view.ConsumedSequence, view.IsComplete, view.RetentionState, view.Counters);
    internal static string FileRef(Guid source, SearchProjectionInput input)
        => StableRef(source, "file", input.Username, input.Filename, input.Visibility.ToString());
    internal static string DirectoryRef(Guid source, PeerDirectoryIdentity identity)
        => StableRef(source, "directory", identity.Username, identity.FolderPath);
    internal static string AlbumRef(Guid source, PeerDirectoryIdentity identity)
        => StableRef(source, "album-group", identity.Username, identity.FolderPath);
    internal static Activity? StartActivity(string operation, Guid id, long revision)
        => Activities.StartActivity(operation)?.SetTag("view.id", id)?.SetTag("view.revision", revision);
    public void Clear()
    {
        views.Clear();
        observations.Clear();
    }
    internal static string StableRef(Guid sourceJobId, string kind, params ReadOnlySpan<string> parts)
    {
        // Preserve the public identity encoding: UTF-16 length + ':' + value,
        // then UTF-8 and the first 128 SHA-256 bits. Build it without allocating
        // the GUID, StringBuilder, complete key, digest and uppercase hex per file.
        int capacity = checked(35 + EncodedPartLength(kind));
        foreach (string part in parts)
            capacity = checked(capacity + EncodedPartLength(part));
        byte[]? rented = null;
        Span<byte> key = capacity <= 1024 ? stackalloc byte[1024] : (rented = ArrayPool<byte>.Shared.Rent(capacity));
        try
        {
            Span<char> source = stackalloc char[32];
            sourceJobId.TryFormat(source, out _, "N");
            int length = AppendPart(key, source);
            length += AppendPart(key[length..], kind);
            foreach (string part in parts)
                length += AppendPart(key[length..], part);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(key[..length], digest);
            return Convert.ToHexStringLower(digest[..16]);
        }
        finally
        {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }

        static int EncodedPartLength(ReadOnlySpan<char> value)
        {
            // Use actual bytes: a UTF-8 upper bound can overflow for valid long inputs.
            Span<char> prefix = stackalloc char[10];
            value.Length.TryFormat(prefix, out int prefixLength);
            return checked(Encoding.UTF8.GetByteCount(prefix[..prefixLength]) + 1
                + Encoding.UTF8.GetByteCount(value));
        }

        static int AppendPart(Span<byte> target, ReadOnlySpan<char> value)
        {
            Span<char> prefix = stackalloc char[10];
            value.Length.TryFormat(prefix, out int prefixLength);
            int written = Encoding.UTF8.GetBytes(prefix[..prefixLength], target);
            target[written++] = (byte)':';
            return written + Encoding.UTF8.GetBytes(value, target[written..]);
        }
    }

    public static bool MatchesStableFileIdentity(
        Guid sourceJobId, string fileRef, string username, string filename)
        => StringComparer.Ordinal.Equals(fileRef,
               StableRef(sourceJobId, "file", username, filename, nameof(SearchResultVisibility.Public)))
           || StringComparer.Ordinal.Equals(fileRef,
               StableRef(sourceJobId, "file", username, filename, nameof(SearchResultVisibility.Locked)));

    internal static string RelativePath(string folderPath, string filename)
    {
        if (folderPath.Length == 0 && filename.IndexOfAny(['\\', '/']) < 0)
            return filename;
        if (!filename.StartsWith(folderPath, StringComparison.Ordinal)
            || filename.Length <= folderPath.Length
            || filename[folderPath.Length] is not ('\\' or '/'))
            throw new InvalidDataException("A projected directory child is outside its exact directory.");
        return filename[(folderPath.Length + 1)..];
    }

    internal static string RelativeDirectoryPrefix(string rootPath, string childPath)
    {
        if (!childPath.StartsWith(rootPath, StringComparison.Ordinal)
            || childPath.Length <= rootPath.Length
            || childPath[rootPath.Length] is not ('\\' or '/'))
            throw new InvalidDataException("A merged projected directory is outside its root.");
        return childPath[(rootPath.Length + 1)..] + childPath[rootPath.Length];
    }

    internal static SearchViewFilePosition Position(
        SearchViewFile row,
        SearchProjectionSortKey key,
        SearchViewSortOrder order = SearchViewSortOrder.Relevance)
        => new(
            key.HighFlags,
            key.UploadSpeedFast,
            key.MidFlags,
            key.InferredTrackCount,
            key.UploadSpeedMedium,
            key.BitRate,
            key.StableTieBreaker,
            row.Input.Sequence,
            row.Ref,
            order switch
            {
                SearchViewSortOrder.UploadSpeed => row.Input.UploadSpeed ?? -1,
                SearchViewSortOrder.QueueDepth => row.Input.QueueLength ?? int.MaxValue,
                SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending
                    => Math.Max(0, row.Input.Size),
                _ => null,
            },
            order == SearchViewSortOrder.QueueDepth
                ? row.Input.UploadSpeed ?? -1 : null);

    internal static SearchViewDirectoryPosition DirectoryPosition(
        SearchViewDirectory row,
        SearchProjectionSortKey key,
        SearchViewSortOrder order = SearchViewSortOrder.Relevance)
        => new(
            key.HighFlags,
            key.UploadSpeedFast,
            key.MidFlags,
            key.InferredTrackCount,
            key.UploadSpeedMedium,
            key.BitRate,
            key.StableTieBreaker,
            row.BestChild.Input.Sequence,
            row.Username,
            row.FolderPath,
            row.Ref,
            // Retrieval expands the card, not the search projection's sort set.
            order switch
            {
                SearchViewSortOrder.UploadSpeed => row.BestChild.Input.UploadSpeed ?? -1,
                SearchViewSortOrder.QueueDepth => row.BestChild.Input.QueueLength ?? int.MaxValue,
                SearchViewSortOrder.ItemSizeAscending or SearchViewSortOrder.ItemSizeDescending
                    or SearchViewSortOrder.DirectorySizeAscending or SearchViewSortOrder.DirectorySizeDescending
                    => checked(row.PublicMatchingBytes + row.LockedMatchingBytes),
                SearchViewSortOrder.FileCountAscending or SearchViewSortOrder.FileCountDescending
                    => checked(row.PublicMatchingFileCount + row.LockedMatchingFileCount),
                _ => null,
            },
            order == SearchViewSortOrder.QueueDepth
                ? row.BestChild.Input.UploadSpeed ?? -1 : null,
            order is SearchViewSortOrder.DirectoryNameAscending
                or SearchViewSortOrder.DirectoryNameDescending
                ? row.FolderPath : null);

}
