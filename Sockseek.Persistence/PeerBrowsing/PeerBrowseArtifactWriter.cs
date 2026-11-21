using System.Buffers;
using System.Buffers.Binary;
using Sockseek.Core.Models;
using Sockseek.Core.PeerBrowsing;

namespace Sockseek.Persistence.PeerBrowsing;

/// <summary>Validates one browse generation and durably publishes its immutable facts.</summary>
public sealed class PeerBrowseArtifactWriter : IPeerBrowseRowSink
{
    private readonly PeerBrowseArtifactStore store;
    private readonly PeerBrowseResource resource;
    private readonly string stagingPath;
    private readonly Dictionary<string, int> directoryIds = new(StringComparer.Ordinal);
    private readonly List<PeerBrowseArtifact.Directory> directories = [];
    private readonly List<PeerBrowseArtifact.File> files = [];
    private readonly Dictionary<string, string> extensions = new(StringComparer.Ordinal);
    private readonly ArrayBufferWriter<byte> attributes = new();
    private string? directoryIdentity;
    private string? directoryWirePath;
    private int directoryId;
    private PeerShareVisibility visibility;
    private PeerBrowseWireFile? file;
    private PeerBrowseFilePath filePath;
    private string? extension;
    private int attributeCount;
    private long wireDirectoryCount, totalBytes;
    private bool completed, disposed;

    private PeerBrowseArtifactWriter(PeerBrowseArtifactStore store, PeerBrowseResource resource)
    {
        this.store = store;
        this.resource = resource;
        stagingPath = store.GetStagingPath(resource.BrowseId);
    }

    internal static ValueTask<PeerBrowseArtifactWriter> CreateAsync(
        PeerBrowseArtifactStore store, PeerBrowseResource resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PeerBrowseArtifactWriter(store, resource));
    }

    public ValueTask BeginDirectoryAsync(string wirePath, PeerShareVisibility visibility, int fileCount,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable(cancellationToken);
        if (file is not null)
            throw Invalid("a new directory began before the previous file ended");
        if (fileCount < 0)
            throw Invalid("directory file count cannot be negative");
        string identity = PeerBrowsePath.NormalizeDirectoryIdentity(wirePath);
        string[] components = identity.Split('\\');
        int parent = 0;
        string prefix = "";
        for (int index = 0; index < components.Length; index++)
        {
            prefix = index == 0 ? components[index] : prefix + "\\" + components[index];
            bool synthetic = index != components.Length - 1;
            if (directoryIds.TryGetValue(prefix, out int id))
            {
                var existing = directories[id - 1];
                if (!synthetic && !existing.Synthetic)
                    throw Invalid("the response contains a duplicate directory identity");
                if (!synthetic || existing.Synthetic)
                    directories[id - 1] = existing with
                    {
                        WirePath = synthetic ? prefix : wirePath,
                        Visibility = synthetic && existing.Visibility != (PeerBrowseEntryVisibility)visibility
                            ? PeerBrowseEntryVisibility.Mixed : (PeerBrowseEntryVisibility)visibility,
                        Synthetic = synthetic,
                    };
                parent = id;
            }
            else
            {
                directories.Add(new(parent, synthetic ? prefix : wirePath, (PeerBrowseEntryVisibility)visibility, synthetic));
                parent = directories.Count;
                directoryIds.Add(prefix, parent);
            }
        }
        directoryId = parent;
        directoryIdentity = identity;
        directoryWirePath = wirePath;
        this.visibility = visibility;
        wireDirectoryCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask BeginFileAsync(PeerBrowseWireFile file, CancellationToken cancellationToken = default)
    {
        EnsureWritable(cancellationToken);
        ArgumentNullException.ThrowIfNull(file);
        if (directoryIdentity is null || directoryWirePath is null)
            throw Invalid("a file appeared before its directory");
        if (this.file is not null)
            throw Invalid("a new file began before the previous file ended");
        if (file.Code is < byte.MinValue or > byte.MaxValue)
            throw Invalid("file code is outside the protocol byte range");
        if (file.Size < 0 || file.AttributeCount < 0)
            throw Invalid("file size or attribute count cannot be negative");
        filePath = PeerBrowsePath.ResolveFile(directoryIdentity, directoryWirePath, file.Filename);
        extension = null;
        if (file.Extension.Length > 0)
        {
            string value = PeerIdentityValidator.ValidateRemotePath(file.Extension);
            if (!extensions.TryGetValue(value, out extension))
                extensions.Add(value, extension = value);
        }
        this.file = file;
        attributes.Clear();
        attributeCount = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask AddAttributeAsync(PeerBrowseWireAttribute attribute, CancellationToken cancellationToken = default)
    {
        EnsureWritable(cancellationToken);
        if (file is null)
            throw Invalid("a file attribute appeared outside a file");
        if (attributeCount >= file.AttributeCount)
            throw Invalid("a file contained more attributes than declared");
        Span<byte> pair = attributes.GetSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(pair, attribute.Type);
        BinaryPrimitives.WriteInt32LittleEndian(pair[4..], attribute.Value);
        attributes.Advance(8);
        attributeCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask EndFileAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable(cancellationToken);
        if (file is null)
            throw Invalid("a file ended before it began");
        if (attributeCount != file.AttributeCount)
            throw Invalid("a file contained fewer attributes than declared");
        // Usually the wire filename is exactly the declared directory plus its
        // raw leaf. Retain an override only when the peer used different spelling.
        string? wireOverride = StringComparer.Ordinal.Equals(filePath.WireFilename, directoryWirePath + "\\" + filePath.LeafName)
            ? null : filePath.WireFilename;
        files.Add(new(directoryId, filePath.LeafName, wireOverride, file.Size, extension, file.Code,
            attributes.WrittenCount == 0 ? null : attributes.WrittenSpan.ToArray(), visibility == PeerShareVisibility.Locked));
        totalBytes = PeerBrowseArtifact.Sum(totalBytes, file.Size);
        file = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable(cancellationToken);
        if (file is not null)
            throw Invalid("the response ended before the current file ended");
        var artifact = new PeerBrowseArtifact(new(PeerBrowseArtifactStore.ArtifactFormatVersion,
            directories.ToArray(), files.ToArray()), cancellationToken);
        artifact.Write(stagingPath, cancellationToken);
        await store.PromoteAsync(resource, stagingPath,
            new(wireDirectoryCount, files.Count, totalBytes), artifact, cancellationToken).ConfigureAwait(false);
        completed = true;
        directories.Clear();
        files.Clear();
        directoryIds.Clear();
        extensions.Clear();
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;
        if (!completed)
            store.DeleteStagingBestEffort(stagingPath);
        return ValueTask.CompletedTask;
    }

    private void EnsureWritable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
            throw new InvalidOperationException("The browse artifact is already complete.");
    }

    internal static PeerBrowseProtocolException Invalid(string detail)
        => new($"The peer returned an invalid browse response: {detail}.");
}
