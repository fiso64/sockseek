using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

public static class Printing
{
    public static readonly object ConsoleLock = new();
    internal static Action<string, ConsoleColor>? LiveWriteLine { get; set; }
    private static readonly AsyncLocal<TextWriter?> ScopedOutput = new();

    private static bool _isBuffering;
    private static readonly List<(string value, ConsoleColor color, bool isNewLine)> _buffer = new();

    public static void SetBuffering(bool value)
    {
        lock (ConsoleLock)
        {
            if (_isBuffering && !value)
                Flush();
            _isBuffering = value;
        }
    }

    public static void Flush()
    {
        lock (ConsoleLock)
        {
            foreach (var (value, color, isNewLine) in _buffer)
            {
                if (isNewLine)
                    WriteLine(value, color, force: true);
                else
                    Write(value, color, force: true);
            }
            _buffer.Clear();
        }
    }

    public static string DisplayString(SongQuery query, FileCandidate candidate,
        bool fullpath = false, string customPath = "",
        bool infoFirst = false, bool showUser = true, bool showSpeed = false, bool showConditions = false)
    {
        string sampleRate = candidate.SampleRate is >= 0 ? $"{(candidate.SampleRate.Value / 1000.0).Normalize()}kHz" : "";
        string bitRate = candidate.BitRate is >= 0 ? $"{candidate.BitRate}kbps" : "";
        string fileSize = candidate.Target.Size.HasValue
            ? $"{candidate.Size / (float)(1024 * 1024):F1}MB" : "unknown size";
        string user = showUser ? candidate.Username + "\\" : "";
        string speed = showSpeed && candidate.UploadSpeed.HasValue
            ? $"({candidate.UploadSpeed.Value / 1024.0 / 1024.0:F2}MB/s) "
            : "";
        string fname = PeerIdentityValidator.ToDisplayText(fullpath
            ? candidate.Filename
            : (showUser ? "..\\" : "") + (customPath.Length == 0 ? Utils.GetFileNameSlsk(candidate.Filename) : customPath));
        string length = Utils.IsMusicFile(candidate.Filename) ? Math.Max(-1, candidate.Length ?? -1) + "s" : "";
        string displayText;
        if (!infoFirst)
        {
            string info = string.Join('/', new[] { length, sampleRate + bitRate, fileSize }.Where(value => value.Length > 0));
            displayText = $"{speed}{user}{fname} [{info}]";
        }
        else
        {
            string info = string.Join('/', new[] { length.PadRight(4), (sampleRate + bitRate).PadRight(8), fileSize.PadLeft(6) });
            displayText = $"[{info}] {speed}{user}{fname}";
        }

        if (candidate.Visibility == SearchResultVisibility.Locked) displayText += " [locked]";
        return showConditions ? displayText + ConditionExplanation(candidate.ProjectionFacts) : displayText;
    }


    public static void PrintTracks(IEnumerable<SongJob> songs, int number = int.MaxValue, bool fullInfo = false,
        bool pathsOnly = false, bool showAncestors = true, bool infoFirst = false, bool showUser = true, bool indices = false,
        bool showConditions = false)
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
                        Write($" [{i + 1:D2}]", ConsoleColor.DarkGray);
                    }
                    if (ancestor.Length == 0)
                        WriteLine("    " + DisplayString(songList[i].Query, c, infoFirst: infoFirst, showUser: showUser));
                    else
                        WriteLine("    " + DisplayString(songList[i].Query, c, customPath: c.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser));
                }
            }
        }
        else if (!fullInfo)
        {
            for (int i = 0; i < number; i++)
                WriteLine($"  {songList[i]}");
        }
        else
        {
            for (int i = 0; i < number; i++)
            {
                var s = songList[i];
                WriteLine($"  Artist:             {s.Query.Artist}");
                WriteLine($"  Title:              {s.Query.Title}");
                if (!string.IsNullOrEmpty(s.Query.Album))
                    WriteLine($"  Album:              {s.Query.Album}");
                if (s.Query.Length > -1)
                    WriteLine($"  Length:             {s.Query.Length}s");
                if (!string.IsNullOrEmpty(s.DownloadPath))
                    WriteLine($"  Local path:         {s.DownloadPath}");
                if (!string.IsNullOrEmpty(s.Query.URI))
                    WriteLine($"  URL/ID:             {s.Query.URI}");
                if (!string.IsNullOrEmpty(s.ExtractorInfo))
                    WriteLine($"  Extractor info:     {s.ExtractorInfo}");
                if (s.Query.ArtistMaybeWrong)
                    WriteLine($"  Artist maybe wrong: {s.Query.ArtistMaybeWrong}");
                if (s.Candidates != null)
                {
                    WriteLine($"  Shares:             {s.Candidates.Count}");
                    foreach (var c in s.Candidates)
                    {
                        if (ancestor.Length == 0)
                            WriteLine("    " + DisplayString(s.Query, c, infoFirst: infoFirst, showUser: showUser, showConditions: showConditions));
                        else
                            WriteLine("    " + DisplayString(s.Query, c, customPath: c.Filename.Replace(ancestor, "").TrimStart('\\'), infoFirst: infoFirst, showUser: showUser, showConditions: showConditions));
                    }
                    if (s.Candidates.Count > 0) WriteLine();
                }

                if (i < number - 1)
                    WriteLine();
            }
        }

        if (number < songList.Count)
            WriteLine($"  ... (etc)");
    }


    public static void PrintResults(
        Job job,
        PrintOption printOption,
        SearchSettings search,
        IReadOnlyDictionary<string, int>? peerFailureSnapshot = null)
        => ResultPrintFormatter.Print(job, printOption, search, peerFailureSnapshot);

    public static void PrintComplete(JobList queue)
    {
        var (successes, fails, skipped) = CountUserFacingCompletionsDetailed(queue);
        PrintComplete(successes, fails, skipped);
    }

    internal static (int Successes, int Fails) CountUserFacingCompletions(JobList queue)
    {
        var (successes, fails, _) = CountUserFacingCompletionsDetailed(queue);
        return (successes, fails);
    }

    internal static (int Successes, int Fails, int Skipped) CountUserFacingCompletionsDetailed(JobList queue)
    {
        int successes = 0, fails = 0;
        int skipped = 0;
        var visited = new HashSet<Guid>();

        foreach (var job in queue.Jobs)
            CountUserFacingCompletion(job, parent: null, visited, ref successes, ref fails, ref skipped);

        return (successes, fails, skipped);
    }

    private static void CountUserFacingCompletion(
        Job job,
        Job? parent,
        ISet<Guid> visited,
        ref int successes,
        ref int fails,
        ref int skipped)
    {
        if (!visited.Add(job.Id))
            return;

        if (job.Config?.DoNotDownload == true)
            return;

        switch (job)
        {
            case ExtractJob extractJob:
                if (extractJob.Result != null)
                    CountUserFacingCompletion(extractJob.Result, parent: null, visited, ref successes, ref fails, ref skipped);
                return;

            case JobList jobList:
                foreach (var child in jobList.Jobs)
                    CountUserFacingCompletion(child, jobList, visited, ref successes, ref fails, ref skipped);
                return;

            case SongGroupsJob songGroupsJob:
                foreach (var song in songGroupsJob.Songs)
                    CountUserFacingCompletion(song, songGroupsJob, visited, ref successes, ref fails, ref skipped);
                return;

            case AlbumGroupsJob albumGroupsJob:
                foreach (var album in albumGroupsJob.Albums)
                    CountUserFacingCompletion(album, albumGroupsJob, visited, ref successes, ref fails, ref skipped);
                return;

            case RetrieveFolderJob:
                return;
        }

        if (job is SongJob && parent is AlbumJob)
            return;

        if (IsSuccessfulCompletion(job)) successes++;
        else if (IsManualSkipCompletion(job)) skipped++;
        else if (job.IsUnsuccessfulTerminal) fails++;
    }

    public static void PrintComplete(int successes, int fails)
        => PrintComplete(successes, fails, skipped: 0);

    public static void PrintComplete(int successes, int fails, int skipped)
    {
        string? message = FormatComplete(successes, fails, skipped);
        if (message is null)
            return;

        WriteLine();
        WriteLine(message);
    }

    internal static string? FormatComplete(int successes, int fails, int skipped)
    {
        if (successes + fails + skipped <= 1 && fails == 0 && skipped == 0)
            return null;

        var skippedPart = skipped > 0 ? $", {skipped} skipped" : "";
        return $"Completed: {successes} succeeded{skippedPart}, {fails} failed.";
    }

    public static bool IsSuccessfulCompletion(Job job)
        => job.IsSuccessfulTerminal;

    public static bool IsManualSkipCompletion(Job job)
        => job.TerminalOutcome == JobTerminalOutcome.Skipped
            && job.SkipReason == JobSkipReason.Manual;


    public static void PrintTracksTbd(List<SongJob> toBeDownloaded, List<SongJob> existing, List<SongJob> notFound,
        bool isNormal, PrintOption printOption, bool summary = true)
    {
        bool printTracks  = printOption.HasFlag(PrintOption.Jobs);
        bool printResults = (printOption & (PrintOption.Results | PrintOption.Json | PrintOption.Link)) != 0;
        bool full         = printOption.HasFlag(PrintOption.Full);

        if (isNormal && !printTracks && toBeDownloaded.Count == 1 && existing.Count + notFound.Count == 0)
            return;

        string notFoundLastTime = notFound.Count > 0 ? $"{notFound.Count} not found" : "";
        string alreadyExist     = existing.Count > 0 ? $"{existing.Count} already exist" : "";
        notFoundLastTime = alreadyExist.Length > 0 && notFoundLastTime.Length > 0 ? ", " + notFoundLastTime : notFoundLastTime;
        string skippedTracks = alreadyExist.Length + notFoundLastTime.Length > 0 ? $" ({alreadyExist}{notFoundLastTime})" : "";
        bool allSkipped = existing.Count + notFound.Count > toBeDownloaded.Count;
        bool printOnly = printTracks || printResults;

        if (summary && printOnly && toBeDownloaded.Count > 0)
        {
            var label = printResults && !isNormal ? "group tracks" : "tracks to download";
            WriteLine($"{toBeDownloaded.Count} {label}:");
        }
        else if (summary && !printOnly && (isNormal || skippedTracks.Length > 0))
        {
            WriteLine($"Downloading {toBeDownloaded.Count} tracks{skippedTracks}{(allSkipped ? '.' : ':')}");
        }

        if (toBeDownloaded.Count > 0)
        {
            bool showAll = !isNormal || printTracks || printResults;
            int limit = showAll ? int.MaxValue : 10;
            PrintTracks(toBeDownloaded, limit, full, infoFirst: printTracks, showConditions: printResults && full);
            if (!showAll && toBeDownloaded.Count > limit)
                WriteLine($"  ... and {toBeDownloaded.Count - limit} more");

            if (full && (existing.Count > 0 || notFound.Count > 0))
                WriteLine("\n-----------------------------------------------\n");
        }

        if (existing.Count > 0)
        {
            WriteLine($"{(toBeDownloaded.Count > 0 ? "\n" : "")}{existing.Count} tracks already exist:");
            PrintTracks(existing, fullInfo: full, infoFirst: printTracks, showConditions: printResults && full);
        }
        if (notFound.Count > 0)
        {
            WriteLine($"{(toBeDownloaded.Count > 0 || existing.Count > 0 ? "\n" : "")}{notFound.Count} tracks were not found during a prior run:");
            PrintTracks(notFound, fullInfo: full, infoFirst: printTracks, showConditions: printResults && full);
        }
    }


    public static void PrintTrackCandidates(IEnumerable<FileCandidate> orderedResults, SongQuery query,
        bool full = false)
    {
        Console.ResetColor();
        int count = 0;
        foreach (var candidate in orderedResults)
        {
            string display = DisplayString(
                query,
                candidate,
                fullpath: full,
                infoFirst: true,
                showSpeed: full,
                showConditions: full);
            WriteLine(display);
            count++;
        }
        WriteLine($"Total: {count}\n", ConsoleColor.Yellow);
    }

    private static string ConditionExplanation(SearchConditionFacts? facts)
    {
        if (facts == null)
            return " (conditions:unavailable)";
        string necessary = facts.Value.NecessaryConditionsSatisfied
            ? "Satisfied"
            : "Unsatisfied";
        string preferred = facts.Value.UnsatisfiedPreferredConditions.Count == 0
            ? "Satisfied"
            : string.Join(
                ", ",
                facts.Value.UnsatisfiedPreferredConditions.Select(condition =>
                    $"{condition} fails"));
        return $" (nec:{necessary}, prf:{preferred})";
    }


    public static void PrintLink(string username, string filename)
    {
        var link = $"slsk://{username}/{filename.Replace('\\', '/')}";
        WriteLine(link);
    }


    public static void PrintAlbumLink(AlbumFolder folder)
    {
        if (!folder.Files.Any(file => file.Candidate.Visibility == SearchResultVisibility.Public)) return;
        string directory = folder.FolderPath;
        var link = $"slsk://{folder.Username}/{directory.Replace('\\', '/').TrimEnd('/')}/";
        WriteLine(link);
    }


    public static void PrintAlbumHeader(AlbumFolder folder, bool force = false)
    {
        if (folder.Files.Count == 0) return;

        lock (ConsoleLock)
        {
            Console.ResetColor();
            var firstCandidate = folder.Files[0].Candidate;
            string noSlot = firstCandidate.HasFreeUploadSlot == false ? ", no upload slots" : "";
            string speed = firstCandidate.UploadSpeed.HasValue
                ? $"{firstCandidate.UploadSpeed.Value / (1024f * 1024f):F3}MB/s"
                : "unknown speed";
            string userInfo = $"{firstCandidate.Username} ({speed}{noSlot})";
            var (parents, propsList) = FolderInfo(folder.Files.Select(f => f.Candidate), folder.FolderPath);

            string format     = propsList.FirstOrDefault() ?? "";
            string otherProps = propsList.Count > 1 ? " / " + string.Join(" / ", propsList.Skip(1)) : "";

            Write($"User  : {userInfo}\nFolder: {PeerIdentityValidator.ToDisplayText(parents)}\nProps :[", ConsoleColor.White, force: force);
            Write(format, GetFormatColor(format), force: force);
            WriteLine(otherProps + "]", ConsoleColor.White, force: force);
        }
    }

    public static int PrintAlbum(AlbumFolder folder, bool indices = false, bool force = false, bool showConditions = false)
    {
        if (folder.Files.Count == 0) return 0;

        Console.ResetColor();
        PrintAlbumHeader(folder, force);

        string ancestor = folder.FolderPath.TrimEnd('\\');
        int i = 0;
        foreach (var af in folder.Files)
        {
            if (indices)
            {
                Write($" [{i + 1:D2}]", ConsoleColor.DarkGray, force: force);
            }
            string customPath = PathRelativeToFolder(af.Candidate.Filename, ancestor);
            WriteLine("    " + DisplayString(af.Query, af.Candidate, customPath: customPath, showUser: false,
                showConditions: showConditions), ConsoleColor.Gray, force: force);
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

    private static string PathRelativeToFolder(string filename, string folderPath)
    {
        if (folderPath.Length == 0)
            return "";

        return filename.StartsWith(folderPath + "\\", StringComparison.Ordinal)
            ? filename[(folderPath.Length + 1)..]
            : Utils.GetFileNameSlsk(filename);
    }

    static (string parents, List<string> props) FolderInfo(IEnumerable<FileCandidate> files, string? folderPath = null)
    {
        var fileList = files.ToList();
        long totalLengthInSeconds = fileList.Sum(f => Math.Max(0L, f.Length ?? 0));
        var sampleRates = fileList.Where(f => f.SampleRate is >= 0).Select(f => f.SampleRate.GetValueOrDefault()).OrderBy(r => r).ToList();
        int? modeSampleRate = sampleRates.GroupBy(rate => rate).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

        var bitRates = fileList.Where(f => f.BitRate is >= 0).Select(f => f.BitRate.GetValueOrDefault()).ToList();
        double? meanBitrate = bitRates.Count > 0 ? (double?)bitRates.Average() : null;
        double totalFileSizeInMB = fileList.Sum(f => (double)f.Target.Size.GetValueOrDefault()) / (1024.0 * 1024.0);

        long hours = totalLengthInSeconds / 3600;
        string totalLengthFormatted = hours >= 1
            ? $"{hours}:{totalLengthInSeconds / 60 % 60:D2}:{totalLengthInSeconds % 60:D2}"
            : $"{totalLengthInSeconds / 60:D2}:{totalLengthInSeconds % 60:D2}";

        var mostCommonExtension = fileList.GroupBy(f => Utils.GetExtensionSlsk(f.Filename))
            .OrderByDescending(g => Utils.IsMusicExtension(g.Key)).ThenByDescending(g => g.Count()).First().Key.TrimStart('.');

        List<string> propsList = new() { mostCommonExtension.ToUpper().Trim(), totalLengthFormatted };
        if (modeSampleRate.HasValue)
            propsList.Add($"{(modeSampleRate.Value / 1000.0).Normalize()} kHz");
        if (meanBitrate.HasValue)
            propsList.Add($"{(int)meanBitrate.Value} kbps");
        propsList.Add(fileList.Any(f => !f.Target.Size.HasValue) ? "unknown size" : $"{totalFileSizeInMB:F2} MB");

        string gcp = string.IsNullOrWhiteSpace(folderPath)
            ? Utils.GreatestCommonDirectorySlsk(fileList.Select(x => x.Filename)).TrimEnd('\\')
            : folderPath.TrimEnd('\\');
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

    public static void RefreshOrPrint(int current, string item, bool print = false)
    {
        if (print)
            WriteLine(item);
    }

    public static void WriteLine(string value = "", ConsoleColor color = ConsoleColor.Gray, bool force = false)
    {
        if (ScopedOutput.Value is { } output)
        {
            output.WriteLine(value);
            return;
        }

        if (!force)
        {
            lock (ConsoleLock)
            {
                if (_isBuffering)
                {
                    _buffer.Add((value, color, true));
                    return;
                }
            }
        }

        if (!force && LiveWriteLine is { } liveWriteLine)
        {
            liveWriteLine(value, color);
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
        if (ScopedOutput.Value is { } output)
        {
            output.Write(value);
            return;
        }

        if (!force)
        {
            lock (ConsoleLock)
            {
                if (_isBuffering)
                {
                    _buffer.Add((value, color, false));
                    return;
                }
            }
        }

        lock (ConsoleLock)
        {
            Console.ForegroundColor = color;
            Console.Write(value);
            Console.ResetColor();
        }
    }

    internal static IDisposable RedirectOutput(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        TextWriter? previous = ScopedOutput.Value;
        ScopedOutput.Value = output;
        return new OutputScope(previous);
    }

    private sealed class OutputScope(TextWriter? previous) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                ScopedOutput.Value = previous;
        }
    }
}
