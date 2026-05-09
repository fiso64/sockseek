using Sldl.Server;

namespace Sldl.Cli;

internal static class JobInfoPrinter
{
    private const int LabelWidth = 16;

    public static void Print(JobDetailDto detail)
    {
        var s = detail.Summary;
        var stateColor = StateColor(s.State);

        Printing.WriteLine(force: true);
        Printing.Write($"[{s.DisplayId:000}] {s.Kind}", ConsoleColor.White, force: true);
        Printing.Write(" • ", ConsoleColor.DarkGray, force: true);
        Printing.WriteLine(s.State.ToString(), stateColor, force: true);

        switch (detail.Payload)
        {
            case SongJobPayloadDto song:
                PrintSong(song);
                break;
            case AlbumJobPayloadDto album:
                PrintAlbum(album, detail.Children);
                break;
            case ExtractJobPayloadDto extract:
                PrintExtract(extract, detail.Children);
                break;
            case AggregateJobPayloadDto agg:
                PrintAggregate(agg, detail.Children);
                break;
            case AlbumAggregateJobPayloadDto albumAgg:
                PrintAlbumAggregate(albumAgg, detail.Children);
                break;
            case JobListPayloadDto list:
                PrintJobList(list, detail.Children);
                break;
            case RetrieveFolderJobPayloadDto retrieve:
                PrintRetrieveFolder(retrieve);
                break;
            case GenericJobPayloadDto generic:
                Field("Info", generic.Text);
                break;
            default:
                if (s.ItemName != null) Field("Name", s.ItemName);
                if (s.QueryText != null) Field("Query", s.QueryText);
                break;
        }

        if (s.FailureMessage != null)
            Field("Error", s.FailureMessage, ConsoleColor.Red);

        Printing.WriteLine(force: true);
    }

    private static void PrintSong(SongJobPayloadDto p)
    {
        var queryText = FormatSongQuery(p.Query);
        if (queryText != null) Field("Query", queryText);

        if (p.ResolvedUsername != null)
            Field("From", p.ResolvedUsername, ConsoleColor.DarkCyan);
        if (p.ResolvedFilename != null)
            Field("Remote path", p.ResolvedFilename);
        if (p.DownloadPath != null)
            Field("Saved to", p.DownloadPath);

        if (p.TotalBytes > 0)
        {
            var xfer = p.BytesTransferred is long x ? FormatBytes(x) : "?";
            var total = FormatBytes(p.TotalBytes.Value);
            var pct = p.ProgressPercent is double pv ? $" ({pv:F0}%)" : "";
            Field("Transfer", $"{xfer} of {total}{pct}");
        }
        else if (p.ResolvedSize > 0)
        {
            Field("Size", FormatBytes(p.ResolvedSize.Value));
        }

        var attrs = FormatAttributes(p.ResolvedAttributes, p.ResolvedSampleRate, null);
        if (attrs != null) Field("Attributes", attrs);

        if (p.CandidateCount is int c && c > 0)
            Field("Candidates", $"{c} found");
    }

    private static void PrintAlbum(AlbumJobPayloadDto p, IReadOnlyList<JobSummaryDto> children)
    {
        var queryText = FormatAlbumQuery(p.Query);
        if (queryText != null) Field("Query", queryText);

        if (p.ResolvedFolderUsername != null)
            Field("From", p.ResolvedFolderUsername, ConsoleColor.DarkCyan);
        if (p.ResolvedFolderPath != null)
            Field("Remote path", p.ResolvedFolderPath);
        if (p.DownloadPath != null)
            Field("Saved to", p.DownloadPath);

        if (p.SelectedFolderFileCount is int total && total > 0)
        {
            var completed = p.SelectedFolderCompletedFileCount ?? 0;
            var ok = p.SelectedFolderSucceededFileCount ?? 0;
            var failed = p.SelectedFolderFailedFileCount ?? 0;
            Field("Progress", $"{completed} / {total} files  ({ok} ok, {failed} failed)");
        }

        if (p.ResultCount > 0)
            Field("Results", $"{p.ResultCount} folders found");

        if (children.Count > 0)
        {
            Printing.WriteLine(force: true);
            Printing.WriteLine($"  Tracks ({children.Count}):", ConsoleColor.Gray, force: true);
            foreach (var child in children)
                PrintChildSummary(child);
        }
    }

    private static void PrintExtract(ExtractJobPayloadDto p, IReadOnlyList<JobSummaryDto> children)
    {
        Field("Input", p.Input);
        if (p.InputType != null) Field("Type", p.InputType);

        var result = children.Count > 0 ? children[0] : null;
        if (result != null && result.DisplayId > 0)
            Field("Result job", $"[{result.DisplayId:000}] {result.Kind}");
    }

    private static void PrintAggregate(AggregateJobPayloadDto p, IReadOnlyList<JobSummaryDto> children)
    {
        var queryText = FormatSongQuery(p.Query);
        if (queryText != null) Field("Query", queryText);

        Field("Songs", $"{p.SongCount} total  •  {p.CompletedSongCount} completed  •  {p.SucceededSongCount} ok  •  {p.FailedSongCount} failed");

        if (children.Count > 0)
        {
            Printing.WriteLine(force: true);
            Printing.WriteLine($"  Children ({children.Count}):", ConsoleColor.Gray, force: true);
            foreach (var child in children)
                PrintChildSummary(child);
        }
    }

    private static void PrintAlbumAggregate(AlbumAggregateJobPayloadDto p, IReadOnlyList<JobSummaryDto> children)
    {
        var queryText = FormatAlbumQuery(p.Query);
        if (queryText != null) Field("Query", queryText);
        Field("Results", $"{p.ResultCount} albums found");

        if (children.Count > 0)
        {
            Printing.WriteLine(force: true);
            Printing.WriteLine($"  Children ({children.Count}):", ConsoleColor.Gray, force: true);
            foreach (var child in children)
                PrintChildSummary(child);
        }
    }

    private static void PrintJobList(JobListPayloadDto p, IReadOnlyList<JobSummaryDto> children)
    {
        Field("Jobs", $"{p.Count} total  •  {p.ActiveJobCount} active  •  {p.SucceededJobCount} ok  •  {p.FailedJobCount} failed");

        if (children.Count > 0)
        {
            Printing.WriteLine(force: true);
            Printing.WriteLine($"  Children ({children.Count}):", ConsoleColor.Gray, force: true);
            foreach (var child in children)
                PrintChildSummary(child);
        }
    }

    private static void PrintRetrieveFolder(RetrieveFolderJobPayloadDto p)
    {
        Field("Username", p.Username, ConsoleColor.DarkCyan);
        Field("Folder", p.FolderPath);
        Field("New files", $"{p.NewFilesFoundCount} found");
        Field("Outcome", p.RetrievalOutcome.ToString());
        if (p.RetrievalCancelled)
            Field("Cancelled", "yes", ConsoleColor.Yellow);
    }

    private static void PrintChildSummary(JobSummaryDto child)
    {
        var stateColor = StateColor(child.State);
        var name = child.ItemName ?? child.QueryText ?? child.JobId.ToString("D");
        Printing.Write($"    [{child.DisplayId:000}] ", ConsoleColor.DarkGray, force: true);
        Printing.Write($"{child.State,-14}", stateColor, force: true);
        Printing.WriteLine(name, ConsoleColor.White, force: true);
    }

    private static void Field(string label, string value, ConsoleColor valueColor = ConsoleColor.White)
    {
        Printing.Write($"  {label,-LabelWidth}  ", ConsoleColor.DarkGray, force: true);
        Printing.WriteLine(value, valueColor, force: true);
    }

    private static string? FormatSongQuery(SongQueryDto? q)
    {
        if (q == null) return null;
        if (!string.IsNullOrWhiteSpace(q.Artist) && !string.IsNullOrWhiteSpace(q.Title))
            return $"{q.Artist} - {q.Title}";
        if (!string.IsNullOrWhiteSpace(q.Title)) return q.Title;
        if (!string.IsNullOrWhiteSpace(q.Artist)) return q.Artist;
        if (!string.IsNullOrWhiteSpace(q.Album)) return q.Album;
        return q.Uri;
    }

    private static string? FormatAlbumQuery(AlbumQueryDto? q)
    {
        if (q == null) return null;
        if (!string.IsNullOrWhiteSpace(q.Artist) && !string.IsNullOrWhiteSpace(q.Album))
            return $"{q.Artist} - {q.Album}";
        if (!string.IsNullOrWhiteSpace(q.Album)) return q.Album;
        if (!string.IsNullOrWhiteSpace(q.Artist)) return q.Artist;
        return null;
    }

    private static string? FormatAttributes(IReadOnlyList<FileAttributeDto>? attrs, int? sampleRate, int? bitDepth)
    {
        var parts = new List<string>();

        if (attrs != null)
        {
            foreach (var a in attrs)
            {
                var formatted = a.Type switch
                {
                    "BitRate"    => $"{a.Value} kbps",
                    "SampleRate" => $"{a.Value} Hz",
                    "BitDepth"   => $"{a.Value}-bit",
                    "Length"     => $"{a.Value / 60}:{a.Value % 60:D2}",
                    _ => null,
                };
                if (formatted != null) parts.Add(formatted);
            }
        }

        if (sampleRate is int sr && !parts.Exists(p => p.EndsWith("Hz")))
            parts.Add($"{sr} Hz");
        if (bitDepth is int bd && !parts.Exists(p => p.EndsWith("-bit")))
            parts.Add($"{bd}-bit");

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    private static ConsoleColor StateColor(ServerJobState state) => state switch
    {
        ServerJobState.Done or ServerJobState.AlreadyExists          => ConsoleColor.Green,
        ServerJobState.Failed                                         => ConsoleColor.Red,
        ServerJobState.Downloading or ServerJobState.Searching
            or ServerJobState.Running or ServerJobState.Extracting   => ConsoleColor.Cyan,
        ServerJobState.Skipped or ServerJobState.NotFoundLastTime     => ConsoleColor.DarkGray,
        _                                                             => ConsoleColor.Gray,
    };
}
