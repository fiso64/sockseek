using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sockseek.Persistence.Sqlite;

/// <summary>A consistent multi-query read without reserving SQLite's single writer slot.</summary>
internal sealed class SqliteReadSnapshot(
    SqliteTransaction transaction,
    IDbContextTransaction contextTransaction) : IAsyncDisposable
{
    public static async Task<SqliteReadSnapshot> BeginAsync(
        SockseekDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // The provider's default BEGIN IMMEDIATE blocks readers behind unrelated writes.
        // Deferred transactions establish the WAL snapshot at the first read instead.
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            var contextTransaction = await context.Database.UseTransactionAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
            return new(transaction, contextTransaction!);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public DbTransaction GetDbTransaction() => transaction;

    public Task CommitAsync(CancellationToken cancellationToken)
        => contextTransaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // UseTransaction does not take ownership of the supplied native transaction.
        try { await contextTransaction.DisposeAsync().ConfigureAwait(false); }
        finally { await transaction.DisposeAsync().ConfigureAwait(false); }
    }
}
