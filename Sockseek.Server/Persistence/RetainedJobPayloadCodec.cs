using System.Text.Json;
using System.Text.Json.Serialization;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Snapshots;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

// Compact execution records are the retained contract too. Structural graphs and
// manifests are reduced at capture; encoding waits for write coalescing. Live and
// retained presentation share ServerSnapshotMapper's typed rules.
internal static class RetainedJobPayloadCodec
{
    internal const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static Lazy<string?> Capture(JobSnapshotPayload payload)
    {
        JobExecutionPayload compact = payload switch
        {
            JobExecutionPayload execution => execution,
            AlbumJobSnapshotPayload album => new AlbumExecutionPayload(album.Query, album.ResultCount,
                album.ResolvedTarget is { } folder ? new(folder.Username, folder.FolderPath, folder.Peer,
                    folder.SearchFileCount, folder.SearchAudioFileCount, folder.IsFullyRetrieved) : null,
                album.Directory, album.Definition),
            RemoteDirectoryJobSnapshotPayload directory => new RemoteDirectoryExecutionPayload(
                directory.SourceKind, directory.DirectorySource,
                ServerSnapshotMapper.RemoteDirectorySourceUsername(directory),
                directory.DirectorySource?.FolderPath ?? directory.Manifest?.Plan.SourceDirectory?.FolderPath
                    ?? directory.ResolvedPlanSource?.SourceDirectory?.FolderPath,
                directory.Manifest?.Plan.DisplayRoot ?? directory.ResolvedPlanSource?.DisplayRoot ?? directory.DirectorySource?.FolderPath,
                directory.Directory),
            SongGroupsJobSnapshotPayload group => new SongGroupsExecutionPayload(group.Query, group.Definition, group.Outcomes),
            JobListSnapshotPayload list => new JobListExecutionPayload(ServerSnapshotMapper.ListOutcomes(list)),
            RetrieveFolderJobSnapshotPayload retrieve => new RetrieveFolderExecutionPayload(
                retrieve.Directory, retrieve.NewFilesFoundCount, retrieve.RetrievalOutcome, retrieve.RetrievalCancelled),
            _ => throw new InvalidOperationException($"Unsupported retained job payload {payload.GetType().Name}."),
        };
        return new(() => JsonSerializer.Serialize(compact, compact.GetType(), Options));
    }

    internal static JobExecutionPayload? Read(PersistedJob job)
    {
        if (job.PayloadSchemaVersion != SchemaVersion)
            throw new InvalidDataException($"Unsupported job payload schema {job.PayloadSchemaVersion} for {job.Id}.");
        if (string.IsNullOrWhiteSpace(job.PayloadJson)) return null;
        Type type = job.Kind switch
        {
            "Extract" => typeof(ExtractJobSnapshotPayload),
            "Search" => typeof(SearchJobSnapshotPayload),
            "Song" => typeof(SongJobSnapshotPayload),
            "Album" => typeof(AlbumExecutionPayload),
            "RemoteFile" => typeof(RemoteFileJobSnapshotPayload),
            "RemoteDirectory" => typeof(RemoteDirectoryExecutionPayload),
            "SongGroups" => typeof(SongGroupsExecutionPayload),
            "AlbumGroups" => typeof(AlbumGroupsJobSnapshotPayload),
            "JobList" => typeof(JobListExecutionPayload),
            "RetrieveFolder" => typeof(RetrieveFolderExecutionPayload),
            "Generic" => typeof(GenericJobSnapshotPayload),
            _ => throw new InvalidDataException($"Unsupported retained job kind '{job.Kind}'."),
        };
        var payload = (JobExecutionPayload?)JsonSerializer.Deserialize(job.PayloadJson, type, Options)
            ?? throw new InvalidDataException($"Job {job.Id} has an empty retained payload.");
        Definition(payload)?.Validate();
        return payload;
    }

    internal static SearchDefinition? Definition(JobSnapshotPayload? payload) => payload switch
    {
        SearchJobSnapshotPayload search => search.Definition,
        SongJobSnapshotPayload song => song.Definition,
        AlbumExecutionPayload album => album.Definition,
        SongGroupsExecutionPayload group => group.Definition,
        AlbumGroupsJobSnapshotPayload group => group.Definition,
        _ => null,
    };
}
