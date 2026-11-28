using System.Collections.Frozen;
using System.Net;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

public static class PeerUsername
{
    /// <summary>Validate a Soulseek username without changing its exact wire spelling.</summary>
    public static string Validate(string username) => PeerIdentityValidator.ValidateUsername(username);
}

public sealed record PeerRestrictionSnapshot(
    IReadOnlySet<string> UploadBlockedUsernames,
    IReadOnlySet<IPAddress> UploadBlockedIpAddresses,
    IReadOnlySet<string> PrivateMessageBlockedUsernames)
{
    public bool HasUploadBlockedIpAddresses => UploadBlockedIpAddresses.Count > 0;
    public bool IsUploadIpAddressBlocked(IPAddress address)
        => UploadBlockedIpAddresses.Contains(PeerRestrictionPolicy.NormalizeIpAddress(address));
}

/// <summary>
/// Configuration is the sole restriction owner. Reloads atomically replace the
/// immutable snapshot; peer admission and message ingress only read frozen sets.
/// </summary>
public sealed class PeerRestrictionPolicy
{
    private PeerRestrictionSnapshot snapshot;

    public PeerRestrictionPolicy(PeerRestrictionSettings settings) => snapshot = CreateSnapshot(settings);
    public PeerRestrictionSnapshot Snapshot => Volatile.Read(ref snapshot);

    public bool IsUploadUsernameBlocked(string username)
        => Snapshot.UploadBlockedUsernames.Contains(PeerUsername.Validate(username));

    public bool IsUploadIpAddressBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Snapshot.IsUploadIpAddressBlocked(address);
    }

    public bool IsUploadAccessBlocked(string username, IPEndPoint? endpoint)
    {
        username = PeerUsername.Validate(username);
        PeerRestrictionSnapshot current = Snapshot;
        return current.UploadBlockedUsernames.Contains(username)
            || endpoint is not null && current.IsUploadIpAddressBlocked(endpoint.Address);
    }

    public bool IsPrivateMessageBlocked(string username)
        => Snapshot.PrivateMessageBlockedUsernames.Contains(PeerUsername.Validate(username));

    public void ReloadConfigured(PeerRestrictionSettings settings)
        => Volatile.Write(ref snapshot, CreateSnapshot(settings));

    private static PeerRestrictionSnapshot CreateSnapshot(PeerRestrictionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            settings.UploadAccess.BlockedUsernames.Select(PeerUsername.Validate).ToFrozenSet(StringComparer.Ordinal),
            settings.UploadAccess.BlockedIpAddresses.Select(ParseIpAddress).ToFrozenSet(),
            settings.PrivateMessages.BlockedUsernames.Select(PeerUsername.Validate).ToFrozenSet(StringComparer.Ordinal));
    }

    public static IPAddress ParseIpAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Contains('/') || !IPAddress.TryParse(value, out IPAddress? address))
        {
            throw new ArgumentException(
                $"Input error: Upload-blocked IP '{value}' must be one exact IPv4 or IPv6 address.");
        }
        return NormalizeIpAddress(address);
    }

    public static IPAddress NormalizeIpAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
