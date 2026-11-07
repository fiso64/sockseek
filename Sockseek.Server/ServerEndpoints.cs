using Sockseek.Api;
using Sockseek.Server.Persistence;
using static Sockseek.Server.ApiErrors;

namespace Sockseek.Server;

internal static class ServerEndpoints
{
    public static void Map(WebApplication app)
    {
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

        app.MapPost("/api/persistence/integrity", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.CheckIntegrityAsync(cancellationToken)); }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
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
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Creates and independently verifies a WAL-safe online backup.")
            .Produces<PersistenceBackupResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/persistence/checkpoint", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.CheckpointAsync(cancellationToken)); }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Requests a bounded passive WAL checkpoint.")
            .Produces<PersistenceCheckpointResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/persistence/retention", async (
            PersistenceCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await coordinator.RunRetentionAsync(cancellationToken)); }
            catch (Exception ex) when (IsInvalidRequest(ex)) { return BadRequest(ex); }
        })
            .WithTags("Persistence")
            .WithSummary("Runs one bounded retention batch.")
            .Produces<PersistenceRetentionResultDto>()
            .Produces<ApiErrorDto>(StatusCodes.Status400BadRequest);

        app.MapHub<ServerEventHub>("/api/events", options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();
    }
}
