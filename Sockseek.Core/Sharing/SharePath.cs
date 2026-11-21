using System.Buffers;
using System.Text;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

/// <summary>
/// Sockseek's platform-independent identity for a Soulseek remote
/// path. Display spelling is kept separately by catalog records.
/// </summary>
public sealed class RemotePathKey : IEquatable<RemotePathKey>
{
    private readonly byte[] bytes;
    private readonly int hashCode;

    private RemotePathKey(byte[] bytes)
    {
        this.bytes = bytes;

        var hash = new HashCode();
        foreach (var value in bytes)
            hash.Add(value);
        hashCode = hash.ToHashCode();
    }

    public static RemotePathKey Create(string remotePath)
    {
        ArgumentNullException.ThrowIfNull(remotePath);

        if (remotePath.Length == 0)
            throw Invalid("Remote path cannot be empty.");
        if (remotePath[0] is '\\' or '/')
            throw Invalid("Remote path cannot be rooted.");

        var wirePath = remotePath.Replace('/', '\\');
        var rawSegments = wirePath.Split('\\', StringSplitOptions.None);

        if (rawSegments.Any(static segment => segment.Length == 0))
            throw Invalid("Remote path cannot contain empty segments.");

        foreach (string segment in rawSegments)
            ValidateSegment(segment);

        return new RemotePathKey(Encoding.UTF8.GetBytes(wirePath));
    }

    public static RemotePathKey CreateAlias(string alias)
    {
        ValidateAlias(alias);
        return Create(alias);
    }

    public static void ValidateAlias(string alias)
    {
        ArgumentNullException.ThrowIfNull(alias);
        if (alias.Length == 0)
            throw Invalid("Share alias cannot be empty.");
        if (alias is "." or "..")
            throw Invalid("Share alias cannot be '.' or '..'.");
        if (alias.IndexOfAny(['/', '\\']) >= 0)
            throw Invalid("Share alias cannot contain a path separator.");

        ValidateWellFormedUnicode(alias, "Share alias");
        if (alias.EnumerateRunes().Any(static rune => Rune.IsControl(rune)))
            throw Invalid("Share alias cannot contain control characters.");
    }

    public byte[] ToArray() => [.. bytes];

    public bool Equals(RemotePathKey? other)
        => other is not null && bytes.AsSpan().SequenceEqual(other.bytes);

    public override bool Equals(object? obj) => Equals(obj as RemotePathKey);

    public override int GetHashCode() => hashCode;

    public override string ToString() => Convert.ToHexString(bytes);

    private static void ValidateSegment(string segment)
    {
        ValidateWellFormedUnicode(segment, "Remote path segment");
        if (segment is "." or "..")
            throw Invalid("Remote path cannot contain '.' or '..' segments.");
        if (segment.Contains('\0'))
            throw Invalid("Remote path cannot contain NUL characters.");
    }

    private static void ValidateWellFormedUnicode(string value, string label)
    {
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out int consumed);
            if (status != OperationStatus.Done)
                throw Invalid($"{label} contains invalid Unicode.");
            remaining = remaining[consumed..];
        }
    }

    private static ArgumentException Invalid(string message)
        => new($"Input error: {message}");
}

/// <summary>
/// Parses the public <c>[Alias]path</c> and alias-less <c>path</c> share syntax.
/// Filesystem canonicalization is performed later with the configuration path
/// variable context.
/// </summary>
public static class ShareRootParser
{
    public static ShareRootSettings Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0)
            throw new ArgumentException("Input error: Share root cannot be empty.");

        string? alias = null;
        string localPath = value;

        if (value[0] == '[')
        {
            int closingBracket = value.IndexOf(']');
            if (closingBracket < 0)
                throw new ArgumentException("Input error: Explicit share alias is missing ']'.");

            alias = value[1..closingBracket].Trim();
            localPath = value[(closingBracket + 1)..].Trim();
            RemotePathKey.ValidateAlias(alias);
        }

        if (localPath.Length == 0)
            throw new ArgumentException("Input error: Share root path cannot be empty.");

        return new ShareRootSettings
        {
            LocalPath = localPath,
            Alias = alias,
        };
    }
}
