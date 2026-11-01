namespace Sockseek.Core.Models;

public static class DirectoryTransferPlanner
{
    public static DirectoryTransferPlan FromSnapshot(PeerDirectorySnapshot snapshot, IReadOnlySet<string>? excludedFilenames = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsComplete)
            throw new ArgumentException("A directory transfer plan requires a complete snapshot.", nameof(snapshot));
        if (snapshot.Files.Count == 0)
            throw new ArgumentException("A directory transfer plan cannot be created from an empty snapshot.", nameof(snapshot));

        snapshot.Identity.RequireAdvertisedFolder();
        return BuildPlan(snapshot, excludedFilenames);
    }

    public static DirectoryTransferPlan FromObservedSelection(
        PeerDirectorySnapshot observation,
        IReadOnlySet<string>? excludedFilenames = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Files.Count == 0)
            throw new ArgumentException("An observed directory selection must contain at least one file.", nameof(observation));
        return BuildPlan(observation, excludedFilenames);
    }

    private static DirectoryTransferPlan BuildPlan(PeerDirectorySnapshot snapshot, IReadOnlySet<string>? excludedFilenames)
    {
        string root = Normalize(snapshot.Identity.FolderPath).TrimEnd('\\');
        string displayRoot = root.Length == 0 ? "" : Leaf(root);
        var entries = new List<DirectoryTransferEntry>(snapshot.Files.Count);

        foreach (var target in snapshot.Files)
        {
            if (excludedFilenames?.Contains(target.Filename) == true)
                continue;
            string filename = Normalize(target.Filename);
            string prefix = root.Length == 0 ? "" : root + "\\";
            if (root.Length == 0 && filename.Contains('\\'))
                continue;
            if (!filename.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string relative = filename[prefix.Length..];
            string[] components = relative.Split('\\', StringSplitOptions.None);
            if (components.Length == 0)
                continue;

            try
            {
                entries.Add(new DirectoryTransferEntry(target, components[..^1]));
            }
            catch (ArgumentException)
            {
                // A malformed peer entry is local to that file. Other exact targets
                // in the retrieved directory remain independently downloadable.
            }
        }

        if (entries.Count == 0)
            throw new ArgumentException("The directory selection contains no valid downloadable files.", nameof(snapshot));
        return new DirectoryTransferPlan(displayRoot, entries,
            representsDirectorySelection: root.Length != 0,
            sourceDirectory: root.Length == 0 ? null : snapshot.Identity);
    }

    private static string Normalize(string value) => value.Replace('/', '\\');

    private static string Leaf(string value)
    {
        int separator = value.LastIndexOf('\\');
        string leaf = separator < 0 ? value : value[(separator + 1)..];
        if (leaf.Length == 0)
            throw new ArgumentException("The selected directory has no display root.", nameof(value));
        return leaf;
    }
}
