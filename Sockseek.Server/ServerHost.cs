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
using Sockseek.Core.Diagnostics;
using Sockseek.Core.Settings;
using Sockseek.Server.Persistence;
using Sockseek.Server.PeerBrowsing;
using Sockseek.Server.UserProfiles;
using Sockseek.Server.Planning;
using Sockseek.Server.PeerRestrictions;
using Sockseek.Server.Authentication;

namespace Sockseek.Server;

public static class ServerHost
{
    internal static bool IsOpenApiGeneration => Assembly.GetEntryAssembly()?.GetName().Name?
        .Contains("getdocument", StringComparison.OrdinalIgnoreCase) == true;

    public static WebApplication Build(string[] args, ServerOptions options, string? url = null, bool logToConsole = true,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        bool isOpenApiGeneration = IsOpenApiGeneration;
        EngineSettings loggingSettings = options.Engine;
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

        builder.Services.AddSingleton<IOptions<ServerOptions>>(Options.Create(options));

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
            options.AddAuthenticationContract();
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
        builder.Services.AddOperatorAuthentication(isOpenApiGeneration);
        builder.Services.AddHostedService<PersistenceMaintenanceHostedService>();
        builder.Services.AddSingleton<InputArtifactCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<InputArtifactCoordinator>());
        builder.Services.AddSingleton<PeerRestrictionCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PeerRestrictionCoordinator>());
        builder.Services.AddSingleton<EngineSupervisor>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().StateStore);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().Runtime);
        builder.Services.AddSingleton(sp => sp.GetRequiredService<EngineSupervisor>().Submissions);
        builder.Services.AddSingleton<JobReadService>();
        builder.Services.AddSingleton<TransferReadService>();
        builder.Services.AddSingleton<DashboardAnalyticsFacade>();
        builder.Services.AddSingleton<LiveTransferCursorCodec>();
        builder.Services.AddSingleton<PeerBrowseCursorCodec>();
        builder.Services.AddSingleton<JobPreviewCursorCodec>();
        builder.Services.AddSingleton<SubmissionCommitCoordinator>();
        builder.Services.AddSingleton<JobPreviewCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobPreviewCoordinator>());
        builder.Services.AddSingleton<SearchViewCursorCodec>();
        builder.Services.AddSingleton<SearchViewCoordinator>();
        builder.Services.AddSingleton<ServerEventBroadcaster>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerEventBroadcaster>());
        builder.Services.AddHostedService<EngineRuntimeHostedService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchViewCoordinator>());

        configureServices?.Invoke(builder.Services);
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

        bool hasWebUi = WebUiHosting.Map(app);

        // The public SPA shell/assets must load before the authenticated API boundary.
        app.UseOperatorAuthentication();
        app.MapOpenApi("/api/openapi.json");
        AuthenticationEndpoints.Map(app);
        MapEndpoints(app);
        if (!hasWebUi)
            app.MapGet("/", () => Results.Redirect("/api/server/info"))
                .AllowAnonymous()
                .ExcludeFromDescription();
        app.Map("/api/{**unmatched}", () => Results.NotFound());
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
        ServerEndpoints.Map(app);
        ChatEndpoints.Map(app);
        JobEndpoints.Map(app);
        TransferEndpoints.Map(app);
        SharingEndpoints.Map(app);
    }
}
