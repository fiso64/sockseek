using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;

namespace Sockseek.Server.Authentication;

public sealed class AuthenticationOptions
{
    public bool Enabled { get; set; } = true;
    public string Username { get; set; } = "admin";
    public string? PasswordFile { get; set; }
    public string? PasswordHash { get; set; }
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);
    public string[] TrustedProxies { get; set; } = ["127.0.0.1", "::1"];
    /// <summary>Explicitly permit unencrypted HTTP, including insecure cookies, for development/private networks.</summary>
    public bool AllowHttp { get; set; }

    internal string? PreparePassword()
    {
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(Username) || Username.Length > 200)
            throw new ArgumentException("The WebUI username must contain between 1 and 200 characters.");
        if (SessionLifetime <= TimeSpan.Zero || SessionLifetime > TimeSpan.FromDays(365))
            throw new ArgumentException("The WebUI session lifetime must be positive and at most 365 days.");
        if ((PasswordFile is null) == (PasswordHash is null))
            throw new ArgumentException("Configure exactly one WebUI password file or password hash, or explicitly disable authentication.");
        if (PasswordFile is not null)
            return HashPassword(ReadSecretFile(PasswordFile));
        try
        {
            byte[] bytes = Convert.FromBase64String(PasswordHash!);
            // Identity v3: marker, PRF, iteration count, salt length, salt, subkey.
            if (bytes.Length < 13 || bytes[0] != 1
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1)) > 2
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)) is 0 or > int.MaxValue)
                throw new FormatException();
            uint saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9));
            if (saltLength < 16 || saltLength > bytes.Length - 29)
                throw new FormatException();
            return PasswordHash;
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid WebUI password hash. Generate one with 'sockseek auth hash-password'.");
        }
    }

    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("The WebUI password must contain between 1 and 4096 characters.");
        return new PasswordHasher<string>().HashPassword("operator", password);
    }

    public static string ReadSecretFile(string path)
    {
        // Permit the one line terminator produced by shell/secret-file tools, preserving all other whitespace.
        string secret = File.ReadAllText(path);
        if (secret.EndsWith("\r\n", StringComparison.Ordinal)) secret = secret[..^2];
        else if (secret.EndsWith('\n')) secret = secret[..^1];
        if (secret.Length == 0) throw new ArgumentException("The credential file is empty.");
        return secret;
    }
}
