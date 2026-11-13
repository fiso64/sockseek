using System.Diagnostics;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Planning;

/// <summary>
/// Resolves request-owned choices against one Search View revision for counts,
/// settings and durable acceptance. It owns no selections or projection lifetime.
/// </summary>
public sealed class SearchViewSelections
{
    private readonly JobSubmissions submissions;
    private readonly SearchViewCache cache;
    private readonly SubmissionCommitCoordinator commits;
    private readonly ILogger logger;

    internal SearchViewSelections(JobSubmissions submissions, SearchViewCache cache,
        SubmissionCommitCoordinator commits, ILogger logger)
    {
        this.submissions = submissions;
        this.cache = cache;
        this.commits = commits;
        this.logger = logger;
    }

    internal static string Fingerprint(Guid viewId, CommitSearchViewSelectionRequestDto request)
        => SubmissionFingerprint.Create("search-view", new
        {
            ViewId = viewId,
            Request = request with
            {
                IdempotencyKey = Guid.Empty,
                Selection = SubmissionFingerprint.Selection(request.Selection),
                ExcludedFileRefs = SubmissionFingerprint.Refs(request.ExcludedFileRefs),
                IncludedFileRefs = SubmissionFingerprint.Refs(request.IncludedFileRefs),
                ExcludedFileIdentities = SubmissionFingerprint.Map(request.ExcludedFileIdentities),
                ChosenOptions = SubmissionFingerprint.Map(request.ChosenOptions),
                AlbumFileSelections = SubmissionFingerprint.Map(request.AlbumFileSelections)
                    .ToDictionary(pair => pair.Key, pair => SubmissionFingerprint.Selection(pair.Value), StringComparer.Ordinal),
                DirectoryPathRules = request.DirectoryPathRules?.OrderBy(rule => rule.DirectoryRef, StringComparer.Ordinal)
                    .ThenBy(rule => rule.RelativePath, StringComparer.Ordinal).ToArray() ?? [],
                UseRepresentativeWhenUnchosen = request.UseRepresentativeWhenUnchosen == true,
                Options = SubmissionFingerprint.Options(request.Options),
            },
        });

    public async Task<CommitSearchViewSelectionResponseDto?> CommitSelectionAsync(
        Guid viewId,
        CommitSearchViewSelectionRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IdempotencyKey == Guid.Empty)
            throw new ArgumentException("A non-empty idempotency key is required.");
        if (request.Options?.WorkflowId != null)
            throw new ArgumentException("A Search View selection owns its workflow ID; download options cannot replace it.");
        var directoryPathRules = ValidateSelection(request.Revision, request.Selection,
            request.IncludedFileRefs, request.ExcludedFileRefs, request.ChosenOptions,
            request.AlbumFileSelections, request.DirectoryPathRules);
        string fingerprint = Fingerprint(viewId, request);
        return await commits.ExecuteAsync(
            request.IdempotencyKey,
            fingerprint,
            async operationToken =>
            {
                // Accepted intent outlives its temporary issuing View. Only new
                // work needs projection availability and revision membership.
                var snapshot = FindSnapshot(viewId, request.Revision, operationToken)
                    ?? throw new KeyNotFoundException("The search view was not found.");
                var sourceView = snapshot.View;
                ValidateProjection(sourceView, request.IncludedFileRefs, request.ExcludedFileRefs,
                    request.ExcludedFileIdentities, request.ChosenOptions, request.AlbumFileSelections,
                    directoryPathRules, request.UseRepresentativeWhenUnchosen == true);
                CommitSearchViewSelectionResponseDto receipt = await CommitSelectionCoreAsync(
                    viewId,
                    sourceView,
                    snapshot,
                    request,
                    directoryPathRules,
                    fingerprint,
                    operationToken).ConfigureAwait(false);
                return receipt;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public ResolveSearchViewSelectionSettingsResponseDto? ResolveSelectionSettings(Guid viewId, ResolveSearchViewSelectionSettingsRequestDto request, CancellationToken cancellationToken)
    {
        var settingsResolver = submissions.SettingsResolver;
        ArgumentNullException.ThrowIfNull(request);
        var directoryPathRules = ValidateSelection(request.Revision, request.Selection,
            request.IncludedFileRefs, null, null, null, request.DirectoryPathRules);

        var snapshot = FindSnapshot(viewId, request.Revision, cancellationToken);
        if (snapshot is null) return null;
        var view = snapshot.View;
        ValidateProjection(view, request.IncludedFileRefs, null, null, null, null, directoryPathRules);

        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var included = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
        if (request.Selection.Mode == RefSelectionMode.Only)
            refs.UnionWith(included);
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        var variants = new Dictionary<ServerJobKind, SearchViewSelectionSettingsVariantDto>();
        var unresolvedOnly = request.Selection.Mode == RefSelectionMode.Only
            ? new HashSet<string>(refs, StringComparer.Ordinal) : null;

        void AddVariant(SearchViewCommitItem item)
        {
            if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
                return;
            ServerJobKind? kind = item switch
            {
                { File: { } file } when file.Input.Visibility != SearchResultVisibility.Locked
                    => UsesMusicFileDownloads(view) ? ServerJobKind.Song : ServerJobKind.RemoteFile,
                { Directory: { PublicMatchingFileCount: > 0 } }
                    => view.ProjectionKind == SearchViewProjectionKind.AlbumDirectories
                        ? ServerJobKind.Album : ServerJobKind.RemoteDirectory,
                { TrackGroup: { SelectableOptionCount: > 0 } } => ServerJobKind.Song,
                { AlbumGroup: { SelectableOptionCount: > 0 } } => ServerJobKind.Album,
                _ => null,
            };
            if (kind == null)
                return;
            // Profile predicates use mode/source and request context, not peer or
            // filename. Resolve once per kind; do not infer metadata for every
            // selected file just to inspect settings.
            if (!variants.ContainsKey(kind.Value))
            {
                Job job = kind switch
                {
                    ServerJobKind.Song => new SongJob(new SongQuery()),
                    ServerJobKind.Album => new AlbumJob(new AlbumQuery()),
                    ServerJobKind.RemoteFile => new RemoteFileJob(item.File!.Input.ToFileCandidate().Target),
                    _ => new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(
                        DirectoryTransferPlanner.FromObservedSelection(new PeerDirectorySnapshot(
                            new PeerDirectoryIdentity(item.Directory!.Username, item.Directory.FolderPath),
                            [item.Directory.BestChild.Input.ToFileCandidate().Target], isComplete: false)))),
                };
                DownloadSettings settings = submissions.PrepareSearchViewSelectionJob(job, options: null, resolver: settingsResolver);
                variants.Add(kind.Value, new SearchViewSelectionSettingsVariantDto(
                    kind.Value, EffectiveSettingsMapper.ToSafeSettings(settings)));
            }
            if (item.Directory != null || item.TrackGroup != null || item.AlbumGroup != null)
                selectedContainers.Add(item.Ref);
        }

        foreach (var item in snapshot.ReadCommitItems(request.Selection.Mode, refs, cancellationToken))
        {
            unresolvedOnly?.Remove(item.Ref);
            AddVariant(item);
            // AllExcept yields only top-level rows; once a public job kind is
            // known, inspecting every remaining row cannot change its defaults.
            if (unresolvedOnly?.Count == 0
                || request.Selection.Mode == RefSelectionMode.AllExcept && variants.Count > 0)
                break;
        }
        if (directoryPathRules.Values.Any(rules => rules.Any(rule => rule.Selected))
            && !variants.ContainsKey(ServerJobKind.RemoteDirectory))
        {
            var rootRef = directoryPathRules.First(pair => pair.Value.Any(rule => rule.Selected)).Key;
            foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, new HashSet<string>(StringComparer.Ordinal) { rootRef }, cancellationToken))
            {
                if (item.Directory != null) AddVariant(item);
            }
        }
        if (request.Selection.Mode == RefSelectionMode.AllExcept && included.Count > 0)
        {
            foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, included, cancellationToken))
                if (item.File != null)
                    AddVariant(item);
        }

        return new ResolveSearchViewSelectionSettingsResponseDto(
            request.Revision,
            variants.Values.OrderBy(variant => variant.JobKind).ToArray());
    }

    public CountSearchViewSelectionResponseDto? CountSelection(Guid viewId, CountSearchViewSelectionRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var directoryPathRules = ValidateSelection(request.Revision, request.Selection,
            request.IncludedFileRefs, request.ExcludedFileRefs, request.ChosenOptions,
            request.AlbumFileSelections, request.DirectoryPathRules);

        var snapshot = FindSnapshot(viewId, request.Revision, cancellationToken);
        if (snapshot is null) return null;
        var view = snapshot.View;
        ValidateProjection(view, request.IncludedFileRefs, request.ExcludedFileRefs,
            request.ExcludedFileIdentities, request.ChosenOptions, request.AlbumFileSelections, directoryPathRules);
        bool directories = view.ProjectionKind is SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories;
        if (directoryPathRules.Count > 0)
        {
            // The ordinary selection provides the cheap indexed baseline. Only
            // roots with path overrides need their revision-bound memberships
            // streamed; no child refs are materialized in the client or here.
            CountSearchViewSelectionResponseDto baseline = CountSelectionWithoutPathRules(snapshot, request, directories, cancellationToken);
            var directoryRefs = new HashSet<string>(directoryPathRules.Keys, StringComparer.Ordinal);
            var foundDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, directoryRefs, cancellationToken))
                if (item.Directory != null) foundDirectories.Add(item.Ref);
            var topRefs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
            var includedRefs = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
            var excludedRefs = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
            long adjustedPublic = baseline.PublicFileCount;
            long adjustedLocked = baseline.LockedFileCount;
            long adjustedItems = baseline.SelectedItemCount;
            foreach (var (directoryRef, rules) in directoryPathRules)
            {
                if (!foundDirectories.Contains(directoryRef)) continue;
                bool rootSelected = request.Selection.Mode == RefSelectionMode.Only
                    ? topRefs.Contains(directoryRef) : !topRefs.Contains(directoryRef);
                bool anyAfter = false;
                long individuallyIncludedBefore = 0;
                foreach (var file in snapshot.ReadDirectoryFileMemberships(directoryRef, cancellationToken))
                {
                    bool before = (rootSelected && !excludedRefs.Contains(file.Ref))
                        || includedRefs.Contains(file.Ref);
                    bool after = (IsSelectedDirectoryFile(file.RelativePath, rootSelected, rules)
                        && !excludedRefs.Contains(file.Ref)) || includedRefs.Contains(file.Ref);
                    if (!rootSelected && before) individuallyIncludedBefore++;
                    anyAfter |= after;
                    if (before == after) continue;
                    long delta = after ? 1 : -1;
                    if (file.Visibility == SearchResultVisibility.Locked) adjustedLocked += delta;
                    else adjustedPublic += delta;
                }
                if (!rootSelected)
                    adjustedItems += (anyAfter ? 1 : 0) - individuallyIncludedBefore;
                else if (!anyAfter)
                    adjustedItems--;
            }
            if (adjustedPublic < 0 || adjustedLocked < 0 || adjustedItems < 0)
                throw new InvalidDataException("The selected file counts are inconsistent with this revision.");

            return new(request.Revision, adjustedPublic, adjustedLocked, adjustedItems);
        }
        var counts = CountSelectionWithoutPathRules(snapshot, request, directories, cancellationToken);

        return counts;
    }

    private SearchViewSnapshot? FindSnapshot(Guid viewId, long revision, CancellationToken cancellationToken)
    {
        var snapshot = cache.GetSnapshot(viewId, revision, cancellationToken);
        if (snapshot is null && cache.IsActive(viewId))
            throw new InvalidOperationException("The search-view revision is stale or unavailable.");
        return snapshot;
    }

    private CountSearchViewSelectionResponseDto CountSelectionWithoutPathRules(SearchViewSnapshot snapshot, CountSearchViewSelectionRequestDto request, bool directories, CancellationToken cancellationToken)
    {
        SearchViewChosenOptions? resolvedChoices = request.ChosenOptions is { Count: > 0 }
            ? snapshot.ResolveChosenOptions(request.ChosenOptions, cancellationToken: cancellationToken)
            : null;

        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var included = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
        var excluded = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedChoices = new HashSet<string>(request.ChosenOptions?.Keys ?? [], StringComparer.Ordinal);
        var unresolvedAlbumSelections = new HashSet<string>(request.AlbumFileSelections?.Keys ?? [], StringComparer.Ordinal);
        long publicCount = 0;
        long lockedCount = 0;
        long selectedItemCount = 0;

        bool IsSelectedTopLevel(string itemRef)
            => request.Selection.Mode == RefSelectionMode.Only
                ? refs.Contains(itemRef) : !refs.Contains(itemRef);

        (long Public, long Locked) CountDirectory(SearchViewDirectory directory)
        {
            if (directory.RetrievedFileCount == null)
                return (directory.PublicMatchingFileCount, directory.LockedMatchingFileCount);
            SearchViewSelectionCounts observed = snapshot.CountObservedDirectoryFiles(directory.Ref, cancellationToken);
            return (observed.PublicFileCount, observed.LockedFileCount);
        }

        (long Public, long Locked) CountItem(SearchViewCommitItem item)
        {
            if (item.File is { } file)
                return file.Input.Visibility == SearchResultVisibility.Locked ? (0, 1) : (1, 0);
            if (item.Directory is { } directory)
                return CountDirectory(directory);
            if (item.TrackGroup is { } track)
            {
                if (request.ChosenOptions?.ContainsKey(item.Ref) == true)
                {
                    return resolvedChoices?.Tracks.TryGetValue(item.Ref, out _) == true
                        ? (1, 0)
                        : throw new ArgumentException("A chosen track option is not public in this revision.");
                }
                return track.Representative.Input.Visibility == SearchResultVisibility.Locked ? (0, 1) : (1, 0);
            }
            if (item.AlbumGroup is not { } album)
                return (0, 0);
            SearchViewDirectory selectedDirectory = album.Representative;
            if (request.ChosenOptions?.ContainsKey(item.Ref) == true)
            {
                selectedDirectory = resolvedChoices?.Albums.GetValueOrDefault(item.Ref)
                    ?? throw new ArgumentException("A chosen album option is not public in this revision.");
            }
            if (request.AlbumFileSelections?.GetValueOrDefault(item.Ref) is not { } files)
                return CountDirectory(selectedDirectory);
            var fileRefs = new HashSet<string>(files.Refs, StringComparer.Ordinal);
            var matching = (snapshot.ReadDirectoryFileRefs(fileRefs, cancellationToken))
                .Where(info => StringComparer.Ordinal.Equals(info.DirectoryRef, selectedDirectory.Ref))
                .GroupBy(info => info.Ref, StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
            if (matching.Length != fileRefs.Count)
                throw new ArgumentException("An album file ref is not in its chosen directory and revision.");
            long selectedPublic = matching.LongCount(info => info.Visibility == SearchResultVisibility.Public);
            long selectedLocked = matching.LongLength - selectedPublic;
            (long directoryPublic, long directoryLocked) = CountDirectory(selectedDirectory);
            return files.Mode switch
            {
                RefSelectionMode.Only => (selectedPublic, selectedLocked),
                RefSelectionMode.AllExcept => (
                    directoryPublic - selectedPublic,
                    directoryLocked - selectedLocked),
                _ => throw new ArgumentException("Album file selection mode is invalid."),
            };
        }

        if (request.Selection.Mode == RefSelectionMode.Only)
        {
            refs.UnionWith(included);
            foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, refs, cancellationToken))
            {
                if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                    || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
                    continue;
                unresolvedChoices.Remove(item.Ref);
                unresolvedAlbumSelections.Remove(item.Ref);
                if (request.ChosenOptions?.ContainsKey(item.Ref) == true
                    && item.TrackGroup == null && item.AlbumGroup == null)
                    throw new ArgumentException("A chosen option must refer to a result group.");
                if (request.AlbumFileSelections?.ContainsKey(item.Ref) == true
                    && item.AlbumGroup == null)
                    throw new ArgumentException("An album file selection must refer to an album group.");
                (long itemPublic, long itemLocked) = CountItem(item);
                publicCount = checked(publicCount + itemPublic);
                lockedCount = checked(lockedCount + itemLocked);
                selectedItemCount = checked(selectedItemCount + 1);
                if (item.Directory != null || item.TrackGroup != null || item.AlbumGroup != null)
                    selectedContainers.Add(item.Ref);
            }
        }
        else
        {
            SearchViewSelectionCounts baseline = snapshot.CountTopLevelSelection(RefSelectionMode.AllExcept, refs, cancellationToken);
            publicCount = baseline.PublicFileCount;
            lockedCount = baseline.LockedFileCount;
            selectedItemCount = baseline.SelectedItemCount;
            var modifiedGroups = new HashSet<string>(unresolvedChoices, StringComparer.Ordinal);
            modifiedGroups.UnionWith(unresolvedAlbumSelections);
            if (modifiedGroups.Count > 0)
            {
                foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, modifiedGroups, cancellationToken))
                {
                    if (!IsSelectedTopLevel(item.Ref)) continue;
                    unresolvedChoices.Remove(item.Ref);
                    unresolvedAlbumSelections.Remove(item.Ref);
                    if (request.ChosenOptions?.ContainsKey(item.Ref) == true
                        && item.TrackGroup == null && item.AlbumGroup == null)
                        throw new ArgumentException("A chosen option must refer to a result group.");
                    if (request.AlbumFileSelections?.ContainsKey(item.Ref) == true
                        && item.AlbumGroup == null)
                        throw new ArgumentException("An album file selection must refer to an album group.");
                    (long actualPublic, long actualLocked) = CountItem(item);
                    var baselineItem = item.AlbumGroup is { } album
                        ? CountDirectory(album.Representative)
                        : item.TrackGroup is { } track
                            ? track.Representative.Input.Visibility == SearchResultVisibility.Locked ? (0L, 1L) : (1L, 0L)
                            : (0L, 0L);
                    publicCount = checked(publicCount + actualPublic - baselineItem.Item1);
                    lockedCount = checked(lockedCount + actualLocked - baselineItem.Item2);
                }
            }
        }
        RejectPresentUnboundChoices(snapshot, unresolvedChoices, unresolvedAlbumSelections, cancellationToken);

        if (directories && (excluded.Count > 0 || included.Count > 0))
        {
            var exceptionRefs = new HashSet<string>(excluded, StringComparer.Ordinal);
            exceptionRefs.UnionWith(included);
            var details = snapshot.ReadDirectoryFileRefs(exceptionRefs, cancellationToken);
            var found = new HashSet<string>(details.Select(info => info.Ref), StringComparer.Ordinal);
            if (exceptionRefs.Any(fileRef => !found.Contains(fileRef)
                && excluded.Contains(fileRef)
                && request.ExcludedFileIdentities?.ContainsKey(fileRef) != true))
                throw new ArgumentException("A file exception ref is not available in this revision.");
            foreach (var group in details.GroupBy(info => info.Ref, StringComparer.Ordinal))
            {
                var info = group.First();
                bool parentSelected = group.Any(mapping => IsSelectedTopLevel(mapping.DirectoryRef));
                if (excluded.Contains(info.Ref) && parentSelected)
                {
                    if (info.Visibility == SearchResultVisibility.Locked) lockedCount--;
                    else publicCount--;
                }
                else if (request.Selection.Mode == RefSelectionMode.AllExcept
                    && included.Contains(info.Ref) && !parentSelected)
                {
                    if (info.Visibility == SearchResultVisibility.Locked) lockedCount++;
                    else publicCount++;
                    selectedItemCount++;
                }
            }
        }
        if (publicCount < 0 || lockedCount < 0)
            throw new InvalidDataException("The selected file counts are inconsistent with this revision.");
        return new CountSearchViewSelectionResponseDto(request.Revision, publicCount, lockedCount,
            selectedItemCount);
    }

    private async Task<CommitSearchViewSelectionResponseDto> CommitSelectionCoreAsync(
        Guid viewId,
        SearchViewInfo sourceView,
        SearchViewSnapshot snapshot,
        CommitSearchViewSelectionRequestDto request,
        IReadOnlyDictionary<string, SearchViewDirectoryPathRuleDto[]> directoryPathRules,
        string commitFingerprint,
        CancellationToken cancellationToken)
    {
        var settingsResolver = submissions.SettingsResolver;
        var started = Stopwatch.StartNew();

        SearchViewChosenOptions? resolvedChoices = request.ChosenOptions is { Count: > 0 }
            ? snapshot.ResolveChosenOptions(request.ChosenOptions, cancellationToken: cancellationToken)
            : null;
        long choiceMs = started.ElapsedMilliseconds;

        var refs = new HashSet<string>(request.Selection.Refs, StringComparer.Ordinal);
        var directoryRuleRefs = new HashSet<string>(directoryPathRules.Keys, StringComparer.Ordinal);
        long missingDirectoryRuleCount = 0;
        if (directoryRuleRefs.Count > 0)
        {
            var foundDirectoryRuleRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, directoryRuleRefs, cancellationToken))
                if (item.Directory != null) foundDirectoryRuleRefs.Add(item.Ref);
            missingDirectoryRuleCount = directoryPathRules.LongCount(pair =>
                !foundDirectoryRuleRefs.Contains(pair.Key)
                && pair.Value.Any(rule => rule.Selected)
                && (request.Selection.Mode != RefSelectionMode.Only
                    || !request.Selection.Refs.Contains(pair.Key)));
        }
        var includedRefs = new HashSet<string>(request.IncludedFileRefs ?? [], StringComparer.Ordinal);
        var includedItems = new List<SearchViewCommitItem>();
        long missingIncludedCount = 0;
        if (includedRefs.Count > 0)
        {
            var unresolved = new HashSet<string>(includedRefs, StringComparer.Ordinal);
            foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, includedRefs, cancellationToken))
            {
                if (item.File is null) continue;
                unresolved.Remove(item.Ref);
                includedItems.Add(item);
            }
            missingIncludedCount = unresolved.Count;
        }
        if (request.Selection.Mode == RefSelectionMode.Only)
            refs.UnionWith(includedRefs);
        var exclusionRefs = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
        if (request.Selection.Mode == RefSelectionMode.AllExcept)
            exclusionRefs.UnionWith(refs);
        var excludedFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string fileRef in request.ExcludedFileRefs ?? [])
        {
            if (request.ExcludedFileIdentities?.GetValueOrDefault(fileRef) is not { } identity)
                continue;
            if (!excludedFiles.TryGetValue(identity.Username, out var names))
                excludedFiles.Add(identity.Username, names = new(StringComparer.Ordinal));
            names.Add(identity.Filename);
        }
        var unresolvedExclusions = new HashSet<string>(request.ExcludedFileRefs ?? [], StringComparer.Ordinal);
        if (exclusionRefs.Count > 0
            && sourceView.ProjectionKind is SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories)
        {
            foreach (var excluded in snapshot.ReadCommitItems(RefSelectionMode.Only, exclusionRefs, cancellationToken))
            {
                if (excluded.File is not { } file) continue;
                unresolvedExclusions.Remove(excluded.Ref);
                var target = file.Input.ToFileCandidate().Target;
                if (!excludedFiles.TryGetValue(target.Username, out var names))
                    excludedFiles.Add(target.Username, names = new(StringComparer.Ordinal));
                names.Add(target.Filename);
            }
        }
        if (unresolvedExclusions.Any(fileRef =>
                request.ExcludedFileIdentities?.ContainsKey(fileRef) != true))
            throw new ArgumentException("An excluded file ref is not available in this Search View revision.");
        var foundRefs = request.Selection.Mode == RefSelectionMode.Only
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        long requested = 0;
        long resolved = 0;
        long submitted = 0;
        long skipped = 0;
        long rejected = 0;
        long planMs = 0;
        var reasons = new Dictionary<(string Reason, string? Message), long>();
        var unboundChoices = new HashSet<string>(request.ChosenOptions?.Keys ?? [], StringComparer.Ordinal);
        var unboundAlbumSelections = new HashSet<string>(request.AlbumFileSelections?.Keys ?? [], StringComparer.Ordinal);
        // This list only owns the accepted batch. A name would also become an
        // output scope for music children, leaking an internal label into paths.
        var root = new JobList { SelectionSource = JobSelectionSource.ManualSearch };
        var selectedContainers = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<SearchViewCommitItem> ReadSelectedItems()
        {
            var emittedRuleRoots = new HashSet<string>(StringComparer.Ordinal);
            if (directoryRuleRefs.Count > 0)
            {
                foreach (var item in snapshot.ReadCommitItems(RefSelectionMode.Only, directoryRuleRefs, cancellationToken))
                {
                    if (item.Directory == null) continue;
                    bool rootSelected = request.Selection.Mode == RefSelectionMode.Only
                        ? request.Selection.Refs.Contains(item.Ref) : !request.Selection.Refs.Contains(item.Ref);
                    if (rootSelected || directoryPathRules[item.Ref].Any(rule => rule.Selected))
                    {
                        emittedRuleRoots.Add(item.Ref);
                        yield return item;
                    }
                }
            }
            foreach (var item in snapshot.ReadCommitItems(request.Selection.Mode, refs, cancellationToken))
                if (!emittedRuleRoots.Contains(item.Ref)) yield return item;
            if (request.Selection.Mode == RefSelectionMode.AllExcept)
                foreach (var item in includedItems)
                    yield return item;
        }
        foreach (SearchViewCommitItem item in ReadSelectedItems())
        {
            requested = checked(requested + 1);
            resolved = checked(resolved + 1);
            foundRefs?.Add(item.Ref);
            if (unboundAlbumSelections.Remove(item.Ref) && item.AlbumGroup is null)
                throw new ArgumentException("An album file selection must refer to an album group.");
            if (unboundChoices.Remove(item.Ref)
                && item.TrackGroup is null && item.AlbumGroup is null)
                throw new ArgumentException("A chosen option must refer to a result group.");
            if ((item.ParentRef != null && selectedContainers.Contains(item.ParentRef))
                || item.ContainerRefs?.Any(selectedContainers.Contains) == true)
            {
                skipped = checked(skipped + 1);
                continue;
            }
            try
            {
                long planStartMs = started.ElapsedMilliseconds;
                (Job? job, string? rejectionReason) = ResolveSelectionJob(
                    snapshot, item, request, excludedFiles,
                    directoryPathRules.GetValueOrDefault(item.Ref), resolvedChoices, cancellationToken);
                planMs += started.ElapsedMilliseconds - planStartMs;
                if (job == null)
                {
                    rejected = checked(rejected + 1);
                    AddReason(reasons, rejectionReason ?? "unavailable-item");
                    continue;
                }
                DownloadSettings settings = submissions.PrepareSearchViewSelectionJob(
                    job,
                    request.Options,
                    resolver: settingsResolver);
                if (job is AlbumJob { ResolvedTarget: { Files.Count: 0 } }
                    && settings.Search.NoBrowseFolder)
                {
                    skipped = checked(skipped + 1);
                    AddReason(reasons, "empty-selection");
                    continue;
                }
                root.Add(job);
                submitted = checked(submitted + 1);
                if (item.Directory != null
                    || item.TrackGroup != null
                    || item.AlbumGroup != null)
                    selectedContainers.Add(item.Ref);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                rejected = checked(rejected + 1);
                // Preserve the actionable error without failing independent
                // selections. Equal explanations share one counted reason.
                AddReason(reasons, "invalid-item", message: exception.Message);
            }
        }

        RejectPresentUnboundChoices(snapshot, unboundChoices, unboundAlbumSelections, cancellationToken);

        if (foundRefs != null)
        {
            long missing = refs.Count - foundRefs.Count;
            if (missing > 0)
            {
                requested = checked(requested + missing);
                rejected = checked(rejected + missing);
                AddReason(reasons, "missing-ref", missing);
            }
        }
        else if (missingIncludedCount > 0)
        {
            requested = checked(requested + missingIncludedCount);
            rejected = checked(rejected + missingIncludedCount);
            AddReason(reasons, "missing-ref", missingIncludedCount);
        }
        if (missingDirectoryRuleCount > 0)
        {
            requested = checked(requested + missingDirectoryRuleCount);
            rejected = checked(rejected + missingDirectoryRuleCount);
            AddReason(reasons, "missing-ref", missingDirectoryRuleCount);
        }

        // Every query above reads one immutable revision. If retention removed
        // it between queries, discard the assembled result before accepting work.
        // Once assembled, the jobs own their complete targets independently of it.

        // A batch needs a container; one chosen file/folder is already a root.
        Job acceptedRoot = root.Count == 1 ? root[0] : root;
        long resolutionMs = started.ElapsedMilliseconds;
        var receipt = new CommitSearchViewSelectionResponseDto(
            viewId,
            request.Revision,
            root.Count == 0 ? null : request.IdempotencyKey,
            root.Count == 0 ? null : acceptedRoot.WorkflowId,
            requested,
            resolved,
            submitted,
            skipped,
            rejected,
            reasons.OrderBy(pair => pair.Key.Reason, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Message, StringComparer.Ordinal)
                .Select(pair => new SubmissionReasonCountDto(pair.Key.Reason, pair.Value, pair.Key.Message))
                .ToArray());
        if (root.Count > 0)
        {
            await submissions.AcceptSearchSelectionAsync(
                acceptedRoot,
                sourceView.SourceJobId,
                request.IdempotencyKey,
                commitFingerprint,
                receipt,
                cancellationToken).ConfigureAwait(false);
        }
        long durationMs = started.ElapsedMilliseconds;
        ServerLogMessages.SearchViewCommitted(
            logger,
            viewId,
            request.Revision,
            durationMs,
            requested,
            submitted,
            rejected);
        ServerLogMessages.SearchViewCommitTiming(
            logger, viewId, request.Revision,
            resolutionMs, choiceMs, planMs, durationMs - resolutionMs);
        return receipt;
    }

    private static void ValidateExcludedIdentities(
        Guid sourceJobId,
        IReadOnlyList<string>? excludedRefs,
        IReadOnlyDictionary<string, FileCandidateRefDto>? identities)
    {
        if (identities is null)
            return;
        var refs = new HashSet<string>(excludedRefs ?? [], StringComparer.Ordinal);
        foreach ((string fileRef, FileCandidateRefDto identity) in identities)
        {
            if (identity is null || string.IsNullOrEmpty(identity.Username)
                || string.IsNullOrEmpty(identity.Filename) || !refs.Contains(fileRef)
                || !SearchViewCache.MatchesStableFileIdentity(
                    sourceJobId, fileRef, identity.Username, identity.Filename))
                throw new ArgumentException(
                    "An excluded file identity does not match a file ref from this search job.");
        }
    }

    private static void RejectPresentUnboundChoices(SearchViewSnapshot snapshot, HashSet<string> choices, HashSet<string> albumSelections, CancellationToken cancellationToken)
    {
        if (choices.Count == 0 && albumSelections.Count == 0)
            return;
        var unbound = new HashSet<string>(choices, StringComparer.Ordinal);
        unbound.UnionWith(albumSelections);
        foreach (SearchViewCommitItem _ in snapshot.ReadCommitItems(RefSelectionMode.Only, unbound, cancellationToken))
            throw new ArgumentException(
                "An option or album-file selection group is not selected in this revision.");
        // A browser can retain a choice while its group is hidden by a newer
        // projection. It is neither persisted nor applied to this revision.
    }

    private static bool UsesMusicFileDownloads(SearchViewInfo view) => view.ProjectionKind switch
    {
        SearchViewProjectionKind.GenericDirectories => false,
        SearchViewProjectionKind.Files => view.Definition.Source.DefaultProjection != SearchDefaultProjectionKind.GenericFile,
        _ => true,
    };

    private static Job CreateFileSelectionJob(SearchViewInfo view, SearchViewFile file)
    {
        FileCandidate candidate = file.Input.ToFileCandidate();
        if (!UsesMusicFileDownloads(view))
            return new RemoteFileJob(candidate.Target);
        SongQuery query = view.Definition.Projection.SongQuery
            ?? view.Definition.Source.FileQuery?.ToQuery()
            ?? new SongQuery { Artist = view.Definition.Source.AlbumQuery?.Artist ?? "",
                Album = view.Definition.Source.AlbumQuery?.Album ?? "" };
        return new SongJob(Searcher.InferSongQuery(candidate.Filename, query))
        { ResolvedTarget = candidate, ExactTarget = candidate.Target };
    }

    private static AlbumQuery AlbumSelectionQuery(SearchViewInfo view)
        => view.Definition.Projection.AlbumQuery ?? view.Definition.Source.AlbumQuery?.ToQuery()
            ?? throw new InvalidOperationException("An album view requires an album query.");

    private static (Job? Job, string? RejectionReason) ResolveSelectionJob(
        SearchViewSnapshot snapshot,
        SearchViewCommitItem item,
        CommitSearchViewSelectionRequestDto request,
        IReadOnlyDictionary<string, HashSet<string>> excludedFiles,
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? directoryPathRules,
        SearchViewChosenOptions? resolvedChoices,
        CancellationToken cancellationToken)
    {
        string? chosenOption = request.ChosenOptions?.GetValueOrDefault(item.Ref);
        var albumFileSelection = request.AlbumFileSelections?.GetValueOrDefault(item.Ref);
        bool rootSelected = request.Selection.Mode == RefSelectionMode.Only
            ? request.Selection.Refs.Contains(item.Ref) : !request.Selection.Refs.Contains(item.Ref);
        if (item.File is { } file)
        {
            if (file.Input.Visibility == SearchResultVisibility.Locked)
                return (null, "locked");
            return (CreateFileSelectionJob(snapshot.View, file), null);
        }
        if (item.Directory is { } directory)
        {
            if (directory.PublicMatchingFileCount == 0)
                return (null, "locked");
            if (snapshot.View.ProjectionKind == SearchViewProjectionKind.GenericDirectories)
            {
                // File Search selects the files projected into this exact view
                // revision. Browsing the peer's entire directory here would add
                // unobserved/nonmatching files to a reviewed selection.
                IReadOnlySet<string>? excluded = excludedFiles.GetValueOrDefault(directory.Username);
                var observed = new List<PeerFileTarget>();
                foreach (SearchViewDirectoryFile child in snapshot.ReadPublicDirectoryFiles(directory.Ref, cancellationToken))
                {
                    PeerFileTarget target = child.File.Input.ToFileCandidate().Target;
                    if ((IsSelectedDirectoryFile(child.RelativePath, rootSelected, directoryPathRules)
                            && excluded?.Contains(target.Filename) != true)
                        || request.IncludedFileRefs?.Contains(child.Ref) == true)
                        observed.Add(target);
                }
                if (observed.Count == 0)
                    return (null, "empty-selection");
                var identity = new PeerDirectoryIdentity(directory.Username, directory.FolderPath);
                var directorySnapshot = new PeerDirectorySnapshot(identity, observed, isComplete: false);
                DirectoryTransferPlan plan = DirectoryTransferPlanner.FromObservedSelection(directorySnapshot);
                return (new RemoteDirectoryJob(new RemoteDirectorySource.Resolved(plan)), null);
            }
            AlbumQuery query = AlbumSelectionQuery(snapshot.View);
            var trackQuery = new SongQuery { Artist = query.Artist, Album = query.Album, URI = query.URI,
                ArtistMaybeWrong = query.ArtistMaybeWrong };
            var excludedFilenames = excludedFiles.GetValueOrDefault(directory.Username);
            var files = new List<AlbumFile>();
            foreach (SearchViewDirectoryFile child in snapshot.ReadPublicDirectoryFiles(directory.Ref, cancellationToken))
            {
                FileCandidate candidate = child.File.Input.ToFileCandidate();
                if (excludedFilenames?.Contains(candidate.Filename) != true)
                    files.Add(AlbumFile.WithLazyQuery(() => Searcher.InferSongQuery(candidate.Filename, trackQuery), candidate));
            }
            // Even when all observed files are excluded, full-folder retrieval
            // may discover other files. NoBrowseFolder is handled after settings resolution.
            var folder = new AlbumFolder(directory.Username, directory.FolderPath, files)
            { IsFullyRetrieved = directory.IsFullyRetrieved };
            return (new AlbumJob(query)
            {
                ResolvedTarget = folder,
                ValidationRequirement = AlbumValidationRequirement.UserAccepted,
                ExcludedFilenames = excludedFilenames,
            }, null);
        }
        if (item.TrackGroup is { } group)
        {
            var candidates = new List<FileCandidate>();
            if (chosenOption == null && request.UseRepresentativeWhenUnchosen == true)
            {
                if (group.Representative.Input.Visibility == SearchResultVisibility.Public)
                    candidates.Add(group.Representative.Input.ToFileCandidate());
            }
            else if (chosenOption != null)
            {
                if (resolvedChoices?.Tracks.GetValueOrDefault(item.Ref) is { } chosenTrack)
                    candidates.Add(chosenTrack.Input.ToFileCandidate());
            }
            else
            {
                foreach (SearchViewFile option in snapshot.ReadPublicTrackGroupOptions(group.Ref, cancellationToken))
                {
                    candidates.Add(option.Input.ToFileCandidate());
                }
            }
            if (candidates.Count == 0)
                return (null, chosenOption == null ? "locked" : "unavailable-option");
            SongQuery query = group.Query;
            var song = new SongJob(query) { Candidates = candidates };
            if (chosenOption != null || request.UseRepresentativeWhenUnchosen == true)
            {
                song.ResolvedTarget = candidates.Single();
                song.ExactTarget = song.ResolvedTarget.Target;
            }
            return (song, null);
        }
        if (item.AlbumGroup is { } albumGroup)
        {
            if (albumFileSelection != null && chosenOption == null)
                throw new ArgumentException("Choosing album files requires an exact album option.");
            AlbumQuery query = albumGroup.Query;
            var defaultTrackQuery = new SongQuery
            {
                Artist = query.Artist,
                Album = query.Album,
                URI = query.URI,
                ArtistMaybeWrong = query.ArtistMaybeWrong,
            };
            var folders = new List<AlbumFolder>();
            IEnumerable<SearchViewDirectory> EligibleOptions()
            {
                if (chosenOption == null && request.UseRepresentativeWhenUnchosen == true)
                {
                    yield return albumGroup.Representative;
                    yield break;
                }
                if (chosenOption != null)
                {
                    if (resolvedChoices?.Albums.GetValueOrDefault(item.Ref) is { } chosenAlbum)
                        yield return chosenAlbum;
                    yield break;
                }
                foreach (SearchViewDirectory option in snapshot.ReadPublicAlbumGroupOptions(albumGroup.Ref, cancellationToken))
                {
                    yield return option;
                }
            }
            foreach (SearchViewDirectory option in EligibleOptions())
            {
                var files = new List<AlbumFile>();
                var selectedFileRefs = albumFileSelection == null
                    ? null : new HashSet<string>(albumFileSelection.Refs, StringComparer.Ordinal);
                if (selectedFileRefs != null)
                {
                    // A locked child is still a valid selection in this revision;
                    // only public children may become download candidates.
                    var memberships = snapshot.ReadDirectoryFileRefs(selectedFileRefs, cancellationToken);
                    var matchedFileRefs = new HashSet<string>(memberships
                        .Where(member => StringComparer.Ordinal.Equals(member.DirectoryRef, option.Ref))
                        .Select(member => member.Ref), StringComparer.Ordinal);
                    if (!selectedFileRefs.SetEquals(matchedFileRefs))
                        throw new ArgumentException("An album file ref is not in the chosen directory and revision.");
                }
                foreach (SearchViewDirectoryFile child in snapshot.ReadPublicDirectoryFiles(option.Ref, cancellationToken))
                {
                    if (albumFileSelection != null &&
                        (albumFileSelection.Mode == RefSelectionMode.Only) != selectedFileRefs!.Contains(child.Ref))
                        continue;
                    FileCandidate candidate = child.File.Input.ToFileCandidate();
                    files.Add(AlbumFile.WithLazyQuery(
                        () => Searcher.InferSongQuery(
                            candidate.Filename,
                            defaultTrackQuery),
                        candidate));
                }
                if (files.Count == 0)
                    continue;
                folders.Add(new AlbumFolder(
                    option.Username,
                    option.FolderPath,
                    files)
                {
                    IsFullyRetrieved = option.IsFullyRetrieved,
                });
            }
            if (folders.Count == 0)
                return (null, chosenOption == null ? "locked" : "unavailable-option");
            var job = new AlbumJob(query) { Results = folders };
            if (chosenOption != null || request.UseRepresentativeWhenUnchosen == true)
            {
                // An explicit source choice is accepted input, not an automatic
                // candidate to reject against discovery conditions again.
                job.ResolvedTarget = folders.Single();
                job.ValidationRequirement = AlbumValidationRequirement.UserAccepted;
            }
            if (albumFileSelection != null)
            {
                // Exact child selection must not be expanded by the album executor's
                // optional full-folder browse before transfers begin.
                job.DirectoryResolutionPolicy = AlbumDirectoryResolutionPolicy.UseSelectedSnapshot;
            }
            return (job, null);
        }
        return (null, "unsupported-item");
    }

    private static void AddReason(
        IDictionary<(string Reason, string? Message), long> reasons,
        string reason,
        long amount = 1,
        string? message = null)
    {
        if (amount <= 0)
            return;
        var key = (reason, message);
        reasons[key] = reasons.TryGetValue(key, out long count)
            ? checked(count + amount)
            : amount;
    }

    private static IReadOnlyDictionary<string, SearchViewDirectoryPathRuleDto[]> GroupDirectoryPathRules(
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? rules)
    {
        if (rules is not { Count: > 0 })
            return new Dictionary<string, SearchViewDirectoryPathRuleDto[]>(StringComparer.Ordinal);
        var paths = new HashSet<(string DirectoryRef, string RelativePath)>();
        foreach (SearchViewDirectoryPathRuleDto rule in rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.DirectoryRef)
                || string.IsNullOrEmpty(rule.RelativePath) || rule.RelativePath.Contains('\\')
                || rule.RelativePath.Split('/').Any(part => part is "" or "." or "..")
                || !paths.Add((rule.DirectoryRef, rule.RelativePath)))
                throw new ArgumentException("Directory path rules must identify distinct relative subfolders.");
        }
        return rules.GroupBy(rule => rule.DirectoryRef, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(rule => rule.RelativePath.Length).ToArray(),
                StringComparer.Ordinal);
    }

    private static bool IsSelectedDirectoryFile(
        string relativePath,
        bool rootSelected,
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? rules)
    {
        if (rules == null) return rootSelected;
        string normalizedPath = relativePath.Replace('\\', '/');
        foreach (SearchViewDirectoryPathRuleDto rule in rules)
            if (normalizedPath.StartsWith(rule.RelativePath + "/", StringComparison.Ordinal))
                return rule.Selected;
        return rootSelected;
    }

    private static IReadOnlyDictionary<string, SearchViewDirectoryPathRuleDto[]> ValidateSelection(
        long revision,
        RefSelectionExpressionDto selection,
        IReadOnlyList<string>? included,
        IReadOnlyList<string>? excluded,
        IReadOnlyDictionary<string, string>? choices,
        IReadOnlyDictionary<string, RefSelectionExpressionDto>? albumFiles,
        IReadOnlyList<SearchViewDirectoryPathRuleDto>? pathRules)
    {
        SelectionExpressionValidator.Validate(selection);
        if (revision < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), "Search View revision cannot be negative.");
        if (excluded?.Any(string.IsNullOrWhiteSpace) == true || included?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("File exception refs cannot be empty.");
        if ((excluded ?? []).Intersect(included ?? [], StringComparer.Ordinal).Any())
            throw new ArgumentException("A file cannot be explicitly included and excluded at the same time.");
        if (choices?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) == true)
            throw new ArgumentException("Chosen group and option refs cannot be empty.");
        ValidateAlbumFileSelections(albumFiles);
        if (albumFiles?.Keys.Any(groupRef => choices?.ContainsKey(groupRef) != true) == true)
            throw new ArgumentException("Choosing album files requires an exact option for each group.");
        var selectedRefs = new HashSet<string>(selection.Refs, StringComparer.Ordinal);
        if ((choices?.Keys ?? []).Concat(albumFiles?.Keys ?? []).Any(groupRef =>
                selection.Mode == RefSelectionMode.Only
                    ? !selectedRefs.Contains(groupRef) : selectedRefs.Contains(groupRef)))
            throw new ArgumentException("An group choice must belong to a selected group.");
        return GroupDirectoryPathRules(pathRules);
    }

    private static void ValidateProjection(
        SearchViewInfo view,
        IReadOnlyList<string>? included,
        IReadOnlyList<string>? excluded,
        IReadOnlyDictionary<string, FileCandidateRefDto>? excludedIdentities,
        IReadOnlyDictionary<string, string>? choices,
        IReadOnlyDictionary<string, RefSelectionExpressionDto>? albumFiles,
        IReadOnlyDictionary<string, SearchViewDirectoryPathRuleDto[]> pathRules,
        bool useRepresentative = false)
    {
        ValidateExcludedIdentities(view.SourceJobId, excluded, excludedIdentities);
        if ((included is { Count: > 0 } || excluded is { Count: > 0 })
            && view.ProjectionKind is not (SearchViewProjectionKind.GenericDirectories or SearchViewProjectionKind.AlbumDirectories))
            throw new ArgumentException("File exceptions require a directory projection.");
        if ((choices is { Count: > 0 } || useRepresentative)
            && view.ProjectionKind is not (SearchViewProjectionKind.TrackGroups or SearchViewProjectionKind.AlbumGroups))
            throw new ArgumentException("Chosen options and representative-only selection require a grouped projection.");
        if (albumFiles is { Count: > 0 } && view.ProjectionKind != SearchViewProjectionKind.AlbumGroups)
            throw new ArgumentException("Album file selections require an album-group projection.");
        if (pathRules.Count > 0 && view.ProjectionKind != SearchViewProjectionKind.GenericDirectories)
            throw new ArgumentException("Directory path rules require a File Search projection.");
    }

    private static void ValidateAlbumFileSelections(
        IReadOnlyDictionary<string, RefSelectionExpressionDto>? selections)
    {
        if (selections?.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
            || pair.Value == null
            || pair.Value.Refs == null
            || pair.Value.Mode is not (RefSelectionMode.Only or RefSelectionMode.AllExcept)
            || pair.Value.Refs.Any(string.IsNullOrWhiteSpace)
            || (pair.Value.Mode == RefSelectionMode.Only && pair.Value.Refs.Count == 0)) == true)
            throw new ArgumentException("Album file selection group, mode, or file refs are invalid.");
    }

}
