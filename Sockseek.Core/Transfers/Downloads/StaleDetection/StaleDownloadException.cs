using Sockseek.Core.Models;

namespace Sockseek.Core.Services;

public sealed class StaleDownloadException : TimeoutException
{
    private const string MessagePrefix = "Download attempt became stale after ";

    public StaleDownloadException(PeerFileTarget target, int maxStaleTimeMs)
        : base(
            $"{MessagePrefix}{maxStaleTimeMs}ms without peer transfer activity: " +
            $"{target.Username}\\{PeerIdentityValidator.ToDisplayText(target.Filename)}")
    {
        Target = target;
        MaxStaleTimeMs = maxStaleTimeMs;
    }

    public StaleDownloadException(FileCandidate candidate, int maxStaleTimeMs)
        : this(candidate.Target, maxStaleTimeMs)
    {
    }

    public PeerFileTarget Target { get; }
    public int MaxStaleTimeMs { get; }

    public static bool IsStaleFailureMessage(string? message)
        => message?.StartsWith(MessagePrefix, StringComparison.Ordinal) == true;
}
