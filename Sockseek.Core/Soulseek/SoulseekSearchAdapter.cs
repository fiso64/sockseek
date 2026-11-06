using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;

/// <summary>
/// Converts disposable Soulseek.NET search values at the protocol boundary.
/// The returned candidate owns only Sockseek values.
/// </summary>
public static class SoulseekSearchAdapter
{
    public static FileCandidate ToFileCandidate(
        Soulseek.SearchResponse response,
        Soulseek.File file,
        FileSearchEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(file);

        var attributes = file.Attributes == null
            ? null
            : Array.AsReadOnly(file.Attributes.Select(attribute =>
                new FileAttributeSnapshot(attribute.Type.ToString(), attribute.Value, (int)attribute.Type)).ToArray());
        var target = new PeerFileTarget(
            new PeerFileIdentity(response.Username, file.Filename),
            file.Size < 0 ? null : file.Size,
            file.Extension,
            file.BitRate,
            file.BitDepth,
            file.SampleRate,
            file.Length,
            attributes);
        var peer = new SearchPeerSnapshot(
            response.Username,
            response.Files.Count,
            response.UploadSpeed,
            response.HasFreeUploadSlot);
        return new FileCandidate(target, peer, evidence);
    }
}
