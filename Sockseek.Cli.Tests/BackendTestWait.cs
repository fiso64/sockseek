using Sockseek.Api;
using Sockseek.Cli;

namespace Tests.Cli;

internal static class BackendTestWait
{
    public static async Task AlbumChoicesReadyAsync(ICliBackend backend, int count)
    {
        await BackendTestWaiter.UntilAsync(backend,
            ct => backend.GetJobsAsync(new JobQuery(null, null, null, null, IncludeAll: true), ct),
            jobs => jobs.Count(job => job.Display.Kind == ServerJobKind.Album
                && job.Lifecycle.LifecycleState == JobLifecycleState.AwaitingSelection) >= count,
            $"Expected {count} albums ready for selection.");
    }
}
