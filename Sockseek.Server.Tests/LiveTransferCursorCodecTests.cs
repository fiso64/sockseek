using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Sockseek.Server.Tests;

[TestClass]
public sealed class LiveTransferCursorCodecTests
{
    [TestMethod]
    public void CursorRoundTripsKeysetAndRejectsMalformedInput()
    {
        var codec = new LiveTransferCursorCodec();
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        Guid transferId = Guid.NewGuid();
        string cursor = codec.Encode(
            requestedAt,
            transferId,
            12);

        LiveTransferCursor decoded = codec.Decode(cursor);
        Assert.AreEqual(requestedAt, decoded.RequestedAtUtc);
        Assert.AreEqual(transferId, decoded.TransferId);
        Assert.AreEqual(12, decoded.ObservedQueueRevision);

        Assert.ThrowsExactly<ArgumentException>(
            () => codec.Decode("not-a-cursor"));
    }
}
