namespace Sockseek.Core.Models;

/// <summary>
/// Exact, non-normalizing in-memory identity for a peer-owned remote file or directory.
/// </summary>
internal readonly record struct PeerPathKey(string Username, string RemotePath);
