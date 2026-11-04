using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using Sockseek.Core.Settings;
using Sockseek.Core.Events;
using Sockseek.Core.Snapshots;
using Sockseek.Core.PeerBrowsing;
using Sockseek.Core.Planning;

namespace Sockseek.Core.Services;


public partial class Searcher : IDisposable
{
    private readonly ISoulseekClient client;
    private readonly IUserSuccessStats userStats;
    private readonly DownloadEvents downloadEvents;
    private readonly SearchEvents searchEvents;
    private readonly RateLimitedSemaphore rateSemaphore;
    private readonly SemaphoreSlim concurrencySemaphore;
    private readonly TimeProvider timeProvider;
    private readonly IPeerDirectorySource directorySource;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<Searcher> logger;

    public Searcher(ISoulseekClient client,
                    IUserSuccessStats userStats,
                    DownloadEvents downloadEvents,
                    int searchesPerTime, int searchRenewTime, int concurrentSearches = 2,
                    SearchEvents? searchEvents = null,
                    TimeProvider? timeProvider = null,
                    IPeerDirectorySource? directorySource = null,
                    ILoggerFactory? loggerFactory = null)
    {
        this.client = client;
        this.userStats = userStats;
        this.downloadEvents = downloadEvents;
        this.searchEvents = searchEvents ?? new SearchEvents();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.directorySource = directorySource
            ?? client as IPeerDirectorySource
            ?? MissingPeerDirectorySource.Instance;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<Searcher>();
        rateSemaphore = new RateLimitedSemaphore(searchesPerTime, TimeSpan.FromSeconds(searchRenewTime));
        concurrencySemaphore = new SemaphoreSlim(concurrentSearches);
    }

    public void Dispose()
    {
        rateSemaphore.Dispose();
        concurrencySemaphore.Dispose();
        GC.SuppressFinalize(this);
    }


    // ── raw search job ──────────────────────────────────────────────────────

    private static void StartObservation(Job job, SearchSession session, SearchSettings search)
    {
        // Capture the settings actually searched, including replayed jobs, before
        // any observations are published. Queued jobs need no executed-search
        // payload in their lifecycle snapshots or history rows.
        job.ExecutedSearchDefinition ??= job is SearchJob { Definition: { } definition }
            ? definition : SearchDefinition.Create(job, search);
        job.SearchObservationSession = session;
    }

    private static void InitializeDiscoveryProgress(Job job)
    {
        if (job.Discovery is { RawResultCount: 0, LockedFileCount: 0, ObservedPeerCount: 0 })
            return;

        job.Discovery = new DiscoverySummary();
    }

    private void UpdateDiscoveryProgress(Job job, SearchSession session)
    {
        job.Discovery ??= new DiscoverySummary();
        var count = session.PublicFileCount;
        var locked = session.LockedFileCount;
        var peers = session.ObservedPeerCount;
        if (job.Discovery.RawResultCount == count
            && job.Discovery.LockedFileCount == locked
            && job.Discovery.ObservedPeerCount == peers)
            return;

        job.Discovery.RawResultCount = count;
        job.Discovery.LockedFileCount = locked;
        job.Discovery.ObservedPeerCount = peers;
        downloadEvents.RaiseJobDiscoveryChanged(job);
    }

    private void LogSessionObserverFailure(string observerName, Exception exception, Guid jobId)
        => DownloadLogMessages.SearchObserverFailed(
            logger,
            exception,
            observerName,
            jobId);

    public async Task<JobOutcome> Search(
        SearchJob job,
        SearchSettings search,
        ResponseData responseData,
        CancellationToken ct,
        Action? onSearch = null,
        bool completeSessionOnError = true,
        Job? phaseOwner = null)
    {
        var session = job.Session;
        var activityJob = phaseOwner ?? job;
        StartObservation(activityJob, session, search);
        session.ConfigureTimeProvider(timeProvider);
        void OnObserverFailed(string name, Exception ex)
            => LogSessionObserverFailure(name, ex, session.JobId);
        session.ObserverFailed += OnObserverFailed;
        InitializeDiscoveryProgress(activityJob);
        void OnSearchChange(CoreChange change) => downloadEvents.RaiseSearchChange(ChangeOwner(change, activityJob.Id));
        session.ChangePublished += OnSearchChange;
        using var progress = new DiscoveryProgress(this, activityJob, session);

        try
        {
            // Interactive SearchJob views can relax conditions later. Preserve
            // all observations here; sorting/filtering belongs to requested projections.
            SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf)
                => new(
                    minimumResponseFileCount: 0,
                    minimumPeerUploadSpeed: 0,
                    searchTimeout: timeout,
                    removeSingleCharacterSearchTerms: search.RemoveSingleCharSearchTerms,
                    responseFilter: _ => true,
                    fileFilter: _ => true);

            activityJob.UpdateActivity(JobActivityPhase.WaitingForSearchConcurrency);
            await concurrencySemaphore.WaitAsync(ct);
            try { await RunSearches(job.NetworkQuery, session, getOpts, session.AddResponse, search, ct, onSearch, activityJob); }
            finally { concurrencySemaphore.Release(); }

            activityJob.UpdateActivity(JobActivityPhase.ProcessingSearchResults);
            responseData.resultCount = session.ResultCount;
            responseData.lockedFilesCount += session.LockedFileCount;
            progress.Flush();
            if (!ReferenceEquals(activityJob, job))
                job.Discovery = new DiscoverySummary
                {
                    RawResultCount = session.PublicFileCount,
                    LockedFileCount = session.LockedFileCount,
                    ObservedPeerCount = session.ObservedPeerCount,
                };
            session.Complete();
            return JobOutcome.Done();
        }
        catch (OperationCanceledException)
        {
            progress.Flush();
            session.Complete();
            throw;
        }
        catch
        {
            progress.Flush();
            if (completeSessionOnError)
                session.Complete();
            throw;
        }
        finally
        {
            session.ChangePublished -= OnSearchChange;
            session.ObserverFailed -= OnObserverFailed;
        }
    }

    private static CoreChange ChangeOwner(CoreChange change, Guid ownerJobId)
        => change switch
        {
            SearchResultsAddedChange added when added.JobId != ownerJobId => added with { JobId = ownerJobId },
            SearchCompletedChange completed when completed.JobId != ownerJobId => completed with { JobId = ownerJobId },
            _ => change,
        };


    // ── song search ─────────────────────────────────────────────────────────

    // Populates song.Candidates (ordered best-first).
    // onFastSearchCandidate: called when a highly-ranked candidate is found early,
    // before the full search completes, so the engine can start a provisional download.
    public async Task SearchSong(SongJob song, SearchSettings search, ResponseData responseData,
        CancellationToken ct, Action? onSearch = null,
        Action<FileCandidate>? onFastSearchCandidate = null)
    {
        search = song.Config?.ResolveSearchSettings(search) ?? search;
        var session = new SearchSession(song.Id, timeProvider, song.Query.ToString(noInfo: true));
        bool readOnly = song.Config?.PrintResults == true;
        StartObservation(song, session, search);
        void OnObserverFailed(string name, Exception ex)
            => LogSessionObserverFailure(name, ex, session.JobId);
        session.ObserverFailed += OnObserverFailed;
        InitializeDiscoveryProgress(song);
        session.ChangePublished += downloadEvents.RaiseSearchChange;
        using var progress = new DiscoveryProgress(this, song, session);

        void responseHandler(SearchResponse r)
        {
            session.AddResponse(r);

            if (!readOnly && onFastSearchCandidate != null && search.FastSearch
                && userStats.GetCounts(song.WorkflowId).GetValueOrDefault(r.Username, 0) > search.DownrankOn
                && r.Files.FirstOrDefault() is { } f)
            {
                var candidate = SoulseekSearchAdapter.ToFileCandidate(r, f);
                if (r.HasFreeUploadSlot && r.UploadSpeed / 1024.0 / 1024.0 >= search.FastSearchMinUpSpeed
                    && ResultSorter.CheapBracketCheck(song.Query, f.Filename)
                    && ConditionSatisfactionPolicy.SearchFileSatisfies(search.PreferredCond, r, f, song.Query))
                {
                    onFastSearchCandidate(candidate);
                }
            }
        }

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf)
            => TrackSearchOptions(timeout, search, nec, song.Query, readOnly);

        song.UpdateActivity(JobActivityPhase.WaitingForSearchConcurrency);
        await concurrencySemaphore.WaitAsync(ct);
        try
        {
            await RunSearches(song.Query, session, getOpts, responseHandler, search, ct, onSearch, song);
        }
        finally
        {
            progress.Flush();
            session.Complete();
            session.ChangePublished -= downloadEvents.RaiseSearchChange;
            session.ObserverFailed -= OnObserverFailed;
            concurrencySemaphore.Release();
        }

        song.UpdateActivity(JobActivityPhase.ProcessingSearchResults);
        responseData.lockedFilesCount += session.LockedFileCount;

        responseData.resultCount = session.ResultCount;
        progress.Flush();

        DownloadLogMessages.SearchCompleted(logger, song.Id, session.ResultCount);

        song.Candidates = SearchResultProjector.SortedTrackCandidates(
            ProjectionInputs(session, includeLocked: song.Config?.PrintResults == true),
            song.Query,
            search,
            userStats.GetCounts(song.WorkflowId),
            useInfer: true);
    }


    // ── album search ─────────────────────────────────────────────────────────

    // Populates job.Results with candidate AlbumFolders found on the network.
    // Album quality/coverage conditions need whole folders, including tracks
    // that fail individual conditions. Early file filtering would silently turn
    // a mixed-quality folder into an apparently complete matching folder.
    public async Task<JobOutcome?> SearchAlbum(AlbumJob job, SearchSettings search, ResponseData responseData, CancellationToken ct)
    {
        search = job.Config?.ResolveSearchSettings(search) ?? search;
        var searchJob = new SearchJob(job.Query);
        var outcome = await Search(searchJob, search, responseData, ct, phaseOwner: job);
        if (outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
            return outcome;

        job.UpdateActivity(JobActivityPhase.ProcessingSearchResults);
        job.Results = searchJob.GetAlbumFolders(
            searchJob.DefaultFolderProjection! with { EnforceMinimumTrackCount = job.Config?.PrintResults == true },
            search, includeLocked: job.Config?.PrintResults == true).Items.ToList();
        return null;
    }

    // ── aggregate search ─────────────────────────────────────────────────────

    // Populates job.Songs: one SongJob per distinct inferred track version found.
    public async Task<JobOutcome?> SearchAggregate(AggregateJob job, SearchSettings search, ResponseData responseData, CancellationToken ct)
    {
        search = job.Config?.ResolveSearchSettings(search) ?? search;
        var session = new SearchSession(job.Id, timeProvider, job.Query.ToString(noInfo: true));
        bool readOnly = job.Config?.PrintResults == true;
        StartObservation(job, session, search);
        void OnObserverFailed(string name, Exception ex)
            => LogSessionObserverFailure(name, ex, session.JobId);
        session.ObserverFailed += OnObserverFailed;
        InitializeDiscoveryProgress(job);
        session.ChangePublished += downloadEvents.RaiseSearchChange;
        using var progress = new DiscoveryProgress(this, job, session);

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf)
            => TrackSearchOptions(timeout, search, nec, job.Query, readOnly);

        job.UpdateActivity(JobActivityPhase.WaitingForSearchConcurrency);
        await concurrencySemaphore.WaitAsync(ct);
        try { await RunSearches(job.Query, session, getOpts, session.AddResponse, search, ct, ownerJob: job); }
        finally
        {
            progress.Flush();
            session.Complete();
            session.ChangePublished -= downloadEvents.RaiseSearchChange;
            session.ObserverFailed -= OnObserverFailed;
            concurrencySemaphore.Release();
        }

        responseData.lockedFilesCount += session.LockedFileCount;
        responseData.resultCount = session.ResultCount;
        progress.Flush();
        job.UpdateActivity(JobActivityPhase.ProcessingSearchResults);
        job.Songs = SearchResultProjector.AggregateTracks(
            ProjectionInputs(session, includeLocked: job.Config?.PrintResults == true),
            job.Query, search, userStats.GetCounts(job.WorkflowId), requiredConditionsAlreadyApplied: true);
        foreach (SongJob song in job.Songs)
            SubmissionIdentity.AssignExecutionChild(job, song);
        return null;
    }

    // Returns new AlbumJobs (one per distinct album version found on the network).
    public async Task<(List<AlbumJob> Albums, JobOutcome? Outcome)> SearchAggregateAlbum(AlbumAggregateJob job, SearchSettings search, ResponseData responseData, CancellationToken ct)
    {
        search = job.Config?.ResolveSearchSettings(search) ?? search;
        var searchJob = new SearchJob(job.Query);
        var outcome = await Search(searchJob, search, responseData, ct, phaseOwner: job);
        if (outcome.TerminalOutcome is JobTerminalOutcome.Failed or JobTerminalOutcome.Cancelled)
            return ([], outcome);

        job.UpdateActivity(JobActivityPhase.ProcessingSearchResults);
        var folders = searchJob.GetAlbumFolders(
            new FolderSearchProjection(
                job.Query,
                IgnoreStringSortConditions: true,
                SortMode: FolderSortMode.DeterministicUnranked,
                EnforceMinimumTrackCount: job.Config?.PrintResults == true),
            search,
            includeLocked: job.Config?.PrintResults == true);
        var albums = SearchResultProjector.AggregateAlbums(folders.Items, job.Query, search);
        foreach (AlbumJob album in albums)
            SubmissionIdentity.AssignExecutionChild(job, album);
        return (albums, null);
    }

    // Print-only discovery is a read-only result view. Automatic/manual download
    // discovery still admits public candidates only, using the same projectors.
    private static IEnumerable<SearchProjectionInput> ProjectionInputs(SearchSession session, bool includeLocked)
        => session.RawSnapshot()
            .Where(row => includeLocked || row.Visibility == SearchResultVisibility.Public)
            .Select(row => row.ProjectionInput);

    // Automatic track execution retains admitted candidates. Filter with this network
    // attempt's conditions before admission: rejected hits must neither start a
    // fast download nor suppress RunSearches' fallback attempts. Those attempts
    // can deliberately relax conditions; counting all raw hits would change them.
    // It also avoids retaining/persisting unusable hits solely for automatic work.
    // Interactive searches and whole-folder album projections intentionally differ.
    // Print-only discovery can show locked-only peers and unknown upload speeds;
    // these observations must neither enable automatic nor provisional downloads.
    private static SearchOptions TrackSearchOptions(
        int timeout, SearchSettings search, FileConditions necessary, SongQuery query, bool readOnly)
        => new(
            minimumResponseFileCount: readOnly ? 0 : 1,
            minimumPeerUploadSpeed: readOnly ? 0 : 1,
            searchTimeout: timeout,
            removeSingleCharacterSearchTerms: search.RemoveSingleCharSearchTerms,
            responseFilter: response => (readOnly || response.UploadSpeed > 0) && necessary.UserSatisfies(response),
            fileFilter: file => ConditionSatisfactionPolicy.SearchFileSatisfies(necessary, null, file, query));



    // ── folder browse ────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves one exact peer directory into a Sockseek-owned snapshot. This
    /// boundary deliberately creates no album query or search response.
    /// </summary>
    public Task<PeerDirectorySnapshot> RetrieveDirectory(
        PeerDirectoryIdentity directory,
        CancellationToken? ct = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return directorySource.RetrieveDirectoryAsync(
            directory,
            ct ?? CancellationToken.None);
    }

    // Appends any new files found in the remote folder to folder.Files.
    // Returns the number of newly added files.
    public async Task<int> CompleteFolder(AlbumFolder folder, CancellationToken? ct = null)
    {
        int newFiles = 0;
        try
        {
            PeerDirectorySnapshot snapshot;
            try
            {
                snapshot = await RetrieveDirectory(folder.DirectoryIdentity, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                DownloadLogMessages.FolderCompletionFailed(
                    logger,
                    e,
                    DirectoryHash(folder));
                return 0;
            }

            newFiles = ApplyDirectorySnapshot(folder, snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DownloadLogMessages.FolderCompletionFailed(
                logger,
                ex,
                DirectoryHash(folder));
        }
        return newFiles;
    }

    private static string DirectoryHash(AlbumFolder folder)
        => LogIdentity.Hash(
            folder.DirectoryIdentity.Username
            + "\0"
            + folder.DirectoryIdentity.FolderPath);

    /// <summary>
    /// Associates a generic retrieval result back with an album candidate. The
    /// retrieval snapshot itself remains free of album evidence.
    /// </summary>
    public static int ApplyDirectorySnapshot(
        AlbumFolder folder,
        PeerDirectorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (folder.DirectoryIdentity != snapshot.Identity)
            throw new ArgumentException("Retrieved directory identity does not match the album candidate.", nameof(snapshot));

        var existing = folder.Files
            .Select(file => file.Filename.Replace('/', '\\'))
            .ToHashSet(StringComparer.Ordinal);
        var firstInfo = folder.Files.FirstOrDefault(file => !file.IsNotAudio)?.Query ?? new SongQuery();
        var representative = folder.Files.FirstOrDefault()?.Candidate;
        var peer = new SearchPeerSnapshot(
            folder.Username,
            snapshot.Files.Count,
            representative?.UploadSpeed,
            representative?.HasFreeUploadSlot);
        int added = 0;

        foreach (var target in snapshot.Files)
        {
            string filename = target.Filename;
            if (existing.Contains(filename))
                continue;

            var candidate = new FileCandidate(target, peer);
            var info = InferSongQuery(
                filename,
                new SongQuery { Artist = firstInfo.Artist, Album = firstInfo.Album });
            folder.Files.Add(new AlbumFile(info, candidate));
            existing.Add(filename.Replace('/', '\\'));
            added++;
        }

        folder.IsFullyRetrieved = snapshot.IsComplete;
        return added;
    }

    internal static string GetBrowseFilePath(string dir, string filename)
    {
        string normalizedDir = dir.Replace('/', '\\').TrimEnd('\\');
        string normalizedFilename = filename.Replace('/', '\\');

        if (normalizedDir.Length == 0 || normalizedFilename.StartsWith(normalizedDir + "\\", StringComparison.Ordinal))
            return normalizedFilename;

        return normalizedDir + "\\" + normalizedFilename.TrimStart('\\');
    }

    // ── query inference ───────────────────────────────────────────────────────

    [GeneratedRegex(@"^(?:(?:[0-9][-\.])?\d{2,3}[. -]|\b\d\.\s|\b\d\s-\s)(?=.+\S)")]
    private static partial Regex TrackNumStartRegex();

    [GeneratedRegex(@"(?<= - )((\d-)?\d{2,3}|\d{2,3}\.?)\s+")]
    private static partial Regex TrackNumMiddleRegex();

    [GeneratedRegex(@"\s+-(\d{2,3})-\s+")]
    private static partial Regex TrackNumMiddleAltRegex();

    [GeneratedRegex(@"-\s*<<tracknum>>\s*-")]
    private static partial Regex TrackNumPlaceholderRegex();

    public static SongQuery InferSongQuery(string filename, SongQuery defaultQuery)
    {
        string artist = defaultQuery.Artist;
        string title = defaultQuery.Title;
        string album = defaultQuery.Album;
        bool artistMaybeWrong = defaultQuery.ArtistMaybeWrong;

        filename = Utils.GetFileNameWithoutExtSlsk(filename);

        // Special case: "(NN) [Artist] Title"
        if (filename.Length >= 6 && filename[0] == '(' && char.IsDigit(filename[1]) && char.IsDigit(filename[2])
            && filename[3] == ')' && filename[4] == ' ' && filename[5] == '[')
        {
            int close = filename.IndexOf(']', 6);
            if (close > 6)
            {
                int titleStart = close + 1;
                if (titleStart < filename.Length && filename[titleStart] == ' ') titleStart++;
                if (titleStart < filename.Length)
                {
                    artist = filename[6..close];
                    title = filename[titleStart..];
                    return new SongQuery(defaultQuery) { Artist = artist.RemoveFt(), Title = title.RemoveFt(), ArtistMaybeWrong = false };
                }
            }
        }

        filename = filename.Replace(" — ", " - ").Replace('_', ' ').Trim().RemoveConsecutiveWs();

        if (TrackNumStartRegex().IsMatch(filename))
        {
            filename = TrackNumStartRegex().Replace(filename, "", 1).Trim();
            if (filename.StartsWith("- ")) filename = filename[2..].Trim();
        }
        else
        {
            var reg = TrackNumMiddleRegex().IsMatch(filename) ? TrackNumMiddleRegex()
                    : TrackNumMiddleAltRegex().IsMatch(filename) ? TrackNumMiddleAltRegex() : null;
            if (reg != null && !reg.IsMatch(defaultQuery.ToString(noInfo: true)))
            {
                filename = reg.Replace(filename, "<<tracknum>>", 1).Trim();
                filename = TrackNumPlaceholderRegex().Replace(filename, "-");
                filename = filename.Replace("<<tracknum>>", "");
            }
        }

        string aname = artist.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        string tname = title.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        string alname = album.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveFt().RemoveConsecutiveWs().Trim();
        string fname = filename.Replace('—', '-').Replace('_', ' ').Replace('[', '(').Replace(']', ')').ReplaceInvalidChars("", true).RemoveConsecutiveWs().Trim();

        bool maybeRemix = aname.Length > 0 && Regex.IsMatch(fname, @$"\({Regex.Escape(aname)} .+\)", RegexOptions.IgnoreCase);
        string[] parts = fname.Split([" - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] realParts = filename.Split([" - "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != realParts.Length) realParts = parts;

        if (parts.Length == 1)
        {
            if (maybeRemix) artistMaybeWrong = true;
            title = parts[0];
        }
        else if (parts.Length == 2)
        {
            artist = realParts[0];
            title = realParts[1];
            if (maybeRemix)
                artistMaybeWrong = true;
            else if (!parts[0].ContainsIgnoreCase(aname) || !parts[1].ContainsIgnoreCase(tname))
                artistMaybeWrong = true;
        }
        else if (parts.Length == 3)
        {
            bool hasTitle = tname.Length > 0 && parts[2].ContainsIgnoreCase(tname);
            if (hasTitle) title = realParts[2];

            int artistPos = -1, albumPos = -1;
            if (aname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(aname)) artistPos = 0;
                else if (parts[1].ContainsIgnoreCase(aname)) artistPos = 1;
                else artistMaybeWrong = true;
            }
            if (alname.Length > 0)
            {
                if (parts[0].ContainsIgnoreCase(alname)) albumPos = 0;
                else if (parts[1].ContainsIgnoreCase(alname)) albumPos = 1;
            }
            if (artistPos >= 0 && artistPos == albumPos) { artistPos = 0; albumPos = 1; }
            if (artistPos == -1 && maybeRemix) { artistMaybeWrong = true; artistPos = 0; albumPos = 1; }

            if (artistPos == -1 && albumPos == -1)
            { artistMaybeWrong = true; artist = realParts[0] + " - " + realParts[1]; }
            else if (artistPos >= 0)
            { artist = parts[artistPos]; }

            title = parts[2];
        }
        else
        {
            if (aname.Length > 0)
            {
                var s = parts.Select((p, i) => (p, i)).Where(x => x.p.ContainsIgnoreCase(aname)).ToList();
                if (s.Count > 0)
                {
                    int pos = s.MinBy(x => Math.Abs(x.p.Length - aname.Length)).i;
                    artist = parts[pos];
                }
            }
            if (tname.Length > 0)
            {
                int artistPos2 = artist == defaultQuery.Artist ? -1 :
                    parts.Select((p, i) => (p, i)).FirstOrDefault(x => x.p == artist).i;
                var ss = parts.Select((p, i) => (p, i)).Where(x => x.i != artistPos2 && x.p.ContainsIgnoreCase(tname)).ToList();
                if (ss.Count > 0)
                    title = parts[ss.MinBy(x => Math.Abs(x.p.Length - tname.Length)).i];
            }
        }

        if (title.Length == 0)
        {
            title = fname;
            artistMaybeWrong = true;
        }
        else if (artist.Length > 0 && !title.ContainsIgnoreCase(defaultQuery.Title) && !artist.ContainsIgnoreCase(defaultQuery.Artist))
        {
            string[] x = [artist, album, title];
            var perm = (0, 1, 2);
            (int, int, int)[] permutations = [(0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)];
            foreach (var p in permutations)
            {
                if (x[p.Item1].ContainsIgnoreCase(defaultQuery.Artist) && x[p.Item3].ContainsIgnoreCase(defaultQuery.Title))
                { perm = p; break; }
            }
            artist = x[perm.Item1];
            album = x[perm.Item2];
            title = x[perm.Item3];
        }

        return new SongQuery(defaultQuery)
        {
            Artist = artist.RemoveFt().Trim(),
            Title = title.RemoveFt().Trim(),
            Album = album.Trim(),
            ArtistMaybeWrong = artistMaybeWrong,
        };
    }

    public static IEnumerable<(SongQuery query, IEnumerable<FileCandidate> candidates)> EquivalentFiles(
        SongQuery query,
        IEnumerable<(SlResponse, SlFile)> fileResponses,
        SearchSettings search,
        int minShares = -1)
    {
        if (minShares == -1) minShares = search.EffectiveMinSharesAggregate;

        SongQuery infer((SearchResponse r, Soulseek.File f) x)
        {
            var q = InferSongQuery(x.f.Filename, query);
            return new SongQuery(q) { Length = x.f.Length ?? -1 };
        }

        return Group();

        IEnumerable<(SongQuery query, IEnumerable<FileCandidate> candidates)> Group()
        {
            var groups = new SongQueryGroups<(SongQuery Query, List<(SlResponse Response, SlFile File)> Files)>(
                search.AggregateLengthTol, static (key, _) => (key, []));
            foreach (var input in fileResponses) groups.GetOrAdd(infer(input)).Files.Add(input);

            foreach (var group in groups.Values
                .Select(group => (Group: group, Shares: group.Files.Select(file => file.Response.Username).Distinct().Count()))
                .Where(group => group.Shares >= minShares)
                .OrderByDescending(group => group.Shares))
            {
                var inferQ = group.Group.Query;
                // fill in length from results if unknown
                if (inferQ.Length == -1)
                {
                    int len = group.Group.Files.FirstOrDefault(file => file.File.Length != null).File?.Length ?? -1;
                    inferQ = new SongQuery(inferQ) { Length = len };
                }
                yield return (inferQ, group.Group.Files.Select(file => SoulseekSearchAdapter.ToFileCandidate(file.Response, file.File)));
            }
        }
    }

    public static bool AlbumsAreSimilar(AlbumFolder f1, AlbumFolder f2, int[]? f1SortedLengths = null, int tolerance = 3)
    {
        var audio1 = f1.Files.Where(f => !f.IsNotAudio).ToList();
        var audio2 = f2.Files.Where(f => !f.IsNotAudio).ToList();
        if (audio1.Count != audio2.Count) return false;

        f1SortedLengths ??= audio1.Select(f => f.Candidate.Length ?? -1).OrderBy(x => x).ToArray();
        var s2 = audio2.Select(f => f.Candidate.Length ?? -1).OrderBy(x => x).ToArray();

        return AlbumLengthsAreSimilar(f1SortedLengths, s2, tolerance);
    }

    internal static bool AlbumLengthsAreSimilar(ReadOnlySpan<int> first, ReadOnlySpan<int> second, int tolerance)
    {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
            if (Math.Abs((long)first[i] - second[i]) > tolerance) return false;
        return true;
    }


    // ── internal search plumbing ──────────────────────────────────────────────

    private async Task RunSearches(SongQuery query, SearchSession session,
        Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions,
        Action<SearchResponse> responseHandler, SearchSettings search,
        CancellationToken? ct = null, Action? onSearch = null, Job? ownerJob = null)
    {
        var batches = SearchAttemptPlanner.Create(query, search);
        for (int i = 0; i < batches.Count; i++)
        {
            // Track execution counts admitted matches; raw album/user discovery
            // keeps its existing collection semantics while applying fallbacks.
            if (i > 0 && session.ResultCount != 0) break;
            await Task.WhenAll(batches[i].Select(attempt => DoSearch(
                attempt.Query,
                getSearchOptions(attempt.Timeout, attempt.Conditions, search.PreferredCond),
                responseHandler, ct, onSearch, ownerJob)));
        }
    }

    private async Task DoSearch(SearchQuery query, SearchOptions opts, Action<SearchResponse> rHandler,
        CancellationToken? ct = null, Action? onSearch = null, Job? ownerJob = null)
    {
        await rateSemaphore.WaitAsync(
            () =>
            {
                ownerJob?.UpdateActivity(JobActivityPhase.SearchRateLimited, rateSemaphore.NextResetTime);
                searchEvents.RaiseSearchRateLimited(rateSemaphore.NextResetTime);
            },
            () =>
            {
                if (ownerJob?.ActivityPhase == JobActivityPhase.SearchRateLimited)
                    ownerJob.UpdateActivity(JobActivityPhase.Searching);
                searchEvents.RaiseSearchResumed();
            },
            ct ?? CancellationToken.None);
        ownerJob?.UpdateActivity(JobActivityPhase.Searching);
        onSearch?.Invoke();
        await SoulseekSearchRunner.RunAsync(client, query, opts, rHandler, ct ?? CancellationToken.None, logger);
    }

    internal static string CleanSearchString(string str, bool removeSpecialChars)
    {
        str = str.ToLower();
        string old;
        if (removeSpecialChars)
        {
            old = str;
            str = str.ReplaceSpecialChars(" ").Trim().RemoveConsecutiveWs();
            if (str.Length == 0) str = old;
        }
        foreach (var banned in bannedTerms)
        {
            if (banned.All(x => str.Contains(x)))
                str = str.Replace(banned[0], string.Concat("*", banned[0].AsSpan(1)));
        }
        return str.Trim();
    }

    // copyright is joke
    public static readonly string[][] bannedTerms =
    [
        ["depeche", "mode"],
        ["beatles"],
        ["prince", "revolutions"],
        ["michael", "jackson"],
        ["coexist"],
        ["bob", "dylan"],
        ["enter", "shikari"],
        ["village", "people"],
        ["lenny", "kravitz"],
        ["beyonce"],
        ["beyoncé"],
        ["lady", "gaga"],
        ["jay", "z"],
        ["kanye", "west"],
        ["rihanna"],
        ["adele"],
        ["kendrick", "lamar"],
        ["romance", "bad"],
        ["born", "this", "way"],
        ["weeknd"],
        ["broken", "hearted"],
        ["highway", "61", "revisited"],
        ["west", "gold", "digger"],
        ["west", "good", "life"],
        ["hold", "my", "hand"],
        ["ymca"],
        ["navy", "in", "the"],
        ["macho"],
        ["west", "go"],
        ["hot", "cop"],
        ["phone", "sex", "over", "the"],
        ["minaj"],
        ["government", "hooker"],
        ["wayne", "lil"],
        ["mood", "4", "eva"],
        ["ghosts", "again"],
        ["purple", "rain"],
    ];
}
