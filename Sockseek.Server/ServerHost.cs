using System.Reflection;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Sockseek.Api;
using Sockseek.Core.Chat;
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Services;
using Sockseek.Core.Settings;
using Sockseek.Server.Persistence;
using Sockseek.Server.PeerBrowsing;
using Sockseek.Server.UserProfiles;
using Sockseek.Server.Planning;
using Sockseek.Server.PeerRestrictions;

namespace Sockseek.Server;

public static class ServerHost
{
    public static WebApplication Build(string[] args, ServerOptions? options = null, string? url = null, bool logToConsole = true)
    {
        var builder = WebApplication.CreateBuilder(args);
        string entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "";
        bool isOpenApiGeneration = entryAssemblyName.Contains(
            "getdocument",
            StringComparison.OrdinalIgnoreCase);
        EngineSettings loggingSettings;
        if (options is not null)
        {
            loggingSettings = options.Engine;
        }
        else
        {
            loggingSettings = new EngineSettings { LogLevel = LogLevel.Debug };
            builder.Configuration.GetSection("SockseekServer:Engine").Bind(loggingSettings);
        }
        builder.Logging.ClearProviders();
        // Keep the factory permissive so each provider can enforce its own
        // level. In particular, the Debug file must remain independent of the
        // configured console level.
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(new CompactTextLoggerProvider(
            record => Console.WriteLine(CompactLogFormatter.Format(
                record,
                includeTimestamp: true,
                includeInformationLevel: true,
                includeSource: loggingSettings.LogLevel <= LogLevel.Debug)),
            isOpenApiGeneration || !logToConsole ? LogLevel.None : loggingSettings.LogLevel));
        if (!isOpenApiGeneration && !string.IsNullOrWhiteSpace(loggingSettings.LogFilePath))
            builder.Logging.AddProvider(new CompactFileLoggerProvider(
                loggingSettings.LogFilePath,
                LogLevel.Debug));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        if (!string.IsNullOrWhiteSpace(url))
            builder.WebHost.UseUrls(url);

        if (options != null)
            builder.Services.AddSingleton<IOptions<ServerOptions>>(Options.Create(options));
        else
        {
            builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("SockseekServer"));
            if (!isOpenApiGeneration && builder.Configuration["SockseekServer:Persistence:Enabled"] == null)
                builder.Services.PostConfigure<ServerOptions>(configured => configured.Persistence.Enabled = true);
        }

        builder.Services.AddOptions<HostOptions>()
            .Configure<IOptions<ServerOptions>>((host, server) =>
            {
                if (server.Value.ShutdownTimeout <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(
                        nameof(ServerOptions.ShutdownTimeout));
                host.ShutdownTimeout = server.Value.ShutdownTimeout;
            });

        builder.Services.Configure<JsonOptions>(jsonOptions =>
        {
            jsonOptions.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            SockseekApiJson.ConfigureSerializerOptions(jsonOptions.SerializerOptions);
        });
        // Search View pages can contain many repeated directory/file fields.
        // Compress public JSON on the wire without changing the paged contract.
        builder.Services.AddResponseCompression(compression =>
        {
            compression.EnableForHttps = true;
            compression.MimeTypes = ["application/json"];
            compression.Providers.Add<BrotliCompressionProvider>();
            compression.Providers.Add<GzipCompressionProvider>();
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
            options.Level = CompressionLevel.Fastest);
        builder.Services.Configure<GzipCompressionProviderOptions>(options =>
            options.Level = CompressionLevel.Fastest);

        builder.Services.AddSignalR(options =>
            // A legitimate fetched extent can contain thousands of row IDs
            // and revisions. Response work is scoped by the requested extent.
            options.MaximumReceiveMessageSize = null)
            .AddJsonProtocol();
        // Both transports use the public HTTP JSON rules and one metadata cache.
        builder.Services.AddOptions<JsonHubProtocolOptions>()
            .Configure<IOptions<JsonOptions>>((hub, http) =>
                hub.PayloadSerializerOptions = http.Value.SerializerOptions);
        builder.Services.AddOpenApi(options =>
        {
            options.AddOperationTransformer(async (operation, context, cancellationToken) =>
            {
                if (context.Description.RelativePath == "api/jobs"
                    && context.Description.HttpMethod == "GET")
                {
                    // Minimal API string binding permits public hyphenated enum
                    // spellings; keep the generated query contract enum-typed.
                    (string Name, Type Type)[] filters =
                    [
                        ("kind", typeof(ServerJobKind)),
                        ("role", typeof(ServerJobRole)),
                    ];
                    foreach (var (name, type) in filters)
                    {
                        if (operation.Parameters?.FirstOrDefault(parameter => parameter.Name == name) is OpenApiParameter parameter)
                            parameter.Schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken);
                    }
                    if (operation.Responses?.TryGetValue("200", out var response) == true
                        && response is OpenApiResponse jobResponse)
                    {
                        jobResponse.Headers ??= new Dictionary<string, IOpenApiHeader>();
                        jobResponse.Headers[ServerProtocol.ReadEpochHeader] = new OpenApiHeader
                        {
                            Description = "Daemon epoch captured before reading live and retained jobs.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                        };
                        jobResponse.Headers[ServerProtocol.ReadSequenceHeader] = new OpenApiHeader
                        {
                            Description = "Daemon sequence at that read position, encoded as a decimal Int64 string.",
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-9]+$" },
                        };
                    }
                }
            });
            // With this generator version, reference extraction collapses these
            // nested target/plan schemas to empty components despite populated
            // serializer metadata. Inline their uses and emit explicit canonical
            // components below; both still derive from the DTOs, not hand-written
            // property lists. OpenApiContractTests guards the emitted properties.
            options.CreateSchemaReferenceId = type =>
                type.Type == typeof(PeerFileTargetDto) || type.Type == typeof(DirectoryTransferPlanDto)
                    ? null
                    : Microsoft.AspNetCore.OpenApi.OpenApiOptions.CreateDefaultSchemaReferenceId(type);
            options.AddSchemaTransformer(async (schema, context, cancellationToken) =>
            {
                // Constructor parameters are not necessarily present on the wire:
                // the shared serializer omits nulls. Derive optionality from the
                // same metadata instead of maintaining per-DTO field lists.
                foreach (var property in context.JsonTypeInfo.Properties)
                {
                    JsonIgnoreCondition condition = property.AttributeProvider?
                        .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
                        .OfType<JsonIgnoreAttribute>().FirstOrDefault()?.Condition
                        ?? context.JsonTypeInfo.Options.DefaultIgnoreCondition;
                    if (!property.IsRequired
                        && condition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault
                        && (Nullable.GetUnderlyingType(property.PropertyType) is not null || property.IsGetNullable))
                        schema.Required?.Remove(property.Name);
                }
                if (context.JsonTypeInfo.Type == typeof(PeerFileTargetDto)
                    && schema.Properties is { } targetProperties)
                {
                    var attributes = await context.GetOrCreateSchemaAsync(
                        typeof(IReadOnlyList<FileAttributeDto>), null, cancellationToken);
                    targetProperties["attributes"] = new OpenApiSchema
                    {
                        AnyOf = [attributes, new OpenApiSchema { Type = JsonSchemaType.Null }],
                    };
                }
                if ((context.JsonTypeInfo.Type == typeof(TransferStatusFieldsDto)
                     || context.JsonTypeInfo.Type == typeof(JobLifecycleFieldsDto))
                    && schema.Properties is { } statusProperties)
                {
                    // Repeated inline delta generation otherwise loses action
                    // items on some transports. Derive them from the same DTO.
                    var actions = await context.GetOrCreateSchemaAsync(
                        typeof(IReadOnlyList<ResourceActionDto>), null, cancellationToken);
                    statusProperties["availableActions"] = context.JsonTypeInfo.Type == typeof(JobLifecycleFieldsDto)
                        ? actions : new OpenApiSchema
                        {
                            AnyOf = [actions, new OpenApiSchema { Type = JsonSchemaType.Null }],
                        };
                }
                if (context.JsonTypeInfo.Type == typeof(StateDeltaDto))
                {
                    schema.Required?.Remove("daemon");
                    if (schema.Properties?.TryGetValue("daemon", out IOpenApiSchema? daemon) == true
                        && daemon is OpenApiSchema daemonSchema)
                    {
                        daemonSchema.Type = (daemonSchema.Type ?? JsonSchemaType.Object)
                            | JsonSchemaType.Null;
                    }
                    // Reuse the canonical resource schema: generating it inline
                    // here loses nested nullable/omitted failure metadata.
                    schema.Required?.Remove("userBrowse");
                    if (schema.Properties is { } properties)
                    {
                        properties["userBrowse"] = new OpenApiSchema
                        {
                            AnyOf = [new OpenApiSchemaReference(nameof(UserBrowseDto)),
                                new OpenApiSchema { Type = JsonSchemaType.Null }],
                        };
                    }
                }
                else if (context.JsonTypeInfo.Type == typeof(LiveProtocolContractVersion))
                {
                    schema.Enum = [JsonValue.Create(LiveProtocol.Version)];
                }
                else if (context.JsonTypeInfo.Type == typeof(ChatLiveMessageTailSize))
                {
                    schema.Enum = [JsonValue.Create(ChatProtocol.LiveMessageTailSize)];
                }
            });
            options.AddDocumentTransformer(async (document, context, cancellationToken) =>
            {
                document.Info = new()
                {
                    Title = "Sockseek daemon API",
                    Version = GetOpenApiVersion(),
                    Description = "HTTP API for the Sockseek daemon."
                };

                Type[] supplementalContractTypes =
                [
                    typeof(PeerFileTargetDto),
                    typeof(DirectoryTransferPlanDto),
                    typeof(LiveProtocolContractVersion),
                    typeof(ChatLiveMessageTailSize),
                    typeof(LiveClientInvocation),
                    typeof(LiveServerMessage),
                    typeof(StateUpdateBatchDto),
                    typeof(StateDeltaDto),
                    typeof(UserBrowseDto),
                    typeof(JobDeltaDto),
                    typeof(TransferDeltaDto),
                    typeof(TransferTimelineUpdateDto),
                    typeof(TransferTimelineScopeDto),
                    typeof(JobObservationUpdateDto),
                    typeof(ChatTargetDeltaDto),
                    typeof(ActivityEventDto),
                    typeof(ActivityPayloadDto),
                    typeof(JobStatusActivityDto),
                    typeof(JobMessageActivityDto),
                    typeof(WorkflowMessageActivityDto),
                    typeof(DiagnosticActivityDto),
                    typeof(ExtractionStartedActivityDto),
                    typeof(DownloadAttemptFailedActivityDto),
                    typeof(TrackBatchResolvedActivityDto),
                ];
                foreach (Type contractType in supplementalContractTypes)
                {
                    var schema = await context.GetOrCreateSchemaAsync(
                        contractType,
                        parameterDescription: null,
                        cancellationToken);
                    document.AddComponent(contractType.Name, schema);
                }

                // The .NET OpenAPI generator names polymorphic derived schemas in
                // discriminator mappings as <base><derived>. These SignalR-only
                // types are not otherwise reachable from HTTP endpoints, so add
                // the mapped component names explicitly as well.
                Type[] activityPayloadTypes =
                [
                    typeof(JobStatusActivityDto),
                    typeof(JobMessageActivityDto),
                    typeof(WorkflowMessageActivityDto),
                    typeof(DiagnosticActivityDto),
                    typeof(ExtractionStartedActivityDto),
                    typeof(DownloadAttemptFailedActivityDto),
                    typeof(TrackBatchResolvedActivityDto),
                ];
                foreach (Type payloadType in activityPayloadTypes)
                {
                    var schema = await context.GetOrCreateSchemaAsync(
                        payloadType,
                        parameterDescription: null,
                        cancellationToken);
                    document.AddComponent(
                        $"{nameof(ActivityPayloadDto)}{payloadType.Name}",
                        schema);
                }
            });
        });
        builder.Services.AddSingleton<PersistenceCoordinator>();
        builder.Services.AddHostedService<PersistenceRuntimeHostedService>();
        builder.Services.AddHostedService<PersistenceMaintenanceHostedService>();
        builder.Services.AddSingleton<InputArtifactCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InputArtifactCoordinator>());
        builder.Services.AddSingleton<PeerRestrictionCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PeerRestrictionCoordinator>());
        builder.Services.AddSingleton<EngineSupervisor>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().StateStore);
        builder.Services.AddSingleton<HistoricalQueryFacade>();
        builder.Services.AddSingleton<DashboardAnalyticsFacade>();
        builder.Services.AddSingleton<LiveTransferCursorCodec>();
        builder.Services.AddSingleton<PeerBrowseCursorCodec>();
        builder.Services.AddSingleton<UserShareSubmissionStore>();
        builder.Services.AddSingleton<JobPreviewCursorCodec>();
        builder.Services.AddSingleton<SubmissionCommitCoordinator>();
        builder.Services.AddSingleton<JobPreviewCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobPreviewCoordinator>());
        builder.Services.AddSingleton<SearchViewCursorCodec>();
        builder.Services.AddSingleton<SearchViewCoordinator>();
        builder.Services.AddSingleton<IOperatorMutationAuthorizer,
            CurrentTrustDomainOperatorAuthorizer>();
        builder.Services.AddSingleton<ServerEventBroadcaster>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerEventBroadcaster>());
        builder.Services.AddHostedService<EngineRuntimeHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchViewCoordinator>());

        var app = builder.Build();

        // Resolve the live contract before accepting requests. Lazy configuration
        // of these broad graphs otherwise blocks unrelated first responses on
        // System.Text.Json's shared metadata lock. This is startup work, not a
        // serialization shortcut: retain full metadata and normal async streaming.
        var wireJson = app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        wireJson.MakeReadOnly();
        foreach (Type response in new[] { typeof(StateSnapshotDto), typeof(StateUpdateBatchDto), typeof(TransferTimelineUpdateDto) })
            wireJson.GetTypeInfo(response);

        app.UseResponseCompression();

        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (PersistenceHandoffException exception)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new ApiErrorDto(exception.Message, "HistoryUnavailable"),
                    context.RequestAborted);
            }
        });

        string webRootPath = app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        bool hasWebUi = File.Exists(Path.Combine(webRootPath, "index.html"));
        var webUiStaticFiles = new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                string path = context.Context.Request.Path.Value ?? string.Empty;
                context.Context.Response.Headers.CacheControl = path.StartsWith(
                    "/assets/", StringComparison.Ordinal)
                    ? "public,max-age=31536000,immutable"
                    : "no-cache";
            },
        };
        if (hasWebUi)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(webUiStaticFiles);
        }

        app.MapOpenApi("/api/openapi.json");
        MapEndpoints(app);
        if (!hasWebUi)
            app.MapGet("/", () => Results.Redirect("/api/server/info"))
                .ExcludeFromDescription();
        app.Map("/api/{**unmatched}", () => Results.NotFound());
        if (hasWebUi)
            app.MapFallbackToFile("index.html", webUiStaticFiles);
        return app;
    }

    private static string GetOpenApiVersion()
    {
        var assembly = typeof(ServerHost).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex >= 0 ? version[..metadataIndex] : version;
    }

    private static void MapEndpoints(WebApplication app)
    {
        UserProfileEndpoints.Map(app);
        PeerRestrictionEndpoints.Map(app);
        UserBrowseEndpoints.Map(app);
        InputArtifactEndpoints.Map(app);
        JobPreviewEndpoints.Map(app);
        SearchViewEndpoints.Map(app);
        app.MapGet("/api/server/info", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetInfo()))
            .WithTags("Server")
            .WithSummary("Gets server identity and protocol information.")
            .Produces<ServerInfoDto>();
        app.MapGet("/api/server/status", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetStatus()))
            .WithTags("Server")
            .WithSummary("Gets current daemon and Soulseek client status.")
            .Produces<ServerStatusDto>();
        app.MapGet("/api/daemon/snapshot", (EngineStateStore stateStore, DaemonStateDetail? detail) =>
                Results.Ok(stateStore.GetDaemonSnapshot(detail ?? DaemonStateDetail.Full)))
            .WithTags("Live State")
            .WithSummary("Gets the bounded daemon replication snapshot and stream position.")
            .WithDescription("Contains active workflows and the jobs, searches, and transfers needed to render them. Retained terminal history remains paginated.")
            .Produces<StateSnapshotDto>();
        app.MapGet("/api/workflows/{workflowId:guid}/snapshot", (
            Guid workflowId,
            EngineStateStore stateStore) =>
                Results.Ok(stateStore.GetWorkflowSnapshot(workflowId)))
            .WithTags("Live State")
            .WithSummary("Gets one complete workflow replication snapshot and its workflow-local stream position.")
            .Produces<StateSnapshotDto>();
        app.MapGet("/api/chat/conversations/{conversationId:guid}/snapshot", async (
            Guid conversationId,
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                StateStreamScopeDto scope = StateStreamScopeDto.ChatConversation(conversationId);
                StateStreamPositionDto position = stateStore.GetChatPosition(scope);
                ChatTargetSnapshotDto? target = await chat.GetConversationSnapshotAsync(
                    conversationId, cancellationToken);
                return target is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(new StateSnapshotDto(
                        scope, position, DateTimeOffset.UtcNow, null, [], [], [], [], target));
            });
        })
            .RequireOperator()
            .WithTags("Live State")
            .WithSummary("Gets a bounded recoverable conversation snapshot.")
            .Produces<StateSnapshotDto>()
            .WithChatErrors();
        app.MapGet("/api/chat/rooms/{roomId:guid}/snapshot", async (
            Guid roomId,
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                StateStreamScopeDto scope = StateStreamScopeDto.ChatRoom(roomId);
                StateStreamPositionDto position = stateStore.GetChatPosition(scope);
                ChatTargetSnapshotDto? target = await chat.GetRoomSnapshotAsync(roomId, cancellationToken);
                return target is null
                    ? ChatNotFound("The room was not found.")
                    : Results.Ok(new StateSnapshotDto(
                        scope, position, DateTimeOffset.UtcNow, null, [], [], [], [], target));
            });
        })
            .RequireOperator()
            .WithTags("Live State")
            .WithSummary("Gets a bounded recoverable room snapshot.")
            .Produces<StateSnapshotDto>()
            .WithChatErrors();
        app.MapPost("/api/persistence/integrity", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.CheckIntegrityAsync(cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Runs SQLite integrity_check against the live database.")
            .Produces<PersistenceIntegrityResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/persistence/backup", async (
            PersistenceBackupRequestDto request,
            PersistenceCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.BackupAsync(request.BackupPath, cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Creates and independently verifies a WAL-safe online backup.")
            .Produces<PersistenceBackupResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/persistence/checkpoint", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.CheckpointAsync(cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Requests a bounded passive WAL checkpoint.")
            .Produces<PersistenceCheckpointResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/persistence/retention", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.RunRetentionAsync(cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Runs one bounded retention batch.")
            .Produces<PersistenceRetentionResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/chat", (EngineSupervisor supervisor) =>
        {
            ChatRuntime? chat = supervisor.Chat;
            return chat is null
                ? ChatUnavailable()
                : Results.Ok(chat.GetState());
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Gets compact chat runtime status.")
            .Produces<ChatRuntimeStateDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations", async (
            bool? unread,
            bool? archived,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                var page = await chat.GetConversationsAsync(
                    unread, archived, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new ConversationPageDto(
                    page.Items.Select(chat.MapConversation).ToArray(), page.NextCursor));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Pages private-message conversations.")
            .Produces<ConversationPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/private-messages", async (
            SendPrivateMessageRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendPrivateMessageAsync(
                    request.Username, request.MessageId, request.Text, cancellationToken)));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Sends an idempotent private message.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/by-peer", async (
            string username,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                var conversation = await chat.GetConversationByPeerAsync(username, cancellationToken);
                return conversation is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(chat.MapConversation(conversation));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Gets an existing conversation by exact peer username without creating one.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/{conversationId:guid}", async (
            Guid conversationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                var conversation = await chat.GetConversationAsync(conversationId, cancellationToken);
                return conversation is null
                    ? ChatNotFound("The conversation was not found.")
                    : Results.Ok(chat.MapConversation(conversation));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Gets a private-message conversation.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                if (await chat.GetConversationAsync(conversationId, cancellationToken) is null)
                    return ChatNotFound("The conversation was not found.");
                var page = await chat.GetMessagesAsync(
                    conversationId, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new ChatMessagePageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Pages messages in a private conversation.")
            .Produces<ChatMessagePageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/messages", async (
            Guid conversationId,
            SendChatMessageRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendConversationMessageAsync(
                    conversationId, request.MessageId, request.Text, cancellationToken)));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Sends an idempotent message to a conversation.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/read", async (
            Guid conversationId,
            MarkChatReadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.MarkConversationReadAsync(
                    conversationId, request.ThroughMessageId, cancellationToken);
                return Results.Ok(chat.MapConversation(
                    await chat.GetConversationAsync(conversationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The conversation was not found.")));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Advances a conversation's local read watermark.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/conversations/{conversationId:guid}/archive", async (
            Guid conversationId,
            ArchiveConversationRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.ArchiveConversationAsync(conversationId, request.Archived, cancellationToken);
                return Results.Ok(chat.MapConversation(
                    await chat.GetConversationAsync(conversationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The conversation was not found.")));
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Archives or reactivates a conversation.")
            .Produces<ConversationSummaryDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/conversations/{conversationId:guid}/history", async (
            Guid conversationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.DeleteConversationHistoryAsync(conversationId, cancellationToken);
                return Results.NoContent();
            });
        })
            .RequireOperator()
            .WithTags("Chat")
            .WithSummary("Permanently deletes conversation history.")
            .Produces(StatusCodes.Status204NoContent)
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/available", async (
            ServerChatRoomKind? kind,
            string? cursor,
            int? limit,
            bool? refresh,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(await chat.GetAvailableRoomsAsync(
                    kind, cursor, limit ?? ServerProtocol.DefaultPageSize, refresh == true, cancellationToken));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages rooms visible to the current Soulseek account.")
            .Produces<AvailableRoomPageDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms", async (
            string? state,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(await chat.GetRoomSummariesAsync(
                    state, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages known room subscriptions and runtime state.")
            .Produces<ChatRoomPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms", async (
            JoinRoomRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(await chat.JoinRoomAsync(
                    request.RoomName, request.Remember, cancellationToken));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Joins a room and optionally remembers it across restarts.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}", async (
            Guid roomId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                var room = await chat.GetRoomDetailAsync(roomId, cancellationToken);
                return room is null ? ChatNotFound("The room was not found.") : Results.Ok(room);
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Gets room subscription, join, and private-room metadata.")
            .Produces<ChatRoomDetailDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/rooms/{roomId:guid}", async (
            Guid roomId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(await chat.LeaveRoomAsync(roomId, cancellationToken));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Leaves a room and removes its runtime subscription.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                if (await chat.GetRoomSummaryAsync(roomId, cancellationToken) is null)
                    return ChatNotFound("The room was not found.");
                var page = await chat.GetMessagesAsync(
                    roomId, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new ChatMessagePageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages messages in a joined or retained room.")
            .Produces<ChatMessagePageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/messages", async (
            Guid roomId,
            SendChatMessageRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(ChatDtoMapper.ToDto(await chat.SendRoomMessageAsync(
                    roomId, request.MessageId, request.Text, cancellationToken)));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Sends an idempotent room message.")
            .Produces<ChatMessageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/read", async (
            Guid roomId,
            MarkChatReadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.MarkRoomReadAsync(roomId, request.ThroughMessageId, cancellationToken);
                return Results.Ok(await chat.GetRoomSummaryAsync(roomId, cancellationToken)
                                  ?? throw new KeyNotFoundException("The room was not found."));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Advances a room's local read watermark.")
            .Produces<ChatRoomSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/chat/rooms/{roomId:guid}/members", async (
            Guid roomId,
            string? cursor,
            int? limit,
            long? revision,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                return Results.Ok(await chat.GetRoomMembersAsync(
                    roomId, cursor, limit ?? ServerProtocol.DefaultPageSize, revision, cancellationToken));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Pages the current ephemeral room roster.")
            .Produces<RoomMemberPageDto>()
            .WithChatErrors();

        app.MapPost("/api/chat/rooms/{roomId:guid}/members", async (
            Guid roomId,
            AddRoomMemberRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.AddPrivateRoomMemberAsync(roomId, request.Username, cancellationToken);
                return Results.Ok(await chat.GetRoomDetailAsync(roomId, cancellationToken));
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Adds a member to a joined private room.")
            .Produces<ChatRoomDetailDto>()
            .WithChatErrors();

        app.MapDelete("/api/chat/rooms/{roomId:guid}/history", async (
            Guid roomId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.DeleteRoomHistoryAsync(roomId, cancellationToken);
                return Results.NoContent();
            });
        })
            .RequireOperator()
            .WithTags("Chat Rooms")
            .WithSummary("Permanently deletes retained room history.")
            .Produces(StatusCodes.Status204NoContent)
            .WithChatErrors();

        app.MapGet("/api/notifications", async (
            bool? unread,
            ServerUserNotificationKind? kind,
            string? cursor,
            int? limit,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                var page = await chat.GetNotificationsAsync(
                    unread, kind, cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return Results.Ok(new NotificationPageDto(
                    page.Items.Select(ChatDtoMapper.ToDto).ToArray(), page.NextCursor));
            });
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Pages durable chat notifications.")
            .Produces<NotificationPageDto>()
            .WithChatErrors();

        app.MapGet("/api/notifications/{notificationId:guid}", async (
            Guid notificationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                var notification = await chat.GetNotificationAsync(notificationId, cancellationToken);
                return notification is null
                    ? ChatNotFound("The notification was not found.")
                    : Results.Ok(ChatDtoMapper.ToDto(notification));
            });
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Gets a durable chat notification.")
            .Produces<UserNotificationDto>()
            .WithChatErrors();

        app.MapPost("/api/notifications/{notificationId:guid}/read", async (
            Guid notificationId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                if (await chat.GetNotificationAsync(notificationId, cancellationToken) is null)
                    return ChatNotFound("The notification was not found.");
                await chat.MarkNotificationsReadAsync(null, [notificationId], cancellationToken);
                return Results.Ok(ChatDtoMapper.ToDto(
                    await chat.GetNotificationAsync(notificationId, cancellationToken)
                    ?? throw new KeyNotFoundException("The notification was not found.")));
            });
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Marks one notification read.")
            .Produces<UserNotificationDto>()
            .WithChatErrors();

        app.MapPost("/api/notifications/read", async (
            MarkNotificationsReadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            return await WithChatAsync(supervisor, async chat =>
            {
                await chat.MarkNotificationsReadAsync(
                    request.ThroughSequence, request.Ids, cancellationToken);
                return Results.Ok(chat.GetNotificationSummary());
            });
        })
            .RequireOperator()
            .WithTags("Notifications")
            .WithSummary("Marks a bounded notification set or sequence range read.")
            .Produces<NotificationSummaryDto>()
            .WithChatErrors();

        app.MapGet("/api/profiles", (EngineSupervisor supervisor) => Results.Ok(supervisor.GetProfiles()))
            .WithTags("Profiles")
            .WithSummary("Lists configured download profiles.")
            .Produces<IReadOnlyList<ProfileSummaryDto>>();

        app.MapGet("/api/submissions", async (
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            bool? archived,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetSubmissionsAsync(
                    cursor,
                    limit ?? ServerProtocol.DefaultPageSize,
                    archived ?? false,
                    cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .RequireOperator()
            .WithTags("Submissions")
            .WithSummary("Lists durable accepted submissions newest first.")
            .WithDescription("Archived submissions are excluded by default. Traverse runtime jobs with /api/jobs?submissionId= rather than embedding them here.")
            .Produces<IReadOnlyList<SubmissionSummaryDto>>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/submissions/{submissionId:guid}", async (
            Guid submissionId,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await queryFacade.GetSubmissionAsync(submissionId, cancellationToken);
                return detail == null ? Results.NotFound() : Results.Ok(detail);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .RequireOperator()
            .WithTags("Submissions")
            .WithSummary("Gets safe durable submission metadata.")
            .Produces<SubmissionDetailDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/submissions/{submissionId:guid}/archive", async (
            Guid submissionId,
            SetSubmissionArchivedRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await supervisor.SetSubmissionArchivedAsync(
                    submissionId,
                    request.Archived,
                    cancellationToken));
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .RequireOperator()
            .WithTags("Submissions")
            .WithSummary("Archives or restores one terminal submission.")
            .WithDescription("Archive is reversible. A nonterminal submission is rejected without affecting unrelated submissions.")
            .Produces<SubmissionArchiveResponseDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/submissions/{submissionId:guid}/rerun", async (
            Guid submissionId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var submitted = await supervisor.RerunSubmissionAsync(
                    submissionId,
                    cancellationToken);
                return submitted == null
                    ? Results.NotFound()
                    : Results.Accepted($"/api/submissions/{submitted.SubmissionId}", submitted);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .RequireOperator()
            .WithTags("Submissions")
            .WithSummary("Reruns a submission from its retained normalized command and effective settings.")
            .WithDescription("Current credentials are rebound only for retained credential slots. Current defaults and profiles do not replace the retained root settings.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/jobs/{jobId:guid}/rerun-search", async (
            Guid jobId,
            EngineSupervisor supervisor,
            CancellationToken cancellationToken) =>
        {
            try
            {
                JobSummaryDto? submitted = await supervisor.RerunSearchJobAsync(
                    jobId, cancellationToken);
                return submitted == null
                    ? Results.NotFound()
                    : Results.Accepted($"/api/jobs/{submitted.JobId}", submitted);
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .RequireOperator()
            .WithTags("Job Submission")
            .WithSummary("Replays only one planned search job from its retained execution settings.")
            .WithDescription("Sibling jobs are not restarted. Older submissions without retained child identities cannot be replayed through this operation.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/jobs", async (
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            ServerJobLifecycleState? lifecycleState,
            ServerJobTerminalOutcome? terminalOutcome,
            ServerJobSkipReason? skipReason,
            string? kind,
            Guid? workflowId,
            Guid? parentJobId,
            Guid? submissionId,
            string? role,
            bool? archived,
            bool? includeAll,
            bool? newestFirst,
            bool? excludeExtractResults,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                ServerJobKind? parsedKind = null;
                if (kind != null)
                {
                    if (!ServerProtocolEnumExtensions.TryParseJobKind(kind, out var value))
                        throw new ArgumentException($"Unknown job kind '{kind}'.", nameof(kind));
                    parsedKind = value;
                }
                ServerJobRole? parsedRole = null;
                if (role != null)
                {
                    if (!ServerProtocolEnumExtensions.TryParseJobRole(role, out var value))
                        throw new ArgumentException($"Unknown job role '{role}'.", nameof(role));
                    parsedRole = value;
                }
                var page = await queryFacade.GetJobsAsync(
                    new JobQuery(
                        lifecycleState,
                        terminalOutcome,
                        parsedKind,
                        workflowId,
                        includeAll ?? false,
                        skipReason,
                        parentJobId,
                        submissionId,
                        parsedRole,
                        archived ?? false,
                        newestFirst ?? false,
                        excludeExtractResults ?? false),
                    cursor,
                    limit ?? ServerProtocol.DefaultNestedPageSize,
                    cancellationToken);
                httpContext.Response.Headers[ServerProtocol.ReadEpochHeader] = page.ReadPosition.Epoch.ToString("D");
                httpContext.Response.Headers[ServerProtocol.ReadSequenceHeader] =
                    page.ReadPosition.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Jobs")
            .WithSummary("Lists known jobs.")
            .WithDescription("Default results contain execution roots in ascending display order. Set newestFirst=true for newest-first moving-keyset pages; cursors are bound to that direction. Set parentJobId for direct children, submissionId or role for submission semantics, or includeAll=true for a flat workflow list. Set excludeExtractResults=true to page extraction sources without also counting their generated results.")
            .Produces<IReadOnlyList<JobSummaryDto>>();

        app.MapPost("/api/jobs/lookup", async (
            JobLookupRequestDto request, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await queryFacade.LookupJobsAsync(request, cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Jobs")
            .WithSummary("Reads current live or retained job summaries by ID.")
            .WithDescription("Returns found IDs once in request order; missing IDs are omitted. Does not hydrate job details or count children.")
            .Produces<IReadOnlyList<JobSummaryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/jobs/{jobId:guid}", async (Guid jobId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var detail = await queryFacade.GetJobAsync(jobId, cancellationToken);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/directory-files", async (
            Guid jobId, int? attemptNumber, int? afterOrdinal, int? limit,
            HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetDirectoryManifestAsync(
                    jobId, attemptNumber, afterOrdinal ?? -1, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                return page == null ? Results.NotFound() : Results.Ok(page);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Jobs")
            .WithSummary("Pages exact files in the accepted attempt of an album or remote-directory job.")
            .Produces<DirectoryManifestPageDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}", async (
            Guid workflowId, int displayId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var detail = await queryFacade.GetJobByDisplayIdAsync(workflowId, displayId, cancellationToken);
            return detail != null ? Results.Ok(detail) : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Gets a job snapshot by workflow and display id.")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{jobId:guid}/raw", async (
            Guid jobId,
            long afterSequence,
            int? limit,
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetRawSearchResultsAsync(jobId, afterSequence, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                if (page?.NextSequence != null)
                    httpContext.Response.Headers["X-Next-Sequence"] = page.NextSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return page != null
                    ? Results.Ok(page.Items)
                    : Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Search Results")
            .WithSummary("Gets raw search responses for a search job.")
            .WithDescription("Use afterSequence to incrementally fetch raw responses after the last seen sequence.")
            .Produces<IReadOnlyList<SearchRawResultDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapGet("/api/transfers", async (
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            Guid? jobId,
            Guid? workflowId,
            string? direction,
            string? source,
            string? state,
            string? terminalOutcome,
            string? username,
            string? groupRef,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            bool? archived,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetTransfersAsync(
                    cursor, limit ?? ServerProtocol.DefaultPageSize, jobId, workflowId,
                    direction, source, state, terminalOutcome, username, fromUtc, toUtc,
                    archived ?? false,
                    cancellationToken,
                    groupRef);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Pages the combined newest-first live and retained transfer timeline.")
            .WithDescription("The cursor is a moving keyset over stable creation time and transfer ID. New transfers may appear above an existing traversal; status changes do not reorder rows.")
            .Produces<TransferTimelinePageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/dashboard/analytics", async (
            string? range,
            DashboardAnalyticsFacade analytics,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await analytics.GetAsync(range, cancellationToken));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Dashboard")
            .WithSummary("Gets bounded transfer analytics for a dashboard range.")
            .WithDescription("Ranges are 24h, 7d, 30d, 90d, 1y, or all. Coverage explicitly reports retention and persistence degradation; byte activity is cumulative-attempt accounting, not transfer creation size.")
            .Produces<DashboardAnalyticsDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/sharing", (EngineSupervisor supervisor) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            return sharing is null
                ? Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(sharing.GetSharingState());
        })
            .WithTags("Sharing")
            .WithSummary("Gets bounded sharing and catalog status.")
            .Produces<SharingStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/sharing/scans", (EngineSupervisor supervisor) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (sharing.GetSharingState().State == DaemonFeatureState.Disabled)
            {
                return Results.Json(
                    new ApiErrorDto("No share roots are configured.", "SharingNotConfigured"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var started = sharing.StartScan();
            if (started is null)
            {
                return Results.Json(
                    new ApiErrorDto("The scan could not be started.", "ScanUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var response = new StartShareScanResponseDto(
                started.Value.Started
                    ? StartShareScanResult.Started
                    : StartShareScanResult.AlreadyRunning,
                started.Value.Scan);
            return started.Value.Started
                ? Results.Json(response, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(response);
        })
            .RequireOperatorMutation()
            .WithTags("Sharing")
            .WithSummary("Starts a share scan or returns the currently active scan.")
            .Produces<StartShareScanResponseDto>(StatusCodes.Status202Accepted)
            .Produces<StartShareScanResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/sharing/scans/{scanId:guid}", (
            Guid scanId,
            EngineSupervisor supervisor) =>
        {
            ShareScanStateDto? scan = supervisor.Sharing?.GetScan(scanId);
            return scan is null
                ? Results.NotFound(new ApiErrorDto(
                    "The scan was not found.",
                    "ScanNotFound"))
                : Results.Ok(scan);
        })
            .WithTags("Sharing")
            .WithSummary("Gets an active or recently completed share scan.")
            .Produces<ShareScanStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapPost("/api/sharing/scans/{scanId:guid}/cancel", (
            Guid scanId,
            EngineSupervisor supervisor) =>
        {
            SharingRuntime? sharing = supervisor.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Sharing infrastructure is unavailable.", "SharingUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            ShareScanStateDto? scan = sharing.GetScan(scanId);
            if (scan is null)
                return Results.NotFound(new ApiErrorDto("The scan was not found.", "ScanNotFound"));
            if (!sharing.CancelScan(scanId))
            {
                return Results.Conflict(
                    new ApiErrorDto("The scan is no longer cancellable.", "ScanNotCancellable"));
            }
            return Results.Ok(sharing.GetScan(scanId) ?? scan);
        })
            .RequireOperatorMutation()
            .WithTags("Sharing")
            .WithSummary("Cancels an active share scan.")
            .Produces<ShareScanStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/transfers/live", (
            EngineSupervisor supervisor,
            EngineStateStore stateStore,
            LiveTransferCursorCodec cursors,
            string? direction,
            string? state,
            string? username,
            string? cursor,
            int? limit) =>
        {
            try
            {
                SharingRuntime? sharing = supervisor.Sharing;
                if (sharing is null)
                {
                    return Results.Json(
                        new ApiErrorDto("Upload runtime is unavailable.", "UploadsUnavailable"),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (direction is not null
                    && !direction.Equals("upload", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only live upload queue rows are available.");
                if (state is not null
                    && !state.Equals("queued", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only queued live transfer rows are pageable.");

                LiveTransferCursor? decoded = cursor is null
                    ? null
                    : cursors.Decode(cursor);
                var page = sharing.Uploads.GetQueuePage(
                    decoded?.RequestedAtUtc,
                    decoded?.TransferId,
                    limit ?? ServerProtocol.DefaultPageSize,
                    decoded?.ObservedQueueRevision,
                    username);
                string? next = page.NextRequestedAtUtc is { } nextTime
                               && page.NextTransferId is { } nextId
                    ? cursors.Encode(
                        nextTime,
                        nextId,
                        page.ObservedQueueRevision)
                    : null;
                var items = page.Items
                    .Select(item => stateStore.GetLiveTransfer(item.TransferId))
                    .Where(item => item is not null)
                    .Cast<TransferStateDto>()
                    .ToArray();
                return Results.Ok(new LiveTransferPageDto(
                    items,
                    next,
                    page.ObservedQueueRevision,
                    page.QueueChanged));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Pages the bounded live transfer queue.")
            .Produces<LiveTransferPageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/transfers/lookup", async (
            TransferLookupRequestDto request,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await queryFacade.LookupTransfersAsync(request, cancellationToken)); }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .WithTags("Transfers")
            .WithSummary("Looks up current unarchived transfer timeline rows by ID.")
            .WithDescription("Uses the same live, queued and retained projection as the timeline. Returns all found IDs in newest-first order without a continuation. Missing IDs are omitted; retained availability is explicit.")
            .Produces<TransferTimelinePageDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/transfers/timeline", async (
            TransferTimelineScopeDto scope,
            HistoricalQueryFacade queryFacade,
            ServerEventBroadcaster events,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await queryFacade.GetTransferTimelineSnapshotAsync(scope, events, cancellationToken));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _)) { return BadRequest(ex); }
        })
            .RequireOperator()
            .WithTags("Transfers")
            .WithSummary("Snapshots a timeline extent, expanded files and requested folder totals.")
            .WithDescription("Uses the same scoped read as ObserveTransferTimeline. Window membership and boundary describe the moving prefix independently of explicitly requested additional files.")
            .Produces<TransferTimelineUpdateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/transfers/group-summaries", async (
            TransferGroupSummariesRequestDto request,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            if (request.Groups is null || request.Groups.Any(group => group is null || string.IsNullOrEmpty(group.GroupRef)))
                return Results.BadRequest(new ApiErrorDto(
                    "Each transfer group needs an exact group reference.",
                    "InvalidTransferGroup"));
            return Results.Ok(await queryFacade.GetTransferGroupSummariesAsync(
                request.Groups,
                cancellationToken));
        })
            .WithTags("Transfers")
            .WithSummary("Gets moving whole-group totals for the requested transfer groups.")
            .WithDescription("Uses the retained transfer ledger with a live overlay; group refs are opaque, and an unavailable retained resource does not yield authoritative totals.")
            .Produces<TransferGroupSummariesResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/transfers/{transferId:guid}", async (
            Guid transferId,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var detail = await queryFacade.GetTransferDetailAsync(transferId, cancellationToken);
                return detail != null
                    ? Results.Ok(detail)
                    : Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"));
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Gets one transfer using a live-first overlay and retained history fallback.")
            .Produces<TransferDetailDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound);

        app.MapPost("/api/transfers/{transferId:guid}/cancel", async (
            Guid transferId,
            EngineSupervisor supervisor,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            TransferStateDto? live = supervisor.StateStore.GetLiveTransfer(transferId);
            if (live?.Identity.Direction.Equals("Download", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!supervisor.TryCancelDownloadTransfer(transferId))
                {
                    return Results.Conflict(new ApiErrorDto(
                        "The download transfer is no longer cancellable.",
                        "TransferNotCancellable"));
                }
                return Results.Ok(supervisor.StateStore.GetLiveTransfer(transferId) ?? live);
            }

            SharingRuntime? sharing = supervisor.Sharing;
            if (sharing is null)
            {
                return Results.Json(
                    new ApiErrorDto("Transfer runtime is unavailable.", "TransfersUnavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            var current = sharing.Uploads.GetTransfer(transferId);
            if (current is null)
            {
                TransferDetailDto? retained = await queryFacade.GetTransferDetailAsync(
                    transferId,
                    cancellationToken);
                return retained is null
                    ? Results.NotFound(new ApiErrorDto(
                        "The transfer was not found.",
                        "TransferNotFound"))
                    : Results.Conflict(new ApiErrorDto(
                        "The retained transfer is no longer cancellable.",
                        "TransferNotCancellable"));
            }
            if (!sharing.Uploads.Cancel(transferId))
            {
                return Results.Conflict(
                    new ApiErrorDto("The transfer is no longer cancellable.", "TransferNotCancellable"));
            }
            return Results.Ok(supervisor.StateStore.GetLiveTransfer(transferId));
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Cancels a queued or active download or upload transfer.")
            .Produces<TransferStateDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/transfers/cancel", (
            BulkCancelTransfersRequestDto request,
            EngineSupervisor supervisor) =>
            Results.Ok(supervisor.CancelTransfers(request)))
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Cancels a snapshot of transfers by direction, queue state, and optional exact peer/group.")
            .WithDescription("Targets are resolved once, then cancelled independently. Races and per-transfer failures are reported in a fixed-size receipt.")
            .Produces<TransferCommandReceiptDto>();

        app.MapPost("/api/transfers/{transferId:guid}/archive", async (
            Guid transferId,
            SetTransferArchivedRequestDto request,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await queryFacade.SetTransfersArchivedAsync(
                    new Sockseek.Persistence.Read.TransferArchiveFilter(TransferId: transferId),
                    request.Archived,
                    cancellationToken));
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Archives or restores one terminal retained transfer.")
            .WithDescription("Archive is reversible and separate from cancellation; a nonterminal transfer is rejected.")
            .Produces<TransferCommandReceiptDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/transfers/archive", async (
            ArchiveTransfersRequestDto request,
            HistoricalQueryFacade queryFacade,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await queryFacade.SetTransfersArchivedAsync(
                    new Sockseek.Persistence.Read.TransferArchiveFilter(
                        Direction: request.Direction,
                        TerminalOutcome: request.TerminalOutcome,
                        Username: request.Username,
                        FromUtc: request.FromUtc,
                        ToUtc: request.ToUtc,
                        GroupRef: request.GroupRef),
                    request.Archived,
                    cancellationToken));
            }
            catch (NotSupportedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .RequireOperatorMutation()
            .WithTags("Transfers")
            .WithSummary("Archives or restores terminal transfer history, optionally scoped to an exact peer/group.")
            .WithDescription("The command is reversible, excludes active transfers, and returns bounded outcome counts.")
            .Produces<TransferCommandReceiptDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/api/transfers/{transferId:guid}/attempts", async (
            Guid transferId,
            int afterAttemptNumber,
            int? limit,
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetTransferAttemptsAsync(
                    transferId, afterAttemptNumber, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                if (page?.NextAttemptNumber != null)
                    httpContext.Response.Headers["X-Next-Attempt-Number"] = page.NextAttemptNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return page != null ? Results.Ok(page.Items) : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Transfers")
            .WithSummary("Lists current and retained attempts for one transfer.")
            .Produces<IReadOnlyList<TransferAttemptHistoryDto>>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/retrieve-folder", async (
            Guid jobId,
            RetrieveFolderRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartRetrieveFolderAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts a folder retrieval job for a selected album result folder.")
            .WithDescription("Retrieves the full remote folder contents for a selected folder result. Search responses can omit child items that did not match the original query.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/downloads/files", async (
            Guid jobId,
            StartFileDownloadsRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summaries = await supervisor.StartFileDownloadsAsync(jobId, request, ct);
                return summaries != null
                    ? Results.Accepted($"/api/jobs/{jobId}", summaries)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts one or more file download jobs from selected search result files.")
            .WithDescription("The source search job identifies where the candidate refs came from. Per-download settings belong in the request options, not in the original search job.")
            .Produces<IReadOnlyList<JobSummaryDto>>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/downloads/folder", async (
            Guid jobId,
            StartFolderDownloadRequestDto request,
            EngineSupervisor supervisor,
            CancellationToken ct) =>
        {
            try
            {
                var summary = await supervisor.StartFolderDownloadAsync(jobId, request, ct);
                return summary != null
                    ? Results.Accepted($"/api/jobs/{summary.JobId}", summary)
                    : Results.NotFound();
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Follow-up Jobs")
            .WithSummary("Starts an album/folder download job from a selected folder result.")
            .WithDescription("The source search job identifies where the folder ref came from. Per-download settings belong in the request options, not in the original search job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/{jobId:guid}/manual/complete", async (Guid jobId, EngineSupervisor supervisor) =>
        {
            return await supervisor.CompleteManualSelectionAsync(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Completes a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to close the manual step without resuming the job.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/manual/skip", async (Guid jobId, EngineSupervisor supervisor) =>
        {
            return await supervisor.SkipManualSelectionAsync(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Skips a manual-selection job without starting additional downloads.")
            .WithDescription("Use this when a DownloadBehavior.Manual job reached AwaitingSelection and the caller wants to record an explicit user skip.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/cancel", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJob(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Cancels a job when cancellation is available.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/cancel-all", (EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelAllJobs();
            return Results.Accepted(
                "/api/jobs",
                new CancelJobsResponseDto(cancelled));
        })
            .WithTags("Jobs")
            .WithSummary("Cancels all currently cancellable daemon jobs.")
            .WithDescription("The daemon remains running and can accept later submissions.")
            .Produces<CancelJobsResponseDto>(StatusCodes.Status202Accepted);

        app.MapPost("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}/cancel", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            return supervisor.CancelJobByDisplayId(workflowId, displayId)
                ? Results.Accepted($"/api/workflows/{workflowId}")
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Cancels a workflow job by display id.")
            .WithDescription("Convenience endpoint for CLI-style cancellation prompts. Normal GUI clients should prefer AvailableActions on known job ids.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{jobId:guid}/next-candidate", (Guid jobId, EngineSupervisor supervisor) =>
        {
            return supervisor.TryNextCandidate(jobId)
                ? Results.Accepted($"/api/jobs/{jobId}")
                : Results.NotFound();
        })
            .WithTags("Jobs")
            .WithSummary("Tries the next candidate for an active job download.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/jobs/display/{displayId:int}/next-candidate", (Guid workflowId, int displayId, EngineSupervisor supervisor) =>
        {
            return supervisor.TryNextCandidateByDisplayId(workflowId, displayId)
                ? Results.Accepted($"/api/workflows/{workflowId}")
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Tries the next candidate for an active job download by display id.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/extract", async (SubmitExtractJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitExtractJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an input extraction job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/effective-settings", async (
            ResolveEffectiveSettingsRequestDto request,
            EngineSupervisor supervisor,
            JobPreviewCoordinator previews,
            CancellationToken ct) =>
        {
            try
            {
                JobDraftDto resolved = await previews.ResolveArtifactDraftForSettingsAsync(
                    request.Job, ct).ConfigureAwait(false);
                return Results.Ok(supervisor.ResolveEffectiveSettings(request with { Job = resolved }));
            }
            catch (InputArtifactUnavailableException ex)
            {
                return Results.Json(
                    new ApiErrorDto(ex.Message, "artifact-unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Job Submission")
            .WithSummary("Resolves the effective settings for a job draft without submitting it.")
            .WithDescription("Uses the same typed settings composer as direct Start. An extract or generic-search draft may omit its not-yet-entered input for provisional settings; actual submission still validates it. Secret and command values are redacted; matched profiles and safe per-field provenance are returned for Review and diagnostics.")
            .Produces<ResolveEffectiveSettingsResponseDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

        app.MapPost("/api/jobs/search", async (SubmitSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a generic Soulseek search job.")
            .WithDescription("Search jobs are discovery-oriented. They store raw Soulseek results; use projection endpoints to view those results as files, album folders, or aggregate candidates, then use follow-up download endpoints for selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/tracks", async (SubmitTrackSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitTrackSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a track search job.")
            .WithDescription("Track search jobs are suitable for exploratory pick-then-download UIs: inspect projected file candidates from the result endpoints, then start follow-up downloads from selected refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/search/albums", async (SubmitAlbumSearchJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumSearchJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album search job.")
            .WithDescription("Album search jobs are suitable for exploratory pick-then-download clients: create an immutable Search View, page its directory projection, then commit revision-bound refs.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/song", async (SubmitSongJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitSongJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a single-file download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/downloads/album", async (SubmitAlbumJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an album/folder download job.")
            .WithDescription("Use DownloadBehavior.Automatic for normal transfer jobs. Use DownloadBehavior.Manual when the job should pause at AwaitingSelection for caller approval/selection before resuming the same job.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/aggregate/tracks", async (SubmitAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAggregateJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an aggregate track search job.")
            .WithDescription("Aggregate jobs can download automatically or, with DownloadBehavior.Manual, pause after candidate grouping so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/aggregate/albums", async (SubmitAlbumAggregateJobRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitAlbumAggregateJobAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits an aggregate album search job.")
            .WithDescription("Aggregate album jobs can download automatically or, with DownloadBehavior.Manual, pause after bucket projection so the caller can choose which child downloads to resume.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/jobs/lists", async (SubmitJobListRequestDto request, EngineSupervisor supervisor, CancellationToken ct) =>
            await SubmitJobAsync(() => supervisor.SubmitJobListAsync(request, ct)))
            .WithTags("Job Submission")
            .WithSummary("Submits a job list from draft child jobs.")
            .WithDescription("Job drafts are submission payloads only. Submitted children appear as normal runtime jobs in subsequent job/workflow snapshots.")
            .Produces<JobSummaryDto>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/workflows", async (
            string? cursor,
            int? limit,
            HistoricalQueryFacade queryFacade,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var page = await queryFacade.GetWorkflowsAsync(cursor, limit ?? ServerProtocol.DefaultPageSize, cancellationToken);
                if (page.NextCursor != null)
                    httpContext.Response.Headers["X-Next-Cursor"] = page.NextCursor;
                return Results.Ok(page.Items);
            }
            catch (Exception ex) when (TryCreateBadRequest(ex, out _))
            {
                return BadRequest(ex);
            }
        })
            .WithTags("Workflows")
            .WithSummary("Lists known workflows.")
            .Produces<IReadOnlyList<WorkflowSummaryDto>>();

        app.MapGet("/api/workflows/{workflowId:guid}", async (
            Guid workflowId, HistoricalQueryFacade queryFacade, CancellationToken cancellationToken) =>
        {
            var workflow = await queryFacade.GetWorkflowAsync(workflowId, cancellationToken);
            return workflow != null ? Results.Ok(workflow) : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Gets a workflow snapshot by id.")
            .WithDescription("Use the cursor-paged jobs collection to list workflow roots or descendants.")
            .Produces<WorkflowDetailDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/workflows/{workflowId:guid}/cancel", (Guid workflowId, EngineSupervisor supervisor) =>
        {
            int cancelled = supervisor.CancelWorkflow(workflowId);
            return cancelled > 0
                ? Results.Accepted($"/api/workflows/{workflowId}", new CancelWorkflowResponseDto(cancelled))
                : Results.NotFound();
        })
            .WithTags("Workflows")
            .WithSummary("Cancels cancellable jobs in a workflow.")
            .Produces<CancelWorkflowResponseDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapHub<ServerEventHub>("/api/events");
    }

    private static async Task<IResult> SubmitJobAsync(Func<Task<JobSummaryDto>> submit)
    {
        try
        {
            var summary = await submit();
            return Results.Accepted($"/api/jobs/{summary.JobId}", summary);
        }
        catch (Exception ex) when (TryCreateBadRequest(ex, out _))
        {
            return BadRequest(ex);
        }
    }

    private static async Task<IResult> OptionalQueryAsync<T>(
        Func<Task<T?>> query,
        bool translateBadRequest = false)
        where T : class
    {
        try
        {
            T? result = await query().ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (Exception exception) when (
            translateBadRequest && TryCreateBadRequest(exception, out _))
        {
            return BadRequest(exception);
        }
    }

    private static IResult BadRequest(Exception ex)
    {
        TryCreateBadRequest(ex, out var error);
        string code = ex is UnsupportedNameFormatVariableException
            ? "invalid-name-format-variable"
            : "InvalidRequest";
        return Results.BadRequest(new ApiErrorDto(error, code));
    }

    private static IResult ChatUnavailable()
        => Results.Json(
            new ApiErrorDto(
                "Chat is unavailable because daemon persistence is disabled or not started.",
                "Unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> WithChatAsync(
        EngineSupervisor supervisor,
        Func<ChatRuntime, Task<IResult>> action)
    {
        if (supervisor.Chat is not { } chat)
            return ChatUnavailable();
        try
        {
            return await action(chat).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return ChatFailure(exception);
        }
    }

    private static RouteHandlerBuilder WithChatErrors(this RouteHandlerBuilder builder)
        => builder
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<ApiErrorDto>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorDto>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorDto>(StatusCodes.Status429TooManyRequests)
            .Produces<ApiErrorDto>(StatusCodes.Status503ServiceUnavailable);

    private static IResult ChatNotFound(string message)
        => Results.NotFound(new ApiErrorDto(message, "NotFound"));

    private static IResult ChatFailure(Exception exception)
    {
        return exception switch
        {
            ArgumentException => Results.BadRequest(
                new ApiErrorDto(exception.Message, "InvalidRequest")),
            KeyNotFoundException => ChatNotFound(exception.Message),
            UnauthorizedAccessException => Results.Json(
                new ApiErrorDto(exception.Message, "Denied"),
                statusCode: StatusCodes.Status403Forbidden),
            ChatCapacityException => Results.Json(
                new ApiErrorDto(exception.Message, "Capacity"),
                statusCode: StatusCodes.Status429TooManyRequests),
            ChatStateConflictException => Results.Conflict(
                new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException when exception.Message.Contains(
                "MessageId", StringComparison.Ordinal) => Results.Conflict(
                    new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException when exception.Message.Contains(
                "revision changed", StringComparison.OrdinalIgnoreCase) => Results.Conflict(
                    new ApiErrorDto(exception.Message, "Conflict")),
            InvalidOperationException => Results.Json(
                new ApiErrorDto(exception.Message, "Unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(
                new ApiErrorDto("The chat operation failed.", "Unavailable"),
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static bool TryCreateBadRequest(Exception ex, out string error)
    {
        error = ex.Message;
        return ex is ArgumentException
            || ex.Message.StartsWith("Input error:", StringComparison.Ordinal);
    }
}
