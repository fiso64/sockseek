using Sockseek.Core.Models;
using Sockseek.Core.Snapshots;
using Soulseek;

namespace Sockseek.Core.PeerBrowsing;

/// <summary>
/// Maps already-owned directory rows to the neutral exact-target snapshot. This
/// is used by local/test clients only; network acquisition streams into an
/// artifact and maps its rows through the same target rules.
/// </summary>
public static class PeerDirectorySnapshotFactory
{
    public static PeerDirectorySnapshot FromBrowseResponse(
        PeerDirectoryIdentity identity,
        BrowseResponse response)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(response);

        string prefix = NormalizeDirectory(identity.FolderPath);
        var targets = new List<PeerFileTarget>();
        foreach (Soulseek.Directory directory in response.Directories)
        {
            string candidate = NormalizeDirectory(directory.Name);
            if (!candidate.Equals(prefix, StringComparison.Ordinal)
                && !candidate.StartsWith(prefix + "\\", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Soulseek.File file in directory.Files)
                targets.Add(ToTarget(identity.Username, directory.Name, file));
        }

        return new PeerDirectorySnapshot(identity, targets, isComplete: true);
    }

    public static PeerFileTarget ToTarget(
        string username,
        string remoteDirectory,
        Soulseek.File file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string filename = Services.Searcher.GetBrowseFilePath(
            remoteDirectory,
            file.Filename);
        var attributes = file.Attributes?
            .Select(attribute => new FileAttributeSnapshot(
                attribute.Type.ToString(),
                attribute.Value,
                (int)attribute.Type))
            .ToArray();
        return new PeerFileTarget(
            new PeerFileIdentity(username, filename),
            file.Size < 0 ? null : file.Size,
            file.Extension,
            file.BitRate,
            file.BitDepth,
            file.SampleRate,
            file.Length,
            attributes);
    }

    private static string NormalizeDirectory(string path)
        => path.Replace('/', '\\').TrimEnd('\\');
}
