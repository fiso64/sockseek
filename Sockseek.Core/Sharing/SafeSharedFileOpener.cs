namespace Sockseek.Core.Sharing;

public enum SharedFileOpenFailureReason
{
    InvalidRelativePath,
    OutsideRoot,
    LinkOrReparsePoint,
    NotRegularFile,
    MissingOrInaccessible,
    SizeChanged,
    LastWriteTimeChanged,
}

public sealed class SharedFileOpenException(
    SharedFileOpenFailureReason reason,
    string message,
    Exception? innerException = null)
    : IOException(message, innerException)
{
    public SharedFileOpenFailureReason Reason { get; } = reason;
}

public sealed record SharedFileFingerprint(
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc);

public sealed class OpenedSharedFile(
    FileStream stream,
    SharedFileFingerprint fingerprint) : IAsyncDisposable, IDisposable
{
    public FileStream Stream { get; } = stream;
    public SharedFileFingerprint Fingerprint { get; } = fingerprint;
    public void Dispose() => Stream.Dispose();
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Resolves a catalog-relative path through ordinary .NET filesystem APIs.
/// Exact catalog lookup and canonical containment are security boundaries;
/// native filesystem identity is optional hardening, not a support gate.
/// </summary>
public static class SafeSharedFileOpener
{
    public static OpenedSharedFile Open(
        string canonicalRoot,
        string relativePath,
        SharedFileFingerprint? expected = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string root = Path.GetFullPath(canonicalRoot);
        string candidate = ResolveCandidate(root, relativePath);
        FileStream? stream = null;
        try
        {
            EnsureNoLinksOrReparsePoints(root, candidate);
            FileAttributes attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw Failure(
                    SharedFileOpenFailureReason.NotRegularFile,
                    "Shared catalog entry is not a file.");
            }

            stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1_024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            // Ask the open handle for length, rather than trusting a path-only
            // FileInfo result. Last-write time is advisory and catches common
            // edits; a local mutation race is not a reason to reject a mount.
            var fingerprint = new SharedFileFingerprint(
                stream.Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(candidate), TimeSpan.Zero));
            if (expected is not null)
                ValidateExpected(fingerprint, expected);
            return new OpenedSharedFile(stream, fingerprint);
        }
        catch (SharedFileOpenException)
        {
            stream?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            stream?.Dispose();
            throw Failure(
                SharedFileOpenFailureReason.MissingOrInaccessible,
                "Shared file is missing or inaccessible.",
                ex);
        }
    }

    private static string ResolveCandidate(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw Failure(
                SharedFileOpenFailureReason.InvalidRelativePath,
                "Catalog relative path cannot be rooted.");
        }

        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Failure(
                SharedFileOpenFailureReason.InvalidRelativePath,
                "Catalog relative path contains an empty, '.' or '..' segment.");
        }

        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithin(candidate, root))
        {
            throw Failure(
                SharedFileOpenFailureReason.OutsideRoot,
                "Catalog relative path resolves outside its configured root.");
        }
        return candidate;
    }

    private static void EnsureNoLinksOrReparsePoints(string root, string candidate)
    {
        Check(root);
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }

        static void Check(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw Failure(
                    SharedFileOpenFailureReason.LinkOrReparsePoint,
                    "Shared paths cannot traverse links or reparse points.");
            }
        }
    }

    private static void ValidateExpected(
        SharedFileFingerprint actual,
        SharedFileFingerprint expected)
    {
        if (actual.SizeBytes != expected.SizeBytes)
        {
            throw Failure(
                SharedFileOpenFailureReason.SizeChanged,
                "Shared file size changed after catalog publication.");
        }
        if (actual.LastWriteTimeUtc != expected.LastWriteTimeUtc)
        {
            throw Failure(
                SharedFileOpenFailureReason.LastWriteTimeChanged,
                "Shared file modification time changed after catalog publication.");
        }
    }

    private static bool IsWithin(string candidate, string root)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative == "."
               || !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static SharedFileOpenException Failure(
        SharedFileOpenFailureReason reason,
        string message,
        Exception? innerException = null)
        => new(reason, message, innerException);
}
