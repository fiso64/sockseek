using Microsoft.EntityFrameworkCore;
using Sockseek.Core.Sharing;
using Sockseek.Persistence.Entities;
using Sockseek.Persistence.Write;

namespace Sockseek.Persistence.PeerRestrictions;

public sealed record StoredPeerRestrictionOverride(
    PeerRestrictionKind Kind,
    string Username,
    PeerUsernameRestrictionOverride Value,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Logical owner of exact-username restriction overrides. Its table, migration,
/// connection policy, backup, integrity, and health belong to the main
/// persistence host; this class owns only restriction read/write semantics.
/// </summary>
public sealed class PeerRestrictionOverrideStore(
    IDbContextFactory<SockseekDbContext> contextFactory,
    PersistenceHealth health,
    PersistenceInbox writes,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<StoredPeerRestrictionOverride>> ReadAllAsync(
        CancellationToken cancellationToken = default)
        => await ObserveAsync(async () =>
        {
            await using SockseekDbContext context = await contextFactory
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            PeerRestrictionOverrideEntity[] rows = await context.PeerRestrictionOverrides
                .AsNoTracking()
                .OrderBy(row => row.RestrictionKind)
                .ThenBy(row => row.Username)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(static row => new StoredPeerRestrictionOverride(
                ParseKind(row.RestrictionKind),
                row.Username,
                ParseOverride(row.OverrideState),
                DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc))).ToArray();
        }).ConfigureAwait(false);

    public async Task SetAsync(
        PeerRestrictionKind kind,
        string username,
        PeerUsernameRestrictionOverride? value,
        CancellationToken cancellationToken = default)
        => await writes.ExecuteAsync(async (context, ct) =>
        {
            string kindValue = Kind(kind);
            PeerRestrictionOverrideEntity? row = await context.PeerRestrictionOverrides
                .SingleOrDefaultAsync(
                    candidate => candidate.RestrictionKind == kindValue
                                 && candidate.Username == username,
                    ct).ConfigureAwait(false);
            if (value is null)
            {
                if (row is not null)
                    context.PeerRestrictionOverrides.Remove(row);
            }
            else if (row is null)
            {
                context.PeerRestrictionOverrides.Add(new PeerRestrictionOverrideEntity
                {
                    RestrictionKind = kindValue,
                    Username = username,
                    OverrideState = Override(value.Value),
                    UpdatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds(),
                });
            }
            else
            {
                row.OverrideState = Override(value.Value);
                row.UpdatedAtUtc = clock.GetUtcNow().ToUnixTimeMilliseconds();
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private async Task<TResult> ObserveAsync<TResult>(Func<Task<TResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            health.RecordOperationalFailure(clock.GetUtcNow(), exception);
            throw;
        }
    }

    private static string Kind(PeerRestrictionKind value)
        => value switch
        {
            PeerRestrictionKind.UploadAccess => "UploadAccess",
            PeerRestrictionKind.PrivateMessages => "PrivateMessages",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static PeerRestrictionKind ParseKind(string value)
        => value switch
        {
            "UploadAccess" => PeerRestrictionKind.UploadAccess,
            "PrivateMessages" => PeerRestrictionKind.PrivateMessages,
            _ => throw new InvalidDataException("Persistence contains an unknown peer restriction kind."),
        };

    private static string Override(PeerUsernameRestrictionOverride value)
        => value switch
        {
            PeerUsernameRestrictionOverride.Blocked => "Blocked",
            PeerUsernameRestrictionOverride.Allowed => "Allowed",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static PeerUsernameRestrictionOverride ParseOverride(string value)
        => value switch
        {
            "Blocked" => PeerUsernameRestrictionOverride.Blocked,
            "Allowed" => PeerUsernameRestrictionOverride.Allowed,
            _ => throw new InvalidDataException("Persistence contains an unknown peer restriction override."),
        };
}
