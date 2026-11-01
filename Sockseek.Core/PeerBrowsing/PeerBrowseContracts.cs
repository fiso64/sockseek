using Sockseek.Core.Models;

namespace Sockseek.Core.PeerBrowsing;

public enum PeerShareVisibility
{
    Public,
    Locked,
}

public sealed record PeerBrowseWireFile(
    int Code,
    string Filename,
    long Size,
    string Extension,
    int AttributeCount);

public sealed record PeerBrowseWireAttribute(int Type, int Value);

/// <summary>First valid scalar values from ordered Soulseek file attributes.</summary>
public readonly record struct PeerBrowseFileMetadata(int? BitRate, int? BitDepth, int? SampleRate, int? Length)
{
    public PeerBrowseFileMetadata WithAttribute(int type, int value) => type switch
    {
        0 when BitRate is null && value > 0 => this with { BitRate = value },
        1 when Length is null && value >= 0 => this with { Length = value },
        4 when SampleRate is null && value > 0 => this with { SampleRate = value },
        5 when BitDepth is null && value > 0 => this with { BitDepth = value },
        _ => this,
    };
}


/// <summary>
/// Receives one materialized browse-response row at a time. Implementations are
/// expected to retain rows in bounded transactions.
/// </summary>
public interface IPeerBrowseRowSink : IAsyncDisposable
{
    ValueTask BeginDirectoryAsync(
        string wirePath,
        PeerShareVisibility visibility,
        int fileCount,
        CancellationToken cancellationToken = default);

    ValueTask BeginFileAsync(
        PeerBrowseWireFile file,
        CancellationToken cancellationToken = default);

    ValueTask AddAttributeAsync(
        PeerBrowseWireAttribute attribute,
        CancellationToken cancellationToken = default);

    ValueTask EndFileAsync(CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}

public sealed record PeerBrowseIndexProgress(
    long DirectoryCount,
    long FileCount,
    long TotalFileBytes);

public sealed record PeerBrowseTransportProgress(
    long CompressedBytesReceived,
    long? CompressedBytesExpected);

/// <summary>
/// The narrow transport boundary between peer browse acquisition and Sockseek's
/// disk-backed artifact writer.
/// </summary>
public interface IPeerBrowseTransport
{
    Task ReceiveAsync(
        string username,
        IPeerBrowseRowSink sink,
        Action<PeerBrowseTransportProgress>? transportProgress = null,
        Action<PeerBrowseIndexProgress>? indexProgress = null,
        CancellationToken cancellationToken = default);
}

public sealed class PeerBrowseProtocolException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// Resolves one exact peer directory subtree. This requires a full peer browse:
/// despite the protocol documentation, directory-content replies from clients
/// including slskd and Sockseek contain only the named folder, with no inventory
/// of child folders. Substituting GetDirectoryContentsAsync would silently omit
/// descendants. Daemon execution reuses a shared artifact; local execution may
/// retain only the requested subtree from the same full browse.
/// </summary>
// TODO: Investigate complete subtree discovery without a full browse, for example
// through Soulseek searches targeted to one peer. Any alternative must preserve
// complete subtree coverage and exact file identities.
public interface IPeerDirectorySource
{
    Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken = default);
}

internal sealed class MissingPeerDirectorySource : IPeerDirectorySource
{
    public static MissingPeerDirectorySource Instance { get; } = new();

    public Task<PeerDirectorySnapshot> RetrieveDirectoryAsync(
        PeerDirectoryIdentity directory,
        CancellationToken cancellationToken = default)
        => Task.FromException<PeerDirectorySnapshot>(new InvalidOperationException(
            "Peer directory retrieval requires the shared peer-browse service."));
}
