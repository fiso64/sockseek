using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;

namespace Sockseek.Server.Authentication;

public sealed class AuthenticationOptions
{
    // Standalone hosting uses ASP.NET configuration; expose the same public spellings as the CLI.
    internal static string[] MapArguments(string[] args, out string[]? trustedProxies)
    {
        trustedProxies = null;
        var mapped = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string[] parts = args[i].Split('=', 2);
            string? member = parts[0] switch
            {
                "--web-username" => nameof(Username), "--web-password-file" => nameof(PasswordFile),
                "--web-password-hash" => nameof(PasswordHash), "--web-session-lifetime" => nameof(SessionLifetime),
                "--trusted-proxies" => nameof(TrustedProxies), "--no-auth" => nameof(Enabled),
                "--allow-http-auth" => nameof(AllowHttp), _ => null,
            };
            if (member is null) { mapped.Add(args[i]); continue; }
            bool boolean = member is nameof(Enabled) or nameof(AllowHttp);
            string value;
            if (parts.Length == 2) value = parts[1];
            else if (i + 1 < args.Length && (!boolean || bool.TryParse(args[i + 1], out _))) value = args[++i];
            else if (boolean) value = "true";
            else throw new ArgumentException($"{parts[0]} requires a value.");
            if (member == nameof(Enabled))
            {
                if (!bool.TryParse(value, out bool disabled)) throw new ArgumentException("--no-auth requires a boolean.");
                value = (!disabled).ToString();
            }
            if (member == nameof(TrustedProxies))
            {
                // Indexed configuration merges arrays across providers. A CLI list must replace
                // every configured address, including when the operator explicitly clears it.
                trustedProxies = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
            else mapped.Add($"--SockseekServer:Authentication:{member}={value}");
        }
        return mapped.ToArray();
    }

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
