using System.Collections.Frozen;
using System.Net;
using Sockseek.Core.Models;
using Sockseek.Core.Settings;

namespace Sockseek.Core.Sharing;

public static class PeerUsername
{
    /// <summary>
    /// Validates a Soulseek username while retaining the exact spelling supplied
    /// by the protocol or caller. Soulseek.NET receives this same value.
    /// </summary>
    public static string Validate(string username)
        => PeerIdentityValidator.ValidateUsername(username);
}

public enum PeerRestrictionKind
{
    UploadAccess,
    PrivateMessages,
}

public enum PeerUsernameRestrictionOverride
{
    Blocked,
    Allowed,
}

public sealed record UsernameRestrictionSnapshot(
    IReadOnlySet<string> ConfiguredBlockedUsernames,
    IReadOnlyDictionary<string, PeerUsernameRestrictionOverride> UsernameOverrides,
    int BlockedUsernameCount)
{
    public bool HasBlockedUsernames => BlockedUsernameCount > 0;

    public bool IsBlocked(string username)
        => UsernameOverrides.TryGetValue(username, out PeerUsernameRestrictionOverride value)
            ? value == PeerUsernameRestrictionOverride.Blocked
            : ConfiguredBlockedUsernames.Contains(username);
}

public sealed record PeerRestrictionSnapshot(
    UsernameRestrictionSnapshot UploadAccess,
    IReadOnlySet<IPAddress> ConfiguredUploadBlockedIpAddresses,
    UsernameRestrictionSnapshot PrivateMessages,
    bool HasUploadBlockedIpAddresses)
{
    public bool IsUploadIpAddressBlocked(IPAddress address)
        => ConfiguredUploadBlockedIpAddresses.Contains(
            PeerRestrictionPolicy.NormalizeIpAddress(address));

    public UsernameRestrictionSnapshot For(PeerRestrictionKind kind)
        => kind switch
        {
            PeerRestrictionKind.UploadAccess => UploadAccess,
            PeerRestrictionKind.PrivateMessages => PrivateMessages,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

/// <summary>
/// One atomic owner for two intentionally independent inbound restrictions.
/// Reloadable configuration and persisted exact-username overrides publish one
/// immutable snapshot. Configured upload IP denial remains independent and
/// always wins when an endpoint is known.
/// </summary>
public sealed class PeerRestrictionPolicy
{
    private readonly object writeGate = new();
    private PeerRestrictionSnapshot snapshot;

    public PeerRestrictionPolicy(PeerRestrictionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        snapshot = CreateSnapshot(
            settings,
            EmptyOverrides(),
            EmptyOverrides());
    }

    public PeerRestrictionSnapshot Snapshot => Volatile.Read(ref snapshot);

    public bool IsUploadUsernameBlocked(string username)
    {
        username = PeerUsername.Validate(username);
        return Snapshot.UploadAccess.IsBlocked(username);
    }

    public bool IsUploadIpAddressBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return Snapshot.IsUploadIpAddressBlocked(address);
    }

    public bool IsUploadAccessBlocked(string username, IPEndPoint? endpoint)
    {
        username = PeerUsername.Validate(username);
        PeerRestrictionSnapshot current = Snapshot;
        return current.UploadAccess.IsBlocked(username)
            || endpoint is not null && current.IsUploadIpAddressBlocked(endpoint.Address);
    }

    public bool IsPrivateMessageBlocked(string username)
    {
        username = PeerUsername.Validate(username);
        return Snapshot.PrivateMessages.IsBlocked(username);
    }

    public void ReloadConfigured(PeerRestrictionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (writeGate)
        {
            PeerRestrictionSnapshot current = Snapshot;
            Volatile.Write(
                ref snapshot,
                CreateSnapshot(
                    settings,
                    current.UploadAccess.UsernameOverrides,
                    current.PrivateMessages.UsernameOverrides));
        }
    }

    public void ReplaceUsernameOverrides(
        IReadOnlyDictionary<(PeerRestrictionKind Kind, string Username),
            PeerUsernameRestrictionOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var upload = new Dictionary<string, PeerUsernameRestrictionOverride>(StringComparer.Ordinal);
        var messages = new Dictionary<string, PeerUsernameRestrictionOverride>(StringComparer.Ordinal);
        foreach (((PeerRestrictionKind kind, string username), PeerUsernameRestrictionOverride value) in overrides)
        {
            Dictionary<string, PeerUsernameRestrictionOverride> target = kind switch
            {
                PeerRestrictionKind.UploadAccess => upload,
                PeerRestrictionKind.PrivateMessages => messages,
                _ => throw new ArgumentOutOfRangeException(nameof(overrides)),
            };
            target.Add(PeerUsername.Validate(username), value);
        }
        lock (writeGate)
        {
            PeerRestrictionSnapshot current = Snapshot;
            Volatile.Write(ref snapshot, Publish(
                current.UploadAccess.ConfiguredBlockedUsernames,
                current.ConfiguredUploadBlockedIpAddresses,
                upload,
                current.PrivateMessages.ConfiguredBlockedUsernames,
                messages));
        }
    }

    public void SetUsernameOverride(
        PeerRestrictionKind kind,
        string username,
        PeerUsernameRestrictionOverride? value)
    {
        username = PeerUsername.Validate(username);
        lock (writeGate)
        {
            PeerRestrictionSnapshot current = Snapshot;
            var upload = new Dictionary<string, PeerUsernameRestrictionOverride>(
                current.UploadAccess.UsernameOverrides,
                StringComparer.Ordinal);
            var messages = new Dictionary<string, PeerUsernameRestrictionOverride>(
                current.PrivateMessages.UsernameOverrides,
                StringComparer.Ordinal);
            Dictionary<string, PeerUsernameRestrictionOverride> target = kind switch
            {
                PeerRestrictionKind.UploadAccess => upload,
                PeerRestrictionKind.PrivateMessages => messages,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            if (value == null)
                target.Remove(username);
            else
                target[username] = value.Value;
            Volatile.Write(ref snapshot, Publish(
                current.UploadAccess.ConfiguredBlockedUsernames,
                current.ConfiguredUploadBlockedIpAddresses,
                upload,
                current.PrivateMessages.ConfiguredBlockedUsernames,
                messages));
        }
    }

    private static PeerRestrictionSnapshot CreateSnapshot(
        PeerRestrictionSettings settings,
        IReadOnlyDictionary<string, PeerUsernameRestrictionOverride> uploadOverrides,
        IReadOnlyDictionary<string, PeerUsernameRestrictionOverride> privateMessageOverrides)
        => Publish(
            settings.UploadAccess.BlockedUsernames
                .Select(PeerUsername.Validate)
                .ToHashSet(StringComparer.Ordinal),
            settings.UploadAccess.BlockedIpAddresses
                .Select(ParseIpAddress)
                .ToHashSet(),
            uploadOverrides,
            settings.PrivateMessages.BlockedUsernames
                .Select(PeerUsername.Validate)
                .ToHashSet(StringComparer.Ordinal),
            privateMessageOverrides);

    private static PeerRestrictionSnapshot Publish(
        IReadOnlySet<string> configuredUploadUsernames,
        IReadOnlySet<IPAddress> configuredUploadIpAddresses,
        IReadOnlyDictionary<string, PeerUsernameRestrictionOverride> uploadOverrides,
        IReadOnlySet<string> configuredPrivateMessageUsernames,
        IReadOnlyDictionary<string, PeerUsernameRestrictionOverride> privateMessageOverrides)
        => new(
            PublishUsernames(configuredUploadUsernames, uploadOverrides),
            configuredUploadIpAddresses.ToFrozenSet(),
            PublishUsernames(configuredPrivateMessageUsernames, privateMessageOverrides),
            configuredUploadIpAddresses.Count > 0);

    private static UsernameRestrictionSnapshot PublishUsernames(
        IReadOnlySet<string> configured,
        IReadOnlyDictionary<string, PeerUsernameRestrictionOverride> overrides)
    {
        FrozenSet<string> configuredCopy = configured.ToFrozenSet(StringComparer.Ordinal);
        FrozenDictionary<string, PeerUsernameRestrictionOverride> overrideCopy = overrides
            .ToFrozenDictionary(StringComparer.Ordinal);
        int blockedCount = configuredCopy.Count(username =>
                overrideCopy.GetValueOrDefault(username) != PeerUsernameRestrictionOverride.Allowed)
            + overrideCopy.Count(pair =>
                pair.Value == PeerUsernameRestrictionOverride.Blocked
                && !configuredCopy.Contains(pair.Key));
        return new(configuredCopy, overrideCopy, blockedCount);
    }

    private static Dictionary<string, PeerUsernameRestrictionOverride> EmptyOverrides()
        => new(StringComparer.Ordinal);

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
