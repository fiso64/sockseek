using Sockseek.Core;

namespace Sockseek.Api;

public enum ActivityLogSeverity
{
    Information,
    Error,
}

public sealed record ActivityLogEntry(
    string CategoryName,
    ActivityLogSeverity Severity,
    string Message);

public sealed class JobActivityLogFormatter
{
    public static readonly IReadOnlySet<string> HandledEventTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "job.upserted",
        "album.download-started",
        "album.track-download-started",
        "album.download-completed",
        "download.started",
        "on-complete.started",
        "on-complete.ended",
        "song.state-changed",
        "extraction.started",
        "extraction.failed",
        "job.started",
        "job.folder-retrieving",
        "song.searching",
    };

    private readonly Dictionary<Guid, ServerJobKind> jobKinds = [];
    private readonly Dictionary<Guid, Guid> parentJobIds = [];
    private readonly Dictionary<Guid, JobSummaryDto> albumSummaries = [];
    private readonly Dictionary<Guid, string> lastMessages = [];
    private readonly object sync = new();

    public ActivityLogEntry? Format(ServerEventEnvelopeDto envelope)
    {
        lock (sync)
        {
            return envelope.Type switch
            {
                "job.upserted" when envelope.Payload is JobSummaryDto payload => HandleJobUpserted(payload),
                "album.download-started" when envelope.Payload is AlbumDownloadStartedEventDto payload => HandleAlbumDownloadStarted(payload),
                "album.track-download-started" when envelope.Payload is AlbumTrackDownloadStartedEventDto payload => HandleAlbumTrackDownloadStarted(payload),
                "album.download-completed" when envelope.Payload is AlbumDownloadCompletedEventDto payload => HandleAlbumDownloadCompleted(payload),
                "download.started" when envelope.Payload is DownloadStartedEventDto payload => HandleDownloadStart(payload),
                "on-complete.started" when envelope.Payload is OnCompleteStartedEventDto payload => Log(payload.JobId, $"OnComplete start: [{payload.DisplayId}] {SongQueryText(payload.Query)}"),
                "on-complete.ended" when envelope.Payload is OnCompleteEndedEventDto payload => Log(payload.JobId, $"OnComplete end: [{payload.DisplayId}] {SongQueryText(payload.Query)}"),
                "song.state-changed" when envelope.Payload is SongStateChangedEventDto payload => HandleSongStateChanged(payload),
                "extraction.started" when envelope.Payload is ExtractionStartedEventDto payload => HandleExtractionStart(payload),
                "extraction.failed" when envelope.Payload is ExtractionFailedEventDto payload => HandleExtractionFailed(payload),
                "job.started" when envelope.Payload is JobStartedEventDto payload => HandleJobStarted(payload),
                "job.folder-retrieving" when envelope.Payload is JobFolderRetrievingEventDto payload => HandleJobFolderRetrieving(payload),
                "song.searching" when envelope.Payload is SongSearchingEventDto payload => Log(payload.JobId, $"[{payload.DisplayId}] SongJob: searching: {SongQueryText(payload.Query)}"),
                _ => null,
            };
        }
    }

    private ActivityLogEntry? HandleExtractionStart(ExtractionStartedEventDto job)
    {
        if (string.IsNullOrWhiteSpace(job.InputType))
            return null;

        return Log(job.Summary.JobId, $"[{job.Summary.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}" + ProfileSuffix(job.Summary));
    }

    private ActivityLogEntry? HandleExtractionFailed(ExtractionFailedEventDto job)
        => Log(
            job.Summary.JobId,
            $"[{job.Summary.DisplayId}] ExtractJob: Failed: {job.Summary.QueryText}\n  Reason:    {job.Reason}",
            ActivityLogSeverity.Error);

    private ActivityLogEntry? HandleJobStarted(JobStartedEventDto job)
    {
        RememberStructure(job.Summary);
        if (IsInlineChild(job.Summary.JobId, job.Summary.Kind))
            return null;
        if (job.Summary.Kind == ServerJobKind.Song)
            return null;

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";
        return Log(job.Summary.JobId, JobStatusLine(job.Summary, status));
    }

    private ActivityLogEntry? HandleJobFolderRetrieving(JobFolderRetrievingEventDto job)
        => Log(job.Summary.JobId, JobStatusLine(job.Summary, "retrieving folder"));

    private ActivityLogEntry? HandleJobUpserted(JobSummaryDto summary)
    {
        RememberStructure(summary);

        if (summary.Kind == ServerJobKind.Song)
            return null;
        if (IsInlineChild(summary.JobId, summary.Kind))
            return null;
        if (summary.Kind == ServerJobKind.Album)
            albumSummaries[summary.JobId] = summary;

        bool isTerminal = IsTerminalJobState(summary.State);
        string label = TerminalStatusLabel(summary.State, summary.FailureReason);

        if (isTerminal)
        {
            if (summary.Kind == ServerJobKind.Album)
            {
                if (IsSuccessfulTerminalState(summary.State))
                    return null;
                albumSummaries.Remove(summary.JobId);
            }

            return Log(summary.JobId, JobStatusLine(summary, label));
        }

        return Log(summary.JobId, JobStatusLine(summary, label));
    }

    private ActivityLogEntry? HandleAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        RememberStructure(job.Summary);
        albumSummaries[job.Summary.JobId] = job.Summary;
        return Log(job.Summary.JobId, JobStatusLine(job.Summary, "downloading"));
    }

    private ActivityLogEntry? HandleAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        RememberStructure(job.Summary);
        albumSummaries[job.Summary.JobId] = job.Summary;
        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;

        if (job.Tracks != null)
        {
            foreach (var track in job.Tracks)
            {
                if (track.JobId is Guid childId)
                {
                    jobKinds[childId] = ServerJobKind.Song;
                    parentJobIds[childId] = job.Summary.JobId;
                }
            }
        }

        return Log(job.Summary.JobId, $"[{job.Summary.DisplayId}] AlbumJob: downloading tracks: {job.Summary.QueryText} - {folderName}");
    }

    private ActivityLogEntry? HandleAlbumDownloadCompleted(AlbumDownloadCompletedEventDto job)
    {
        albumSummaries.TryGetValue(job.Summary.JobId, out var album);
        var entry = Log(job.Summary.JobId, AlbumCompletedLogMessage(album ?? job.Summary, null, job.DownloadPath));
        albumSummaries.Remove(job.Summary.JobId);
        return entry;
    }

    private ActivityLogEntry? HandleDownloadStart(DownloadStartedEventDto song)
        => Log(song.JobId, $"[{song.DisplayId}] SongJob: downloading: {WithName(SongQueryText(song.Query), CandidateDisplayShort(song.Candidate.Ref))}");

    private ActivityLogEntry? HandleSongStateChanged(SongStateChangedEventDto song)
    {
        string label = TerminalStatusLabel(song.State, song.FailureReason);
        string detail = SongQueryText(song.Query);
        if (IsTerminalJobState(song.State) && song.ChosenCandidate is FileCandidateDto candidate)
            detail = WithName(detail, CandidateDisplayShort(candidate.Ref));

        string prefix = "SongJob: ";
        if (IsInlineChild(song.JobId, ServerJobKind.Song)
            && parentJobIds.TryGetValue(song.JobId, out var parentId)
            && albumSummaries.TryGetValue(parentId, out var album))
        {
            prefix = "AlbumJob: ";
            if (IsTerminalJobState(song.State))
            {
                string itemName = song.ChosenCandidate?.Ref.Filename != null
                    ? Utils.GetFileNameSlsk(song.ChosenCandidate.Ref.Filename)
                    : detail;
                return Log(song.JobId, $"[{album.DisplayId}] {prefix}{label}: {itemName}");
            }
        }

        string line = $"[{song.DisplayId}] {prefix}{label}: {detail}";
        if (!string.IsNullOrEmpty(song.FailureMessage))
            line += "\n" + $"    Error: {song.FailureMessage}";

        return Log(song.JobId, line);
    }

    private ActivityLogEntry? Log(Guid jobId, string message, ActivityLogSeverity severity = ActivityLogSeverity.Information)
    {
        if (lastMessages.TryGetValue(jobId, out var last) && last == message)
            return null;

        lastMessages[jobId] = message;
        return new ActivityLogEntry(SockseekLog.Categories.Jobs, severity, message);
    }

    private void RememberStructure(JobSummaryDto summary)
    {
        jobKinds[summary.JobId] = summary.Kind;
        if (summary.ParentJobId is Guid parentId)
            parentJobIds[summary.JobId] = parentId;
    }

    private bool IsInlineChild(Guid jobId, ServerJobKind kind)
        => kind == ServerJobKind.Song
            && parentJobIds.TryGetValue(jobId, out var parentId)
            && jobKinds.TryGetValue(parentId, out var parentKind)
            && parentKind == ServerJobKind.Album;

    private static bool IsTerminalJobState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists or ServerProtocol.JobStates.Failed or ServerProtocol.JobStates.Skipped;

    private static bool IsSuccessfulTerminalState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

    private static string TerminalStatusLabel(ServerJobState state, ServerFailureReason? reason)
        => state switch
        {
            ServerProtocol.JobStates.Done => "succeeded",
            ServerProtocol.JobStates.AlreadyExists => "already exists",
            ServerProtocol.JobStates.Pending => "pending",
            ServerProtocol.JobStates.Searching => "searching",
            ServerProtocol.JobStates.Downloading => "downloading",
            ServerProtocol.JobStates.Extracting => "extracting",
            ServerProtocol.JobStates.Running => "running",
            ServerProtocol.JobStates.Skipped => "skipped",
            _ => reason != null ? $"failed [{FailureReasonLabel(reason)}]" : "failed",
        };

    private static string FailureReasonLabel(ServerFailureReason? reason) => reason switch
    {
        ServerFailureReason.NoSuitableFileFound => "No suitable file found",
        ServerFailureReason.InvalidSearchString => "Invalid search string",
        ServerFailureReason.OutOfDownloadRetries => "Out of download retries",
        ServerFailureReason.AllDownloadsFailed => "All downloads failed",
        ServerFailureReason.ExtractionFailed => "Extraction failed",
        ServerFailureReason.Cancelled => "Cancelled",
        ServerFailureReason.Other => "Unknown error",
        _ => "",
    };

    private static string JobStatusLine(JobSummaryDto summary, string status)
    {
        var name = summary.ItemName ?? "";
        var detail = summary.QueryText ?? name;
        var prefix = summary.Kind switch
        {
            ServerJobKind.RetrieveFolder => "Retrieve Folder: ",
            ServerJobKind.JobList => "Job List: ",
            ServerJobKind.AlbumAggregate => "Album Aggregate: ",
            _ => $"{char.ToUpperInvariant(summary.Kind.ToWireString()[0])}{summary.Kind.ToWireString()[1..]}Job: ",
        };
        var line = $"[{summary.DisplayId}] {prefix}{status}: {WithName(name, detail)}" + ProfileSuffix(summary);

        if (summary.State == ServerProtocol.JobStates.Done && summary.Kind == ServerJobKind.Search && summary.DiscoveryResultCount.HasValue)
            line += $": Found {summary.DiscoveryResultCount.Value} files";

        if (!string.IsNullOrEmpty(summary.FailureMessage))
            line += "\n" + $"    Error: {summary.FailureMessage}";

        return line;
    }

    private static string AlbumCompletedLogMessage(JobSummaryDto summary, string? remoteFolderDisplay, string? completedPath)
    {
        var albumName = summary.QueryText ?? summary.ItemName ?? "";
        var status = TerminalStatusLabel(summary.State, summary.FailureReason);
        bool succeeded = summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

        if (succeeded && !string.IsNullOrWhiteSpace(remoteFolderDisplay))
            return $"{status}: {WithName(albumName, remoteFolderDisplay)}" + ProfileSuffix(summary);

        if (!string.IsNullOrWhiteSpace(completedPath))
            return $"{status}: {WithName(albumName, $"completed at {completedPath}")}" + ProfileSuffix(summary);

        return $"{status}: {albumName}" + ProfileSuffix(summary);
    }

    private static string SongQueryText(SongQueryDto query)
    {
        bool hasArtist = !string.IsNullOrWhiteSpace(query.Artist);
        bool hasTitle = !string.IsNullOrWhiteSpace(query.Title);
        if (hasArtist && hasTitle) return $"{query.Artist} - {query.Title}";
        if (hasArtist) return query.Artist!;
        if (hasTitle) return query.Title!;
        return query.Album ?? query.Uri ?? "";
    }

    private static string WithName(string name, string detail)
        => string.IsNullOrWhiteSpace(name) || name == detail ? detail : $"{name}: {detail}";

    private static string ProfileSuffix(JobSummaryDto summary)
        => summary.AppliedAutoProfiles.Count > 0 ? $" [{string.Join(", ", summary.AppliedAutoProfiles)}]" : "";

    private static string CandidateDisplayShort(FileCandidateRefDto candidate)
    {
        var filename = candidate.Filename.Replace('/', '\\').TrimStart('\\');
        var parts = filename.Split('\\');
        bool truncated = parts.Length > 3;
        var displayed = truncated ? string.Join('\\', parts[^3..]) : filename;
        return truncated ? $"{candidate.Username}\\..\\{displayed}" : $"{candidate.Username}\\{displayed}";
    }
}
