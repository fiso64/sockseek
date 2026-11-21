using Sockseek.Core.Services;

namespace Sockseek.Core.Models;

/// <summary>
/// The peer and advertised folder path. An empty path groups observed bare
/// filenames only; it never denotes the peer root or a retrievable directory.
/// </summary>
public sealed record PeerDirectoryIdentity
{
    public PeerDirectoryIdentity(string username, string folderPath)
    {
        Username = PeerIdentityValidator.ValidateUsername(username);
        ArgumentNullException.ThrowIfNull(folderPath);
        FolderPath = folderPath.Length == 0 ? folderPath : PeerIdentityValidator.ValidateRemotePath(folderPath);
    }

    public string Username { get; }
    public string FolderPath { get; }

    public void RequireAdvertisedFolder()
    {
        if (FolderPath.Length == 0)
            throw new ArgumentException("These results did not advertise a folder. Select their observed files instead.");
    }
}

/// <summary>An owned, immutable result of retrieving one peer directory subtree.</summary>
public sealed record PeerDirectorySnapshot
{
    public PeerDirectorySnapshot(
        PeerDirectoryIdentity identity,
        IReadOnlyList<PeerFileTarget> files,
        bool isComplete)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(files);

        var owned = files.ToArray();
        if (owned.Any(file => file is null))
            throw new ArgumentException("Directory files cannot contain null targets.", nameof(files));
        if (owned.Any(file => !StringComparer.Ordinal.Equals(file.Username, identity.Username)))
            throw new ArgumentException("Every directory file must belong to the directory peer.", nameof(files));
        if (owned.Select(file => file.Identity).Distinct().Count() != owned.Length)
            throw new ArgumentException("A directory snapshot cannot contain duplicate exact targets.", nameof(files));

        Identity = identity;
        Files = Array.AsReadOnly(owned);
        IsComplete = isComplete;
    }

    public PeerDirectoryIdentity Identity { get; }
    public IReadOnlyList<PeerFileTarget> Files { get; }
    public bool IsComplete { get; }
}
