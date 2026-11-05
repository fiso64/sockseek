namespace Sockseek.Api;

public sealed record InputArtifactDto(
    string ArtifactId,
    string Sha256,
    long Length,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? OriginalName = null);
