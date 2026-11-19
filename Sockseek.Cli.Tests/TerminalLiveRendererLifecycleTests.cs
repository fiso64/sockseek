using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Cli;

namespace Tests.Cli;

[TestClass]
public class TerminalLiveRendererLifecycleTests
{
    [TestMethod]
    [DataRow("🎵abc", 4, true, "abc")]
    [DataRow("🎵abc", 2, false, "…")]
    [DataRow("ae\u0301bc", 2, false, "a…")]
    [DataRow("abc界", 3, true, "c界")]
    [DataRow("abc", 3, false, "abc")]
    public void FitText_PreservesTextElementsWithinCellBudget(string input, int width, bool keepEnd, string expected)
    {
        Assert.AreEqual(expected, TerminalLiveRenderer.FitText(input, width, keepEnd));
    }

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
