using System.Text.Json;

namespace Sockseek.Api;

public static class ServerEventPayloadConverter
{
    public static ServerEventEnvelopeDto RehydrateEnvelope(ServerEventEnvelopeDto envelope, JsonSerializerOptions? options = null)
    {
        if (envelope.Payload is not JsonElement payload)
            return envelope;

        options ??= SockseekApiJson.CreateSerializerOptions();

        object typedPayload = envelope.Type switch
        {
            "job.upserted" => Deserialize<JobSummaryDto>(payload, options),
            "workflow.upserted" => Deserialize<WorkflowSummaryDto>(payload, options),
            "search.updated" => Deserialize<SearchUpdatedDto>(payload, options),
            "diagnostic.error" => Deserialize<DiagnosticErrorEventDto>(payload, options),
            "extraction.started" => Deserialize<ExtractionStartedEventDto>(payload, options),
            "extraction.failed" => Deserialize<ExtractionFailedEventDto>(payload, options),
            "job.started" => Deserialize<JobStartedEventDto>(payload, options),
            "job.status" => Deserialize<JobStatusEventDto>(payload, options),
            "job.message" => Deserialize<JobMessageEventDto>(payload, options),
            "job.folder-retrieving" => Deserialize<JobFolderRetrievingEventDto>(payload, options),
            "song.searching" => Deserialize<SongSearchingEventDto>(payload, options),
            "download.started" => Deserialize<DownloadStartedEventDto>(payload, options),
            "download.progress" => Deserialize<DownloadProgressEventDto>(payload, options),
            "download.state-changed" => Deserialize<DownloadStateChangedEventDto>(payload, options),
            "download.attempt-failed" => Deserialize<DownloadAttemptFailedEventDto>(payload, options),
            "song.state-changed" => Deserialize<SongStateChangedEventDto>(payload, options),
            "album.download-started" => Deserialize<AlbumDownloadStartedEventDto>(payload, options),
            "album.track-download-started" => Deserialize<AlbumTrackDownloadStartedEventDto>(payload, options),
            "album.state-changed" => Deserialize<AlbumStateChangedEventDto>(payload, options),
            "on-complete.started" => Deserialize<OnCompleteStartedEventDto>(payload, options),
            "on-complete.ended" => Deserialize<OnCompleteEndedEventDto>(payload, options),
            "search.rate-limited" => Deserialize<SearchRateLimitedEventDto>(payload, options),
            "search.resumed" => Deserialize<SearchResumedEventDto>(payload, options),
            "track-batch.resolved" => Deserialize<TrackBatchResolvedEventDto>(payload, options),
            _ => payload,
        };

        return envelope with { Payload = typedPayload };
    }

    private static T Deserialize<T>(JsonElement payload, JsonSerializerOptions options)
        => payload.Deserialize<T>(options)
            ?? throw new InvalidOperationException($"Failed to deserialize server event payload as {typeof(T).Name}.");
}
