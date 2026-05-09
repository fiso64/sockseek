using Sldl.Core;
using Sldl.Core.Models;
using Sldl.Server;

namespace Sldl.Cli;

internal sealed class EventLogger
{
    private readonly ICliBackend _backend;
    private readonly bool _liveMode;

    private readonly Dictionary<Guid, ServerJobKind> _jobKinds = new();
    private readonly Dictionary<Guid, Guid> _parentJobIds = new();
    private readonly Dictionary<Guid, string> _jobStatuses = new();

    public EventLogger(ICliBackend backend, bool liveMode)
    {
        _backend = backend;
        _liveMode = liveMode;
    }

    public void Attach()
    {
        _backend.EventReceived += HandleEvent;
    }

    private void HandleEvent(ServerEventEnvelopeDto envelope)
    {
        switch (envelope.Type)
        {
            case "job.upserted":
                HandleJobUpserted((JobSummaryDto)envelope.Payload);
                break;
            case "album.download-started":
                HandleAlbumDownloadStarted((AlbumDownloadStartedEventDto)envelope.Payload);
                break;
            case "album.track-download-started":
                HandleAlbumTrackDownloadStarted((AlbumTrackDownloadStartedEventDto)envelope.Payload);
                break;
            case "album.download-completed":
                HandleAlbumDownloadCompleted((AlbumDownloadCompletedEventDto)envelope.Payload);
                break;
            case "download.start":
                HandleDownloadStart((DownloadStartedEventDto)envelope.Payload);
                break;
            case "on-complete.start":
                HandleOnCompleteStart((OnCompleteStartedEventDto)envelope.Payload);
                break;
            case "on-complete.end":
                HandleOnCompleteEnd((OnCompleteEndedEventDto)envelope.Payload);
                break;
            case "song.state-changed":
                HandleSongStateChanged((SongStateChangedEventDto)envelope.Payload);
                break;
            case "extract.start":
                HandleExtractionStart((ExtractionStartedEventDto)envelope.Payload);
                break;
            case "extract.failed":
                HandleExtractionFailed((ExtractionFailedEventDto)envelope.Payload);
                break;
            case "job.started":
                HandleJobStarted((JobStartedEventDto)envelope.Payload);
                break;
            case "job.folder-retrieving":
                HandleJobFolderRetrieving((JobFolderRetrievingEventDto)envelope.Payload);
                break;
            case "song.searching":
                HandleSongSearching((SongSearchingEventDto)envelope.Payload);
                break;
        }
    }

    private void HandleExtractionStart(ExtractionStartedEventDto job)
    {
        if (string.IsNullOrWhiteSpace(job.InputType)) return;
        Log(job.Summary.JobId, $"[{job.Summary.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}" + ProfileSuffix(job.Summary), ephemeral: true);
    }

    private void HandleExtractionFailed(ExtractionFailedEventDto job)
    {
        // Extraction failure is an Error level event in the original code.
        var message = $"[{job.Summary.DisplayId}] ExtractJob: Failed: {job.Summary.QueryText}\n  Reason:    {job.Reason}";
        if (_liveMode)
            Logger.LogNonConsole(Logger.LogLevel.Error, message);
        else
            Logger.Error(message);
    }

    private void HandleJobStarted(JobStartedEventDto job)
    {
        _jobKinds[job.Summary.JobId] = job.Summary.Kind;
        if (job.Summary.ParentJobId is Guid parentId) _parentJobIds[job.Summary.JobId] = parentId;

        if (IsInlineChild(job.Summary.JobId, job.Summary.Kind)) return;
        if (job.Summary.Kind == ServerJobKind.Song) return; // song.searching handles this

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";
        Log(job.Summary.JobId, JobStatusLine(job.Summary, status), ephemeral: true);
    }

    private void HandleJobFolderRetrieving(JobFolderRetrievingEventDto job)
    {
        Log(job.Summary.JobId, JobStatusLine(job.Summary, "retrieving folder"), ephemeral: true);
    }

    private void HandleSongSearching(SongSearchingEventDto song)
    {
        Log(song.JobId, $"[{song.DisplayId}] SongJob: searching: {SongQueryText(song.Query)}", ephemeral: true);
    }

    private readonly Dictionary<Guid, JobSummaryDto> _albumSummaries = new();

    // ... HandleEvent case "job.upserted" ...

    private void HandleJobUpserted(JobSummaryDto summary)
    {
        _jobKinds[summary.JobId] = summary.Kind;
        if (summary.ParentJobId is Guid parentId)
        {
             _parentJobIds[summary.JobId] = parentId;
        }
        
        if (summary.Kind == ServerJobKind.Song) return; // Handled by song.state-changed or download-start
        if (IsInlineChild(summary.JobId, summary.Kind)) return;
        if (summary.Kind == ServerJobKind.Album) _albumSummaries[summary.JobId] = summary;

        bool isTerminal = IsTerminalJobState(summary.State);
        string label = TerminalStatusLabel(summary.State, summary.FailureReason);

        if (IsInlineChild(summary.JobId, summary.Kind))
        {
            if (!isTerminal) return;
            if (summary.ParentJobId is Guid albumId && _albumSummaries.TryGetValue(albumId, out var album))
            {
                if (summary.State == ServerProtocol.JobStates.Done) label = "downloaded";
                Log(summary.JobId, $"[{album.DisplayId}] AlbumJob: {label}: {summary.ItemName}", ephemeral: true);
            }
            return;
        }

        if (isTerminal)
        {
            if (summary.Kind == ServerJobKind.Album)
            {
                if (IsSuccessfulTerminalState(summary.State)) return; // Let album.download-completed handle it
                _albumSummaries.Remove(summary.JobId);
            }

            Log(summary.JobId, JobStatusLine(summary, label), ephemeral: false);
        }
        else
        {
            if (summary.Kind == ServerJobKind.Song) return;
            Log(summary.JobId, JobStatusLine(summary, label), ephemeral: true);
        }
    }

    private void HandleAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        _jobKinds[job.Summary.JobId] = job.Summary.Kind;
        _albumSummaries[job.Summary.JobId] = job.Summary;
        Log(job.Summary.JobId, JobStatusLine(job.Summary, "downloading"), ephemeral: true);
    }

    private void HandleAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        _jobKinds[job.Summary.JobId] = job.Summary.Kind;
        _albumSummaries[job.Summary.JobId] = job.Summary;
        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;
        if (job.Tracks != null)
        {
            foreach (var track in job.Tracks)
            {
                if (track.JobId is Guid childId)
                {
                    _jobKinds[childId] = ServerJobKind.Song;
                    _parentJobIds[childId] = job.Summary.JobId;
                }
            }
        }

        Log(job.Summary.JobId, $"[{job.Summary.DisplayId}] AlbumJob: downloading tracks: {job.Summary.QueryText} - {folderName}", ephemeral: true);
    }

    private void HandleSongStateChanged(SongStateChangedEventDto song)
    {
        var summary = new JobSummaryDto(
            song.JobId, song.DisplayId, song.WorkflowId, ServerJobKind.Song, song.State,
            null, SongQueryText(song.Query), song.FailureReason, song.FailureMessage,
            null, null, null, null, null, [], []);

        string label = TerminalStatusLabel(song.State, song.FailureReason);
        string detail = SongQueryText(song.Query);
        if (IsTerminalJobState(song.State) && song.ChosenCandidate is FileCandidateDto c)
            detail = WithName(detail, CandidateDisplayShort(c.Ref));

        string prefix = "SongJob: ";
        bool isInline = IsInlineChild(song.JobId, ServerJobKind.Song);
        bool hasParentId = _parentJobIds.TryGetValue(song.JobId, out var parentId);
        
        JobSummaryDto? album = null;
        if (hasParentId) _albumSummaries.TryGetValue(parentId, out album);
        bool hasAlbum = album != null;

        if (isInline && hasParentId && hasAlbum)
        {
            prefix = "AlbumJob: ";
            if (IsTerminalJobState(song.State))
            {
                // Matches old behavior: [AlbumID] AlbumJob: downloaded: SongName
                string itemName = song.ChosenCandidate?.Ref.Filename != null ? System.IO.Path.GetFileName(song.ChosenCandidate.Ref.Filename) : detail;
                Log(song.JobId, $"[{album!.DisplayId}] {prefix}{label}: {itemName}", ephemeral: false);
                return;
            }
        }

        string line = $"[{song.DisplayId}] {prefix}{label}: {detail}";
        if (!string.IsNullOrEmpty(song.FailureMessage))
            line += Environment.NewLine + $"    Error: {song.FailureMessage}";

        Log(song.JobId, line, ephemeral: !IsTerminalJobState(song.State));
    }

    private void HandleAlbumDownloadCompleted(AlbumDownloadCompletedEventDto job)
    {
        _albumSummaries.TryGetValue(job.Summary.JobId, out var album);
        Log(job.Summary.JobId, AlbumCompletedLogMessage(album ?? job.Summary, null, job.DownloadPath), ephemeral: false);
        _albumSummaries.Remove(job.Summary.JobId);
    }

    private void HandleDownloadStart(DownloadStartedEventDto song)
    {
        Log(song.JobId, $"[{song.DisplayId}] SongJob: downloading: {WithName(SongQueryText(song.Query), CandidateDisplayShort(song.Candidate.Ref))}", ephemeral: true);
    }

    private void HandleOnCompleteStart(OnCompleteStartedEventDto song)
    {
        Log(song.JobId, $"OnComplete start: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}", ephemeral: true);
    }

    private void HandleOnCompleteEnd(OnCompleteEndedEventDto song)
    {
        Log(song.JobId, $"OnComplete end: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}", ephemeral: true);
    }

    private readonly Dictionary<Guid, string> _lastMessages = new();

    private void Log(Guid jobId, string message, bool ephemeral)
    {
        if (_lastMessages.TryGetValue(jobId, out var last) && last == message) return;
        _lastMessages[jobId] = message;

        // If we are in Live Mode, EventLogger output always goes to the file only.
        if (_liveMode)
            Logger.LogNonConsole(Logger.LogLevel.Info, message);
        else
            Logger.Info(message);
    }
    
    private void Log(string message, bool ephemeral)
    {
        // For events without a JobId, we use a global last message tracker or just allow them?
        // Most events have a jobId. 
        if (_liveMode)
            Logger.LogNonConsole(Logger.LogLevel.Info, message);
        else
            Logger.Info(message);
    }
    
    private void LogError(string message)
    {
        if (_liveMode)
            Logger.LogNonConsole(Logger.LogLevel.Error, message);
        else
            Logger.Error(message);
    }

    // --- Formatting Helpers (mirrored from CliProgressReporter) ---

    private bool IsInlineChild(Guid jobId, ServerJobKind kind)
    {
        return kind == ServerJobKind.Song
            && _parentJobIds.TryGetValue(jobId, out var parentId)
            && _jobKinds.TryGetValue(parentId, out var parentKind)
            && parentKind == ServerJobKind.Album;
    }

    private static bool IsTerminalJobState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists or ServerProtocol.JobStates.Failed or ServerProtocol.JobStates.Skipped;

    private static bool IsSuccessfulTerminalState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;

    private static string TerminalStatusLabel(ServerJobState state, ServerFailureReason? reason)
    {
        return state switch
        {
            ServerProtocol.JobStates.Done => "succeeded",
            ServerProtocol.JobStates.AlreadyExists => "already exists",
            ServerProtocol.JobStates.Pending => "pending",
            ServerProtocol.JobStates.Searching => "searching",
            ServerProtocol.JobStates.Downloading => "downloading",
            ServerProtocol.JobStates.Extracting => "extracting",
            ServerProtocol.JobStates.Running => "running",
            ServerProtocol.JobStates.Skipped => "skipped",
            _ => reason != null ? $"failed [{FailureReasonLabel(reason)}]" : "failed"
        };
    }

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
        var d = summary.QueryText ?? name;
        var prefix = summary.Kind switch {
            ServerJobKind.RetrieveFolder => "Retrieve Folder: ",
            ServerJobKind.JobList => "Job List: ",
            ServerJobKind.AlbumAggregate => "Album Aggregate: ",
            _ => $"{char.ToUpperInvariant(summary.Kind.ToWireString()[0])}{summary.Kind.ToWireString()[1..]}Job: "
        };
        var line = $"[{summary.DisplayId}] {prefix}{status}: {WithName(name, d)}" + ProfileSuffix(summary);
        if (!string.IsNullOrEmpty(summary.FailureMessage))
            line += Environment.NewLine + $"    Error: {summary.FailureMessage}";
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
        var shortPath = truncated ? $"{candidate.Username}\\..\\{displayed}" : $"{candidate.Username}\\{displayed}";
        return shortPath;
    }
}
