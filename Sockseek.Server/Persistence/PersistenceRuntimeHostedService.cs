namespace Sockseek.Server.Persistence;

public sealed class PersistenceRuntimeHostedService(PersistenceCoordinator coordinator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => coordinator.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => coordinator.StopAsync(cancellationToken);
}
