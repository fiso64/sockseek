using System.Text.Json;
using Sockseek.Api;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Planning;
using Sockseek.Core.Services;
using Sockseek.Persistence.Read;

namespace Sockseek.Server.Persistence;

internal static class HistoricalJobDtoMapper
{
    public static async Task<SearchDefinition> SearchDefinitionAsync(
        ISubmissionStore? submissions,
        PersistedJob job,
        CancellationToken cancellationToken)
    {
        SearchDefinition? retainedJobDefinition = SearchDefinitionFromPayload(job);
        if (job.SubmissionId is Guid submissionId)
        {
            if (submissions == null)
                return retainedJobDefinition ?? throw MissingSearchDefinition(job.Id);
            PersistedSubmission? submission = await submissions.GetSubmissionAsync(
                submissionId,
                cancellationToken).ConfigureAwait(false);
            if (submission == null)
                return retainedJobDefinition ?? throw MissingSearchDefinition(job.Id);
            SubmissionSpecification specification = SubmissionSpecificationCodec.Deserialize(
                submission.SpecificationJson);
            SearchDefinition? acceptedDefinition = specification.Search
                ?? specification.Command.SearchDefinition;
            if (acceptedDefinition != null && retainedJobDefinition != null
                && !string.Equals(
                    SearchDefinitionCodec.Serialize(acceptedDefinition),
                    SearchDefinitionCodec.Serialize(retainedJobDefinition),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Historical search {job.Id} has conflicting accepted and execution definitions.");
            }

            // A directly accepted search is fully described by its immutable
            // submission. Extractors can create search children only after the
            // root was accepted; their derived execution definition therefore
            // belongs to the retained search-job record.
            return acceptedDefinition
                ?? retainedJobDefinition
                ?? throw MissingSearchDefinition(job.Id);
        }
        return retainedJobDefinition ?? throw MissingSearchDefinition(job.Id);
    }

    private static SearchDefinition? SearchDefinitionFromPayload(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return null;
        using var document = JsonDocument.Parse(job.PayloadJson);
        JsonElement definition = Child(document.RootElement, "Definition");
        if (definition.ValueKind == JsonValueKind.Object)
            return SearchDefinitionCodec.Deserialize(definition.GetRawText());

        // Compatibility for prototype and pre-submission rows that nested the
        // stable SearchDefinition serialization as a string.
        string? json = Text(document.RootElement, "DefinitionJson");
        return string.IsNullOrWhiteSpace(json)
            ? null
            : SearchDefinitionCodec.Deserialize(json);
    }

    private static InvalidOperationException MissingSearchDefinition(Guid jobId)
        => new($"Historical search {jobId} has no retained search definition and cannot be reprojected authoritatively.");

    public static FileSearchProjection? DefaultFileProjection(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return null;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var projection = Child(document.RootElement, "DefaultFileProjection");
        if (projection.ValueKind != JsonValueKind.Object)
            return null;
        var query = Child(projection, "Query");
        return new FileSearchProjection(
            new SongQuery
            {
                Artist = Text(query, "Artist") ?? "",
                Title = Text(query, "Title") ?? "",
                Album = Text(query, "Album") ?? "",
                URI = Text(query, "URI") ?? "",
                Length = NullableInt(query, "Length") ?? -1,
                ArtistMaybeWrong = Bool(query, "ArtistMaybeWrong"),
            },
            Bool(projection, "IncludeFullResults"));
    }

    public static FolderSearchProjection? DefaultFolderProjection(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return null;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var projection = Child(document.RootElement, "DefaultFolderProjection");
        if (projection.ValueKind != JsonValueKind.Object)
            return null;
        var query = Child(projection, "Query");
        return new FolderSearchProjection(
            new AlbumQuery
            {
                Artist = Text(query, "Artist") ?? "",
                Album = Text(query, "Album") ?? "",
                SearchHint = Text(query, "SearchHint") ?? "",
                URI = Text(query, "URI") ?? "",
                ArtistMaybeWrong = Bool(query, "ArtistMaybeWrong"),
            },
            Bool(projection, "IncludeFiles"));
    }

    public static JobSummaryDto ToSummary(PersistedJob job)
    {
        SearchDefinition? searchDefinition = SearchPresentationDefinition(job);
        return new(
            job.Id,
            checked((int)job.DisplayId),
            job.WorkflowId,
            Parse(job.Kind, ServerJobKind.Generic),
            Parse(job.LifecycleState, ServerJobLifecycleState.Pending),
            Parse(job.ActivityPhase, ServerJobActivityPhase.None),
            job.ActivityUntilUtc,
            Parse(job.TerminalOutcome, ServerJobTerminalOutcome.None),
            Parse(job.SkipReason, ServerJobSkipReason.None),
            job.ItemName,
            job.QueryText,
            job.FailureReason == "None" ? null : Parse(job.FailureReason, ServerJobFailureReason.Other),
            job.FailureMessage,
            job.ParentJobId,
            job.ResultJobId,
            job.SourceJobId,
            ToNullableInt(job.DiscoveryPublicFileCount),
            ToNullableInt(job.DiscoveryLockedFileCount),
            [],
            [],
            job.FailureDetail,
            Parse(job.CancellationSource, ServerJobCancellationSource.None),
            SubmissionId: job.SubmissionId,
            Role: Parse(job.SemanticRole, ServerJobRole.Legacy),
            CreatedAtUtc: job.CreatedAtUtc,
            DiscoveryPublicFileCount: ToNullableInt(job.DiscoveryPublicFileCount),
            DiscoveryObservedPeerCount: ToNullableInt(job.DiscoveryObservedPeerCount),
            Stats: ToSummaryStats(job),
            SearchDefaultProjection: searchDefinition?.DefaultProjection.ToServer(),
            SearchAggregatePresentation: searchDefinition?.AggregatePresentation,
            SourceDetail: SourceDetail(job));
    }

    private static string? SourceDetail(PersistedJob job)
    {
        if (job.SourceDetail != null || job.Kind != "Extract") return job.SourceDetail;
        if (!string.IsNullOrWhiteSpace(job.PayloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(job.PayloadJson);
                var root = document.RootElement;
                if (Enum.TryParse<Sockseek.Core.InputType>(Text(root, "InputType"), ignoreCase: true, out var type))
                    return $"{Sockseek.Core.Extractors.ExtractorRegistry.SourceName(type)} import";
            }
            catch (JsonException) { }
        }
        return "Import";
    }

    private static SearchDefinition? SearchPresentationDefinition(PersistedJob job)
    {
        if (!string.Equals(job.Kind, nameof(ServerJobKind.Search), StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return SearchDefinitionFromPayload(job);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
        {
            // A malformed legacy definition must not hide the rest of a history page.
            return null;
        }
    }

    private static JobSummaryStatsDto? ToSummaryStats(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return null;
        ServerJobKind kind = Parse(job.Kind, ServerJobKind.Generic);
        if (kind is ServerJobKind.Search or ServerJobKind.Extract or ServerJobKind.Generic)
            return null;
        try
        {
            using var document = JsonDocument.Parse(job.PayloadJson);
            JsonElement root = document.RootElement;
            JsonElement directory = Child(root, "Directory");
            return kind switch
            {
                ServerJobKind.Song => new(CandidateCount: NullableInt(root, "CandidateCount")),
                ServerJobKind.Album => new(
                    MemberCount: NullableInt(directory, "FileCount"),
                    CompletedMemberCount: NullableInt(directory, "TerminalFileCount"),
                    FailedMemberCount: NullableInt(directory, "FailedFileCount"),
                    FolderCount: NullableInt(root, "ResultCount"),
                    FileCount: NullableInt(directory, "FileCount"),
                    ProgressPercent: DirectoryProgressPercent(directory)),
                ServerJobKind.RemoteFile => new(KnownBytes:
                    NullableLong(Child(root, "File"), "FileSize")
                    ?? NullableLong(Child(root, "Target"), "Size")),
                ServerJobKind.RemoteDirectory => new(
                    MemberCount: NullableInt(directory, "FileCount"),
                    CompletedMemberCount: NullableInt(directory, "TerminalFileCount"),
                    FailedMemberCount: NullableInt(directory, "FailedFileCount"),
                    FileCount: NullableInt(directory, "FileCount"),
                    ProgressPercent: DirectoryProgressPercent(directory)),
                ServerJobKind.Aggregate => new(
                    MemberCount: NullableInt(root, "SongCount"),
                    CompletedMemberCount: NullableInt(root, "CompletedSongCount"),
                    FailedMemberCount: NullableInt(root, "FailedSongCount")),
                ServerJobKind.AlbumAggregate => new(
                    MemberCount: NullableInt(root, "AlbumCount"),
                    CompletedMemberCount: NullableInt(root, "CompletedAlbumCount"),
                    FailedMemberCount: NullableInt(root, "FailedAlbumCount")),
                ServerJobKind.JobList => new(
                    MemberCount: NullableInt(root, "Count"),
                    CompletedMemberCount: NullableInt(root, "CompletedJobCount"),
                    FailedMemberCount: NullableInt(root, "FailedJobCount")),
                ServerJobKind.RetrieveFolder => new(
                    NewFilesFoundCount: NullableInt(root, "NewFilesFoundCount")),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // A malformed optional payload must not make the entire retained
            // Jobs page unavailable; detail reads can still report that row.
            return null;
        }
    }

    private static int? ToNullableInt(long? value)
        => value.HasValue ? checked((int)value.Value) : null;

    private static double? DirectoryProgressPercent(JsonElement directory)
        => ServerSnapshotMapper.DirectoryProgressPercent(
            Int(directory, "FileCount"), Int(directory, "SuccessfulFileCount"),
            Long(directory, "BytesTransferred"), Long(directory, "TotalKnownBytes"),
            directory.ValueKind == JsonValueKind.Object
                && directory.TryGetProperty("Progress", out var progress)
                && progress.ValueKind == JsonValueKind.Number
                ? progress.GetDouble() : null);

    public static JobPayloadDto ToPayload(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson))
            return new GenericJobPayloadDto(job.Kind);
        using var document = JsonDocument.Parse(job.PayloadJson);
        var root = document.RootElement;
        return Parse(job.Kind, ServerJobKind.Generic) switch
        {
            ServerJobKind.Extract => new ExtractJobPayloadDto(
                Text(root, "Input") ?? "",
                Text(root, "InputType"),
                job.ResultJobId),
            ServerJobKind.Search => new SearchJobPayloadDto(
                Text(root, "QueryText") ?? job.QueryText ?? "",
                DefaultFolderProjection(job) != null
                    ? ServerSearchDefaultProjectionKind.Album
                    : DefaultFileProjection(job) != null
                        ? ServerSearchDefaultProjectionKind.Track
                        : ServerSearchDefaultProjectionKind.GenericFile,
                DefaultFileProjection(job) is { } fileProjection
                    ? ServerSnapshotMapper.ToSongQueryDto(fileProjection.Query)
                    : null,
                DefaultFolderProjection(job) is { } folderProjection
                    ? ServerSnapshotMapper.ToAlbumQueryDto(folderProjection.Query)
                    : null,
                DefaultFileProjection(job)?.IncludeFullResults ?? false,
                Int(root, "ResultCount"),
                Int(root, "Revision"),
                Bool(root, "IsComplete"),
                SearchDefinitionFromPayload(job)?.AggregatePresentation ?? false),
            ServerJobKind.Song => new SongJobPayloadDto(
                SongQuery(root),
                NullableInt(root, "CandidateCount"),
                FileState(root),
                ResolvedUsername: Text(Child(root, "ResolvedTarget"), "Username"),
                ResolvedFilename: Text(Child(root, "ResolvedTarget"), "Filename"),
                ResolvedHasFreeUploadSlot: NullableBool(Child(Child(root, "ResolvedTarget"), "Peer"), "HasFreeUploadSlot"),
                ResolvedUploadSpeed: NullableInt(Child(Child(root, "ResolvedTarget"), "Peer"), "UploadSpeed"),
                ResolvedSize: NullableLong(Child(root, "ResolvedTarget"), "Size"),
                ResolvedSampleRate: NullableInt(Child(root, "ResolvedTarget"), "SampleRate"),
                ResolvedExtension: Text(Child(root, "ResolvedTarget"), "Extension"),
                ResolvedAttributes: ChildArray(Child(root, "ResolvedTarget"), "Attributes")
                    .Select(attribute => new FileAttributeDto(Text(attribute, "Type") ?? "Unknown", Int(attribute, "Value")))
                    .ToArray(),
                JobId: job.Id,
                DisplayId: checked((int)job.DisplayId),
                LifecycleState: Parse(job.LifecycleState, ServerJobLifecycleState.Pending),
                ActivityPhase: Parse(job.ActivityPhase, ServerJobActivityPhase.None),
                ActivityUntilUtc: job.ActivityUntilUtc,
                TerminalOutcome: Parse(job.TerminalOutcome, ServerJobTerminalOutcome.None),
                SkipReason: Parse(job.SkipReason, ServerJobSkipReason.None),
                FailureReason: job.FailureReason == "None" ? null : Parse(job.FailureReason, ServerJobFailureReason.Other),
                FailureMessage: job.FailureMessage,
                AvailableActions: [],
                CancellationSource: Parse(job.CancellationSource, ServerJobCancellationSource.None),
                DownloadSource: Parse(Text(root, "DownloadSource"), ServerSongDownloadSource.None),
                ExactTarget: NullablePeerTarget(Child(root, "ExactTarget")),
                LastAttemptedFile: Child(root, "LastAttemptedCandidate") is { ValueKind: JsonValueKind.Object } attempted
                    ? ServerSnapshotMapper.ToAttemptedSongFileDto(
                        attempted.Deserialize<Sockseek.Core.Snapshots.FileCandidateSnapshot>()) : null),
            ServerJobKind.Album => new AlbumJobPayloadDto(
                AlbumQuery(root),
                Int(root, "ResultCount"),
                DirectoryState(root),
                Text(root, "ResolvedFolderUsername"),
                Text(root, "ResolvedFolderPath")),
            ServerJobKind.RemoteFile => RemoteFilePayload(root),
            ServerJobKind.RemoteDirectory => RemoteDirectoryPayload(root),
            ServerJobKind.Aggregate => new AggregateJobPayloadDto(
                SongQuery(root),
                Int(root, "SongCount"),
                NullableInt(root, "CompletedSongCount"),
                NullableInt(root, "SucceededSongCount"),
                NullableInt(root, "FailedSongCount")),
            ServerJobKind.AlbumAggregate => new AlbumAggregateJobPayloadDto(
                AlbumQuery(root),
                Int(root, "AlbumCount"),
                NullableInt(root, "CompletedAlbumCount"),
                NullableInt(root, "SucceededAlbumCount"),
                NullableInt(root, "FailedAlbumCount")),
            ServerJobKind.JobList => new JobListPayloadDto(Int(root, "Count"),
                NullableInt(root, "ActiveJobCount"), NullableInt(root, "CompletedJobCount"),
                NullableInt(root, "SucceededJobCount"), NullableInt(root, "FailedJobCount")),
            ServerJobKind.RetrieveFolder => new RetrieveFolderJobPayloadDto(
                Text(root, "FolderPath") ?? "",
                Text(root, "Username") ?? "",
                Int(root, "NewFilesFoundCount"),
                Parse(Text(root, "RetrievalOutcome"), ServerFolderRetrievalOutcome.None),
                Bool(root, "RetrievalCancelled")),
            _ => new GenericJobPayloadDto(Text(root, "Text") ?? job.Kind),
        };
    }

    private static SongQueryDto SongQuery(JsonElement root)
    {
        var query = Child(root, "Query");
        return new SongQueryDto(
            Text(query, "Artist"),
            Text(query, "Title"),
            Text(query, "Album"),
            Text(query, "URI"),
            NullableInt(query, "Length"),
            Bool(query, "ArtistMaybeWrong"));
    }

    private static AlbumQueryDto AlbumQuery(JsonElement root)
    {
        var query = Child(root, "Query");
        return new AlbumQueryDto(
            Text(query, "Artist"),
            Text(query, "Album"),
            Text(query, "SearchHint"),
            Text(query, "URI"),
            Bool(query, "ArtistMaybeWrong"));
    }

    private static FileDownloadStateDto FileState(JsonElement root)
    {
        var file = Child(root, "File");
        if (file.ValueKind != JsonValueKind.Object)
            return new FileDownloadStateDto(Text(root, "DownloadPath"), 0, null, null);

        long transferred = Long(file, "BytesTransferred");
        long? size = NullableLong(file, "FileSize");
        return new FileDownloadStateDto(
            Text(file, "DownloadPath"),
            transferred,
            size,
            size > 0 ? Math.Round((double)transferred / size.Value * 100, 2) : null);
    }

    private static DirectoryDownloadStateDto DirectoryState(JsonElement root)
    {
        var directory = Child(root, "Directory");
        if (directory.ValueKind != JsonValueKind.Object)
        {
            return new DirectoryDownloadStateDto(
                "unresolved", null, Text(root, "DownloadPath"), 0, 0, 0, 0, 0, 0, null);
        }

        return new DirectoryDownloadStateDto(
            Text(directory, "Phase") ?? "unresolved",
            NullableInt(directory, "AttemptNumber"),
            Text(directory, "DownloadPath"),
            Int(directory, "FileCount"),
            Int(directory, "TerminalFileCount"),
            Int(directory, "SuccessfulFileCount"),
            Int(directory, "FailedFileCount"),
            Long(directory, "BytesTransferred"),
            Long(directory, "TotalKnownBytes"),
            DirectoryProgressPercent(directory),
            NullableInt(directory, "UnknownFileSizeCount"));
    }

    private static RemoteFileJobPayloadDto RemoteFilePayload(JsonElement root)
    {
        var output = Child(root, "OutputPath");
        return new RemoteFileJobPayloadDto(
            PeerTarget(Child(root, "Target")),
            StringArray(output, "Components"),
            FileState(root));
    }

    private static RemoteDirectoryJobPayloadDto RemoteDirectoryPayload(JsonElement root)
    {
        var source = Child(root, "DirectorySource");
        var plan = Child(root, "ResolvedPlanSource");
        var planSource = Child(plan, "SourceDirectory");
        var manifestSource = Child(Child(Child(root, "Manifest"), "Plan"), "SourceDirectory");
        return new RemoteDirectoryJobPayloadDto(
            Parse(Text(root, "SourceKind"), RemoteDirectorySourceKindDto.PeerDirectory),
            Text(root, "SourceUsername") ?? Text(source, "Username")
                ?? Text(planSource, "Username") ?? Text(manifestSource, "Username"),
            Text(root, "SourceFolderPath") ?? Text(source, "FolderPath") ?? Text(planSource, "FolderPath")
                ?? Text(manifestSource, "FolderPath"),
            DirectoryState(root),
            Text(root, "DisplayRoot") ?? Text(plan, "DisplayRoot")
                ?? Text(source, "FolderPath"));
    }

    internal static PeerFileTargetDto? ToSelectedFileTarget(PersistedJob job)
    {
        if (string.IsNullOrWhiteSpace(job.PayloadJson)) return null;
        using var document = JsonDocument.Parse(job.PayloadJson);
        var root = document.RootElement;
        var selected = job.Kind == "RemoteFile" ? Child(root, "Target") : Child(root, "ResolvedTarget");
        if (selected.ValueKind != JsonValueKind.Object) selected = Child(root, "ExactTarget");
        if (selected.ValueKind != JsonValueKind.Object) selected = Child(root, "LastAttemptedCandidate");
        if (selected.ValueKind != JsonValueKind.Object) return null;
        if (Child(selected, "Target") is { ValueKind: JsonValueKind.Object } target) selected = target;
        var result = PeerTarget(selected);
        if (result.Size is < 0) result = result with { Size = null };
        return string.IsNullOrEmpty(result.Username) || string.IsNullOrEmpty(result.Filename) ? null : result;
    }

    private static PeerFileTargetDto PeerTarget(JsonElement element)
    {
        var identity = Child(element, "Identity");
        if (identity.ValueKind != JsonValueKind.Object) identity = element;
        var attributes = ChildArray(element, "Attributes")
            .Select(attribute => new FileAttributeDto(
                Text(attribute, "Type") ?? "Unknown",
                Int(attribute, "Value")))
            .ToArray();
        return new PeerFileTargetDto(
            Text(identity, "Username") ?? "",
            Text(identity, "Filename") ?? "",
            NullableLong(element, "Size"),
            Text(element, "Extension"),
            NullableInt(element, "BitRate"),
            NullableInt(element, "BitDepth"),
            NullableInt(element, "SampleRate"),
            NullableInt(element, "Length"),
            attributes.Length == 0 ? null : attributes);
    }

    private static PeerFileTargetDto? NullablePeerTarget(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? PeerTarget(element) : null;

    private static JsonElement Child(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;

    private static int Int(JsonElement root, string name)
        => NullableInt(root, name) ?? 0;

    private static int? NullableInt(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result)
                ? result
                : null;

    private static long Long(JsonElement root, string name)
        => NullableLong(root, name) ?? 0;

    private static long? NullableLong(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long result)
                ? result
                : null;

    private static IReadOnlyList<JsonElement> ChildArray(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();

    private static IReadOnlyList<string> StringArray(JsonElement root, string name)
        => ChildArray(root, name)
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .ToArray();

    private static bool? NullableBool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static bool Bool(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
