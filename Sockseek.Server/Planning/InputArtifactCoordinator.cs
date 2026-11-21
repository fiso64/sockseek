using System.Diagnostics;
using Sockseek.Api;
using Sockseek.Core.Planning;
using Sockseek.Persistence.Planning;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.Planning;

public sealed class InputArtifactUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public sealed class InputArtifactCoordinator(
    PersistenceCoordinator persistence,
    ILogger<InputArtifactCoordinator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        InputArtifactStore? candidate = persistence.InputArtifacts;
        if (candidate is null)
        {
            ServerLogMessages.InputArtifactUnavailable(logger);
            return;
        }
        try
        {
            await candidate.PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ServerLogMessages.InputArtifactUnavailable(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<InputArtifactDto> UploadAsync(
        Stream content,
        string? originalName,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        StoredInputArtifact artifact = await RequiredStore().CreateAsync(
            content,
            originalName,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ServerLogMessages.InputArtifactUploaded(
            logger,
            artifact.Id,
            started.ElapsedMilliseconds,
            artifact.Length);
        return ToDto(artifact);
    }

    public async Task<InputArtifactDto?> GetAsync(
        string artifactId,
        CancellationToken cancellationToken)
    {
        ResolvedInputArtifact? lease = await RequiredStore().ResolveAsync(
            artifactId,
            cancellationToken).ConfigureAwait(false);
        return lease == null ? null : ToDto(lease.Artifact);
    }

    internal Task<ResolvedInputArtifact?> ResolveAsync(
        string artifactId,
        CancellationToken cancellationToken)
        => RequiredStore().ResolveAsync(artifactId, cancellationToken);

    internal Task<IReadOnlyDictionary<string, ResolvedInputArtifact>> RetainAsync(
        IEnumerable<string> artifactIds,
        Guid ownerId,
        CancellationToken cancellationToken)
        => RequiredStore().RetainAsync(artifactIds, ownerId, cancellationToken);

    internal Task<int> ReleaseAsync(Guid ownerId, CancellationToken cancellationToken)
        => RequiredStore().ReleaseAsync(ownerId, cancellationToken);

    internal static SubmissionSourceRevision Revision(ResolvedInputArtifact lease)
        => new(
            "input-artifact",
            lease.Artifact.Id,
            lease.Artifact.Sha256,
            lease.Artifact.Length,
            lease.Artifact.CreatedAtUtc);

    private InputArtifactStore RequiredStore()
        => persistence.InputArtifacts ?? throw new InputArtifactUnavailableException(
            "Input artifacts are unavailable; ordinary local-path and text inputs remain available.");

    private static InputArtifactDto ToDto(StoredInputArtifact artifact)
        => new(
            artifact.Id,
            artifact.Sha256,
            artifact.Length,
            artifact.CreatedAtUtc,
            artifact.ExpiresAtUtc,
            artifact.OriginalName);

}
