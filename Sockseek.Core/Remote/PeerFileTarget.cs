using System.Text.Json.Serialization;
using System.Buffers;
using System.Globalization;
using System.Text;
using Sockseek.Core.Snapshots;

namespace Sockseek.Core.Models;

// TODO [v4]: ToDisplayText is part of the validator? Actually, why do we even need this validator..

/// <summary>
/// Validates exact outbound peer identity without trimming, case folding, or
/// Unicode normalization.
/// </summary>
public static class PeerIdentityValidator
{
    public static string ValidateUsername(string username)
        => Validate(username, "Peer username", allowControls: false);

    public static string ValidateRemotePath(string remotePath)
        => Validate(remotePath, "Remote path", allowControls: true);

    /// <summary>
    /// Projects exact peer-supplied text to a single-line, terminal-safe display
    /// value without changing the wire identity retained by Sockseek.
    /// </summary>
    public static string ToDisplayText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? result = null;
        int offset = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            int length = rune.Utf16SequenceLength;
            bool malformed = rune == Rune.ReplacementChar && char.IsSurrogate(value[offset]);
            if (category is not (UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator) && !malformed)
            {
                result?.Append(value.AsSpan(offset, length));
                offset += length;
                continue;
            }

            // Most peer paths need no escaping. Allocate only at the first
            // unsafe scalar; appending original spans also avoids boxing Rune.
            result ??= new StringBuilder(value.Length).Append(value.AsSpan(0, offset));
            if (malformed)
                result.Append('\ufffd');
            else if (rune.Value <= 0x1f)
                result.Append((char)(0x2400 + rune.Value));
            else if (rune.Value == 0x7f)
                result.Append('\u2421');
            else
                result.Append(rune.Value <= 0xffff
                    ? $"<U+{rune.Value:X4}>"
                    : $"<U+{rune.Value:X8}>");
            offset += length;
        }
        return result?.ToString() ?? value;
    }

    private static string Validate(string value, string label, bool allowControls)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            throw Invalid($"{label} cannot be empty.");

        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
                throw Invalid($"{label} contains invalid Unicode.");
            if (!allowControls && Rune.IsControl(rune))
                throw Invalid($"{label} cannot contain control characters.");
            remaining = remaining[consumed..];
        }

        return value;
    }

    private static ArgumentException Invalid(string message)
        => new($"Input error: {message}");
}

/// <summary>The exact peer and wire filename used by a Soulseek download.</summary>
public sealed record PeerFileIdentity
{
    public PeerFileIdentity(string username, string filename)
    {
        Username = PeerIdentityValidator.ValidateUsername(username);
        Filename = PeerIdentityValidator.ValidateRemotePath(filename);
    }

    public string Username { get; }
    public string Filename { get; }
}

/// <summary>
/// Sockseek-owned metadata for one exact peer file. Search availability and
/// ranking evidence deliberately live outside this value. Immutable collections
/// make this same value safe to retain in execution snapshots and commands.
/// </summary>
public sealed record PeerFileTarget
{
    public PeerFileTarget(
        PeerFileIdentity identity,
        long? size,
        string? extension,
        int? bitRate = null,
        int? bitDepth = null,
        int? sampleRate = null,
        int? length = null,
        IReadOnlyList<FileAttributeSnapshot>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Known file size cannot be negative.");

        Identity = identity;
        Size = size;
        Extension = extension;
        BitRate = bitRate;
        BitDepth = bitDepth;
        SampleRate = sampleRate;
        Length = length;
        Attributes = attributes is null
            ? null
            : Array.AsReadOnly(attributes.ToArray());
    }

    public PeerFileIdentity Identity { get; }
    public long? Size { get; }
    public string? Extension { get; }
    public int? BitRate { get; }
    public int? BitDepth { get; }
    public int? SampleRate { get; }
    public int? Length { get; }
    public IReadOnlyList<FileAttributeSnapshot>? Attributes { get; }

    public PeerFileTarget WithSize(long size)
        => new(Identity, size, Extension, BitRate, BitDepth, SampleRate, Length, Attributes);

    [JsonIgnore]
    public string Username => Identity.Username;
    [JsonIgnore]
    public string Filename => Identity.Filename;
}

/// <summary>Response-time peer facts used by search presentation and ranking.</summary>
public sealed record SearchPeerSnapshot
{
    public SearchPeerSnapshot(
        string username,
        int responseFileCount,
        int? uploadSpeed,
        bool? hasFreeUploadSlot,
        int? queueLength = null,
        DateTimeOffset? observedAtUtc = null)
    {
        if (responseFileCount < 0)
            throw new ArgumentOutOfRangeException(nameof(responseFileCount));

        Username = PeerIdentityValidator.ValidateUsername(username);
        ResponseFileCount = responseFileCount;
        UploadSpeed = uploadSpeed;
        HasFreeUploadSlot = hasFreeUploadSlot;
        QueueLength = queueLength;
        ObservedAtUtc = observedAtUtc ?? DateTimeOffset.UnixEpoch;
    }

    public string Username { get; }
    public int ResponseFileCount { get; }
    public int? UploadSpeed { get; }
    public bool? HasFreeUploadSlot { get; }
    public int? QueueLength { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Search ordering/revision evidence that is not a remote-file fact.</summary>
public sealed record FileSearchEvidence(
    long Sequence,
    int Revision,
    DateTimeOffset ObservedAtUtc,
    SearchResultVisibility Visibility = SearchResultVisibility.Public)
{
    public static FileSearchEvidence Unspecified { get; } = new(0, 0, DateTimeOffset.UnixEpoch);
}
