using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Server;

namespace Tests.Server;

[TestClass]
public class ServerEventCatalogTests
{
    [TestMethod]
    public void Describe_ClassifiesSnapshotInvalidationEventsAsState()
    {
        var descriptor = ServerEventCatalog.Describe("job.upserted");

        Assert.AreEqual(ServerEventCatalog.StateCategory, descriptor.Category);
        Assert.IsTrue(descriptor.SnapshotInvalidation);
        Assert.AreEqual(nameof(JobSummaryDto), descriptor.PayloadDto);
    }

    [TestMethod]
    public void Describe_ClassifiesDownloadProgressSeparatelyFromState()
    {
        var descriptor = ServerEventCatalog.Describe("download.progress");

        Assert.AreEqual(ServerEventCatalog.ProgressCategory, descriptor.Category);
        Assert.IsFalse(descriptor.SnapshotInvalidation);
        Assert.AreEqual(nameof(DownloadProgressEventDto), descriptor.PayloadDto);
    }

    [TestMethod]
    public void Describe_ClassifiesLowLevelEngineEventsAsActivity()
    {
        var descriptor = ServerEventCatalog.Describe("song.searching");

        Assert.AreEqual(ServerEventCatalog.ActivityCategory, descriptor.Category);
        Assert.IsFalse(descriptor.SnapshotInvalidation);
        Assert.AreEqual(nameof(SongSearchingEventDto), descriptor.PayloadDto);
    }
}
