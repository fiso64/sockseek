using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sockseek.Persistence.Sqlite;

internal sealed class SqliteConnectionInterceptor(int busyTimeoutMilliseconds) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Configure(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Configure(connection);
        return Task.CompletedTask;
    }

    private void Configure(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout={busyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
    }
}
