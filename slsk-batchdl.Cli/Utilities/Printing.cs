using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core;
using Sldl.Core.Services;
using Konsole;
using ProgressBar = Konsole.ProgressBar;
using SearchResponse = Soulseek.SearchResponse;
using SlFile = Soulseek.File;
using Sldl.Core.Settings;

namespace Sldl.Cli;

public interface IProgressBar
{
    int Y { get; }
    string? Line1 { get; }
    int Current { get; }
    void Refresh(int current, string item);
}

public static class Printing
{
    public static readonly object ConsoleLock = new();
    public static bool IsBuffering { get; private set; }
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _buffer = new();

    public static void SetBuffering(bool enable)
    {
        lock (ConsoleLock)
        {
            IsBuffering = enable;
        }
    }

    public static void Flush()
    {
        lock (ConsoleLock)
        {
            while (_buffer.TryDequeue(out var action))
                action();
        }
    }

    internal static void Enqueue(Action action) => _buffer.Enqueue(action);

    private class BufferedProgressBar : IProgressBar
    {
        private Konsole.ProgressBar? _inner;
        private int _lastCurrent;
        private string _lastItem = "";
        private bool _isQueued = false;

        public BufferedProgressBar()
        {
            if (!IsBuffering)
                _inner = GetRealProgressBar();
        }

        public int Y => _inner?.Y ?? Console.CursorTop;
        public string? Line1 => _inner?.Line1;
        public int Current => _inner?.Current ?? _lastCurrent;

        public void Refresh(int current, string item)
        {
            _lastCurrent = current;
            _lastItem = item;

            if (IsBuffering)
            {
                if (!_isQueued)
                {
                    _isQueued = true;
                    Enqueue(() => { _isQueued = false; RealRefresh(); });
                }
                return;
            }

            RealRefresh();
        }

        private void RealRefresh()
        {
            lock (ConsoleLock)
            {
                if (_inner == null)
                    _inner = GetRealProgressBar();
                try { _inner?.Refresh(_lastCurrent, _lastItem); } catch { }
            }
        }
    }

    public static string DisplayString(SongQuery query, Soulseek.File? file = null, SearchResponse? response = null,
        FileConditions? nec = null, FileConditions? pref = null, bool fullpath = false, string customPath = "",
        bool infoFirst = false, bool showUser = true, bool showSpeed = false)
    {
        if (file == null)
            return query.ToString();

        string sampleRate  = file.SampleRate.HasValue ? $"{(file.SampleRate.Value / 1000.0).Normalize()}kHz" : "";
        string bitRate     = file.BitRate.HasValue ? $"{file.BitRate}kbps" : "";
        string fileSize    = $"{file.Size / (float)(1024 * 1024):F1}MB";
        string user        = showUser && response?.Username != null ? response.Username + "\\" : "";
        string speed       = showSpeed && response?.Username != null ? $"({response.UploadSpeed / 1024.0 / 1024.0:F2}MB/s) " : "";
        string fname       = fullpath ? file.Filename : (showUser ? "..\\" : "") + (customPath.Length == 0 ? Utils.GetFileNameSlsk(file.Filename) : customPath);
        string length      = Utils.IsMusicFile(file.Filename) ? (file.Length ?? -1).ToString() + "s" : "";
        string displayText;
        if (!infoFirst)
        {
            string info = string.Join('/', new string[] { length, sampleRate + bitRate, fileSize }.Where(value => value.Length > 0));
            displayText = $"{speed}{user}{fname} [{info}]";
        }
        else
        {
            string info = string.Join('/', new string[] { length.PadRight(4), (sampleRate + bitRate).PadRight(8), fileSize.PadLeft(6) });
            displayText = $"[{info}] {speed}{user}{fname}";
        }

        string necStr  = nec  != null ? $"nec:{nec.GetNotSatisfiedName(file, query, response)}, " : "";
        string prefStr = pref != null ? $"prf:{pref.GetNotSatisfiedName(file, query, response)}" : "";
        string cond    = "";
        if (nec != null || pref != null)
            cond = $" ({(necStr + prefStr).TrimEnd(' ', ',')})";

        return displayText + cond;
    }


    public static void PrintTracks(IEnumerable<SongJob> songs, int number = int.MaxValue, bool fullInfo = false,
        bool pathsOnly = false, bool showAncestors = true, bool infoFirst = false, bool showUser = true, bool indices = false)
    {
        Console.ResetColor();
        var songList = songs.ToList();
        if (songList.Count == 0)
            return;

        number = Math.Min(songList.Count, number);

        string ancestor = "";
        if (!showAncestors)
            ancestor = Utils.GreatestCommonDirectorySlsk(
                songList.SelectMany(s => s.Candidates?.Select(c => c.Filename) ?? []));

        if (pathsOnly)
        {
            for (int i = 0; i < number; i++)
            {
                foreach (var c in songList[i].Candidates ?? Enumerable.Empty<FileCandidate>())
                {
                    if (indices)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write($" [{i + 1:D2}]");
                        Console.ResetColor();
                    }
                    if (ancestor.Length == 0)
                        Console.WriteLine("    " + DisplayString(songList[i].Query, c.File, c.Response, infoFirst: infoFirst, showUser: showUser));
                    else
                        Console.WriteLine("    " + DisplayString(songList[i].Query, c.File, c.Response, customPath: c.File.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                }
            }
        }
        else if (!fullInfo)
        {
            for (int i = 0; i < number; i++)
                Console.WriteLine($"  {songList[i]}");
        }
        else
        {
            for (int i = 0; i < number; i++)
            {
                var s = songList[i];
                Console.WriteLine($"  Artist:             {s.Query.Artist}");
                Console.WriteLine($"  Title:              {s.Query.Title}");
                if (!string.IsNullOrEmpty(s.Query.Album))
                    Console.WriteLine($"  Album:              {s.Query.Album}");
                if (s.Query.Length > -1)
                    Console.WriteLine($"  Length:             {s.Query.Length}s");
                if (!string.IsNullOrEmpty(s.DownloadPath))
                    Console.WriteLine($"  Local path:         {s.DownloadPath}");
                if (!string.IsNullOrEmpty(s.Query.URI))
                    Console.WriteLine($"  URL/ID:             {s.Query.URI}");
                if (!string.IsNullOrEmpty(s.Other))
                    Console.WriteLine($"  Other:              {s.Other}");
                if (s.Query.ArtistMaybeWrong)
                    Console.WriteLine($"  Artist maybe wrong: {s.Query.ArtistMaybeWrong}");
                if (s.Candidates != null)
                {
                    Console.WriteLine($"  Shares:             {s.Candidates.Count}");
                    foreach (var c in s.Candidates)
                    {
                        if (ancestor.Length == 0)
                            Console.WriteLine("    " + DisplayString(s.Query, c.File, c.Response, infoFirst: infoFirst, showUser: showUser));
                        else
                            Console.WriteLine("    " + DisplayString(s.Query, c.File, c.Response, customPath: c.File.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                    }
                    if (s.Candidates.Count > 0) Console.WriteLine();
                }

                if (i < number - 1)
                    Console.WriteLine();
            }
        }

        if (number < songList.Count)
            Console.WriteLine($"  ... (etc)");
    }


    public static void PrintResults(Job job, PrintOption printOption, SearchSettings search)
    {
        if (job is JobList slj)
        {
            bool nonVerbose = (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
            foreach (var song in slj.Jobs.OfType<SongJob>())
            {
                PrintSongResults(song, printOption, search);
                if (!nonVerbose)
                    Console.WriteLine();
            }
        }
        else if (job is SongJob songJob)
        {
            PrintSongResults(songJob, printOption, search);
        }
        else if (job is AggregateJob ag)
        {
            var existing = ag.Songs.Where(s => s.State == JobState.AlreadyExists).ToList();
            var notFound = ag.Songs.Where(s => s.FailureReason == FailureReason.NoSuitableFileFound).ToList();
            if (printOption.HasFlag(PrintOption.Json))
            {
                JsonPrinter.PrintAggregateJson(ag.Songs.Where(s => s.State == JobState.Pending));
            }
            else if (printOption.HasFlag(PrintOption.Link))
            {
                var first = ag.Songs.FirstOrDefault(s => s.ChosenCandidate != null);
                if (first?.ChosenCandidate != null)
                    PrintLink(first.ChosenCandidate.Username, first.ChosenCandidate.Filename);
            }
            else
            {
                Console.WriteLine($"Results for aggregate {job.ToString(true)}:");
                PrintTracksTbd(ag.Songs.Where(s => s.State == JobState.Pending).ToList(), existing, notFound, false, printOption);
            }
        }
        else if (job is AlbumJob albumJob)
        {
            if (printOption.HasFlag(PrintOption.Json))
            {
                var foldersToPrint = printOption.HasFlag(PrintOption.Full)
                    ? albumJob.Results
                    : albumJob.Results.Take(1).ToList();
                JsonPrinter.PrintAlbumJson(foldersToPrint, albumJob);
            }
            else if (printOption.HasFlag(PrintOption.Link))
            {
                if (albumJob.Results.Count > 0)
                    PrintAlbumLink(albumJob.Results[0]);
            }
            else
            {
                if (!printOption.HasFlag(PrintOption.Full))
                    Console.WriteLine($"Result 1 of {albumJob.Results.Count} for album {job.ToString(true)}:");
                else
                    Console.WriteLine($"Results ({albumJob.Results.Count}) for album {job.ToString(true)}:");

                if (albumJob.Results.Count > 0)
                {
                    if (!search.NoBrowseFolder)
                        Console.WriteLine("[Skipping full folder retrieval]");

                    foreach (var folder in albumJob.Results)
                    {
                        PrintAlbum(folder);
                        if (!printOption.HasFlag(PrintOption.Full))
                            break;
                    }
                }
            }
        }
        else if (job is AlbumAggregateJob albumAggregateJob)
        {
            if (albumAggregateJob.Albums.Count == 0)
            {
                Console.WriteLine("No results.");
                return;
            }

            bool nonVerbose = (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
            foreach (var album in albumAggregateJob.Albums)
            {
                PrintResults(album, printOption, search);
                if (!nonVerbose)
                    Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("No results.");
        }
    }

    private static void PrintSongResults(SongJob song, PrintOption printOption, SearchSettings search)
    {
        bool printFull = printOption.HasFlag(PrintOption.Full);
        bool nonVerbose = (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
        var orderedResults = song.Candidates?
            .Select(candidate => (candidate.Response, candidate.File))
            .ToList();

        if (!nonVerbose)
            Console.WriteLine($"Results for {song}:");

        if (orderedResults == null || orderedResults.Count == 0)
        {
            if (printOption.HasFlag(PrintOption.Json))
                JsonPrinter.PrintTrackResultJson(song.Query, []);
            if (!nonVerbose)
                WriteLine("No results", ConsoleColor.Yellow);
            return;
        }

        if (!nonVerbose)
            Console.WriteLine();

        if (printOption.HasFlag(PrintOption.Json))
            JsonPrinter.PrintTrackResultJson(song.Query, orderedResults, printFull);
        else if (printOption.HasFlag(PrintOption.Link))
            PrintLink(orderedResults.First().Response.Username, orderedResults.First().File.Filename);
        else
            PrintTrackResults(orderedResults.Select(x => (x.Response, x.File)), song.Query, printFull, search.NecessaryCond, search.PreferredCond);
    }

    public static void PrintPlannedOutput(JobList queue)
    {
        foreach (var job in queue.Jobs)
            PrintPlannedOutput(job);
    }

    private static void PrintPlannedOutput(Job job)
    {
        switch (job)
        {
            case ExtractJob extractJob when extractJob.Result != null:
                PrintPlannedOutput(extractJob.Result);
                break;

            case JobList jobList:
                var plannedJobs = CollectPlannedDownloadJobs(jobList).ToList();
                if (plannedJobs.Count > 0)
                {
                    if (jobList.Config!.PrintTracks)
                    {
                        PrintPlannedDownloads(plannedJobs, jobList.Config);
                    }
                    else if (jobList.Config!.PrintResults)
                    {
                        PrintPlannedResults(plannedJobs, jobList.Config);
                    }
                    break;
                }

                foreach (var child in jobList.Jobs)
                    PrintPlannedOutput(child);
                break;

            default:
                if (job.Config?.PrintTracks == true)
                    PrintPlannedDownloads([job], job.Config);
                else if (job.Config?.PrintResults == true)
                    PrintResults(job, job.Config.PrintOption, job.Config.Search);
                break;
        }
    }

    public static void PrintPlannedDownloads(IReadOnlyList<Job> plannedJobs, DownloadSettings config)
    {
        var songs = plannedJobs.OfType<SongJob>().ToList();
        var otherJobs = plannedJobs.Where(job => job is not SongJob).ToList();

        if (songs.Count > 0)
        {
            var existing = songs.Where(s => s.State == JobState.AlreadyExists).ToList();
            var notFound = songs.Where(s => s.FailureReason == FailureReason.NoSuitableFileFound).ToList();
            PrintTracksTbd(songs.Where(s => s.State == JobState.Pending).ToList(), existing, notFound, true, config.PrintOption);
        }

        var existingJobs = otherJobs.Where(IsAlreadyExistingPlannedJob).ToList();
        var notFoundJobs = otherJobs.Where(IsNotFoundPlannedJob).ToList();
        var pendingJobs = otherJobs.Except(existingJobs).Except(notFoundJobs).ToList();

        if (pendingJobs.Count > 0)
        {
            if (songs.Count > 0)
                Console.WriteLine();

            PrintPlannedJobLines($"Downloading {pendingJobs.Count} {(pendingJobs.Count == 1 ? "item" : "items")}:", pendingJobs, config);
        }

        if (config.PrintTracks || (config.PrintOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0)
        {
            if (existingJobs.Count > 0)
            {
                Console.WriteLine();
                PrintPlannedJobLines("The following items already exist:", existingJobs, config, includePath: true);
            }

            if (notFoundJobs.Count > 0)
            {
                Console.WriteLine();
                PrintPlannedJobLines("The following items were not found during a prior run:", notFoundJobs, config);
            }
        }
    }

    private static void PrintPlannedResults(IReadOnlyList<Job> plannedJobs, DownloadSettings config)
    {
        bool nonVerbose = (config.PrintOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
        bool printedAny = false;

        foreach (var plannedJob in plannedJobs)
        {
            if (printedAny && !nonVerbose)
                Console.WriteLine();

            PrintResults(plannedJob, config.PrintOption, config.Search);
            printedAny = true;
        }
    }

    private static IEnumerable<Job> CollectPlannedDownloadJobs(Job job)
    {
        switch (job)
        {
            case SongJob song:
                yield return song;
                break;

            case AlbumAggregateJob albumAggregate when albumAggregate.Albums.Count > 0:
                foreach (var album in albumAggregate.Albums)
                    yield return album;
                break;

            case AlbumJob or AggregateJob or AlbumAggregateJob:
                yield return job;
                break;

            case ExtractJob extractJob when extractJob.Result != null:
                foreach (var plannedJob in CollectPlannedDownloadJobs(extractJob.Result))
                    yield return plannedJob;
                break;

            case JobList jobList:
                foreach (var child in jobList.Jobs)
                foreach (var plannedJob in CollectPlannedDownloadJobs(child))
                    yield return plannedJob;
                break;
        }
    }

    private static void PrintPlannedJobLines(string header, IReadOnlyList<Job> jobs, DownloadSettings config, bool includePath = false)
    {
        Logger.Info(header);
        Console.ResetColor();
        foreach (var job in jobs)
        {
            var path = includePath ? GetPlannedJobDownloadPath(job) : null;
            Console.WriteLine($"  {job.ToString(noInfo: config.PrintOption.HasFlag(PrintOption.Tracks))}{(string.IsNullOrWhiteSpace(path) ? "" : $" -> {path}")}");
        }
    }

    private static bool IsAlreadyExistingPlannedJob(Job job)
        => job.State == JobState.AlreadyExists
        || job.State == JobState.Skipped && !string.IsNullOrWhiteSpace(GetPlannedJobDownloadPath(job));

    private static bool IsNotFoundPlannedJob(Job job)
        => job.State == JobState.NotFoundLastTime
        || job.FailureReason == FailureReason.NoSuitableFileFound;

    private static string? GetPlannedJobDownloadPath(Job job)
        => job switch
        {
            SongJob song => song.DownloadPath,
            AlbumJob album => album.DownloadPath,
            _ => null,
        };


    public static void PrintComplete(JobList queue)
    {
        int successes = 0, fails = 0;
        foreach (var job in queue.Jobs)
        {
            IEnumerable<SongJob> songs = job switch
            {
                JobList jl      => jl.Jobs.OfType<SongJob>(),
                AggregateJob ag => ag.Songs,
                _               => Enumerable.Empty<SongJob>(),
            };
            foreach (var s in songs)
            {
                if (IsSuccessfulCompletion(s.State)) successes++;
                else if (s.State == JobState.Failed) fails++;
            }
            if (job is AlbumJob albumJob && albumJob.ResolvedTarget != null)
            {
                foreach (var f in albumJob.ResolvedTarget.Files.Where(f => !f.IsNotAudio))
                {
                    if (IsSuccessfulCompletion(f.State)) successes++;
                    else if (f.State == JobState.Failed) fails++;
                }
            }
        }
        PrintComplete(successes, fails);
    }

    public static void PrintComplete(int successes, int fails)
    {
        if (successes + fails > 1)
        {
            Console.WriteLine();
            Logger.Info($"Completed: {successes} succeeded, {fails} failed.");
        }
    }

    public static bool IsSuccessfulCompletion(JobState state)
        => state is JobState.Done or JobState.AlreadyExists;


    public static void PrintTracksTbd(List<SongJob> toBeDownloaded, List<SongJob> existing, List<SongJob> notFound,
        bool isNormal, PrintOption printOption, bool summary = true)
    {
        bool printTracks  = printOption.HasFlag(PrintOption.Tracks);
        bool printResults = (printOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
        bool full         = printOption.HasFlag(PrintOption.Full);

        if (isNormal && !printTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist     = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool allSkipped = existing.Count + notFound.Count > toBeDownloaded.Count;

        if (summary && (isNormal || skippedTracks.Length > 0))
            Logger.Info($"Downloading {toBeDownloaded.Count} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");

        if (toBeDownloaded.Count > 0)
        {
            bool showAll = !isNormal || printTracks || printResults;
            PrintTracks(toBeDownloaded, showAll ? int.MaxValue : 10, full, infoFirst: printTracks);

            if (full && (existing.Count > 0 || notFound.Count > 0))
                Console.WriteLine("\n-----------------------------------------------\n");
        }

        if (printTracks || printResults)
        {
            if (existing.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks already exist:");
                PrintTracks(existing, fullInfo: full, infoFirst: printTracks);
            }
            if (notFound.Count > 0)
            {
                Console.WriteLine($"\nThe following tracks were not found during a prior run:");
                PrintTracks(notFound, fullInfo: full, infoFirst: printTracks);
            }
        }
    }


    public static void PrintTrackResults(IEnumerable<(SearchResponse, Soulseek.File)> orderedResults, SongQuery query,
        bool full = false, FileConditions? necCond = null, FileConditions? prefCond = null)
    {
        Console.ResetColor();
        int count = 0;
        foreach (var (response, file) in orderedResults)
        {
            Console.WriteLine(DisplayString(query, file, response,
                full ? necCond : null, full ? prefCond : null,
                fullpath: full, infoFirst: true, showSpeed: full));
            count++;
        }
        WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
    }


    public static void PrintLink(string username, string filename)
    {
        var link = $"slsk://{username}/{filename.Replace('\\', '/')}";
        Console.WriteLine(link);
    }


    public static void PrintAlbumLink(AlbumFolder folder)
    {
        if (folder.Files.Count == 0) return;
        string directory = Utils.GreatestCommonDirectorySlsk(folder.Files.Select(f => f.ResolvedTarget!.Filename));
        var link = $"slsk://{folder.Username}/{directory.Replace('\\', '/').TrimEnd('/')}/";
        Console.WriteLine(link);
    }


    public static void PrintAlbumHeader(AlbumFolder folder)
    {
        if (folder.Files.Count == 0) return;

        lock (ConsoleLock)
        {
            Console.ResetColor();
            var firstResponse = folder.Files[0].ResolvedTarget!.Response;
            string noSlot   = !firstResponse.HasFreeUploadSlot ? ", no upload slots" : "";
            string userInfo = $"{firstResponse.Username} ({((float)firstResponse.UploadSpeed / (1024 * 1024)):F3}MB/s{noSlot})";
            var (parents, propsList) = FolderInfo(folder.Files.Select(f => f.ResolvedTarget!.File));

            string format     = propsList.FirstOrDefault() ?? "";
            string otherProps = propsList.Count > 1 ? " / " + string.Join(" / ", propsList.Skip(1)) : "";

            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"User  : {userInfo}\nFolder: {parents}\nProps : [");
            Console.ForegroundColor = GetFormatColor(format);
            Console.Write(format);
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(otherProps + "]");
            Console.ResetColor();
        }
    }

    public static int PrintAlbum(AlbumFolder folder, bool indices = false)
    {
        if (folder.Files.Count == 0) return 0;

        Console.ResetColor();
        PrintAlbumHeader(folder);

        string ancestor = Utils.GreatestCommonDirectorySlsk(folder.Files.Select(f => f.ResolvedTarget!.Filename));
        int i = 0;
        foreach (var af in folder.Files)
        {
            if (indices)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($" [{i + 1:D2}]");
                Console.ResetColor();
            }
            string customPath = ancestor.Length > 0 ? af.ResolvedTarget!.File.Filename.Replace(ancestor, "").TrimStart('\\') : "";
            Console.WriteLine("    " + DisplayString(af.Query, af.ResolvedTarget!.File, af.ResolvedTarget!.Response, customPath: customPath, showUser: false));
            i++;
        }

        return 3 + folder.Files.Count;
    }

    public static string FormatList<T>(ICollection<T> items, Func<T, string> format, string indent = "  ", int maxCount = 10)
    {
        var result = new System.Text.StringBuilder();
        int count = 1;
        foreach (var item in items)
        {
            if (count > 1) result.Append('\n');
            if (count > maxCount) { result.Append($"... and {items.Count - count} more"); break; }
            result.Append(indent);
            result.Append(format(item));
            count++;
        }
        return result.ToString();
    }

    static (string parents, List<string> props) FolderInfo(IEnumerable<SlFile> files)
    {
        int totalLengthInSeconds = files.Sum(f => f.Length ?? 0);
        var sampleRates = files.Where(f => f.SampleRate.HasValue).Select(f => f.SampleRate.GetValueOrDefault()).OrderBy(r => r).ToList();
        int? modeSampleRate = sampleRates.GroupBy(rate => rate).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

        var bitRates = files.Where(f => f.BitRate.HasValue).Select(f => f.BitRate.GetValueOrDefault()).ToList();
        double? meanBitrate = bitRates.Count > 0 ? (double?)bitRates.Average() : null;
        double totalFileSizeInMB = files.Sum(f => f.Size) / (1024.0 * 1024.0);

        TimeSpan totalTimeSpan = TimeSpan.FromSeconds(totalLengthInSeconds);
        string totalLengthFormatted = totalTimeSpan.TotalHours >= 1
            ? string.Format("{0}:{1:D2}:{2:D2}", (int)totalTimeSpan.TotalHours, totalTimeSpan.Minutes, totalTimeSpan.Seconds)
            : string.Format("{0:D2}:{1:D2}", totalTimeSpan.Minutes, totalTimeSpan.Seconds);

        var mostCommonExtension = files.GroupBy(f => Utils.GetExtensionSlsk(f.Filename))
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key.TrimStart('.');

        List<string> propsList = new() { mostCommonExtension.ToUpper().Trim(), totalLengthFormatted };
        if (modeSampleRate.HasValue)
            propsList.Add($"{(modeSampleRate.Value / 1000.0).Normalize()} kHz");
        if (meanBitrate.HasValue)
            propsList.Add($"{(int)meanBitrate.Value} kbps");
        propsList.Add($"{totalFileSizeInMB:F2} MB");

        string gcp = Utils.GreatestCommonDirectorySlsk(files.Select(x => x.Filename)).TrimEnd('\\');
        int lastIndex = gcp.LastIndexOf('\\');
        if (lastIndex != -1)
        {
            int secondLastIndex = gcp.LastIndexOf('\\', lastIndex - 1);
            gcp = secondLastIndex == -1 ? gcp : gcp[(secondLastIndex + 1)..];
        }

        return (gcp, propsList);
    }

    static ConsoleColor GetFormatColor(string format)
    {
        return format.ToLower() switch
        {
            "flac" => ConsoleColor.DarkYellow,
            "mp3"  => ConsoleColor.DarkRed,
            "ogg"  => ConsoleColor.DarkGreen,
            "wav"  => ConsoleColor.White,
            "opus" => ConsoleColor.DarkBlue,
            "m4a"  => ConsoleColor.Cyan,
            _      => ConsoleColor.Gray,
        };
    }

    public static void RefreshOrPrint(IProgressBar? progress, int current, string item, bool print = false, bool refreshIfOffscreen = false)
    {
        if (IsBuffering)
        {
            _buffer.Enqueue(() => RefreshOrPrint(progress, current, item, print, refreshIfOffscreen));
            return;
        }

        lock (ConsoleLock)
        {
            if (progress != null && !Console.IsOutputRedirected && (refreshIfOffscreen || progress.Y >= Console.WindowTop))
            {
                progress.Refresh(current, item);

                if (print)
                    Logger.LogNonConsole(Logger.LogLevel.Info, item);
            }
            else if ((progress == null || Console.IsOutputRedirected) && print)
            {
                Logger.Info(item);
            }
        }
    }

    public static void WriteLine(string value = "", ConsoleColor color = ConsoleColor.Gray, bool force = false)
    {
        if (IsBuffering && !force)
        {
            _buffer.Enqueue(() => WriteLine(value, color, force));
            return;
        }

        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(value);
            Console.ResetColor();
        }
    }

    public static void Write(string value, ConsoleColor color = ConsoleColor.Gray, bool force = false)
    {
        if (IsBuffering && !force)
        {
            _buffer.Enqueue(() => Write(value, color, force));
            return;
        }

        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.Write(value);
            Console.ResetColor();
        }
    }

    public static IProgressBar? GetProgressBar()
    {
        return new BufferedProgressBar();
    }

    private static Konsole.ProgressBar? GetRealProgressBar()
    {
        lock (ConsoleLock)
        {
            try { return new Konsole.ProgressBar(PbStyle.SingleLine, 100, Console.WindowWidth - 10, character: ' '); }
            catch { return null; }
        }
    }
}
