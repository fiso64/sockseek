using System.Text.Json;

namespace Sockseek.Server;

/// <summary>
/// Bounded, opaque keyset cursor for the volatile upload queue. It is not an
/// authorization token: decoded values are validated and used only as query
/// parameters. A revision is carried solely to provide a best-effort change
/// hint between pages.
/// </summary>
public sealed class LiveTransferCursorCodec
{
    public string Encode(
        DateTimeOffset requestedAtUtc,
        Guid transferId,
        long observedQueueRevision)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(
            requestedAtUtc,
            transferId,
            observedQueueRevision));
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public LiveTransferCursor Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 512)
            throw new ArgumentException("The live transfer cursor is too long.", nameof(value));

        CursorPayload decoded;
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            decoded = JsonSerializer.Deserialize<CursorPayload>(
                          Convert.FromBase64String(padded))
                      ?? throw new JsonException("Cursor payload was empty.");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The live transfer cursor is malformed.", nameof(value), ex);
        }

        if (decoded.TransferId == Guid.Empty || decoded.ObservedQueueRevision < 0)
            throw new ArgumentException("The live transfer cursor is malformed.", nameof(value));
        return new LiveTransferCursor(
            decoded.RequestedAtUtc,
            decoded.TransferId,
            decoded.ObservedQueueRevision);
    }

    private sealed record CursorPayload(
        DateTimeOffset RequestedAtUtc,
        Guid TransferId,
        long ObservedQueueRevision);
}

public sealed record LiveTransferCursor(
    DateTimeOffset RequestedAtUtc,
    Guid TransferId,
    long ObservedQueueRevision);
