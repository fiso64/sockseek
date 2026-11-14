namespace Sockseek.Core.Models;

/// <summary>A validated logical destination relative to an output parent.</summary>
public sealed record RelativeOutputPath
{
    public RelativeOutputPath(IReadOnlyList<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var owned = components.ToArray();
        if (owned.Length == 0)
            throw new ArgumentException("A relative output path must contain at least one component.", nameof(components));
        foreach (string component in owned)
            DirectoryTransferEntry.ValidateLogicalComponent(component, nameof(components));
        Components = Array.AsReadOnly(owned);
    }

    public IReadOnlyList<string> Components { get; }

    public string ToPlatformPath() => Path.Join(Components.ToArray());

    public static RelativeOutputPath FromRemoteFile(PeerFileTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string normalized = target.Filename.Replace('/', '\\').TrimEnd('\\');
        int separator = normalized.LastIndexOf('\\');
        string leaf = separator < 0 ? normalized : normalized[(separator + 1)..];
        return new RelativeOutputPath([leaf]);
    }
}
