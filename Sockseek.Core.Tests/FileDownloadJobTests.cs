using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.Core;

[TestClass]
public sealed class FileDownloadJobTests
{
    [TestMethod]
    public void SongAndRemoteFileJobs_AreSiblingFileDownloadJobs()
    {
        var song = new SongJob(new SongQuery { Artist = "Artist", Title = "Track" });
        var remote = new RemoteFileJob(Target(@"Folder\File.bin", 20));

        Assert.IsInstanceOfType<FileDownloadJob>(song);
        Assert.IsInstanceOfType<FileDownloadJob>(remote);
        Assert.AreEqual(typeof(FileDownloadJob), typeof(SongJob).BaseType);
        Assert.AreEqual(typeof(FileDownloadJob), typeof(RemoteFileJob).BaseType);
        Assert.AreEqual(@"Folder\File.bin", remote.Target.Filename);
        Assert.AreEqual("File.bin", remote.OutputPath.ToPlatformPath());
    }

    [TestMethod]
    public void SharedProgressState_HandlesKnownAndUnknownSize()
    {
        var remote = new RemoteFileJob(Target(@"Folder\File.bin", size: null));
        remote.BytesTransferred = 10;
        Assert.AreEqual(0, remote.Progress);

        remote.FileSize = 20;
        Assert.AreEqual(0.5, remote.Progress);
        remote.BytesTransferred = 25;
        Assert.AreEqual(1, remote.Progress);
    }

    [TestMethod]
    public void RemoteFileJob_HasNoMissingTargetConstructionPath()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new RemoteFileJob(null!));
        Assert.IsFalse(typeof(RemoteFileJob).GetConstructors().Any(constructor =>
            constructor.GetParameters().All(parameter => parameter.HasDefaultValue)));
    }

    [TestMethod]
    public void InheritedMusicNameFormat_FallsBackToOrdinaryFilePlacement()
    {
        var job = new RemoteFileJob(Target(@"Folder\File.bin", 20));
        var settings = new DownloadSettings
        {
            Output = { NameFormat = "{artist}/{filename}" },
        };

        JobPreparer.PrepareSubtree(job, settings);

        Assert.AreEqual("", job.Config.Output.NameFormat);
    }

    private static PeerFileTarget Target(string filename, long? size)
        => new(new PeerFileIdentity("Peer", filename), size, Path.GetExtension(filename));
}
