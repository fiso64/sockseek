namespace Sldl.Server;

public static class ServerEventCatalog
{
    public const string StateCategory = "state";
    public const string ActivityCategory = "activity";
    public const string ProgressCategory = "progress";

    private static readonly IReadOnlyDictionary<string, ServerEventDescriptorDto> Descriptors =
        new[]
        {
            State("job.upserted", nameof(JobSummaryDto)),
            State("workflow.upserted", nameof(WorkflowSummaryDto)),
            State("search.updated", nameof(SearchUpdatedDto)),
            Progress("download.progress", nameof(DownloadProgressEventDto)),
            Activity("extraction.started", nameof(ExtractionStartedEventDto)),
            Activity("extraction.failed", nameof(ExtractionFailedEventDto)),
            Activity("job.started", nameof(JobStartedEventDto)),
            Activity("job.status", nameof(JobStatusEventDto)),
            Activity("job.folder-retrieving", nameof(JobFolderRetrievingEventDto)),
            Activity("song.searching", nameof(SongSearchingEventDto)),
            Activity("download.started", nameof(DownloadStartedEventDto)),
            Activity("download.state-changed", nameof(DownloadStateChangedEventDto)),
            Activity("song.state-changed", nameof(SongStateChangedEventDto)),
            Activity("album.download-started", nameof(AlbumDownloadStartedEventDto)),
            Activity("album.track-download-started", nameof(AlbumTrackDownloadStartedEventDto)),
            Activity("album.download-completed", nameof(AlbumDownloadCompletedEventDto)),
            Activity("on-complete.started", nameof(OnCompleteStartedEventDto)),
            Activity("on-complete.ended", nameof(OnCompleteEndedEventDto)),
            Activity("track-batch.resolved", nameof(TrackBatchResolvedEventDto)),
        }
        .ToDictionary(descriptor => descriptor.Type, StringComparer.Ordinal);

    public static IReadOnlyList<ServerEventDescriptorDto> All { get; } =
        Descriptors.Values.OrderBy(descriptor => descriptor.Type, StringComparer.Ordinal).ToList();

    public static ServerEventDescriptorDto Describe(string type)
        => Descriptors.TryGetValue(type, out var descriptor)
            ? descriptor
            : new ServerEventDescriptorDto(type, ActivityCategory, false, "object");

    private static ServerEventDescriptorDto State(string type, string payloadDto)
        => new(type, StateCategory, true, payloadDto);

    private static ServerEventDescriptorDto Activity(string type, string payloadDto)
        => new(type, ActivityCategory, false, payloadDto);

    private static ServerEventDescriptorDto Progress(string type, string payloadDto)
        => new(type, ProgressCategory, false, payloadDto);
}
