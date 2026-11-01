using Sockseek.Api;
using Sockseek.Core.Models;
using Sockseek.Server;
using Soulseek;

namespace Sockseek.Cli;

internal static class JobInfoPrinter
{
    private const int LabelWidth = 16;

    public static void Print(JobDetailDto detail, IReadOnlyList<JobSummaryDto> children)
    {
        var s = detail.Summary;

        var status = CliJobStatusPresenter.ForSummary(
            s,
            detail.Payload is SongJobPayloadDto songPayload ? songPayload.TransferState : null);

        Printing.WriteLine(force: true);
        Printing.Write($"[{s.DisplayId:000}] {s.Kind}", ConsoleColor.White, force: true);
        Printing.Write(" • ", ConsoleColor.DarkGray, force: true);

        Printing.WriteLine(status.Label, status.Color, force: true);
        PrintSplitState(s);
        if (!string.IsNullOrEmpty(s.SourceDetail))
            Field("Source", s.SourceDetail);

        switch (detail.Payload)
        {
            case SongJobPayloadDto song:
                PrintSong(song);
                break;
            case AlbumJobPayloadDto album:
                PrintAlbum(album, children);
                break;
            case ExtractJobPayloadDto extract:
                PrintExtract(extract, children);
                break;
            case AggregateJobPayloadDto agg:
                PrintAggregate(agg, children);
                break;
            case AlbumAggregateJobPayloadDto albumAgg:
                PrintAlbumAggregate(albumAgg, children);
                break;
            case JobListPayloadDto list:
                PrintJobList(list, children);
                break;
            case RetrieveFolderJobPayloadDto retrieve:
                PrintRetrieveFolder(retrieve);
                break;
            case RemoteFileJobPayloadDto remoteFile:
                PrintRemoteFile(remoteFile);
                break;
            case RemoteDirectoryJobPayloadDto remoteDirectory:
                PrintRemoteDirectory(remoteDirectory, children);
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
            Field("Remote path", PeerIdentityValidator.ToDisplayText(p.ResolvedFilename));
        if (p.File.DownloadPath != null)
            Field("Saved to", p.File.DownloadPath);

        if (p.File.FileSize > 0)
        {
            var xfer = FormatBytes(p.File.BytesTransferred);
            var total = FormatBytes(p.File.FileSize.Value);
            var pct = p.File.ProgressPercent is double pv ? $" ({pv:F0}%)" : "";
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
            Field("Remote path", PeerIdentityValidator.ToDisplayText(p.ResolvedFolderPath));
        if (p.Directory.DownloadPath != null)
            Field("Saved to", p.Directory.DownloadPath);

        if (p.Directory.FileCount is int total && total > 0)
        {
            var completed = p.Directory.TerminalFileCount;
            var ok = p.Directory.SuccessfulFileCount;
            var failed = p.Directory.FailedFileCount;
            Field("Progress", $"{completed} / {total} files  ({ok} ok, {failed} failed)");
        }

        if (p.ResultCount > 0)
            Field("Results", $"{p.ResultCount} folders found");

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

        Field("Songs", $"{p.SongCount} total  •  {CountOrUnknown(p.CompletedSongCount)} completed  •  {CountOrUnknown(p.SucceededSongCount)} ok  •  {CountOrUnknown(p.FailedSongCount)} failed");

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
        Field("Jobs", $"{p.Count} total  •  {CountOrUnknown(p.ActiveJobCount)} active  •  {CountOrUnknown(p.SucceededJobCount)} ok  •  {CountOrUnknown(p.FailedJobCount)} failed");

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
        Field("Folder", PeerIdentityValidator.ToDisplayText(p.FolderPath));
        Field("New files", $"{p.NewFilesFoundCount} found");
        Field("Outcome", p.RetrievalOutcome.ToString());
        if (p.RetrievalCancelled)
            Field("Cancelled", "yes", ConsoleColor.Yellow);
    }

    private static void PrintRemoteFile(RemoteFileJobPayloadDto p)
    {
        Field("From", p.Target.Username, ConsoleColor.DarkCyan);
        Field("Remote path", PeerIdentityValidator.ToDisplayText(p.Target.Filename));
        if (p.File.DownloadPath != null)
            Field("Saved to", p.File.DownloadPath);
        if (p.File.FileSize > 0)
        {
            var percent = p.File.ProgressPercent is double value ? $" ({value:F0}%)" : "";
            Field("Transfer", $"{FormatBytes(p.File.BytesTransferred)} of {FormatBytes(p.File.FileSize.Value)}{percent}");
        }
    }

    private static void PrintRemoteDirectory(
        RemoteDirectoryJobPayloadDto p,
        IReadOnlyList<JobSummaryDto> children)
    {
        if (p.SourceUsername != null)
            Field("From", p.SourceUsername, ConsoleColor.DarkCyan);
        if (p.SourceFolderPath != null)
            Field("Remote path", PeerIdentityValidator.ToDisplayText(p.SourceFolderPath));
        if (p.Directory.DownloadPath != null)
            Field("Saved to", p.Directory.DownloadPath);
        Field("Directory phase", p.Directory.Phase);
        if (p.Directory.FileCount > 0)
        {
            Field("Progress", $"{p.Directory.TerminalFileCount} / {p.Directory.FileCount} files  " +
                $"({p.Directory.SuccessfulFileCount} ok, {p.Directory.FailedFileCount} failed)");
        }
        if (children.Count > 0)
        {
            Printing.WriteLine(force: true);
            Printing.WriteLine($"  Files ({children.Count}):", ConsoleColor.Gray, force: true);
            foreach (var child in children)
                PrintChildSummary(child);
        }
    }

    private static void PrintChildSummary(JobSummaryDto child)
    {
        var status = CliJobStatusPresenter.ForSummary(child);
        var name = child.ItemName ?? child.QueryText ?? child.JobId.ToString("D");
        Printing.Write($"    [{child.DisplayId:000}] ", ConsoleColor.DarkGray, force: true);
        Printing.Write($"{status.Label,-18}", status.Color, force: true);
        Printing.WriteLine(name, ConsoleColor.White, force: true);
    }

    private static void PrintSplitState(JobSummaryDto s)
    {
        Field("Lifecycle", s.LifecycleState.ToString(), ConsoleColor.Gray);
        if (s.ActivityPhase != ServerJobActivityPhase.None)
            Field("Activity", s.ActivityPhase.ToString(), ConsoleColor.Cyan);
        if (s.ActivityUntilUtc is DateTimeOffset until)
            Field("Activity until", until.ToLocalTime().ToString("u"), ConsoleColor.DarkCyan);
        if (s.TerminalOutcome != ServerJobTerminalOutcome.None)
            Field("Outcome", s.TerminalOutcome.ToString(),
                s.TerminalOutcome == ServerJobTerminalOutcome.Succeeded
                    || (s.TerminalOutcome == ServerJobTerminalOutcome.Skipped && s.SkipReason == ServerJobSkipReason.AlreadyExists)
                    ? ConsoleColor.Green
                    : ConsoleColor.Yellow);
        if (s.SkipReason != ServerJobSkipReason.None)
            Field("Skip reason", s.SkipReason.ToString(), ConsoleColor.DarkGray);
        if (s.FailureReason is { } reason)
            Field("Failure reason", CliJobStatusPresenter.FailureReasonLabel(reason), ConsoleColor.Yellow);
    }

    private static void Field(string label, string value, ConsoleColor valueColor = ConsoleColor.White)
    {
        Printing.Write($"  {label,-LabelWidth}  ", ConsoleColor.DarkGray, force: true);
        Printing.WriteLine(value, valueColor, force: true);
    }

    private static string CountOrUnknown(int? count)
        => count?.ToString() ?? "unknown";

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
                if (a.Value < 0) continue;
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

        if (sampleRate is int sr && sr >= 0 && !parts.Exists(p => p.EndsWith("Hz")))
            parts.Add($"{sr} Hz");
        if (bitDepth is int bd && bd >= 0 && !parts.Exists(p => p.EndsWith("-bit")))
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

}
