using System.Threading.Channels;
using Sockseek.Core.Jobs;
using Sockseek.Core.Settings;
using Sockseek.Core.Transfers.Downloads.Runtime;

namespace Sockseek.Core.Transfers.Downloads.Queueing;

internal sealed class DownloadJobQueue
{
    private readonly Channel<QueuedDownloadJob> channel =
        Channel.CreateUnbounded<QueuedDownloadJob>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(
        Job job,
        DownloadSettings settings,
        WorkflowLifetimeCoordinator.WorkflowRootLease? lifetime,
        bool settingsAreFinal)
    {
        job.EnsureDisplayId();
        channel.Writer.TryWrite(QueuedDownloadJob.Root(job, settings, lifetime, settingsAreFinal));
    }

    public void Resume(Job job, WorkflowLifetimeCoordinator.WorkflowRootLease? lifetime)
    {
        job.EnsureDisplayId();
        channel.Writer.TryWrite(QueuedDownloadJob.Resume(job, lifetime));
    }

    public void Complete() => channel.Writer.Complete();

    public IAsyncEnumerable<QueuedDownloadJob> ReadAllAsync(CancellationToken ct)
        => channel.Reader.ReadAllAsync(ct);
}

internal sealed record QueuedDownloadJob(
    Job Job,
    DownloadSettings? Settings,
    bool IsResume,
    bool SettingsAreFinal,
    WorkflowLifetimeCoordinator.WorkflowRootLease? Lifetime)
{
    public static QueuedDownloadJob Root(
        Job job,
        DownloadSettings settings,
        WorkflowLifetimeCoordinator.WorkflowRootLease? lifetime,
        bool settingsAreFinal)
        => new(job, settings, false, settingsAreFinal, lifetime);

    public static QueuedDownloadJob Resume(
        Job job,
        WorkflowLifetimeCoordinator.WorkflowRootLease? lifetime)
        => new(job, null, true, false, lifetime);
}
