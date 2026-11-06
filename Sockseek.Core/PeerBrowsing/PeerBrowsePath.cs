using Sockseek.Core.Models;

namespace Sockseek.Core.PeerBrowsing;

/// <summary>
/// Shared browse-wire path parsing for disk-backed daemon artifacts and the
/// one-shot directory fallback. Display normalization is deliberately separate.
/// </summary>
public static class PeerBrowsePath
{
    public static string NormalizeDirectoryIdentity(string wirePath)
    {
        string normalized = wirePath.Replace('/', '\\');
        if (normalized.Length == 0 || normalized[0] == '\\')
            throw Invalid("relative directory path");
        string[] components = normalized.Split('\\', StringSplitOptions.None);
        if (components.Any(static component => component.Length == 0 || component is "." or ".."))
            throw Invalid("directory path component");
        foreach (string component in components)
            PeerIdentityValidator.ValidateRemotePath(component);
        return string.Join('\\', components);
    }

    public static PeerBrowseFilePath ResolveFile(
        string directoryIdentity,
        string directoryWirePath,
        string fileWireName)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryIdentity);
        ArgumentException.ThrowIfNullOrEmpty(directoryWirePath);
        if (string.IsNullOrEmpty(fileWireName))
            throw Invalid("filename");

        string normalizedFilename = fileWireName.Replace('/', '\\');
        string identityFilename;
        string wireFilename;
        if (normalizedFilename.Contains('\\'))
        {
            string prefix = directoryIdentity + "\\";
            if (!normalizedFilename.StartsWith(prefix, StringComparison.Ordinal)
                || normalizedFilename[prefix.Length..].Contains('\\'))
            {
                throw Invalid("filename outside its declared directory");
            }
            identityFilename = normalizedFilename;
            wireFilename = fileWireName;
        }
        else
        {
            identityFilename = directoryIdentity + "\\" + normalizedFilename;
            wireFilename = directoryWirePath + "\\" + fileWireName;
        }

        PeerIdentityValidator.ValidateRemotePath(wireFilename);
        string validatedIdentity = PeerIdentityValidator.ValidateRemotePath(identityFilename);
        string leaf = validatedIdentity[(validatedIdentity.LastIndexOf('\\') + 1)..];
        return new PeerBrowseFilePath(validatedIdentity, wireFilename, leaf);
    }

    public static bool IsSameOrDescendant(string candidate, string root)
        => StringComparer.Ordinal.Equals(candidate, root)
           || IsDescendant(candidate, root);

    public static bool IsDescendant(string candidate, string root)
        => candidate.Length > root.Length
           && candidate.StartsWith(root, StringComparison.Ordinal)
           && candidate[root.Length] == '\\';

    public static string ToDisplayPath(string identityPath)
        => string.Join('\\', identityPath.Split('\\').Select(PeerIdentityValidator.ToDisplayText));

    public static string Leaf(string identityPath)
    {
        int separator = identityPath.LastIndexOf('\\');
        return separator < 0 ? identityPath : identityPath[(separator + 1)..];
    }

    private static PeerBrowseProtocolException Invalid(string detail)
        => new($"The peer returned an invalid browse response: invalid {detail}.");
}

public readonly record struct PeerBrowseFilePath(
    string IdentityFilename,
    string WireFilename,
    string LeafName);
