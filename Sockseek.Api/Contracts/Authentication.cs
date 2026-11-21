namespace Sockseek.Api;

public sealed record LoginRequestDto(string Username, string Password);
public sealed record SessionDto(bool AuthenticationEnabled, bool Authenticated,
    string? Username, DateTimeOffset? ExpiresAt, string AntiforgeryToken);
public sealed record ApiKeyDto(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record CreateApiKeyRequestDto(string Name);
public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret);
