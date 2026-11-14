using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public class TerminalLiveRendererLifecycleTests
{
    [TestMethod]
    public async Task Pause_StopsRenderLoopBeforeReturning_AndResumeStartsFreshLoop()
    {
        using var started = new SemaphoreSlim(0);
        using var stopped = new SemaphoreSlim(0);

        async Task RunLoop(CancellationToken ct)
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                stopped.Release();
            }
        }

        var renderer = new TerminalLiveRenderer(RunLoop);
        try
        {
            Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)));

            renderer.IsPaused = true;

            Assert.IsTrue(stopped.Wait(0), "Pause returned before the active render loop stopped.");

            renderer.IsPaused = false;

            Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            renderer.Dispose(printSummary: false);
        }

        Assert.IsTrue(stopped.Wait(0), "Dispose returned before the resumed render loop stopped.");
    }
}
