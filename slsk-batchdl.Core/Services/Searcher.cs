using Soulseek;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core;
using SearchResponse = Soulseek.SearchResponse;
using SlResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using SlDictionary = System.Collections.Concurrent.ConcurrentDictionary<string, (Soulseek.SearchResponse, Soulseek.File)>;
using Sldl.Core.Settings;

namespace Sldl.Core.Services;


public partial class Searcher
{
    private readonly ISoulseekClient client;
    private readonly ISearchRegistry searchRegistry;
    private readonly IUserStats userStats;
    private readonly EngineEvents events;
    private readonly RateLimitedSemaphore rateSemaphore;
    private readonly SemaphoreSlim concurrencySemaphore;

    public Searcher(ISoulseekClient client,
                    ISearchRegistry searchRegistry,
                    IUserStats userStats,
                    EngineEvents events,
                    int searchesPerTime, int searchRenewTime, int concurrentSearches = 2)
    {
        this.client = client;
        this.searchRegistry = searchRegistry;
        this.userStats = userStats;
        this.events = events;
        rateSemaphore = new RateLimitedSemaphore(searchesPerTime, TimeSpan.FromSeconds(searchRenewTime));
        concurrencySemaphore = new SemaphoreSlim(concurrentSearches);
    }


    // ── raw search job ──────────────────────────────────────────────────────

    public async Task Search(SearchJob job, SearchSettings search, ResponseData responseData, CancellationToken ct, Action? onSearch = null)
    {
        var session = job.Session;
        job.UpdateState(JobState.Searching);

        try
        {
            SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf)
                => new(
                    minimumResponseFileCount: 0,
                    minimumPeerUploadSpeed: 0,
                    searchTimeout: timeout,
                    removeSingleCharacterSearchTerms: search.RemoveSingleCharSearchTerms,
                    responseFilter: _ => true,
                    fileFilter: _ => true);

            await concurrencySemaphore.WaitAsync(ct);
            try { await RunSearches(job.NetworkQuery, session.Results, getOpts, session.AddResponse, search, ct, onSearch); }
            finally { concurrencySemaphore.Release(); }

            responseData.lockedFilesCount += session.LockedFileCount;
            job.Discovery = new DiscoverySummary { ResultCount = session.Results.Count, LockedFileCount = session.LockedFileCount };
            job.SetDone();
        }
        catch (OperationCanceledException)
        {
            job.Fail(FailureReason.Cancelled);
            throw;
        }
        catch (Exception e)
        {
            job.Fail(FailureReason.Other, e.Message);
            throw;
        }
        finally
        {
            session.Complete();
        }
    }


    // ── song search ─────────────────────────────────────────────────────────

    // Populates song.Candidates (ordered best-first).
    // onFastSearchCandidate: called when a highly-ranked candidate is found early,
    // before the full search completes, so the engine can start a provisional download.
    public async Task SearchSong(SongJob song, SearchSettings search, ResponseData responseData,
        CancellationToken ct, Action? onSearch = null,
        Action<FileCandidate>? onFastSearchCandidate = null)
    {
        var session = new SearchSession();
        searchRegistry.Searches.TryAdd(song, new SearchInfo(session.Results));

        void responseHandler(SearchResponse r)
        {
            session.AddResponse(r);

            if (onFastSearchCandidate != null && search.FastSearch
                && userStats.UserSuccessCounts.GetValueOrDefault(r.Username, 0) > search.DownrankOn)
            {
                var f = r.Files.First();
                var candidate = new FileCandidate(r, f);
                if (r.HasFreeUploadSlot && r.UploadSpeed / 1024.0 / 1024.0 >= search.FastSearchMinUpSpeed
                    && ResultSorter.CheapBracketCheck(song.Query, f.Filename)
                    && search.PreferredCond.FileSatisfies(f, song.Query, r))
                {
                    onFastSearchCandidate(candidate);
                }
            }
        }

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf) =>
            new SearchOptions(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                searchTimeout: timeout,
                removeSingleCharacterSearchTerms: search.RemoveSingleCharSearchTerms,
                responseFilter: r => r.UploadSpeed > 0 && nec.UserSatisfies(r),
                fileFilter: f => nec.FileSatisfies(f, song.Query, null));

        song.UpdateState(JobState.Searching);
        await concurrencySemaphore.WaitAsync(ct);
        try
        {
            await RunSearches(song.Query, session.Results, getOpts, responseHandler, search, ct, onSearch);
        }
        finally
        {
            concurrencySemaphore.Release();
            searchRegistry.Searches.TryRemove(song, out _);
        }

        responseData.lockedFilesCount += session.LockedFileCount;

        song.Discovery ??= new DiscoverySummary();
        song.Discovery.ResultCount = session.Results.Count;

        SldlLog.Debug($"{session.Results.Count} results found: {song}");

        if (!session.Results.IsEmpty)
        {
            SldlLog.Debug(string.Join("\n", session.Results.Select(r => $"  {r.Value.Item1.Username}: {r.Value.Item2.Filename}")));
        }

        song.Candidates = SearchResultProjector.SortedTrackCandidates(
            session.Snapshot(),
            song.Query,
            search,
            userStats.UserSuccessCounts,
            useInfer: true);
    }


    // ── album search ─────────────────────────────────────────────────────────

    // Populates job.Results with candidate AlbumFolders found on the network.
    public async Task SearchAlbum(AlbumJob job, SearchSettings search, ResponseData responseData, CancellationToken ct)
    {
        var searchJob = new SearchJob(job.Query);
        job.UpdateState(JobState.Searching);
        await Search(searchJob, search, responseData, ct);
        job.Results = searchJob.GetAlbumFolders(search).Items.ToList();
    }

    // ── aggregate search ─────────────────────────────────────────────────────

    // Populates job.Songs: one SongJob per distinct inferred track version found.
    public async Task SearchAggregate(AggregateJob job, SearchSettings search, ResponseData responseData, CancellationToken ct)
    {
        var session = new SearchSession();

        SearchOptions getOpts(int timeout, FileConditions nec, FileConditions prf) =>
            new(
                minimumResponseFileCount: 1,
                minimumPeerUploadSpeed: 1,
                removeSingleCharacterSearchTerms: search.RemoveSingleCharSearchTerms,
                searchTimeout: timeout,
                responseFilter: r => r.UploadSpeed > 0 && nec.UserSatisfies(r),
                fileFilter: f => nec.FileSatisfies(f, job.Query, null));

        job.UpdateState(JobState.Searching);
        await concurrencySemaphore.WaitAsync(ct);
        try { await RunSearches(job.Query, session.Results, getOpts, session.AddResponse, search, ct); }
        finally { concurrencySemaphore.Release(); }

        responseData.lockedFilesCount += session.LockedFileCount;
        job.Songs = SearchResultProjector.AggregateTracks(session.Snapshot(), job.Query, search, userStats.UserSuccessCounts);
    }

    // Returns new AlbumJobs (one per distinct album version found on the network).
    public async Task<List<AlbumJob>> SearchAggregateAlbum(AlbumAggregateJob job, SearchSettings search, ResponseData responseData, CancellationToken ct)
    {
        job.UpdateState(JobState.Searching);
        var tempJob = new AlbumJob(job.Query);
        await SearchAlbum(tempJob, search, responseData, ct);
        return SearchResultProjector.AggregateAlbums(tempJob.Results, job.Query, search);
    }



    // ── folder browse ────────────────────────────────────────────────────────

    public async Task<List<(string dir, SlFile file)>> GetAllFilesInFolder(string user, string folderPrefix, CancellationToken? ct = null)
    {
        var res = new List<(string dir, SlFile file)>();
        folderPrefix = folderPrefix.Replace('/', '\\').TrimEnd('\\') + '\\';
        var userFileList = await client.BrowseAsync(user, new BrowseOptions(), ct);
        foreach (var dir in userFileList.Directories)
        {
            string dirname = dir.Name.Replace('/', '\\').TrimEnd('\\') + '\\';
            if (dirname.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                res.AddRange(dir.Files.Select(x => (dir.Name, x)));
        }
        return res;
    }

    // Appends any new files found in the remote folder to folder.Files.
    // Returns the number of newly added files.
    public async Task<int> CompleteFolder(AlbumFolder folder, CancellationToken? ct = null)
    {
        int newFiles = 0;
        try
        {
            List<(string dir, SlFile file)> allFiles;
            try
            {
                allFiles = await GetAllFilesInFolder(folder.Username, folder.FolderPath, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { SldlLog.Error($"Error getting all files in '{folder.FolderPath}': {e}"); return 0; }

            var existing = folder.Files
                .Select(f => f.ResolvedTarget!.Filename.Replace('/', '\\'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var firstInfo = folder.Files.FirstOrDefault(f => !f.IsNotAudio)?.Query ?? new SongQuery();
            var firstResp = folder.Files.FirstOrDefault()?.ResolvedTarget?.Response
                            ?? new SearchResponse(folder.Username, -1, false, -1, -1, null);

            foreach (var (dir, file) in allFiles)
            {
                string filename = GetBrowseFilePath(dir, file.Filename);
                if (existing.Contains(filename)) continue;

                newFiles++;
                var slFile = new SlFile(file.Code, filename, file.Size, file.Extension, file.Attributes);
                var candidate = new FileCandidate(firstResp, slFile);
                var info = InferSongQuery(filename, new SongQuery { Artist = firstInfo.Artist, Album = firstInfo.Album });
                folder.Files.Add(new SongJob(info) { ResolvedTarget = candidate });
            }

            folder.IsFullyRetrieved = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SldlLog.Error($"Error completing folder: {ex}");
        }
        return newFiles;
    }

    internal static string GetBrowseFilePath(string dir, string filename)
    {
        string normalizedDir = dir.Replace('/', '\\').TrimEnd('\\');
        string normalizedFilename = filename.Replace('/', '\\');

        if (normalizedDir.Length == 0 || normalizedFilename.StartsWith(normalizedDir + "\\", StringComparison.OrdinalIgnoreCase))
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
                var s = parts.Select((p, i) => (p, i)).Where(x => x.p.ContainsIgnoreCase(aname));
                if (s.Any())
                {
                    int pos = s.MinBy(x => Math.Abs(x.p.Length - aname.Length)).i;
                    artist = parts[pos];
                }
            }
            if (tname.Length > 0)
            {
                int artistPos2 = artist == defaultQuery.Artist ? -1 :
                    parts.Select((p, i) => (p, i)).FirstOrDefault(x => x.p == artist).i;
                var ss = parts.Select((p, i) => (p, i)).Where(x => x.i != artistPos2 && x.p.ContainsIgnoreCase(tname));
                if (ss.Any())
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
        if (minShares == -1) minShares = search.MinSharesAggregate;

        SongQuery infer((SearchResponse r, Soulseek.File f) x)
        {
            var q = InferSongQuery(x.f.Filename, query);
            return new SongQuery(q) { Length = x.f.Length ?? -1 };
        }

        return fileResponses
            .GroupBy(infer, new SongQueryComparer(ignoreCase: true, search.AggregateLengthTol))
            .Select(g => (g, g.Select(x => x.Item1.Username).Distinct().Count()))
            .Where(x => x.Item2 >= minShares)
            .OrderByDescending(x => x.Item2)
            .Select(x =>
            {
                var grp = x.g;
                var inferQ = grp.Key;
                // fill in length from results if unknown
                if (inferQ.Length == -1)
                {
                    int len = grp.FirstOrDefault(y => y.Item2.Length != null).Item2?.Length ?? -1;
                    inferQ = new SongQuery(inferQ) { Length = len };
                }
                return (inferQ, grp.Select(y => new FileCandidate(y.Item1, y.Item2)));
            });
    }

    public static bool AlbumsAreSimilar(AlbumFolder f1, AlbumFolder f2, int[]? f1SortedLengths = null, int tolerance = 3)
    {
        var audio1 = f1.Files.Where(f => !f.IsNotAudio).ToList();
        var audio2 = f2.Files.Where(f => !f.IsNotAudio).ToList();
        if (audio1.Count != audio2.Count) return false;

        f1SortedLengths ??= audio1.Select(f => f.ResolvedTarget!.File.Length ?? -1).OrderBy(x => x).ToArray();
        var s2 = audio2.Select(f => f.ResolvedTarget!.File.Length ?? -1).OrderBy(x => x).ToArray();

        for (int i = 0; i < f1SortedLengths.Length; i++)
            if (Math.Abs(f1SortedLengths[i] - s2[i]) > tolerance) return false;

        return true;
    }


    // ── internal search plumbing ──────────────────────────────────────────────

    public async Task RunSearches(SongQuery query, SlDictionary results,
        Func<int, FileConditions, FileConditions, SearchOptions> getSearchOptions,
        Action<SearchResponse> responseHandler, SearchSettings search,
        CancellationToken? ct = null, Action? onSearch = null)
    {
        bool artist = query.Artist.Length > 0;
        bool title = query.Title.Length > 0;
        bool album = query.Album.Length > 0;

        string searchStr = GetSearchString(query, isAlbum: false);
        var searchTasks = new List<Task>();
        bool noRemoveSpecialChars = search.NoRemoveSpecialChars;

        var defaultOpts = getSearchOptions(search.SearchTimeout, search.NecessaryCond, search.PreferredCond);
        searchTasks.Add(DoSearch(searchStr, defaultOpts, responseHandler, noRemoveSpecialChars, ct, onSearch));

        if (searchStr.RemoveDiacriticsIfExist(out string noDiacr) && !query.ArtistMaybeWrong)
            searchTasks.Add(DoSearch(noDiacr, defaultOpts, responseHandler, noRemoveSpecialChars, ct, onSearch));

        await Task.WhenAll(searchTasks);

        if (results.IsEmpty && query.ArtistMaybeWrong && title)
        {
            var inferred = InferSongQuery(query.Title, new SongQuery());
            var cond = new FileConditions(search.NecessaryCond) { StrictTitle = inferred.Title == query.Title, StrictArtist = false };
            var opts = getSearchOptions(Math.Min(search.SearchTimeout, 5000), cond, search.PreferredCond);
            searchTasks.Add(DoSearch($"{inferred.Artist} {inferred.Title}", opts, responseHandler, noRemoveSpecialChars, ct, onSearch));
        }

        if (search.DesperateSearch)
        {
            await Task.WhenAll(searchTasks);

            if (results.IsEmpty && !query.ArtistMaybeWrong)
            {
                if (artist && album && title)
                {
                    var cond = new FileConditions(search.NecessaryCond) { StrictTitle = true, StrictAlbum = true };
                    searchTasks.Add(DoSearch($"{query.Artist} {query.Album}",
                        getSearchOptions(Math.Min(search.SearchTimeout, 5000), cond, search.PreferredCond),
                        responseHandler, noRemoveSpecialChars, ct, onSearch));
                }
                if (artist && title && query.Length != -1 && search.NecessaryCond.LengthTolerance != -1)
                {
                    var cond = new FileConditions(search.NecessaryCond) { LengthTolerance = -1, StrictTitle = true, StrictArtist = true };
                    searchTasks.Add(DoSearch($"{query.Artist} {query.Title}",
                        getSearchOptions(Math.Min(search.SearchTimeout, 5000), cond, search.PreferredCond),
                        responseHandler, noRemoveSpecialChars, ct, onSearch));
                }
            }

            await Task.WhenAll(searchTasks);

            if (results.IsEmpty)
            {
                var q2 = query.ArtistMaybeWrong ? InferSongQuery(query.Title, new SongQuery()) : query;

                if (query.Album.Length > 3 && album)
                {
                    var cond = new FileConditions(search.NecessaryCond)
                    { StrictAlbum = true, StrictTitle = !query.ArtistMaybeWrong, StrictArtist = !query.ArtistMaybeWrong, LengthTolerance = -1 };
                    searchTasks.Add(DoSearch(query.Album, getSearchOptions(Math.Min(search.SearchTimeout, 5000), cond, search.PreferredCond),
                        responseHandler, noRemoveSpecialChars, ct, onSearch));
                }
                if (q2.Title.Length > 3 && artist)
                {
                    var cond = new FileConditions(search.NecessaryCond)
                    { StrictTitle = !query.ArtistMaybeWrong, StrictArtist = !query.ArtistMaybeWrong, LengthTolerance = -1 };
                    searchTasks.Add(DoSearch(q2.Title, getSearchOptions(Math.Min(search.SearchTimeout, 5000), cond, search.PreferredCond),
                        responseHandler, noRemoveSpecialChars, ct, onSearch));
                }
                if (q2.Artist.Length > 3 && title)
                {
                    var cond = new FileConditions(search.NecessaryCond)
                    { StrictTitle = !query.ArtistMaybeWrong, StrictArtist = !query.ArtistMaybeWrong, LengthTolerance = -1 };
                    searchTasks.Add(DoSearch(q2.Artist, getSearchOptions(Math.Min(search.SearchTimeout, 5000), cond, search.PreferredCond),
                        responseHandler, noRemoveSpecialChars, ct, onSearch));
                }
            }
        }

        await Task.WhenAll(searchTasks);
    }

    private async Task DoSearch(string search, SearchOptions opts, Action<SearchResponse> rHandler,
        bool noRemoveSpecialChars, CancellationToken? ct = null, Action? onSearch = null)
    {
        await rateSemaphore.WaitAsync(() => events.RaiseSearchRateLimited(rateSemaphore.NextResetTime), events.RaiseSearchResumed, ct ?? CancellationToken.None);
        search = CleanSearchString(search, !noRemoveSpecialChars);
        var q = SearchQuery.FromText(search);
        onSearch?.Invoke();
        await client.SearchAsync(q, options: opts, cancellationToken: ct, responseHandler: rHandler);
    }

    private static string GetSearchString(SongQuery query, bool isAlbum)
    {
        if (isAlbum)
        {
            if (query.Album.Length > 0)
                return (query.Artist + " " + query.Album).Trim();
            if (query.Title.Length > 0)
                return (query.Artist + " " + query.Title).Trim();
            return query.Artist.Trim();
        }
        else
        {
            if (query.Title.Length > 0)
                return (query.Artist + " " + query.Title).Trim();
            else if (query.Album.Length > 0)
                return (query.Artist + " " + query.Album).Trim();
            return query.Artist.Trim();
        }
    }

    private static string CleanSearchString(string str, bool removeSpecialChars)
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

// TODO [ARCHITECTURE]: Standardize on the Result/Outcome pattern across all job processors.
// Currently, control flow is mixed:
// 1. Expected domain failures in single-song downloads throw exceptions (e.g., NoSuitableFileFoundException).
// 2. High-level processors (ProcessAlbumJob, ProcessAggregateJob) directly mutate the Job state (job.Fail()).
//
// We should refactor all job handlers (ProcessSongJob, ProcessAlbumJob, etc.) to return a 
// standardized Result object (e.g., `JobOutcome`). This will:
// - Eliminate boilerplate exceptions and stack-trace overhead for expected domain outcomes.
// - Decouple execution from state mutation.
// - Fix race conditions in EngineEvents by localizing Job state mutation to the main orchestration loop.

public abstract class SearchAndDownloadException : Exception
{
    public FailureReason Reason { get; }
    protected SearchAndDownloadException(FailureReason reason, string message) : base(message) { Reason = reason; }
    protected SearchAndDownloadException(FailureReason reason, string message, Exception inner) : base(message, inner) { Reason = reason; }
}

public class OutOfDownloadRetriesException : SearchAndDownloadException
{
    public OutOfDownloadRetriesException(Exception inner)
        : base(FailureReason.OutOfDownloadRetries, "Out of download retries.", inner) { }
}

public class NoSuitableFileFoundException : SearchAndDownloadException
{
    public NoSuitableFileFoundException(string? details = null)
        : base(FailureReason.NoSuitableFileFound, details ?? "") { }
    public NoSuitableFileFoundException(string details, Exception inner)
        : base(FailureReason.NoSuitableFileFound, details, inner) { }
}

public class AllDownloadsFailedException : SearchAndDownloadException
{
    public AllDownloadsFailedException()
        : base(FailureReason.AllDownloadsFailed, "All downloads failed.") { }

    public AllDownloadsFailedException(Exception inner)
        : base(FailureReason.AllDownloadsFailed, inner.Message, inner) { }
}

public class ManuallySkippedException : Exception {}
