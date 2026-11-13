using System.Collections.Concurrent;
using Sockseek.Core;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;

namespace Sockseek.Cli;

internal static class PrintOutputRenderer
{
    private readonly record struct PrintRequest(Job Job, PrintOption Option);

    public static bool PrintRequestedOutput(
        JobList queue,
        Func<Guid, IReadOnlyDictionary<string, int>>? getPeerFailureCounts = null)
    {
        var requests = queue.Jobs.SelectMany(CollectPrintRequests).ToList();
        if (requests.Count == 0)
            return false;

        bool printedAny = false;
        for (int i = 0; i < requests.Count;)
        {
            var request = requests[i];
            if (printedAny && !IsMachineReadable(request.Option))
                Printing.WriteLine();

            if (IsJobPrint(request.Option))
            {
                var jobs = new List<Job>();
                var option = request.Option;
                do
                {
                    jobs.Add(requests[i].Job);
                    i++;
                }
                while (i < requests.Count && requests[i].Option == option && IsJobPrint(requests[i].Option));

                JobPrintFormatter.Print(jobs, option);
            }
            else
            {
                ResultPrintFormatter.Print(
                    request.Job,
                    request.Option,
                    request.Job.Config.Search,
                    getPeerFailureCounts?.Invoke(request.Job.WorkflowId));
                i++;
            }

            printedAny = true;
        }

        return true;
    }

    public static bool HasDownloadableJobs(JobList queue)
        => queue.Jobs.Any(HasDownloadableJob);

    public static bool HasRequestedOutput(JobList queue)
        => queue.Jobs.Any(HasRequestedOutput);

    private static IEnumerable<PrintRequest> CollectPrintRequests(Job job)
    {
        switch (job)
        {
            case ExtractJob { Result: { } result }:
                foreach (var request in CollectPrintRequests(result))
                    yield return request;
                yield break;

            case ExtractJob:
                yield break;

            case JobList list:
                foreach (var child in list.Jobs)
                foreach (var request in CollectPrintRequests(child))
                    yield return request;
                yield break;
        }

        if (job.Config.PrintJobs)
        {
            yield return new PrintRequest(job, job.Config.PrintOption);
            yield break;
        }

        if (job.Config.PrintResults)
            yield return new PrintRequest(job, job.Config.PrintOption);
    }

    private static bool HasDownloadableJob(Job job)
    {
        switch (job)
        {
            case ExtractJob { Result: { } result }:
                return HasDownloadableJob(result);
            case ExtractJob:
                return false;
            case JobList list:
                return list.Jobs.Any(HasDownloadableJob);
            case RetrieveFolderJob:
                return false;
            default:
                return !job.Config.DoNotDownload;
        }
    }

    private static bool HasRequestedOutput(Job job)
    {
        switch (job)
        {
            case ExtractJob { Result: { } result }:
                return HasRequestedOutput(result);
            case ExtractJob:
                return false;
            case JobList list:
                return list.Jobs.Any(HasRequestedOutput);
            default:
                return job.Config.PrintJobs || job.Config.PrintResults;
        }
    }

    private static bool IsJobPrint(PrintOption option)
        => option.HasFlag(PrintOption.Jobs);

    private static bool IsMachineReadable(PrintOption option)
        => (option & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;
}

internal static class JobPrintFormatter
{
    public static void Print(IReadOnlyList<Job> jobs, PrintOption option)
    {
        lock (Printing.ConsoleLock)
        {
            if (jobs.Count == 0)
                return;

            bool full = option.HasFlag(PrintOption.Full);
            PrintHeaderCore(jobs.Count);

            for (int i = 0; i < jobs.Count; i++)
                PrintJobCore(jobs[i], full, separate: i > 0);
        }
    }

    public static void PrintHeader(int count)
    {
        lock (Printing.ConsoleLock)
        {
            if (count > 0)
                PrintHeaderCore(count);
        }
    }

    public static void PrintJob(Job job, PrintOption option, bool separate)
    {
        lock (Printing.ConsoleLock)
            PrintJobCore(job, option.HasFlag(PrintOption.Full), separate);
    }

    private static void PrintHeaderCore(int count)
        => Printing.WriteLine($"{count} {Pluralize("job", count)}:");

    private static void PrintJobCore(Job job, bool full, bool separate)
    {
        if (full)
        {
            if (separate)
                Printing.WriteLine();
            foreach (var line in FormatFull(job))
                Printing.WriteLine("  " + line);
        }
        else
        {
            Printing.WriteLine($"  {FormatNormal(job)}");
        }
    }

    public static string FormatNormal(Job job)
        => $"{JobKind(job)}: {DisplayName(job, noInfo: true)}";

    private static IEnumerable<string> FormatFull(Job job)
    {
        yield return $"{JobKind(job)}:";

        switch (job)
        {
            case SongJob song:
                foreach (var line in SongFields(song))
                    yield return "  " + line;
                break;

            case AlbumJob album:
                foreach (var line in AlbumFields(album))
                    yield return "  " + line;
                break;

            case SongGroupsJob group:
                foreach (var line in SongQueryFields(group.Query))
                    yield return "  " + line;
                break;

            case AlbumGroupsJob group:
                foreach (var line in AlbumQueryFields(group.Query))
                    yield return "  " + line;
                break;

            case SearchJob search:
                yield return $"  Query:              {search.QueryText}";
                break;

            case RetrieveFolderJob folder:
                yield return $"  User:               {folder.Directory.Username}";
                yield return $"  Folder:             {folder.Directory.FolderPath}";
                break;

            default:
                yield return $"  Name:               {DisplayName(job, noInfo: true)}";
                break;
        }
        if (!string.IsNullOrWhiteSpace(job.ExtractorInfo))
            yield return $"  Extractor info:     {job.ExtractorInfo}";
    }

    private static IEnumerable<string> SongFields(SongJob song)
    {
        foreach (var line in SongQueryFields(song.Query))
            yield return line;
        if (!string.IsNullOrWhiteSpace(song.DownloadPath))
            yield return $"Local path:         {song.DownloadPath}";
    }

    private static IEnumerable<string> AlbumFields(AlbumJob album)
    {
        foreach (var line in AlbumQueryFields(album.Query))
            yield return line;
        if (!string.IsNullOrWhiteSpace(album.DownloadPath))
            yield return $"Local path:         {album.DownloadPath}";
    }

    private static IEnumerable<string> SongQueryFields(SongQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Artist))
            yield return $"Artist:             {query.Artist}";
        if (!string.IsNullOrWhiteSpace(query.Title))
            yield return $"Title:              {query.Title}";
        if (!string.IsNullOrWhiteSpace(query.Album))
            yield return $"Album:              {query.Album}";
        if (query.Length > -1)
            yield return $"Length:             {query.Length}s";
        if (!string.IsNullOrWhiteSpace(query.URI))
            yield return $"URL/ID:             {query.URI}";
        if (query.ArtistMaybeWrong)
            yield return $"Artist maybe wrong: {query.ArtistMaybeWrong}";
    }

    private static IEnumerable<string> AlbumQueryFields(AlbumQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Artist))
            yield return $"Artist:             {query.Artist}";
        if (!string.IsNullOrWhiteSpace(query.Album))
            yield return $"Album:              {query.Album}";
        if (!string.IsNullOrWhiteSpace(query.SearchHint))
            yield return $"Search hint:        {query.SearchHint}";
        if (!string.IsNullOrWhiteSpace(query.URI))
            yield return $"URL/ID:             {query.URI}";
        if (query.ArtistMaybeWrong)
            yield return $"Artist maybe wrong: {query.ArtistMaybeWrong}";
    }

    private static string DisplayName(Job job, bool noInfo)
        => job.ToString(noInfo);

    private static string JobKind(Job job) => job switch
    {
        SongJob => "Song",
        AlbumJob => "Album",
        SongGroupsJob => "Song Groups",
        AlbumGroupsJob => "Album Groups",
        SearchJob => "Search",
        RetrieveFolderJob => "Retrieve Folder",
        JobList => "Job List",
        ExtractJob => "Extract",
        _ => job.GetType().Name,
    };

    private static string Pluralize(string noun, int count)
        => count == 1 ? noun : noun + "s";
}

internal static class ResultPrintFormatter
{
    public static void Print(
        Job job,
        PrintOption printOption,
        SearchSettings search,
        IReadOnlyDictionary<string, int>? peerFailureSnapshot = null)
    {
        lock (Printing.ConsoleLock)
        {
            switch (job)
            {
                case JobList list:
                    PrintJobListResults(list, printOption, search, peerFailureSnapshot);
                    break;
                case SearchJob searchJob:
                    PrintSearchResults(
                        searchJob,
                        printOption,
                        search,
                        peerFailureSnapshot);
                    break;
                case SongJob song:
                    PrintSongResults(song, printOption);
                    break;
                case SongGroupsJob group:
                    PrintGroupResults(group, printOption);
                    break;
                case AlbumJob album:
                    PrintAlbumResults(album, printOption, search);
                    break;
                case AlbumGroupsJob group:
                    PrintAlbumGroupsResults(group, printOption, search);
                    break;
                default:
                    Printing.WriteLine("No results.");
                    break;
            }
        }
    }

    private static void PrintSearchResults(
        SearchJob searchJob,
        PrintOption printOption,
        SearchSettings search,
        IReadOnlyDictionary<string, int>? peerFailureSnapshot)
    {
        if (searchJob.DefaultFolderProjection != null)
        {
            var folders = searchJob.GetAlbumFolders(
                searchJob.DefaultFolderProjection, search, includeLocked: true,
                peerFailureCounts: peerFailureSnapshot == null ? null
                    : new ConcurrentDictionary<string, int>(peerFailureSnapshot, StringComparer.Ordinal));
            var album = new AlbumJob(searchJob.DefaultFolderProjection.Query)
            {
                Results = folders.Items.ToList(),
                Config = searchJob.Config,
            };
            PrintAlbumResults(album, printOption, search);
            return;
        }

        var projection = searchJob.DefaultFileProjection
            ?? new FileSearchProjection(new SongQuery { Title = searchJob.QueryText });
        var kernel = new SearchViewKernel(
            projection,
            search,
            peerFailureSnapshot);
        kernel.Apply(
            searchJob.RawSnapshot().Select(result => result.ProjectionInput),
            searchJob.Revision,
            searchJob.IsComplete);
        var candidates = kernel.Snapshot().Files
            .Select(file => file.Input.ToFileCandidate(file.ConditionFacts))
            .ToArray();
        var song = new SongJob(projection.Query)
        {
            Candidates = candidates.ToList(),
            Config = searchJob.Config,
        };
        PrintSongResults(song, printOption);
    }

    private static void PrintJobListResults(
        JobList list,
        PrintOption printOption,
        SearchSettings search,
        IReadOnlyDictionary<string, int>? peerFailureSnapshot)
    {
        bool nonVerbose = IsMachineReadable(printOption);
        bool printedAny = false;
        foreach (var child in list.Jobs)
        {
            if (child is ExtractJob)
                continue;

            if (printedAny && !nonVerbose)
                Printing.WriteLine();

            Print(child, printOption, search, peerFailureSnapshot);
            printedAny = true;
        }
    }

    private static void PrintGroupResults(SongGroupsJob group, PrintOption printOption)
    {
        var existing = group.Songs.Where(IsAlreadyExistingResultJob).ToList();
        var notFound = group.Songs.Where(song => IsNotFoundFailure(song.FailureReason)).ToList();

        if (printOption.HasFlag(PrintOption.Json))
        {
            JsonPrinter.PrintGroupJson(group.Songs.Where(s => s.IsPending));
        }
        else if (printOption.HasFlag(PrintOption.Link))
        {
            var first = group.Songs.SelectMany(song => song.Candidates ?? [])
                .FirstOrDefault(candidate => candidate.Visibility == SearchResultVisibility.Public);
            if (first != null)
                Printing.PrintLink(first.Username, first.Filename);
        }
        else
        {
            Printing.PrintTracksTbd(group.Songs.Where(s => s.IsPending).ToList(), existing, notFound, false, printOption);
        }
    }

    private static void PrintAlbumGroupsResults(AlbumGroupsJob group, PrintOption printOption, SearchSettings search)
    {
        if (group.Albums.Count == 0)
        {
            Printing.WriteLine("No results.");
            return;
        }

        bool nonVerbose = IsMachineReadable(printOption);
        for (int i = 0; i < group.Albums.Count; i++)
        {
            PrintAlbumResults(
                group.Albums[i],
                printOption,
                search,
                groupResultIndex: i + 1,
                groupResultCount: group.Albums.Count,
                groupDisplayName: group.ToString(true));

            if (!nonVerbose && i < group.Albums.Count - 1)
                Printing.WriteLine();
        }
    }

    private static void PrintAlbumResults(
        AlbumJob album,
        PrintOption printOption,
        SearchSettings search,
        int? groupResultIndex = null,
        int? groupResultCount = null,
        string? groupDisplayName = null)
    {
        if (printOption.HasFlag(PrintOption.Json))
        {
            var foldersToPrint = printOption.HasFlag(PrintOption.Full)
                ? album.Results
                : album.Results.Take(1).ToList();
            JsonPrinter.PrintAlbumJson(foldersToPrint, album);
            return;
        }

        if (printOption.HasFlag(PrintOption.Link))
        {
            var first = album.Results.FirstOrDefault(folder =>
                folder.Files.Any(file => file.Candidate.Visibility == SearchResultVisibility.Public));
            if (first != null)
                Printing.PrintAlbumLink(first);
            return;
        }

        string displayName = groupDisplayName ?? album.ToString(true);
        if (groupResultIndex is { } resultIndex && groupResultCount is { } resultCount)
            Printing.WriteLine($"Result {resultIndex} of {resultCount} for album {displayName}:");
        else if (!printOption.HasFlag(PrintOption.Full))
            Printing.WriteLine($"Result 1 of {album.Results.Count} for album {displayName}:");
        else
            Printing.WriteLine($"Results ({album.Results.Count}) for album {displayName}:");

        if (album.Results.Count == 0)
            return;

        if (!search.NoBrowseFolder)
            Printing.WriteLine("[Skipping full folder retrieval]");

        foreach (var folder in album.Results)
        {
            Printing.PrintAlbum(folder, showConditions: printOption.HasFlag(PrintOption.Full));
            if (!printOption.HasFlag(PrintOption.Full))
                break;
        }
    }

    private static void PrintSongResults(SongJob song, PrintOption printOption)
    {
        bool printFull = printOption.HasFlag(PrintOption.Full);
        bool nonVerbose = IsMachineReadable(printOption);
        var orderedResults = song.Candidates?.ToList();

        if (!nonVerbose)
            Printing.WriteLine($"Results for {song}:");

        if (orderedResults == null || orderedResults.Count == 0)
        {
            if (printOption.HasFlag(PrintOption.Json))
                JsonPrinter.PrintTrackResultJson(song.Query, Array.Empty<FileCandidate>());
            if (!nonVerbose)
                Printing.WriteLine("No results", ConsoleColor.Yellow);
            return;
        }

        if (!nonVerbose)
            Printing.WriteLine();

        if (printOption.HasFlag(PrintOption.Json))
            JsonPrinter.PrintTrackResultJson(song.Query, orderedResults, printFull);
        else if (printOption.HasFlag(PrintOption.Link))
        {
            var first = orderedResults.FirstOrDefault(candidate => candidate.Visibility == SearchResultVisibility.Public);
            if (first != null) Printing.PrintLink(first.Username, first.Filename);
        }
        else
            Printing.PrintTrackCandidates(
                orderedResults,
                song.Query,
                printFull);
    }

    private static bool IsMachineReadable(PrintOption printOption)
        => (printOption & (PrintOption.Json | PrintOption.Link | PrintOption.Index)) != 0;

    private static bool IsAlreadyExistingResultJob(Job job)
        => job.TerminalOutcome == JobTerminalOutcome.Skipped && job.SkipReason == JobSkipReason.AlreadyExists
        || job.TerminalOutcome == JobTerminalOutcome.Skipped && !string.IsNullOrWhiteSpace(GetResultJobDownloadPath(job));

    private static bool IsNotFoundFailure(JobFailureReason reason)
        => reason is JobFailureReason.NoSearchResults or JobFailureReason.NoMatchingResults;

    private static string? GetResultJobDownloadPath(Job job)
        => job switch
        {
            SongJob song => song.DownloadPath,
            AlbumJob album => album.DownloadPath,
            _ => null,
        };
}
