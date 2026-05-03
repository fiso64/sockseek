namespace Sldl.Server;

public sealed class EngineRuntimeHostedService : BackgroundService
{
    private readonly EngineSupervisor supervisor;

    public EngineRuntimeHostedService(EngineSupervisor supervisor)
    {
        this.supervisor = supervisor;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => supervisor.RunAsync(stoppingToken);
}
