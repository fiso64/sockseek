using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sockseek.Api;
using Sockseek.Core.Settings;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.PeerRestrictions;
using Sockseek.Server.Persistence;

namespace Sockseek.Server.PeerRestrictions;

public sealed class PeerRestrictionPersistenceUnavailableException(
    string message,
    Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// One daemon owner for independent upload-access and private-message
/// restrictions. Persistence is committed through the main persistence
/// lifecycle before a new immutable policy snapshot becomes visible.
/// </summary>
public sealed class PeerRestrictionCoordinator(
    IOptions<ServerOptions> options,
    PersistenceCoordinator persistence,
    ILogger<PeerRestrictionCoordinator> logger,
    IOptionsMonitor<ServerOptions>? optionsMonitor = null) : IHostedService, IAsyncDisposable
{
    private PeerRestrictionOverrideStore? store;
    private Exception? initializationFailure;
    private IDisposable? reloadRegistration;
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public PeerRestrictionPolicy Policy { get; } = new(options.Value.Engine.PeerRestrictions);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        reloadRegistration = optionsMonitor?.OnChange(updated =>
        {
            try
            {
                Policy.ReloadConfigured(updated.Engine.PeerRestrictions);
                ServerLogMessages.PeerRestrictionsReloaded(logger);
            }
            catch (Exception exception)
            {
                ServerLogMessages.PeerRestrictionsReloadRejected(logger, exception);
            }
        });

        PeerRestrictionOverrideStore? candidate = persistence.PeerRestrictions;
        if (candidate is null)
        {
            initializationFailure = new InvalidOperationException(
                "The shared persistence runtime is disabled or unavailable.");
            ServerLogMessages.PeerRestrictionsUnavailable(logger);
            return;
        }

        try
        {
            IReadOnlyList<StoredPeerRestrictionOverride> stored = await candidate.ReadAllAsync(
                cancellationToken).ConfigureAwait(false);
            Policy.ReplaceUsernameOverrides(stored.ToDictionary(
                row => (row.Kind, row.Username),
                row => row.Value));
            store = candidate;
            ServerLogMessages.PeerRestrictionsInitialized(logger, stored.Count);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            initializationFailure = exception;
            ServerLogMessages.PeerRestrictionsInitializationFailed(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public UserRestrictionsDto Get(string username)
    {
        username = PeerUsername.Validate(username);
        PeerRestrictionSnapshot snapshot = Policy.Snapshot;
        return new(
            username,
            ToDto(snapshot.UploadAccess, username),
            ToDto(snapshot.PrivateMessages, username));
    }

    public async Task<UserRestrictionsDto> SetAsync(
        string username,
        UserRestrictionKind kind,
        UserRestrictionOverrideState? value,
        CancellationToken cancellationToken)
    {
        username = PeerUsername.Validate(username);
        PeerRestrictionKind coreKind = kind switch
        {
            UserRestrictionKind.UploadAccess => PeerRestrictionKind.UploadAccess,
            UserRestrictionKind.PrivateMessages => PeerRestrictionKind.PrivateMessages,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        PeerUsernameRestrictionOverride? coreValue = value switch
        {
            UserRestrictionOverrideState.Blocked => PeerUsernameRestrictionOverride.Blocked,
            UserRestrictionOverrideState.Allowed => PeerUsernameRestrictionOverride.Allowed,
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
        string operationId = Guid.NewGuid().ToString("N");
        var elapsed = Stopwatch.StartNew();
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PeerRestrictionOverrideStore repository = store
                ?? throw new PeerRestrictionPersistenceUnavailableException(
                    "Peer restriction overrides are unavailable; configured baselines remain active.",
                    initializationFailure);
            try
            {
                await repository.SetAsync(coreKind, username, coreValue, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ServerLogMessages.PeerRestrictionMutationFailed(
                    logger,
                    exception,
                    operationId,
                    SafeUserHash(username),
                    elapsed.ElapsedMilliseconds);
                throw new PeerRestrictionPersistenceUnavailableException(
                    "The peer restriction override was not applied because shared persistence failed.",
                    exception);
            }

            Policy.SetUsernameOverride(coreKind, username, coreValue);
            UserRestrictionsDto result = Get(username);
            UsernameRestrictionStateDto changed = kind == UserRestrictionKind.UploadAccess
                ? result.UploadAccess
                : result.PrivateMessages;
            ServerLogMessages.PeerRestrictionMutationApplied(
                logger,
                operationId,
                SafeUserHash(username),
                elapsed.ElapsedMilliseconds,
                kind.ToString(),
                value?.ToString() ?? "Configured",
                changed.IsBlocked);
            return result;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public void ReloadConfigured(PeerRestrictionSettings settings)
        => Policy.ReloadConfigured(settings);

    private static UsernameRestrictionStateDto ToDto(
        UsernameRestrictionSnapshot snapshot,
        string username)
    {
        snapshot.UsernameOverrides.TryGetValue(username, out PeerUsernameRestrictionOverride value);
        return new(
            snapshot.IsBlocked(username),
            snapshot.ConfiguredBlockedUsernames.Contains(username),
            snapshot.UsernameOverrides.ContainsKey(username) ? ToDto(value) : null);
    }

    private static UserRestrictionOverrideState ToDto(PeerUsernameRestrictionOverride value)
        => value switch
        {
            PeerUsernameRestrictionOverride.Blocked => UserRestrictionOverrideState.Blocked,
            PeerUsernameRestrictionOverride.Allowed => UserRestrictionOverrideState.Allowed,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static string SafeUserHash(string username)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username)))[..12];

    public ValueTask DisposeAsync()
    {
        reloadRegistration?.Dispose();
        reloadRegistration = null;
        store = null;
        mutationGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
