using Microsoft.Data.Sqlite;
using System.Text;
using SQLitePCL;

namespace Sockseek.Persistence.Sqlite;

/// <summary>
/// A prepared row writer on its owner's existing transaction. Binding installs
/// SQLite scalar values immediately; reset retains them, so callers bind batch
/// constants once and changing fields for each row. No provider parameter objects,
/// per-row binder allocations or speculative value comparisons are needed.
/// Stores own conversions and ordering; transaction policy owns lock retries.
/// </summary>
internal sealed class SqliteWriteStatement : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly sqlite3_stmt statement;
    private bool[]? unbound;
    private bool disposed;

    public SqliteWriteStatement(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        this.connection = connection;
        this.transaction = transaction;
        RequireTransaction();
        Check(raw.sqlite3_prepare_v2(connection.Handle, sql, out statement, out string tail));
        if (statement == null || !string.IsNullOrWhiteSpace(tail))
        {
            statement?.Dispose();
            throw new InvalidOperationException("A prepared row write must contain exactly one SQL statement.");
        }
        unbound = new bool[raw.sqlite3_bind_parameter_count(statement)];
        Array.Fill(unbound, true);
    }

    public long LastInsertRowId => raw.sqlite3_last_insert_rowid(connection.Handle);

    public void Bind(string name, object? value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int index = raw.sqlite3_bind_parameter_index(statement, name);
        if (index == 0) throw new ArgumentException("A write parameter is missing from its statement.", nameof(name));
        Bind(index - 1, value);
    }

    // Ordinals follow the SQL parameter order and are zero-based. Named bindings
    // are convenient for sparse changes; consecutive row values avoid name lookups.
    public void Bind(int ordinal, object? value)
    {
        int index = ParameterIndex(ordinal);
        Bound(ordinal, value switch
        {
            null or DBNull => raw.sqlite3_bind_null(statement, index),
            int number => raw.sqlite3_bind_int(statement, index, number),
            long number => raw.sqlite3_bind_int64(statement, index, number),
            double number when !double.IsNaN(number) => raw.sqlite3_bind_double(statement, index, number),
            string text => raw.sqlite3_bind_text(statement, index, text),
            Guid identity => BindGuid(index, identity),
            byte[] { Length: 0 } => raw.sqlite3_bind_zeroblob(statement, index, 0),
            byte[] bytes => raw.sqlite3_bind_blob(statement, index, bytes),
            _ => throw new InvalidOperationException("A prepared write requires a converted SQLite scalar."),
        });
    }

    // Static row fields use typed bindings: routing every integer through an
    // object span boxes it once per row. Dynamic model-converted values can
    // still use the scalar/object binding above.
    public void Bind(int ordinal, long value)
    {
        int index = ParameterIndex(ordinal);
        Bound(ordinal, raw.sqlite3_bind_int64(statement, index, value));
    }

    public void Bind(int ordinal, long? value)
    {
        int index = ParameterIndex(ordinal);
        Bound(ordinal, value.HasValue ? raw.sqlite3_bind_int64(statement, index, value.Value)
            : raw.sqlite3_bind_null(statement, index));
    }

    public void Bind(int ordinal, Guid value)
    {
        int index = ParameterIndex(ordinal);
        Bound(ordinal, BindGuid(index, value));
    }

    // Prepared JSON already owns UTF-8. Keep it as SQLite TEXT without creating
    // a UTF-16 copy or relying on JSON accepting text stored as a BLOB.
    public void BindUtf8Text(int ordinal, ReadOnlySpan<byte> value)
    {
        int index = ParameterIndex(ordinal);
        Bound(ordinal, raw.sqlite3_bind_text(statement, index, value));
    }

    private int ParameterIndex(int ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return checked(ordinal + 1);
    }

    private void Bound(int ordinal, int result)
    {
        Check(result);
        if (unbound != null) unbound[ordinal] = false;
    }

    public void BindValues(ReadOnlySpan<object?> values, int offset = 0)
    {
        for (int i = 0; i < values.Length; i++) Bind(offset + i, values[i]);
    }

    private void RequireTransaction()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (transaction.Connection != connection || raw.sqlite3_get_autocommit(connection.Handle) != 0)
            throw new InvalidOperationException("A prepared write requires its owner's active transaction.");
    }

    private void Validate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireTransaction();
        if (unbound != null)
        {
            if (unbound.Contains(true)) throw new InvalidOperationException("A write statement has unbound parameters.");
            unbound = null;
        }
    }

    private int BindGuid(int index, Guid value)
    {
        // Match Microsoft.Data.Sqlite's TEXT representation without temporary
        // UTF-16 strings. SQLitePCL's span overload copies the UTF-8 binding.
        Span<byte> text = stackalloc byte[36];
        value.TryFormat(text, out _, "D");
        Ascii.ToUpperInPlace(text, out _);
        return raw.sqlite3_bind_text(statement, index, text);
    }

    public int Execute(CancellationToken cancellationToken)
    {
        Validate(cancellationToken);
        try
        {
            int result = raw.sqlite3_step(statement);
            if (result != raw.SQLITE_DONE) Check(result);
            if (result != raw.SQLITE_DONE) throw new InvalidOperationException("A row write unexpectedly returned data.");
            return raw.sqlite3_changes(connection.Handle);
        }
        finally
        {
            raw.sqlite3_reset(statement);
        }
    }

    // Consume an integer RETURNING column without allocating provider rows or
    // retaining a returned value for every inserted entry. The caller owns the
    // meaning of the integer; this only counts rows and sums their first column.
    public (int Count, long Sum) ExecuteSum(CancellationToken cancellationToken)
    {
        Validate(cancellationToken);
        try
        {
            int count = 0;
            long sum = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int result = raw.sqlite3_step(statement);
                if (result == raw.SQLITE_DONE) return (count, sum);
                if (result != raw.SQLITE_ROW) Check(result);
                count = checked(count + 1);
                sum = checked(sum + raw.sqlite3_column_int64(statement, 0));
            }
        }
        finally
        {
            raw.sqlite3_reset(statement);
        }
    }

    public string? ExecuteScalarText(CancellationToken cancellationToken)
    {
        Validate(cancellationToken);
        try
        {
            int result = raw.sqlite3_step(statement);
            if (result == raw.SQLITE_DONE) return null;
            if (result != raw.SQLITE_ROW) Check(result);
            string? value = raw.sqlite3_column_type(statement, 0) == raw.SQLITE_NULL
                ? null : raw.sqlite3_column_text(statement, 0).utf8_to_string();
            result = raw.sqlite3_step(statement);
            if (result != raw.SQLITE_DONE) Check(result);
            if (result != raw.SQLITE_DONE) throw new InvalidOperationException("A scalar write returned multiple rows.");
            return value;
        }
        finally
        {
            raw.sqlite3_reset(statement);
        }
    }

    private void Check(int result)
    {
        if (result == raw.SQLITE_OK) return;
        var database = connection.Handle;
        throw new SqliteException($"SQLite Error {result}: '{raw.sqlite3_errmsg(database).utf8_to_string()}'.",
            result & 0xff, raw.sqlite3_extended_errcode(database));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        statement.Dispose();
    }
}
