using System.Security.Cryptography;
using System.Text.Json;

namespace Sockseek.Server;

internal static class AuthenticatedCursorCodec
{
    private const int SignatureLength = 32;
    private const int MaximumEncodedLength = 4096;

    public static string Encode<T>(T payload, byte[] key)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] signature = HMACSHA256.HashData(key, body);
        byte[] signed = new byte[body.Length + signature.Length];
        body.CopyTo(signed, 0);
        signature.CopyTo(signed, body.Length);
        return Convert.ToBase64String(signed)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static T Decode<T>(string value, byte[] key, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEncodedLength)
            throw Invalid(resourceName);
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            byte[] signed = Convert.FromBase64String(padded);
            if (signed.Length <= SignatureLength)
                throw Invalid(resourceName);
            ReadOnlySpan<byte> body = signed.AsSpan(0, signed.Length - SignatureLength);
            ReadOnlySpan<byte> signature = signed.AsSpan(signed.Length - SignatureLength);
            if (!CryptographicOperations.FixedTimeEquals(
                    signature,
                    HMACSHA256.HashData(key, body)))
            {
                throw Invalid(resourceName);
            }
            return JsonSerializer.Deserialize<T>(body)
                ?? throw Invalid(resourceName);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw Invalid(resourceName, exception);
        }
    }

    private static ArgumentException Invalid(string resourceName, Exception? inner = null)
        => new($"The {resourceName} cursor is invalid.", "cursor", inner);
}
