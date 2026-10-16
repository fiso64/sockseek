using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Persistence.Planning;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class SearchViewStoreTests
{
    [TestMethod]
    public async Task ExpiredViewsDisappearAndPruneWithoutAWait()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
        await using var store = new SearchViewStore(
            directory.DatabasePath,
            clock);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.Files,
            "{}",
            retention: TimeSpan.FromMinutes(1));
        Assert.IsNotNull(await store.GetFilesAsync(view.Id, 0, null, 10));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNull(await store.GetFilesAsync(view.Id, 0, null, 10));
        var incomplete = new List<StoredSearchView>();
        await foreach (StoredSearchView item in store.ReadIncompleteAsync())
            incomplete.Add(item);
        Assert.AreEqual(0, incomplete.Count);
        Assert.AreEqual(1, await store.PruneExpiredAsync());
        Assert.IsNull(await store.GetAsync(view.Id));
    }

    [TestMethod]
    public async Task ImmutableRevisionsPageBySortKeyAndRowsSurviveRestart()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        string path = directory.DatabasePath;
        Guid viewId;
        SearchViewKernelUpdate firstUpdate;
        SearchViewKernelUpdate secondUpdate;
        var kernel = Kernel();
        firstUpdate = kernel.Apply(
            [Input(1, "slow", @"Music\Track.mp3", 192)],
            sourceRevision: 1,
            isComplete: false);
        secondUpdate = kernel.Apply(
            [Input(2, "fast", @"Music\Track.flac", 900)],
            sourceRevision: 2,
            isComplete: false);

        await using (var store = new SearchViewStore(path))
        {
            await store.InitializeAsync();
            StoredSearchView view = await store.CreateAsync(
                Guid.NewGuid(),
                SearchViewProjectionKind.Files,
                "{\"projection\":\"file\"}");
            viewId = view.Id;
            Assert.AreEqual(0, (await store.GetFilesAsync(view.Id, 0, null, 10))!.Items.Count);

            view = await store.PublishAsync(view.Id, firstUpdate, "Live");
            Assert.AreEqual(1L, view.Revision);
            view = await store.PublishAsync(view.Id, secondUpdate, "Live");
            Assert.AreEqual(2L, view.Revision);

            StoredSearchViewFilePage revisionOne = (await store.GetFilesAsync(
                view.Id, 1, null, 10))!;
            CollectionAssert.AreEqual(
                new[] { "slow" },
                revisionOne.Items.Select(item => item.Input.Username).ToArray());
            Assert.IsFalse(revisionOne.Items.Single().NecessaryConditionsSatisfied);

            StoredSearchViewFilePage firstPage = (await store.GetFilesAsync(
                view.Id, 2, null, 1))!;
            Assert.AreEqual(1, firstPage.Items.Count);
            Assert.IsNotNull(firstPage.NextPosition);
            StoredSearchViewFilePage secondPage = (await store.GetFilesAsync(
                view.Id, 2, firstPage.NextPosition, 1))!;
            CollectionAssert.AreEquivalent(
                new[] { "slow", "fast" },
                firstPage.Items.Concat(secondPage.Items)
                    .Select(item => item.Input.Username)
                    .ToArray());

            SearchViewKernelUpdate thirdUpdate = kernel.Apply(
                [Input(3, "later", @"Music\Track.ogg", 320)],
                sourceRevision: 3,
                isComplete: false);
            view = await store.PublishAsync(view.Id, thirdUpdate, "Live");
            Assert.AreEqual(3L, view.Revision);
        }

        await using (var reopened = new SearchViewStore(path))
        {
            await reopened.InitializeAsync();
            StoredSearchView view = (await reopened.GetAsync(viewId))!;
            Assert.AreEqual(3L, view.Revision);
            Assert.AreEqual(3L, view.Counters.ProjectedFileCount);
            StoredSearchViewFilePage revisionTwo = (await reopened.GetFilesAsync(
                viewId, 2, null, 10))!;
            Assert.AreEqual(2, revisionTwo.Items.Count);
            Assert.IsFalse(revisionTwo.Items.Single(item => item.Input.Username == "slow")
                .NecessaryConditionsSatisfied);
            Assert.IsTrue(revisionTwo.Items.Single(item => item.Input.Username == "fast")
                .NecessaryConditionsSatisfied);
            Assert.AreEqual(3, (await reopened.GetFilesAsync(viewId, 3, null, 10))!.Items.Count);
        }
    }

    [TestMethod]
    public async Task DirectoryVersionsAndChildrenPageAtTheExactBoundRevision()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.GenericDirectories,
            "{\"projection\":\"generic-directories\"}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions { MinBitrate = 256 },
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);

        view = await store.PublishAsync(view.Id, kernel.Apply(
            [Input(1, "Peer", @"Share\One\Track.mp3", 192)], 1, false), "Live");
        long firstRevision = view.Revision;
        view = await store.PublishAsync(view.Id, kernel.Apply(
            [Input(2, "Peer", @"Share\One\Track.flac", 900) with
            {
                Visibility = SearchResultVisibility.Locked,
            }], 2, false), "Live");
        long secondRevision = view.Revision;
        view = await store.PublishAsync(view.Id, kernel.Apply(
            [Input(3, "Other", @"Share\Two\Track.flac", 900)], 3, false), "Live");

        StoredSearchViewDirectoryPage first = (await store.GetDirectoriesAsync(
            view.Id, firstRevision, null, 10))!;
        Assert.AreEqual(1, first.Items.Count);
        Assert.AreEqual(1L, first.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(0L, first.Items[0].LockedMatchingFileCount);

        StoredSearchViewDirectoryPage second = (await store.GetDirectoriesAsync(
            view.Id, secondRevision, null, 10))!;
        Assert.AreEqual(1, second.Items.Count);
        Assert.AreEqual(1L, second.Items[0].PublicMatchingFileCount);
        Assert.AreEqual(1L, second.Items[0].LockedMatchingFileCount);
        Assert.AreEqual(first.Items[0].Ref, second.Items[0].Ref,
            "A directory keeps one stable opaque ref across versions.");

        StoredSearchViewDirectoryFilePage childOne = (await store.GetDirectoryFilesAsync(
            view.Id, second.Items[0].Ref, secondRevision, null, 1))!;
        Assert.AreEqual(1, childOne.Items.Count);
        Assert.IsNotNull(childOne.NextPosition);
        StoredSearchViewDirectoryFilePage childTwo = (await store.GetDirectoryFilesAsync(
            view.Id, second.Items[0].Ref, secondRevision, childOne.NextPosition, 1))!;
        CollectionAssert.AreEquivalent(
            new[] { "Track.mp3", "Track.flac" },
            childOne.Items.Concat(childTwo.Items)
                .Select(item => item.RelativePath)
                .ToArray());

        var selectedChild = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            secondRevision,
            SearchViewProjectionKind.GenericDirectories.ToString(),
            "Only",
            new HashSet<string>([childOne.Items[0].Ref], StringComparer.Ordinal)))
        {
            selectedChild.Add(item);
        }
        Assert.AreEqual(1, selectedChild.Count);
        Assert.AreEqual("DirectoryFile", selectedChild[0].Kind);
        Assert.AreEqual(second.Items[0].Ref, selectedChild[0].ParentRef);
        Assert.AreEqual(childOne.Items[0].Ref, selectedChild[0].File!.Ref);

        StoredSearchViewDirectoryPage pageOne = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, null, 1))!;
        StoredSearchViewDirectoryPage pageTwo = (await store.GetDirectoriesAsync(
            view.Id, view.Revision, pageOne.NextPosition, 1))!;
        Assert.AreEqual(2, pageOne.Items.Concat(pageTwo.Items)
            .Select(item => item.Ref).Distinct(StringComparer.Ordinal).Count());

    }

    [TestMethod]
    public async Task RetrievedDirectoryPublishesTotalsAndChildrenWithoutChangingIssuingRevision()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.GenericDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);
        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(1, "Peer", @"Share\Album\Track.mp3", 192)],
                1,
                false),
            "Live");
        long issuingRevision = view.Revision;
        StoredSearchViewDirectory issued = (await store.GetDirectoriesAsync(
            view.Id,
            issuingRevision,
            null,
            10))!.Items.Single();

        var snapshot = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Share\Album"),
            [
                new PeerFileTarget(
                    new PeerFileIdentity("Peer", @"Share\Album\Track.mp3"),
                    1000,
                    ".mp3",
                    bitRate: 192),
                new PeerFileTarget(
                    new PeerFileIdentity("Peer", @"Share\Album\Cover.jpg"),
                    2000,
                    ".jpg"),
            ],
            isComplete: true);
        var sorter = new IncrementalResultSorter(
            new SongQuery { Title = "Track" },
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(),
            retainProjectedRows: false);
        ProjectedFileCandidate[] projected = sorter.AddRangeAndGetProjected(
            [
                Input(2, "Peer", @"Share\Album\Track.mp3", 192),
                Input(3, "Peer", @"Share\Album\Cover.jpg", 0),
            ]).ToArray();

        var mismatched = new PeerDirectorySnapshot(
            new PeerDirectoryIdentity("Peer", @"Share\album"),
            snapshot.Files,
            isComplete: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.PublishRetrievedDirectoryAsync(
                view.Id,
                issued.Ref,
                mismatched,
                projected));
        Assert.AreEqual(issuingRevision, (await store.GetAsync(view.Id))!.Revision);

        StoredSearchViewDirectoryPublishResult published = await store
            .PublishRetrievedDirectoryAsync(
                view.Id,
                issued.Ref,
                snapshot,
                projected);
        Assert.AreEqual(1, published.NewFileCount);
        Assert.AreEqual(issuingRevision + 1, published.View.Revision);

        StoredSearchViewDirectory current = (await store.GetDirectoriesAsync(
            view.Id,
            published.View.Revision,
            null,
            10))!.Items.Single();
        Assert.IsTrue(current.IsFullyRetrieved);
        Assert.AreEqual(2L, current.RetrievedFileCount);
        Assert.AreEqual(3000L, current.RetrievedBytes);
        Assert.AreEqual(2, (await store.GetDirectoryFilesAsync(
            view.Id,
            issued.Ref,
            published.View.Revision,
            null,
            10))!.Items.Count);

        StoredSearchViewDirectory old = (await store.GetDirectoriesAsync(
            view.Id,
            issuingRevision,
            null,
            10))!.Items.Single();
        Assert.IsFalse(old.IsFullyRetrieved);
        Assert.IsNull(old.RetrievedFileCount);
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(
            view.Id,
            issued.Ref,
            issuingRevision,
            null,
            10))!.Items.Count);
    }

    [TestMethod]
    public async Task DirectSelectionReadsExactDiskBackedRevisionAndStreamsLockedRowsIndependently()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        string path = directory.DatabasePath;
        Guid viewId;
        long viewRevision;
        string[] selectedRefs;
        await using (var store = new SearchViewStore(path))
        {
            await store.InitializeAsync();
            StoredSearchView view = await store.CreateAsync(
                Guid.NewGuid(),
                SearchViewProjectionKind.Files,
                "{}");
            var kernel = Kernel();
            view = await store.PublishAsync(
                view.Id,
                kernel.Apply(
                    [
                        Input(1, "Public", @"Share\Track.mp3", 192),
                        Input(2, "Locked", @"Share\Track.flac", 900) with
                        {
                            Visibility = SearchResultVisibility.Locked,
                        },
                    ],
                    1,
                    false),
                "Live");
            StoredSearchViewFilePage page = (await store.GetFilesAsync(
                view.Id,
                view.Revision,
                null,
                10))!;
            viewId = view.Id;
            viewRevision = view.Revision;
            selectedRefs = page.Items.Select(file => file.Ref).ToArray();
        }

        await using (var reopened = new SearchViewStore(path))
        {
            await reopened.InitializeAsync();
            StoredSearchView view = (await reopened.GetAsync(viewId))!;
            Assert.AreEqual(SearchViewProjectionKind.Files.ToString(), view.ProjectionKind);
            var selected = new List<StoredSearchViewCommitItem>();
            await foreach (StoredSearchViewCommitItem item in reopened.ReadCommitItemsAsync(
                viewId,
                viewRevision,
                SearchViewProjectionKind.Files.ToString(),
                "Only",
                new HashSet<string>(selectedRefs, StringComparer.Ordinal)))
            {
                selected.Add(item);
            }
            Assert.AreEqual(2, selected.Count);
            Assert.AreEqual(1, selected.Count(item =>
                item.File?.Input.Visibility == SearchResultVisibility.Locked));

            var allExcept = new List<StoredSearchViewCommitItem>();
            await foreach (StoredSearchViewCommitItem item in reopened.ReadCommitItemsAsync(
                viewId,
                viewRevision,
                SearchViewProjectionKind.Files.ToString(),
                "AllExcept",
                new HashSet<string>([selectedRefs[0]], StringComparer.Ordinal)))
            {
                allExcept.Add(item);
            }
            Assert.AreEqual(1, allExcept.Count);
            Assert.AreNotEqual(selectedRefs[0], allExcept[0].Ref);
        }
    }

    [TestMethod]
    public async Task AlbumDirectoryRemovalKeepsOlderRevisionAndEndsCurrentMembership()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AlbumDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AlbumDirectories,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions { MaxTrackCount = 1 },
            },
            trackPeerIdentities: false);
        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(1, "Peer", @"ELO\Time\01. Track.flac", 900)],
                1,
                false),
            "Live");
        long presentRevision = view.Revision;
        StoredSearchViewDirectory present = (await store.GetDirectoriesAsync(
            view.Id,
            presentRevision,
            null,
            10))!.Items.Single();

        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(2, "Peer", @"ELO\Time\02. Track.flac", 900)],
                2,
                false),
            "Live");
        Assert.AreEqual(0, (await store.GetDirectoriesAsync(
            view.Id,
            view.Revision,
            null,
            10))!.Items.Count);
        Assert.AreEqual(1, (await store.GetDirectoriesAsync(
            view.Id,
            presentRevision,
            null,
            10))!.Items.Count);
        Assert.AreEqual(1, (await store.GetDirectoryFilesAsync(
            view.Id,
            present.Ref,
            presentRevision,
            null,
            10))!.Items.Count);
        Assert.IsNull(await store.GetDirectoryFilesAsync(
            view.Id,
            present.Ref,
            view.Revision,
            null,
            10));
    }

    [TestMethod]
    public async Task AggregateTrackSummaryAndAlternativesAreRevisionBoundAndPaged()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AggregateTracks,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateTracks,
                new SongQuery { Artist = "ELO" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                MinSharesAggregate = 1,
                Relax = true,
            },
            trackPeerIdentities: false);
        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(1, "Peer-A", @"ELO - Track.flac", 900)],
                1,
                false),
            "Live");
        long firstRevision = view.Revision;
        StoredSearchViewAggregateTrackGroup first = (await store.GetAggregateTracksAsync(
            view.Id,
            firstRevision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(1, first.ShareCount);

        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(2, "Peer-B", @"ELO - Track.mp3", 192)],
                2,
                false),
            "Live");
        StoredSearchViewAggregateTrackGroup current = (await store.GetAggregateTracksAsync(
            view.Id,
            view.Revision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(first.Ref, current.Ref);
        Assert.AreEqual(2, current.ShareCount);
        Assert.AreEqual(1, (await store.GetAggregateTrackOptionsAsync(
            view.Id,
            current.Ref,
            firstRevision,
            null,
            10))!.Items.Count);
        StoredSearchViewAggregateTrackOptionPage firstPage = (await store
            .GetAggregateTrackOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                null,
                1))!;
        Assert.IsNotNull(firstPage.NextPosition);
        StoredSearchViewAggregateTrackOptionPage secondPage = (await store
            .GetAggregateTrackOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                firstPage.NextPosition,
                1))!;
        Assert.AreEqual(2, firstPage.Items.Concat(secondPage.Items).Count());

        string optionRef = firstPage.Items[0].Ref;
        var selectedOption = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            view.Revision,
            SearchViewProjectionKind.AggregateTracks.ToString(),
            "Only",
            new HashSet<string>([optionRef], StringComparer.Ordinal)))
        {
            selectedOption.Add(item);
        }
        Assert.AreEqual(1, selectedOption.Count);
        Assert.AreEqual("AggregateTrackFile", selectedOption[0].Kind);
        CollectionAssert.Contains(selectedOption[0].ContainerRefs!.ToArray(), current.Ref);
    }

    [TestMethod]
    public async Task AggregateAlbumSummaryAndDirectoryAlternativesAreRevisionBound()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(
            directory.DatabasePath);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.AggregateAlbums,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.AggregateAlbums,
                AlbumQuery: new AlbumQuery { Artist = "ELO", Album = "Time" }),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
                NecessaryFolderCond = new FolderConditions(),
                MinSharesAggregate = 1,
            },
            trackPeerIdentities: false);
        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(1, "Peer-A", @"ELO\Time\01. Track.flac", 900)],
                1,
                false),
            "Live");
        long firstRevision = view.Revision;
        StoredSearchViewAggregateAlbumGroup first = (await store.GetAggregateAlbumsAsync(
            view.Id,
            firstRevision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(1, first.ShareCount);
        Assert.AreEqual(1, (await store.GetAggregateAlbumOptionsAsync(
            view.Id,
            first.Ref,
            firstRevision,
            null,
            10))!.Items.Count);

        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(
                [Input(2, "Peer-B", @"ELO\Time\01. Track.flac", 900)],
                2,
                false),
            "Live");
        StoredSearchViewAggregateAlbumGroup current = (await store.GetAggregateAlbumsAsync(
            view.Id,
            view.Revision,
            null,
            10))!.Items.Single();
        Assert.AreEqual(first.Ref, current.Ref);
        Assert.AreEqual(2, current.ShareCount);
        StoredSearchViewAggregateAlbumOptionPage pageOne = (await store
            .GetAggregateAlbumOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                null,
                1))!;
        StoredSearchViewAggregateAlbumOptionPage pageTwo = (await store
            .GetAggregateAlbumOptionsAsync(
                view.Id,
                current.Ref,
                view.Revision,
                pageOne.NextPosition,
                1))!;
        Assert.AreEqual(2, pageOne.Items.Concat(pageTwo.Items).Count());
        Assert.AreEqual(1, (await store.GetAggregateAlbumOptionsAsync(
            view.Id,
            current.Ref,
            firstRevision,
            null,
            10))!.Items.Count);

        StoredSearchViewDirectory selectedDirectory = pageOne.Items[0];
        StoredSearchViewDirectoryFile selectedFile = (await store.GetDirectoryFilesAsync(
            view.Id,
            selectedDirectory.Ref,
            view.Revision,
            null,
            10))!.Items.Single();
        var selectedNested = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            view.Revision,
            SearchViewProjectionKind.AggregateAlbums.ToString(),
            "Only",
            new HashSet<string>(
                [selectedDirectory.Ref, selectedFile.Ref],
                StringComparer.Ordinal)))
        {
            selectedNested.Add(item);
        }
        Assert.AreEqual(2, selectedNested.Count);
        StoredSearchViewCommitItem directoryItem = selectedNested.Single(item =>
            item.Kind == "AggregateAlbumDirectory");
        CollectionAssert.Contains(directoryItem.ContainerRefs!.ToArray(), current.Ref);
        StoredSearchViewCommitItem fileItem = selectedNested.Single(item =>
            item.Kind == "AggregateAlbumFile");
        CollectionAssert.Contains(fileItem.ContainerRefs!.ToArray(), current.Ref);
        CollectionAssert.Contains(fileItem.ContainerRefs!.ToArray(), selectedDirectory.Ref);
    }

    [TestMethod]
    public async Task LargeDirectoryPagesEveryChildAndResolvesExplicitChildSelectionWithoutACollectionLimit()
    {
        await using TemporaryDirectory directory = await TemporaryDirectory.CreateAsync();
        await using var store = new SearchViewStore(directory.DatabasePath);
        await store.InitializeAsync();
        StoredSearchView view = await store.CreateAsync(
            Guid.NewGuid(),
            SearchViewProjectionKind.GenericDirectories,
            "{}");
        var kernel = new SearchViewKernel(
            new SearchViewProjectionDefinition(
                SearchViewProjectionKind.GenericDirectories,
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions(),
                PreferredCond = new FileConditions(),
            },
            retainProjectedRows: false,
            trackPeerIdentities: false);
        const int childCount = SearchViewStore.MaximumPageSize + 5;
        SearchProjectionInput[] inputs = Enumerable.Range(1, childCount)
            .Select(index => Input(
                index,
                "Peer",
                $@"Share\Large\Track {index:D3}.flac",
                900))
            .ToArray();
        view = await store.PublishAsync(
            view.Id,
            kernel.Apply(inputs, childCount, false),
            "Live");
        StoredSearchViewDirectory parent = (await store.GetDirectoriesAsync(
            view.Id,
            view.Revision,
            null,
            1))!.Items.Single();

        var children = new List<StoredSearchViewDirectoryFile>();
        SearchViewDirectoryFilePosition? position = null;
        do
        {
            StoredSearchViewDirectoryFilePage page = (await store.GetDirectoryFilesAsync(
                view.Id,
                parent.Ref,
                view.Revision,
                position,
                SearchViewStore.MaximumPageSize))!;
            Assert.IsTrue(page.Items.Count <= SearchViewStore.MaximumPageSize);
            children.AddRange(page.Items);
            position = page.NextPosition;
        }
        while (position != null);
        Assert.AreEqual(childCount, children.Count);

        var selected = new List<StoredSearchViewCommitItem>();
        await foreach (StoredSearchViewCommitItem item in store.ReadCommitItemsAsync(
            view.Id,
            view.Revision,
            SearchViewProjectionKind.GenericDirectories.ToString(),
            "Only",
            new HashSet<string>(children.Select(child => child.Ref), StringComparer.Ordinal)))
        {
            selected.Add(item);
        }
        Assert.AreEqual(childCount, selected.Count);
        Assert.IsTrue(selected.All(item => item.Kind == "DirectoryFile"));
    }

    private static SearchViewKernel Kernel()
        => new(
            new FileSearchProjection(
                new SongQuery { Title = "Track" },
                IncludeFullResults: true),
            new SearchSettings
            {
                NecessaryCond = new FileConditions { Formats = ["flac"] },
                PreferredCond = new FileConditions { MinBitrate = 256 },
            });

    private static SearchProjectionInput Input(
        long sequence,
        string username,
        string filename,
        int bitrate)
        => new(
            sequence,
            checked((int)sequence),
            username,
            1,
            filename,
            1000,
            bitrate,
            null,
            44_100,
            180,
            Path.GetExtension(filename),
            bitrate * 100,
            true,
            null,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            0,
            SearchResultVisibility.Public);

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        private readonly SqliteDatabaseOwner owner;

        private TemporaryDirectory(string path, SqliteDatabaseOwner owner)
        {
            Path = path;
            this.owner = owner;
            DatabasePath = System.IO.Path.Combine(path, "sockseek.db");
        }

        public string Path { get; }
        public string DatabasePath { get; }

        public static async Task<TemporaryDirectory> CreateAsync()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sockseek-search-view-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            var options = new SockseekSqliteOptions(
                System.IO.Path.Combine(path, "sockseek.db"));
            SqliteDatabaseOwner owner = SqliteDatabaseOwner.Acquire(options);
            var factory = new SockseekDbContextFactory(
                SockseekDbContextOptions.Create(options));
            try
            {
                await new SqliteInitializer(factory, options, owner).InitializeAsync();
                return new TemporaryDirectory(path, owner);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
