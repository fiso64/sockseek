namespace Sockseek.Server;

public sealed class EngineRuntimeHostedService : BackgroundService
{
    private readonly EngineSupervisor supervisor;
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EngineRuntimeHostedService(EngineSupervisor supervisor)
    {
        this.supervisor = supervisor;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        // BackgroundService runs on the pool. Publish local services before
        // Kestrel accepts requests, without waiting for a remote session/login.
        Task completed = await Task.WhenAny(initialized.Task, ExecuteTask!)
            .WaitAsync(cancellationToken);
        await completed;
        if (!initialized.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException("The daemon runtime stopped before initialization.");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => supervisor.RunAsync(stoppingToken, () => initialized.TrySetResult());
}
