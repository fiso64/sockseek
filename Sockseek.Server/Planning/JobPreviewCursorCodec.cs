using System.Security.Cryptography;

namespace Sockseek.Server.Planning;

public sealed class JobPreviewCursorCodec
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public string Encode(Guid previewId, string? parentRef, long ordinal)
        => AuthenticatedCursorCodec.Encode(
            new CursorPayload(1, previewId, parentRef, ordinal),
            key);

    public long Decode(string cursor, Guid previewId, string? parentRef)
    {
        CursorPayload payload = AuthenticatedCursorCodec.Decode<CursorPayload>(
            cursor,
            key,
            "job-preview");
        if (payload.Version != 1
            || payload.PreviewId != previewId
            || !string.Equals(payload.ParentRef, parentRef, StringComparison.Ordinal)
            || payload.Ordinal < 0)
        {
            throw Invalid();
        }
        return payload.Ordinal;
    }

    private static ArgumentException Invalid(Exception? inner = null)
        => new("The job-preview cursor is invalid.", "cursor", inner);

    private sealed record CursorPayload(
        int Version,
        Guid PreviewId,
        string? ParentRef,
        long Ordinal);
}
