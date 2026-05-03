using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sldl.Server;

namespace Tests.Server;

[TestClass]
public class ServerEventCoalescerTests
{
    [TestMethod]
    public void Flush_PublishesOnlyLatestDownloadProgressPerJob()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            (type, payload) => published.Add((type, payload)),
            TimeSpan.FromHours(1));
        var jobId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();

        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 10, 100));
        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 20, 100));
        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 30, 100));

        Assert.AreEqual(0, published.Count);

        coalescer.Flush();

        Assert.AreEqual(1, published.Count);
        Assert.AreEqual("download.progress", published[0].Type);
        var progress = (DownloadProgressEventDto)published[0].Payload;
        Assert.AreEqual(jobId, progress.JobId);
        Assert.AreEqual(30, progress.BytesTransferred);
        Assert.AreEqual(100, progress.TotalBytes);
    }

    [TestMethod]
    public void Publish_NonProgressEventFlushesPendingProgressFirst()
    {
        var published = new List<(string Type, object Payload)>();
        using var coalescer = new ServerEventCoalescer(
            (type, payload) => published.Add((type, payload)),
            TimeSpan.FromHours(1));
        var jobId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var status = new object();

        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 10, 100));
        coalescer.Publish("download.progress", new DownloadProgressEventDto(jobId, workflowId, 20, 100));
        coalescer.Publish("download.state-changed", status);

        Assert.AreEqual(2, published.Count);
        Assert.AreEqual("download.progress", published[0].Type);
        Assert.AreEqual(20, ((DownloadProgressEventDto)published[0].Payload).BytesTransferred);
        Assert.AreEqual("download.state-changed", published[1].Type);
        Assert.AreSame(status, published[1].Payload);
    }
}
