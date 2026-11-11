using System.Reflection;
using System.Text.Json;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;
using Sockseek.Core.Models;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Core.Snapshots;
using Sockseek.Api;
using Sockseek.Server;
using Sockseek.Server.Persistence;
using Sockseek.Persistence.Read;

namespace Tests.Server;

[TestClass]
public class OpenApiContractTests
{
    [TestMethod]
    public async Task TypedSubmissionHistoryPagesSummariesAndDeletesAcceptedWork()
    {
        string directory = Directory.CreateTempSubdirectory("sockseek-submission-api-").FullName;
        try
        {
            await using var app = global::Tests.TestServerHost.Build([], new ServerOptions
            {
                Engine = new EngineSettings { LogLevel = LogLevel.None },
                Persistence = new ServerPersistenceOptions
                {
                    Enabled = true,
                    DataDirectory = directory,
                    RetentionEnabled = false,
                },
            }, "http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                var store = app.Services.GetRequiredService<PersistenceCoordinator>().Submissions!;
                Guid[] ids = Enumerable.Range(1, 3)
                    .Select(value => Guid.Parse($"40000000-0000-0000-0000-{value:x12}")).ToArray();
                foreach (Guid id in ids)
                    await store.CreateAsync(new SubmissionRegistration(
                        id, DateTimeOffset.FromUnixTimeMilliseconds(1000), 99,
                        "{\"retained\":\"not-public\"}", null, null, "artifact"));
                using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
                var client = new SockseekApiClient(http);
                var first = await client.GetSubmissionsPageAsync(limit: 1);
                Assert.AreEqual(ids[2], first.Items.Single().SubmissionId);
                Assert.IsNotNull(first.NextCursor);
                var second = await client.GetSubmissionsPageAsync(cursor: first.NextCursor, limit: 1);
                var third = await client.GetSubmissionsPageAsync(cursor: second.NextCursor, limit: 1);
                CollectionAssert.AreEqual(ids.Reverse().ToArray(),
                    first.Items.Concat(second.Items).Concat(third.Items)
                        .Select(item => item.SubmissionId).ToArray());
                Assert.IsNull(third.NextCursor);
                Assert.IsTrue(first.Items.Concat(second.Items).Concat(third.Items)
                    .All(item => item.TotalJobCount == 0 && item.Revision == 1));
                var deleted = await client.DeleteSubmissionAsync(ids[1]);
                Assert.AreEqual(0, deleted.RejectedSubmissionCount);
                CollectionAssert.AreEqual(new[] { ids[2], ids[0] },
                    (await client.GetSubmissionsPageAsync()).Items.Select(item => item.SubmissionId).ToArray());
                Assert.IsNull(await client.GetSubmissionAsync(ids[1]));
                using var response = await http.GetAsync("/api/submissions?limit=1");
                response.EnsureSuccessStatusCode();
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(JsonValueKind.Array, body.RootElement.ValueKind);
                Assert.IsFalse(body.RootElement[0].TryGetProperty("specificationJson", out _));
                Assert.IsTrue(response.Headers.Contains("X-Next-Cursor"));
                foreach (string path in new[] { "/api/jobs", "/api/jobs?includeAll=true" })
                {
                    using var jobsResponse = await http.GetAsync(path);
                    jobsResponse.EnsureSuccessStatusCode();
                    using var jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
                    Assert.AreEqual(0, jobs.RootElement.GetArrayLength(),
                        "Optional job flags must default without making an unfiltered request invalid.");
                }
                Assert.AreEqual("{\"retained\":\"not-public\"}",
                    (await store.GetSubmissionAsync(ids[0]))!.SpecificationJson);
            }
            finally
            {
                await app.StopAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void JobQueryEnums_UseCanonicalWireSpellings()
    {
        foreach (var kind in Enum.GetValues<ServerJobKind>())
        {
            Assert.IsTrue(ServerProtocolEnumExtensions.TryParseJobKind(kind.ToWireString(), out var parsed));
            Assert.AreEqual(kind, parsed);
        }
        Assert.IsFalse(ServerProtocolEnumExtensions.TryParseJobKind("unknown-kind", out _));
        Assert.IsFalse(ServerProtocolEnumExtensions.TryParseJobKind("7", out _));
        Assert.IsTrue(ServerProtocolEnumExtensions.TryParseJobKind("RetrieveFolder", out var legacyKind));
        Assert.AreEqual(ServerJobKind.RetrieveFolder, legacyKind);
        foreach (var role in Enum.GetValues<ServerJobRole>())
        {
            Assert.IsTrue(ServerProtocolEnumExtensions.TryParseJobRole(role.ToWireString(), out var parsed));
            Assert.AreEqual(role, parsed);
        }
        Assert.IsFalse(ServerProtocolEnumExtensions.TryParseJobRole("unknown-role", out _));
        Assert.IsFalse(ServerProtocolEnumExtensions.TryParseJobRole("2", out _));
        Assert.IsTrue(ServerProtocolEnumExtensions.TryParseJobRole("ExecutionChild", out var legacyRole));
        Assert.AreEqual(ServerJobRole.ExecutionChild, legacyRole);

        string documentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "openapi.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(documentPath));
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/jobs")
            .GetProperty("get").GetProperty("parameters");
        var kindParameter = parameters.EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "kind");
        var values = kindParameter.GetProperty("schema").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        CollectionAssert.Contains(values, ServerJobKind.RetrieveFolder.ToWireString());
        var roleParameter = parameters.EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "role");
        var roleValues = roleParameter.GetProperty("schema").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        CollectionAssert.Contains(roleValues, ServerJobRole.ExecutionChild.ToWireString());
    }

    [TestMethod]
    [DataRow(nameof(UserBrowseDto))]
    [DataRow(nameof(UserProfileDto))]
    [DataRow(nameof(UserProfileSectionDto))]
    public void WireSchema_AcceptsResourcesWithOmittedUnknownFields(string resourceType)
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "openapi.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        JsonElement schema = resourceType == nameof(UserBrowseDto)
            ? schemas.GetProperty(nameof(StateDeltaDto)).GetProperty("properties").GetProperty("userBrowse")
            : schemas.GetProperty(resourceType);
        while (true)
        {
            if (schema.TryGetProperty("$ref", out var reference))
                schema = schemas.GetProperty(reference.GetString()!.Split('/')[^1]);
            else if (schema.TryGetProperty("anyOf", out var choices))
                schema = choices.EnumerateArray().Single(choice =>
                    !choice.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "null");
            else break;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        object resource;
        string[] unknownFields;
        string[] requiredFields;
        if (resourceType == nameof(UserBrowseDto))
        {
            resource = new UserBrowseDto(Guid.NewGuid(), "ExactPeer", UserBrowseState.Running,
                UserBrowsePhase.Receiving, 0, null, 0, 0, 0, now, now, null, null, 1);
            unknownFields = ["compressedBytesExpected", "expiresAt", "failure"];
            requiredFields = ["browseId", "username", "state", "revision"];
        }
        else if (resourceType == nameof(UserProfileDto))
        {
            var unavailable = new UserProfileSectionDto(ResourceSectionState.Unavailable, "offline");
            resource = new UserProfileDto("ExactPeer", false, false, UserProfilePresence.Unknown,
                unavailable, unavailable, unavailable, unavailable,
                null, null, null, null, null, null, null, null, null, now);
            unknownFields = ["description", "sharedFileCount", "sharedDirectoryCount", "averageUploadSpeed",
                "uploadCount", "uploadSlots", "queueLength", "hasFreeUploadSlot", "picture"];
            requiredFields = ["username", "uploadAccessBlocked", "privateMessagesBlocked", "presence", "observedAt"];
        }
        else
        {
            resource = new UserProfileSectionDto(ResourceSectionState.Available, null);
            unknownFields = ["reason"];
            requiredFields = ["state"];
        }
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(resource, SockseekApiJson.CreateSerializerOptions()));
        foreach (string field in unknownFields)
        {
            Assert.IsFalse(wire.RootElement.TryGetProperty(field, out _),
                "The configured wire serializer omits unknown nullable fields.");
            Assert.IsFalse(schema.GetProperty("required").EnumerateArray().Any(value => value.GetString() == field),
                $"The {resourceType} schema requires {field}, but a valid resource omits it.");
        }
        foreach (string field in requiredFields)
            Assert.IsTrue(schema.GetProperty("required").EnumerateArray().Any(value => value.GetString() == field),
                $"The non-null {resourceType}.{field} contract must remain required.");
    }

    [TestMethod]
    public void OpenApiDocument_ContainsCoreServerContractSchemas()
    {
        string documentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "docs", "openapi.json"));
        Assert.IsTrue(File.Exists(documentPath), $"Generated OpenAPI document was not found at {documentPath}.");
        using FileStream stream = File.OpenRead(documentPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        var json = document.RootElement.GetRawText();

        var version = document.RootElement
            .GetProperty("info")
            .GetProperty("version")
            .GetString();

        Assert.AreEqual(ExpectedOpenApiVersion(), version);

        StringAssert.Contains(json, nameof(JobSummaryDto));
        StringAssert.Contains(json, nameof(SubmitAlbumJobRequestDto));
        StringAssert.Contains(json, nameof(AlbumJobPayloadDto));
        StringAssert.Contains(json, nameof(RemoteFileJobPayloadDto));
        StringAssert.Contains(json, nameof(RemoteDirectoryJobPayloadDto));
        StringAssert.Contains(json, nameof(PeerFileTargetDto));
        StringAssert.Contains(json, nameof(DirectoryTransferPlanDto));
        Assert.IsFalse(json.Contains("FileCandidateDto", StringComparison.Ordinal));
        StringAssert.Contains(json, nameof(SearchViewFileDto));
        StringAssert.Contains(json, nameof(FileMetadataDto));
        StringAssert.Contains(json, nameof(WorkflowDetailDto));
        StringAssert.Contains(json, nameof(JobDetailDto));
        Assert.IsFalse(json.Contains("WorkflowTreeDto", StringComparison.Ordinal));
        StringAssert.Contains(json, nameof(StateSnapshotDto));
        StringAssert.Contains(json, nameof(StateUpdateBatchDto));
        StringAssert.Contains(json, nameof(LiveProtocolContractVersion));
        StringAssert.Contains(json, nameof(LiveClientInvocation));
        StringAssert.Contains(json, nameof(LiveServerMessage));
        StringAssert.Contains(json, nameof(StateDeltaDto));
        StringAssert.Contains(json, nameof(ActivityEventDto));
        StringAssert.Contains(json, nameof(JobDeltaDto));
        StringAssert.Contains(json, nameof(TransferDeltaDto));
        StringAssert.Contains(json, $"{nameof(ActivityPayloadDto)}{nameof(JobStatusActivityDto)}");
        StringAssert.Contains(json, $"{nameof(ActivityPayloadDto)}{nameof(DownloadAttemptFailedActivityDto)}");
        StringAssert.Contains(json, nameof(ApiErrorDto));
        StringAssert.Contains(json, nameof(SharingStateDto));
        StringAssert.Contains(json, nameof(UploadRuntimeStateDto));
        StringAssert.Contains(json, nameof(LiveTransferPageDto));
        StringAssert.Contains(json, nameof(TransferDetailDto));
        StringAssert.Contains(json, nameof(DashboardAnalyticsDto));
        StringAssert.Contains(json, nameof(ChatMessageDto));
        StringAssert.Contains(json, nameof(ConversationPageDto));
        StringAssert.Contains(json, nameof(ChatRoomDetailDto));
        StringAssert.Contains(json, nameof(NotificationPageDto));
        StringAssert.Contains(json, nameof(UserProfileDto));
        StringAssert.Contains(json, nameof(UserRestrictionsDto));
        StringAssert.Contains(json, nameof(SetUserRestrictionOverrideRequestDto));
        StringAssert.Contains(json, nameof(UserBrowseDto));
        StringAssert.Contains(json, nameof(BrowseDirectoryEntryDto));
        StringAssert.Contains(json, nameof(BrowseFileEntryDto));
        StringAssert.Contains(json, nameof(BrowseSearchPageDto));
        StringAssert.Contains(json, nameof(BrowseSearchEntryDto));
        Assert.IsTrue(document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(nameof(BrowseDirectoryEntryDto)).GetProperty("properties")
            .TryGetProperty("totalRecursiveFileCount", out _));
        Assert.IsTrue(document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(nameof(BrowseSearchEntryDto)).GetProperty("properties")
            .TryGetProperty("totalSubtreeFileCount", out _));
        StringAssert.Contains(json, nameof(StartUserShareDownloadsRequestDto));
        StringAssert.Contains(json, nameof(ResolveEffectiveSettingsRequestDto));
        StringAssert.Contains(json, nameof(ResolveEffectiveSettingsResponseDto));
        StringAssert.Contains(json, nameof(InputArtifactDto));
        StringAssert.Contains(json, nameof(JobPreviewSummaryDto));
        StringAssert.Contains(json, nameof(JobPreviewNodeDto));
        StringAssert.Contains(json, nameof(CommitJobPreviewResponseDto));
        StringAssert.Contains(json, nameof(SearchViewSummaryDto));
        StringAssert.Contains(json, nameof(SearchViewDirectoryPageDto));
        StringAssert.Contains(json, nameof(SearchViewDirectoryFilePageDto));
        Assert.IsTrue(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(nameof(SearchViewSummaryDto))
            .GetProperty("properties")
            .TryGetProperty("kind", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(nameof(CommitSearchViewSelectionRequestDto))
            .GetProperty("properties")
            .TryGetProperty("options", out _));
        StringAssert.Contains(json, "lifecycleState");
        StringAssert.Contains(json, "activityPhase");
        StringAssert.Contains(json, "terminalOutcome");
        StringAssert.Contains(json, "discriminator");
        StringAssert.Contains(json, "kind");
        StringAssert.Contains(json, "requestedMode");
        StringAssert.Contains(json, "exactTarget");
        StringAssert.Contains(json, ServerProtocol.JobKinds.RemoteFile);
        StringAssert.Contains(json, ServerProtocol.JobKinds.RemoteDirectory);

        JsonElement schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        JsonElement albumGroupsProperties = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith(nameof(AlbumGroupsJobPayloadDto), StringComparison.Ordinal))
            .Value.GetProperty("properties");
        foreach (string property in new[] { "completedAlbumCount", "succeededAlbumCount", "failedAlbumCount" })
            Assert.IsTrue(albumGroupsProperties.TryGetProperty(property, out _),
                $"Album-group payload schema is missing {property}.");
        foreach (var (payload, outcomeFields) in new[]
        {
            (nameof(AlbumGroupsJobPayloadDto), new[] { "completedAlbumCount", "succeededAlbumCount", "failedAlbumCount" }),
            (nameof(SongGroupsJobPayloadDto), new[] { "completedSongCount", "succeededSongCount", "failedSongCount" }),
            (nameof(JobListPayloadDto), new[] { "activeJobCount", "completedJobCount", "succeededJobCount", "failedJobCount" }),
        })
        {
            JsonElement schema = schemas.GetProperty($"{nameof(JobPayloadDto)}{payload}");
            JsonElement requiredFields = schema.GetProperty("required");
            foreach (string field in outcomeFields)
            {
                Assert.IsTrue(requiredFields.EnumerateArray().Any(entry => entry.GetString() == field),
                    $"{payload}.{field} must carry the current outcome count.");
                Assert.IsFalse(schema.GetProperty("properties").GetProperty(field)
                    .GetProperty("type").EnumerateArray().Any(entry => entry.GetString() == "null"),
                    $"{payload}.{field} must not allow null.");
            }
        }
        JsonElement stateDelta = schemas.GetProperty(nameof(StateDeltaDto));
        JsonElement targetProperties = schemas.GetProperty(nameof(PeerFileTargetDto))
            .GetProperty("properties");
        foreach (string property in new[] { "username", "filename", "size", "extension", "bitRate", "bitDepth", "sampleRate", "length", "attributes" })
            Assert.IsTrue(targetProperties.TryGetProperty(property, out _),
                $"Remote-file target schema is missing {property}.");
        JsonElement planProperties = schemas.GetProperty(nameof(DirectoryTransferPlanDto))
            .GetProperty("properties");
        Assert.IsTrue(planProperties.TryGetProperty("displayRoot", out _));
        Assert.IsTrue(planProperties.TryGetProperty("entries", out _));
        Assert.IsTrue(planProperties.TryGetProperty("totalKnownBytes", out _));
        JsonElement remoteTarget = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith(nameof(RemoteFileJobPayloadDto), StringComparison.Ordinal))
            .Value.GetProperty("properties").GetProperty("target");
        JsonElement remoteAttributes = remoteTarget.GetProperty("properties")
            .GetProperty("attributes").GetProperty("anyOf")[0];
        Assert.AreEqual("array", remoteAttributes.GetProperty("type").GetString());
        StringAssert.EndsWith(remoteAttributes.GetProperty("items").GetProperty("$ref").GetString()!,
            "/" + nameof(FileAttributeDto));
        Assert.IsFalse(stateDelta.GetProperty("required").EnumerateArray().Any(value =>
            value.GetString() == "daemon"));
        Assert.IsTrue(stateDelta.GetProperty("properties")
            .GetProperty("daemon")
            .GetProperty("type")
            .EnumerateArray()
            .Any(value => value.GetString() == "null"));
        foreach (string enumSchema in new[]
        {
            nameof(ServerSearchViewProjectionKind),
            nameof(SearchViewRetentionState),
            nameof(SearchViewDirectoryVisibility),
            nameof(SearchViewDirectoryRetrievalState),
            nameof(ServerSearchResultVisibility),
            nameof(ServerSearchPreferenceTier),
            nameof(ServerSearchPreferenceCondition),
        })
        {
            JsonElement values = schemas.GetProperty(enumSchema).GetProperty("enum");
            Assert.IsTrue(values.GetArrayLength() > 0);
            Assert.IsTrue(
                values.EnumerateArray().All(value =>
                    value.ValueKind is JsonValueKind.String or JsonValueKind.Null),
                $"{enumSchema} must remain a readable public string enum.");
        }
        JsonElement workflowProperties = schemas
            .GetProperty(nameof(WorkflowSummaryDto))
            .GetProperty("properties");
        Assert.IsTrue(workflowProperties.TryGetProperty("rootJobCount", out _));
        Assert.IsFalse(workflowProperties.TryGetProperty("rootJobIds", out _));
        JsonElement jobDetailProperties = schemas
            .GetProperty(nameof(JobDetailDto))
            .GetProperty("properties");
        Assert.IsTrue(jobDetailProperties.TryGetProperty("childCount", out _));
        Assert.IsFalse(jobDetailProperties.TryGetProperty("children", out _));
        JsonElement albumPayloadProperties = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith(nameof(AlbumJobPayloadDto), StringComparison.Ordinal))
            .Value
            .GetProperty("properties");
        Assert.IsFalse(albumPayloadProperties.TryGetProperty("tracks", out _));
        Assert.IsFalse(albumPayloadProperties.TryGetProperty("results", out _));
        JsonElement directoryPayloadProperties = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith(nameof(RemoteDirectoryJobPayloadDto), StringComparison.Ordinal))
            .Value
            .GetProperty("properties");
        Assert.IsFalse(directoryPayloadProperties.TryGetProperty("activePlan", out _));
        Assert.IsFalse(directoryPayloadProperties.TryGetProperty("resolvedPlanSource", out _));
        JsonElement extractPayloadProperties = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith(nameof(ExtractJobPayloadDto), StringComparison.Ordinal))
            .Value
            .GetProperty("properties");
        Assert.IsTrue(extractPayloadProperties.TryGetProperty("resultJobId", out _));
        Assert.IsFalse(extractPayloadProperties.TryGetProperty("autoProcessResult", out _));
        Assert.IsFalse(extractPayloadProperties.TryGetProperty("resultDraft", out _));
        JsonElement submitExtractProperties = schemas
            .GetProperty(nameof(SubmitExtractJobRequestDto))
            .GetProperty("properties");
        Assert.IsFalse(submitExtractProperties.TryGetProperty("autoStartExtractedResult", out _));
        JsonElement extractDraftProperties = schemas.EnumerateObject()
            .Single(schema => schema.Name.EndsWith(nameof(ExtractJobDraftDto), StringComparison.Ordinal))
            .Value
            .GetProperty("properties");
        Assert.IsFalse(extractDraftProperties.TryGetProperty("autoStartExtractedResult", out _));
        JsonElement transferDetailProperties = schemas
            .GetProperty(nameof(TransferDetailDto))
            .GetProperty("properties");
        Assert.IsTrue(transferDetailProperties.TryGetProperty("transfer", out _));
        Assert.IsTrue(transferDetailProperties.TryGetProperty("latestAttempt", out _));
        Assert.IsFalse(transferDetailProperties.TryGetProperty("attempts", out _));
        Assert.IsFalse(json.Contains("attemptLimit", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("includeFiles", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("includeCandidates", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("includeFolders", StringComparison.Ordinal));

        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/daemon/snapshot", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/workflows/{workflowId}/snapshot", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/jobs/cancel-all", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/jobs/effective-settings", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/input-artifacts", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/job-previews", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/job-previews/{previewId}/nodes", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/job-previews/{previewId}/commit", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/jobs/{jobId}/search-views", out _));
        Assert.IsFalse(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/jobs/{jobId}/search-views/files", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/search-views/{viewId}/directories", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/search-views/{viewId}/directories/retrieve", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("RetrieveSearchViewDirectoryRequestDto")
            .GetProperty("required")
            .EnumerateArray()
            .Any(field => field.GetString() == "idempotencyKey"));
        foreach (string supersededPath in new[]
        {
            "/api/jobs/{jobId}/results/files",
            "/api/jobs/{jobId}/results/files/project",
            "/api/jobs/{jobId}/results/folders",
            "/api/jobs/{jobId}/results/folders/project",
            "/api/jobs/{jobId}/results/track-groups",
            "/api/jobs/{jobId}/results/track-groups/project",
            "/api/jobs/{jobId}/results/album-groups",
            "/api/jobs/{jobId}/results/album-groups/project",
        })
        {
            Assert.IsFalse(document.RootElement
                .GetProperty("paths")
                .TryGetProperty(supersededPath, out _), supersededPath);
        }
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty(
                "/api/search-views/{viewId}/directories/{directoryRef}/files",
                out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/sharing", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/sharing/scans", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/transfers/live", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/transfers/{transferId}/cancel", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/transfers/cancel", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/transfers/{transferId}", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/transfers/remove", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/dashboard/analytics", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/chat/conversations", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/chat/private-messages", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/chat/rooms/{roomId}/members", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/notifications/read", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/users/{username}/profile", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/users/{username}/restrictions", out _));
        Assert.IsFalse(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/users/{username}/access", out _));
        Assert.IsTrue(schemas
            .GetProperty(nameof(UserProfileDto))
            .GetProperty("properties")
            .TryGetProperty("uploadAccessBlocked", out _));
        Assert.IsTrue(schemas
            .GetProperty(nameof(UserProfileDto))
            .GetProperty("properties")
            .TryGetProperty("privateMessagesBlocked", out _));
        Assert.IsTrue(schemas
            .GetProperty(nameof(ConversationSummaryDto))
            .GetProperty("properties")
            .TryGetProperty("privateMessagesBlocked", out _));
        JsonElement refreshParameter = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/users/{username}/profile")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "refresh");
        Assert.IsTrue(
            !refreshParameter.TryGetProperty("required", out JsonElement required)
            || !required.GetBoolean());
        Assert.IsFalse(refreshParameter
            .GetProperty("schema")
            .GetProperty("default")
            .GetBoolean());
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/users/{username}/picture", out _));
        string[] pictureContentTypes = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/users/{username}/picture")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .EnumerateObject()
            .Select(content => content.Name)
            .ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "image/jpeg", "image/png", "image/gif", "image/webp" },
            pictureContentTypes);
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/users/{username}/browses", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/user-browses/{browseId}/directories", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/user-browses/{browseId}/search", out _));
        Assert.IsTrue(document.RootElement
            .GetProperty("paths")
            .TryGetProperty("/api/user-browses/{browseId}/downloads", out _));
        foreach (JsonProperty path in document.RootElement.GetProperty("paths").EnumerateObject()
                     .Where(item => item.Name.StartsWith("/api/chat", StringComparison.Ordinal)
                                    || item.Name.StartsWith("/api/notifications", StringComparison.Ordinal)))
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject()
                         .Where(item => item.Name is "get" or "post" or "delete" or "put" or "patch"))
            {
                string[] responses = operation.Value.GetProperty("responses")
                    .EnumerateObject().Select(item => item.Name).ToArray();
                Assert.IsTrue(
                    responses.Any(code => code.StartsWith('2')),
                    $"{operation.Name.ToUpperInvariant()} {path.Name} has no documented success response.");
                foreach (string code in new[] { "400", "403", "404", "409", "429", "503" })
                    CollectionAssert.Contains(responses, code, $"{operation.Name.ToUpperInvariant()} {path.Name}");
            }
        }
        Assert.IsFalse(json.Contains("ServerEventEnvelopeDto", StringComparison.Ordinal));

        var jobListParameterNames = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/jobs")
            .GetProperty("get")
            .GetProperty("parameters")
            .EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString())
            .ToList();
        CollectionAssert.Contains(jobListParameterNames, "lifecycleState");
        CollectionAssert.Contains(jobListParameterNames, "terminalOutcome");
        Assert.IsFalse(jobListParameterNames.Contains("state"), "/api/jobs should not expose the old flattened state filter.");
    }

    private static string ExpectedOpenApiVersion()
    {
        var assemblyVersion = typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ServerHost).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var metadataIndex = assemblyVersion.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex >= 0 ? assemblyVersion[..metadataIndex] : assemblyVersion;
    }

    [TestMethod]
    public void ApiAssembly_DoesNotDependOnCore()
    {
        Assembly apiAssembly = typeof(SockseekApiClient).Assembly;
        string coreAssemblyName = typeof(DownloadSettings).Assembly.GetName().Name!;

        Assert.IsFalse(
            apiAssembly.GetReferencedAssemblies().Any(reference =>
                string.Equals(reference.Name, coreAssemblyName, StringComparison.Ordinal)),
            "The public API contracts and remote client must not reference Sockseek.Core.");
    }

    [TestMethod]
    public void ApiOwnedSettingsEnums_PreserveExistingWireRepresentationsAndPrintFlags()
    {
        ServerPrintOption combinedPrint = ServerPrintOption.Jobs | ServerPrintOption.Full;
        var settings = new DownloadSettingsPatchDto(
            Output: new OutputSettingsPatchDto(AlbumArtOption: ServerAlbumArtOption.Largest),
            Skip: new SkipSettingsPatchDto(SkipMode: ServerSkipMode.Index),
            Extraction: new ExtractionSettingsPatchDto(
                InputType: ServerInputType.Soulseek,
                RequestedMode: ServerExtractionMode.General),
            PrintOption: combinedPrint);
        var behavior = new DownloadBehaviorPolicyDto(Album: ServerDownloadBehavior.Manual);
        using JsonDocument settingsJson = JsonDocument.Parse(JsonSerializer.Serialize(
            settings,
            SockseekApiJsonContext.Default.DownloadSettingsPatchDto));
        using JsonDocument behaviorJson = JsonDocument.Parse(JsonSerializer.Serialize(
            behavior,
            SockseekApiJsonContext.Default.DownloadBehaviorPolicyDto));

        Assert.AreEqual(JsonValueKind.Number, settingsJson.RootElement.GetProperty("printOption").ValueKind);
        Assert.AreEqual((int)combinedPrint, settingsJson.RootElement.GetProperty("printOption").GetInt32());
        Assert.AreEqual(JsonValueKind.Number, settingsJson.RootElement.GetProperty("output").GetProperty("albumArtOption").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, settingsJson.RootElement.GetProperty("skip").GetProperty("skipMode").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, settingsJson.RootElement.GetProperty("extraction").GetProperty("inputType").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, settingsJson.RootElement.GetProperty("extraction").GetProperty("requestedMode").ValueKind);
        Assert.AreEqual("Manual", behaviorJson.RootElement.GetProperty("album").GetString());
        Assert.AreEqual(combinedPrint, (PrintOption.Jobs | PrintOption.Full).ToServer());
        Assert.AreEqual(PrintOption.Jobs | PrintOption.Full, combinedPrint.ToCore());
    }

    [TestMethod]
    public void SockseekApiJsonContext_CoversApiDtoContracts()
    {
        var dtoTypes = typeof(SockseekApiJsonContext).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Namespace == typeof(SockseekApiJsonContext).Namespace
                && type.Name.EndsWith("Dto", StringComparison.Ordinal)
                && !type.ContainsGenericParameters)
            .OrderBy(type => type.FullName)
            .ToList();

        var closedGenericDtoTypes = new[]
        {
            typeof(CollectionPatchDto<string>),
            typeof(CollectionPatchDto<RegexRuleDto>),
        };

        var missing = dtoTypes
            .Concat(closedGenericDtoTypes)
            .Distinct()
            .Where(type => !HasApiJsonTypeInfo(type))
            .Select(type => type.FullName)
            .ToList();

        Assert.AreEqual(
            0,
            missing.Count,
            "Missing SockseekApiJsonContext metadata for:" + "\n" + string.Join("\n", missing));
    }

    [TestMethod]
    public void LiveBatch_JsonRoundTrip_PreservesTypedDeltaAndPolymorphicActivity()
    {
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var summary = new JobSummaryDto(
            jobId,
            4,
            workflowId,
            ServerJobKind.Search,
            ServerJobLifecycleState.Running,
            ServerJobActivityPhase.Searching,
            null,
            ServerJobTerminalOutcome.None,
            ServerJobSkipReason.None,
            "item",
            "query",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            []);
        var batch = new StateUpdateBatchDto(
            StateStreamScopeDto.Workflow(workflowId),
            epoch,
            7,
            8,
            DateTimeOffset.UtcNow,
            StateDeltaDto.Empty with
            {
                Jobs =
                [
                    new JobDeltaDto(
                        jobId,
                        1,
                        Added: JobStateDto.FromSummary(summary, 1)),
                ],
            },
            [
                new ActivityEventDto(
                    8,
                    DateTimeOffset.UtcNow,
                    "job.message",
                    workflowId,
                    jobId,
                    null,
                    new JobMessageActivityDto(4, "Information", "test", "hello")),
                new ActivityEventDto(
                    9,
                    DateTimeOffset.UtcNow,
                    "workflow.message",
                    workflowId,
                    null,
                    null,
                    new WorkflowMessageActivityDto("Information", "test", "monitor attached")),
            ]);
        ActivityPayloadDto[] otherActivities =
        [
            new JobStatusActivityDto(4, "Running"),
            new DiagnosticActivityDto(4, "job", "failed", "IOException", "test detail", null),
            new ExtractionStartedActivityDto(4, "input", null, "extractor"),
            new DownloadAttemptFailedActivityDto(4, "Peer", @"Root\File.flac", "/output/file.flac",
                1, 3, "IOException", "retry", "test detail"),
            new TrackBatchResolvedActivityDto(4, true, ServerPrintOption.Full, 1, 2, 3),
        ];
        batch = batch with { Activity = [.. batch.Activity, .. otherActivities.Select((payload, index) =>
            new ActivityEventDto(10 + index, DateTimeOffset.UtcNow, "test", workflowId, jobId, null, payload))] };
        var options = SockseekApiJson.CreateSerializerOptions();

        string json = JsonSerializer.Serialize(batch, options);
        var roundTripped = JsonSerializer.Deserialize<StateUpdateBatchDto>(json, options);

        Assert.IsNotNull(roundTripped);
        Assert.AreEqual(batch.Scope, roundTripped.Scope);
        Assert.AreEqual(epoch, roundTripped.Epoch);
        Assert.AreEqual(jobId, roundTripped.State.Jobs.Single().Added?.JobId);
        Assert.IsInstanceOfType<JobMessageActivityDto>(roundTripped.Activity[0].Payload);
        var jobMessage = (JobMessageActivityDto)roundTripped.Activity[0].Payload;
        Assert.AreEqual("hello", jobMessage.Message);
        Assert.IsInstanceOfType<WorkflowMessageActivityDto>(
            roundTripped.Activity[1].Payload);
        var workflowMessage = (WorkflowMessageActivityDto)roundTripped.Activity[1].Payload;
        Assert.AreEqual("monitor attached", workflowMessage.Message);
        StringAssert.Contains(json, "\"kind\":\"jobMessage\"");
        StringAssert.Contains(json, "\"kind\":\"workflowMessage\"");

        CollectionAssert.AreEqual(batch.Activity.Select(item => item.Payload.GetType()).ToArray(),
            roundTripped.Activity.Select(item => item.Payload.GetType()).ToArray());
    }

    [TestMethod]
    public async Task ServerInfo_AdvertisesEnforcedLiveProtocolVersion()
    {
        string url = "http://127.0.0.1:0";
        await using var app = TestServerHost.Build([], new ServerOptions
        {
            Engine = new EngineSettings(),
            DefaultDownload = new DownloadSettings(),
            Profiles = ProfileCatalog.Empty,
        }, url);

        await app.StartAsync();
        url = app.Urls.Single();
        try
        {
            using var http = await global::Tests.TestServerHost.CreateHttpClientAsync(app);
            var info = await new SockseekApiClient(http).GetServerInfoAsync();

            Assert.AreEqual(LiveProtocol.Version, info.LiveProtocolVersion);
            using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(url),
            };
            using var root = await noRedirect.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.Redirect, root.StatusCode);
            Assert.AreEqual("/api/server/info", root.Headers.Location?.OriginalString);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static bool HasApiJsonTypeInfo(Type type)
    {
        try
        {
            return SockseekApiJsonContext.Default.GetTypeInfo(type) != null;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }


}
