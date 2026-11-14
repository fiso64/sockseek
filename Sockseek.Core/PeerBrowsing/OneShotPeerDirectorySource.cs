using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Soulseek;

namespace Sockseek.Core.PeerBrowsing;

/// <summary>
/// One-shot fallback for directory retrieval outside the daemon. It consumes the
/// same browse transport but retains only the requested public subtree. Daemon
/// execution supplies <c>PeerBrowseService</c> instead so acquisitions can be
/// reused and paged from an artifact.
/// </summary>
public sealed class OneShotPeerDirectorySource(IPeerBrowseTransport transport)
    : IPeerDirectorySource
{
    private readonly IPeerBrowseTransport transport = transport
        ?? throw new ArgumentNullException(nameof(transport));

    public async Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.RequireAdvertisedFolder();
        var sink = new RequestedDirectorySink(directory);
        // A directory-content reply cannot establish complete subtree membership;
        // see IPeerDirectorySource for the full-browse compatibility requirement.
        await transport.ReceiveAsync(
            directory.Username,
            sink,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return sink.Snapshot;
    }

    private sealed class RequestedDirectorySink(PeerDirectoryIdentity identity)
        : IPeerBrowseRowSink
    {
        private readonly string requestedIdentity =
            PeerBrowsePath.NormalizeDirectoryIdentity(identity.FolderPath);
        private readonly List<PeerFileTarget> targets = [];
        private string? currentDirectoryIdentity;
        private string? currentDirectoryWirePath;
        private PeerBrowseWireFile? currentFile;
        private PeerBrowseFilePath currentFilePath;
        private string? currentExtension;
        private List<FileAttributeSnapshot>? currentAttributes;
        private PeerBrowseFileMetadata metadata;
        private bool collectingDirectory;
        private bool completed;

        public PeerDirectorySnapshot Snapshot
            => completed
                ? new PeerDirectorySnapshot(identity, targets, isComplete: true)
                : throw new InvalidOperationException("The directory browse is not complete.");

        public ValueTask BeginDirectoryAsync(
            string wirePath,
            PeerShareVisibility visibility,
            int fileCount,
            CancellationToken cancellationToken = default)
        {
            EnsureNoOpenFile();
            string directoryIdentity = PeerBrowsePath.NormalizeDirectoryIdentity(wirePath);
            collectingDirectory = visibility == PeerShareVisibility.Public
                                  && PeerBrowsePath.IsSameOrDescendant(directoryIdentity, requestedIdentity);
            currentDirectoryIdentity = directoryIdentity;
            currentDirectoryWirePath = wirePath;
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginFileAsync(
            PeerBrowseWireFile file,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            EnsureNoOpenFile();
            if (!collectingDirectory)
                return ValueTask.CompletedTask;
            if (currentDirectoryIdentity is null || currentDirectoryWirePath is null)
                throw Invalid("a file appeared before its directory");
            if (file.Size < 0 || file.AttributeCount < 0)
                throw Invalid("a file size or attribute count was negative");

            currentFile = file;
            currentFilePath = PeerBrowsePath.ResolveFile(
                currentDirectoryIdentity,
                currentDirectoryWirePath,
                file.Filename);
            currentExtension = file.Extension.Length == 0
                ? null
                : PeerIdentityValidator.ValidateRemotePath(file.Extension);
            currentAttributes = file.AttributeCount == 0 ? null : [];
            metadata = default;
            return ValueTask.CompletedTask;
        }

        public ValueTask AddAttributeAsync(
            PeerBrowseWireAttribute attribute,
            CancellationToken cancellationToken = default)
        {
            if (!collectingDirectory)
                return ValueTask.CompletedTask;
            if (currentFile is null || currentAttributes is null)
                throw Invalid("a file attribute appeared outside its declared file");
            if (currentAttributes.Count >= currentFile.AttributeCount)
                throw Invalid("a file contained more attributes than declared");

            currentAttributes.Add(new FileAttributeSnapshot(
                ((FileAttributeType)attribute.Type).ToString(),
                attribute.Value,
                attribute.Type));
            metadata = metadata.WithAttribute(attribute.Type, attribute.Value);
            return ValueTask.CompletedTask;
        }

        public ValueTask EndFileAsync(CancellationToken cancellationToken = default)
        {
            if (!collectingDirectory)
                return ValueTask.CompletedTask;
            if (currentFile is null)
                throw Invalid("a file ended before it began");
            if ((currentAttributes?.Count ?? 0) != currentFile.AttributeCount)
                throw Invalid("a file contained fewer attributes than declared");

            targets.Add(new PeerFileTarget(
                new PeerFileIdentity(identity.Username, currentFilePath.WireFilename),
                currentFile.Size,
                currentExtension,
                metadata.BitRate,
                metadata.BitDepth,
                metadata.SampleRate,
                metadata.Length,
                currentAttributes));
            currentFile = null;
            currentExtension = null;
            currentAttributes = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        {
            EnsureNoOpenFile();
            completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void EnsureNoOpenFile()
        {
            if (currentFile is not null)
                throw Invalid("the response changed rows before the current file ended");
        }

        private static PeerBrowseProtocolException Invalid(string detail)
            => new($"The peer returned an invalid browse response: {detail}.");
    }
}
