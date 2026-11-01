namespace Sockseek.Core.Models;

/// <summary>Projects an already selected album directory into neutral exact-file work.</summary>
public static class AlbumTransferPlanner
{
    public static DirectoryTransferPlan FromSelectedDirectory(AlbumFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return FromSelectedDirectory(folder, folder.Files);
    }

    public static DirectoryTransferPlan FromSelectedDirectory(
        AlbumFolder folder,
        IReadOnlyList<AlbumFile> selectedFiles)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(selectedFiles);
        if (selectedFiles.Count == 0)
            throw new ArgumentException("A selected album directory cannot be empty.", nameof(folder));
        if (selectedFiles.Any(file => file is null || !folder.Files.Contains(file)))
            throw new ArgumentException("Every selected album file must belong to the directory.", nameof(selectedFiles));

        folder.DirectoryIdentity.RequireAdvertisedFolder();
        return DirectoryTransferPlanner.FromObservedSelection(new PeerDirectorySnapshot(
            folder.DirectoryIdentity,
            selectedFiles.Select(file => file.Candidate.Target).ToArray(),
            folder.IsFullyRetrieved));
    }
}
