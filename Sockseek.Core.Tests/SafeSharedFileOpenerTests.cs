using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core.IO;
using Sockseek.Core.Sharing;

namespace Tests.Core;

[TestClass]
public sealed class SafeSharedFileOpenerTests
{
    [TestMethod]
    public async Task Open_ReturnsHandleFingerprintAndReadableStream()
    {
        using var fixture = new FileFixture();
        string relative = Path.Combine("artist", "track.flac");
        fixture.CreateFile(relative, [1, 2, 3, 4]);

        await using var opened = SafeSharedFileOpener.Open(fixture.Root, relative);
        var bytes = new byte[4];
        int read = await opened.Stream.ReadAsync(bytes);

        Assert.AreEqual(4, read);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.AreEqual(4, opened.Fingerprint.SizeBytes);
    }

    [TestMethod]
    public void Open_RejectsTraversalBeforeOpening()
    {
        using var fixture = new FileFixture();

        var ex = Assert.ThrowsExactly<SharedFileOpenException>(
            () => SafeSharedFileOpener.Open(
                fixture.Root,
                Path.Combine("..", "outside.flac")));

        Assert.AreEqual(SharedFileOpenFailureReason.InvalidRelativePath, ex.Reason);
    }

    [TestMethod]
    public async Task Open_DoesNotRequireNativeIdentityForOrdinaryFilesystems()
    {
        using var fixture = new FileFixture();
        const string relative = "track.flac";
        string originalPath = fixture.CreateFile(relative, [1, 2, 3, 4]);

        SharedFileFingerprint expected;
        using (var original = SafeSharedFileOpener.Open(fixture.Root, relative))
            expected = original.Fingerprint;

        string retainedOriginal = Path.Combine(fixture.Root, "original-retained.flac");
        File.Move(originalPath, retainedOriginal);
        File.WriteAllBytes(originalPath, [9, 8, 7, 6]);
        File.SetLastWriteTimeUtc(originalPath, expected.LastWriteTimeUtc.UtcDateTime);

        await using var replacement = SafeSharedFileOpener.Open(
            fixture.Root,
            relative,
            expected);
        var bytes = new byte[4];
        _ = await replacement.Stream.ReadAsync(bytes);

        CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 6 }, bytes);
    }

    [TestMethod]
    public void Open_RejectsLinkWhoseFinalTargetEscapesRoot()
    {
        using var fixture = new FileFixture();
        string outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"sockseek-safe-open-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        string outsideFile = Path.Combine(outsideRoot, "secret.flac");
        File.WriteAllBytes(outsideFile, [1]);
        string linkPath = Path.Combine(fixture.Root, "linked");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (Exception linkError) when (
                linkError is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
            {
                Assert.Inconclusive(
                    $"Symbolic links are unavailable on this runner: {linkError.Message}");
            }

            var ex = Assert.ThrowsExactly<SharedFileOpenException>(
                () => SafeSharedFileOpener.Open(
                    fixture.Root,
                    Path.Combine("linked", "secret.flac")));

            Assert.AreEqual(SharedFileOpenFailureReason.LinkOrReparsePoint, ex.Reason);
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExactLengthReadStream_CapsReadsAndEndsAtDeclaredLength()
    {
        await using var stream = new ExactLengthReadStream(
            new MemoryStream([1, 2, 3, 4, 5]),
            length: 3);
        var buffer = new byte[10];

        int first = await stream.ReadAsync(buffer);
        int eof = await stream.ReadAsync(buffer);

        Assert.AreEqual(3, first);
        Assert.AreEqual(0, eof);
        Assert.AreEqual(3, stream.Position);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, buffer[..3]);
    }

    [TestMethod]
    public async Task ExactLengthReadStream_ThrowsOnPrematureEof()
    {
        await using var stream = new ExactLengthReadStream(
            new MemoryStream([1, 2]),
            length: 3);
        var buffer = new byte[3];

        Assert.AreEqual(2, await stream.ReadAsync(buffer));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(
            async () => _ = await stream.ReadAsync(buffer));
    }

    [TestMethod]
    public async Task SelfExpiringReadStream_ReleasesOwnerAtEofExactlyOnce()
    {
        int releases = 0;
        await using var stream = new SelfExpiringReadStream(
            new MemoryStream([1]),
            TimeSpan.FromSeconds(1),
            () => Interlocked.Increment(ref releases));
        var buffer = new byte[1];

        Assert.AreEqual(1, await stream.ReadAsync(buffer));
        Assert.AreEqual(0, await stream.ReadAsync(buffer));
        await stream.DisposeAsync();

        Assert.AreEqual(1, releases);
        Assert.IsTrue(stream.IsExpired);
    }

    [TestMethod]
    public async Task SelfExpiringReadStream_ReleasesOwnerAtIdleDeadlineAndFutureReadsFail()
    {
        var released = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new SelfExpiringReadStream(
            new MemoryStream([1, 2, 3]),
            TimeSpan.FromMilliseconds(50),
            () => released.TrySetResult());

        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(stream.IsExpired);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => _ = await stream.ReadAsync(new byte[1]));
    }

    private sealed class FileFixture : IDisposable
    {
        public FileFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"sockseek-safe-open-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateFile(string relativePath, byte[] contents)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
