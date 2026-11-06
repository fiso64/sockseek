using Sockseek.Core.Transfers.Downloads.JobTracking;
using Sockseek.Core.Transfers.Downloads.State;
using Sockseek.Core.Jobs;

namespace Sockseek.Core.Transfers.Downloads.Commands;

internal sealed class DownloadCommandTargetResolver
{
    private readonly DownloadJobTracker jobs;
    private readonly ActiveDownloadTracker activeDownloads;

    public DownloadCommandTargetResolver(DownloadJobTracker jobs, ActiveDownloadTracker activeDownloads)
    {
        this.jobs = jobs;
        this.activeDownloads = activeDownloads;
    }

    public Job? Resolve(Guid jobId) => jobs.GetJob(jobId);

    public Job? ResolveDisplayId(int displayId, Guid? workflowId)
    {
        var job = jobs.GetJob(displayId);
        if (job == null)
            return null;

        return workflowId.HasValue && job.WorkflowId != workflowId.Value ? null : job;
    }

    public IReadOnlyList<Job> ResolveWorkflow(Guid workflowId) => jobs.GetJobsByWorkflow(workflowId);

    public IReadOnlyList<ActiveDownload> ActiveDownloadsFor(Job job)
    {
        var targetIds = CommandTargetIds(job);
        return activeDownloads.ActiveDownloads
            .Where(download => targetIds.Contains(download.Owner.Id))
            .ToList();
    }

    private static HashSet<Guid> CommandTargetIds(Job job)
    {
        var ids = new HashSet<Guid>();
        Add(job);
        return ids;

        void Add(Job current)
        {
            ids.Add(current.Id);

            switch (current)
            {
                case JobList list:
                    foreach (var child in list.Jobs)
                        Add(child);
                    break;

                case ExtractJob { Result: { } result }:
                    Add(result);
                    break;

                case AlbumJob album:
                    foreach (var song in album.TrackJobs)
                        Add(song);
                    break;

                case AggregateJob aggregate:
                    foreach (var song in aggregate.Songs)
                        Add(song);
                    break;

                case AlbumAggregateJob aggregate:
                    foreach (var album in aggregate.Albums)
                        Add(album);
                    break;
            }
        }
    }
}
