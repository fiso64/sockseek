using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests;

[TestClass]
public class TestSignalTests
{
    [TestMethod]
    public async Task WaitUntilAsync_ObservesNotificationRaisedDuringStateCheck()
    {
        using var signal = new TestSignal();
        int checks = 0;
        await signal.WaitUntilAsync(() =>
        {
            if (++checks == 1)
            {
                signal.Pulse();
                return false;
            }
            return true;
        });

        Assert.AreEqual(2, checks, "A notification between checking state and awaiting must not be lost.");
    }
}
