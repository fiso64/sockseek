using System.Buffers;
using System.Text;

namespace Sockseek.Core.Models;

/// <summary>One exact target and its logical directory path within a selection.</summary>
public sealed record DirectoryTransferEntry
{
    public DirectoryTransferEntry(
        PeerFileTarget target,
        IReadOnlyList<string> relativeDirectoryComponents)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(relativeDirectoryComponents);

        var owned = relativeDirectoryComponents.ToArray();
        foreach (string component in owned)
            ValidateLogicalComponent(component, nameof(relativeDirectoryComponents));

        Target = target;
        RelativeDirectoryComponents = Array.AsReadOnly(owned);
    }

    public PeerFileTarget Target { get; }
    public IReadOnlyList<string> RelativeDirectoryComponents { get; }

    internal static void ValidateLogicalComponent(string component, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Length == 0)
            throw new ArgumentException("Logical path components cannot be empty.", parameterName);
        if (component is "." or "..")
            throw new ArgumentException("Logical path components cannot be traversal markers.", parameterName);
        if (component.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(component))
            throw new ArgumentException("Logical path components cannot be rooted or contain separators.", parameterName);

        ReadOnlySpan<char> remaining = component.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out int consumed);
            if (status != OperationStatus.Done)
                throw new ArgumentException("Logical path components contain invalid Unicode.", parameterName);
            remaining = remaining[consumed..];
        }
    }
}

/// <summary>
/// An immutable, self-contained directory selection. It carries logical tree
/// intent but no final local placement or music semantics.
/// </summary>
public sealed record DirectoryTransferPlan
{
    public DirectoryTransferPlan(
        string displayRoot,
        IReadOnlyList<DirectoryTransferEntry> entries,
        bool representsDirectorySelection = true,
        PeerDirectoryIdentity? sourceDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(displayRoot);
        if (displayRoot.Length != 0 || representsDirectorySelection)
            DirectoryTransferEntry.ValidateLogicalComponent(displayRoot, nameof(displayRoot));
        ArgumentNullException.ThrowIfNull(entries);

        var source = entries.ToArray();
        if (source.Length == 0)
            throw new ArgumentException("A directory transfer plan must contain at least one entry.", nameof(entries));
        if (source.Any(entry => entry is null))
            throw new ArgumentException("Directory transfer entries cannot be null.", nameof(entries));

        string username = source[0].Target.Username;
        if (source.Any(entry => !StringComparer.Ordinal.Equals(entry.Target.Username, username)))
            throw new ArgumentException("A directory transfer plan cannot mix peers.", nameof(entries));
        if (sourceDirectory != null)
        {
            if (!representsDirectorySelection
                || !StringComparer.Ordinal.Equals(sourceDirectory.Username, username))
            {
                throw new ArgumentException(
                    "A source directory must identify this directory selection's exact peer.",
                    nameof(sourceDirectory));
            }
            string prefix = sourceDirectory.FolderPath.Replace('/', '\\').TrimEnd('\\') + "\\";
            if (source.Any(entry => !entry.Target.Filename.Replace('/', '\\')
                    .StartsWith(prefix, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Every target must belong to the exact source directory.",
                    nameof(sourceDirectory));
            }
        }

        source = source
            .DistinctBy(entry => entry.Target.Identity)
            .ToArray();
        Array.Sort(source, DirectoryTransferEntryComparer.Instance);
        long knownBytes = 0;
        int unknownFileSizeCount = 0;
        foreach (var entry in source)
        {
            knownBytes = SaturatingAdd(knownBytes, entry.Target.Size ?? 0);
            if (entry.Target.Size is null)
                unknownFileSizeCount++;
        }

        DisplayRoot = displayRoot;
        Entries = Array.AsReadOnly(source);
        Username = username;
        TotalKnownBytes = knownBytes;
        UnknownFileSizeCount = unknownFileSizeCount;
        RepresentsDirectorySelection = representsDirectorySelection;
        SourceDirectory = sourceDirectory;
    }

    /// <summary>Placement root; empty only for independent files placed directly under the output parent.</summary>
    public string DisplayRoot { get; }
    public string Username { get; }
    public IReadOnlyList<DirectoryTransferEntry> Entries { get; }
    public long TotalKnownBytes { get; }
    public int UnknownFileSizeCount { get; }
    /// <summary>False when independently chosen files only share a placement root.</summary>
    public bool RepresentsDirectorySelection { get; }
    /// <summary>Exact source only when this plan was built from one known peer directory.</summary>
    public PeerDirectoryIdentity? SourceDirectory { get; }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private sealed class DirectoryTransferEntryComparer : IComparer<DirectoryTransferEntry>
    {
        public static DirectoryTransferEntryComparer Instance { get; } = new();

        public int Compare(DirectoryTransferEntry? left, DirectoryTransferEntry? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            int shared = Math.Min(
                left.RelativeDirectoryComponents.Count,
                right.RelativeDirectoryComponents.Count);
            for (int index = 0; index < shared; index++)
            {
                int component = StringComparer.Ordinal.Compare(
                    left.RelativeDirectoryComponents[index],
                    right.RelativeDirectoryComponents[index]);
                if (component != 0)
                    return component;
            }

            int count = left.RelativeDirectoryComponents.Count.CompareTo(
                right.RelativeDirectoryComponents.Count);
            return count != 0
                ? count
                : StringComparer.Ordinal.Compare(left.Target.Filename, right.Target.Filename);
        }
    }
}
