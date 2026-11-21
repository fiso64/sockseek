using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sockseek.Persistence.Sqlite;

/// <summary>Execute bounded SQL with the context's connection, diagnostics and fault interceptors.</summary>
internal static class SqliteReadQuery
{
    public static Task<RelationalDataReader> OpenAsync(SockseekDbContext context,
        string sql, IEnumerable<object> parameters, CancellationToken cancellationToken)
    {
        var command = context.GetService<IRawSqlCommandBuilder>().Build(sql, parameters, context.Model);
        return command.RelationalCommand.ExecuteReaderAsync(new(
            context.GetService<IRelationalConnection>(), command.ParameterValues, null, context,
            context.GetService<IRelationalCommandDiagnosticsLogger>(), CommandSource.FromSqlQuery),
            cancellationToken);
    }

    public static async Task<List<T>> ReadAsync<T>(SockseekDbContext context,
        string sql, IEnumerable<object> parameters, Func<DbDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await using var result = await OpenAsync(context, sql, parameters, cancellationToken).ConfigureAwait(false);
        var rows = new List<T>();
        while (await result.DbDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(read(result.DbDataReader));
        return rows;
    }
}
