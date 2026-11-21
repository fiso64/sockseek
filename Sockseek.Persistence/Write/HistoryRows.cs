using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sockseek.Persistence.Sqlite;
using Sockseek.Persistence.Entities;

namespace Sockseek.Persistence.Write;

/// <summary>
/// Transaction-local history rows, including known-absent identities. Ordinary
/// history writes do not enter EF's change tracker. The model still owns column
/// mappings and converters; compare scalars once and prepare each changed shape.
/// Tracking is reserved for retrying a failed bulk insert to identify its owner.
/// </summary>
internal sealed class HistoryRows<T> where T : class, IRevisionedHistoryRow
{
    private readonly SockseekDbContext context;
    private readonly bool tracked;
    private readonly HistoryRowMapping<T> shape;
    private readonly Dictionary<Guid, T> rows;
    private readonly Dictionary<Guid, object?[]> originals = [];
    private readonly int[] overwriteColumns;

    private HistoryRows(SockseekDbContext context, Dictionary<Guid, T> rows, bool tracked, int[] overwriteColumns)
    {
        this.context = context;
        this.rows = rows;
        this.tracked = tracked;
        this.overwriteColumns = overwriteColumns;
        shape = HistoryRowMapping<T>.For(context);
        if (!tracked)
            foreach (var (id, row) in rows) originals.Add(id, shape.Values(row));
    }

    public static async Task<HistoryRows<T>> LoadAsync(SockseekDbContext context,
        IReadOnlyCollection<Guid> ids, bool tracked, CancellationToken cancellationToken,
        params string[] overwrittenProperties)
    {
        var shape = HistoryRowMapping<T>.For(context);
        int[] overwrite = tracked ? [] : overwrittenProperties.Select(name =>
        {
            int index = Array.FindIndex(shape.Properties, property => property.Name == name);
            return index >= 0 ? index : throw new ArgumentException("Unknown history property.", nameof(overwrittenProperties));
        }).Distinct().ToArray();
        if (ids.Count == 0) return new(context, [], tracked, overwrite);
        // Read and write shapes share one owner: an omitted value is always
        // replaced when a newer row is accepted, including an explicit NULL.
        // Failed/replayed rows remain untouched. Exceptional tracked retries
        // load the complete model so EF retains its normal failure attribution.
        // Projection order and scalar readers come from the same model shape.
        string columns = string.Join(", ", shape.ColumnNames.Select((column, index) =>
            overwrite.Contains(index) ? $"NULL AS {column}" : column));
        string sql = $"SELECT {columns} FROM {shape.TableName} WHERE {shape.ColumnNames[shape.KeyIndex]} "
            + "IN (SELECT value FROM json_each($ids))";
        var parameter = new SqliteParameter("$ids", JsonSerializer.Serialize(
            ids.Select(id => id.ToString("D").ToUpperInvariant())));
        Dictionary<Guid, T> rows;
        if (tracked)
        {
            rows = await context.Set<T>().FromSqlRaw(sql, parameter).AsTracking()
                .ToDictionaryAsync(shape.Identity, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Keep EF's connection, command diagnostics and fault interceptors,
            // without compiling a LINQ query or invoking its materializer per row.
            await using var result = await SqliteReadQuery.OpenAsync(
                context, sql, [parameter], cancellationToken).ConfigureAwait(false);
            rows = [];
            while (await result.DbDataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                T row = shape.Read(result.DbDataReader);
                rows.Add(shape.Identity(row), row);
            }
        }
        return new(context, rows, tracked, overwrite);
    }

    public bool TryGetValue(Guid id, out T? row) => rows.TryGetValue(id, out row);

    public void Add(Guid id, T row)
    {
        rows.Add(id, row);
        if (tracked) context.Add(row);
    }

    public PreparedInsert? PrepareInsert()
    {
        if (tracked) return null;
        var added = rows.Where(pair => !originals.ContainsKey(pair.Key))
            .Select(pair => (pair.Key, Values: shape.Values(pair.Value))).ToArray();
        if (added.Length == 0) return null;

        // One statement permits forward/cyclic job references in the batch.
        // Column order, provider conversions and comparison snapshots share the
        // model owner; positional JSON needs no parallel entity/SQL field list.
        // Capture/encoding requires no transaction. The prepared insert retains
        // exactly those values for comparison after the SQL succeeds.
        return new(this, added, JsonSerializer.Serialize(added, shape.InsertJson));
    }

    public sealed class PreparedInsert(
        HistoryRows<T> owner, (Guid Key, object?[] Values)[] entries, string payload)
    {
        public async Task WriteAsync(CancellationToken cancellationToken)
        {
            await owner.context.Database.ExecuteSqlRawAsync(owner.shape.InsertBatchSql,
                [new SqliteParameter("$rows", payload)], cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries) owner.originals[entry.Key] = entry.Values;
        }
    }

    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        if (tracked) return; // The exceptional EF retry writes its own tracked rows.
        var pending = new Dictionary<string, ChangeGroup>(StringComparer.Ordinal);
        var shapes = pending.GetAlternateLookup<ReadOnlySpan<char>>();
        var columns = new char[shape.Properties.Length + 1];
        foreach (var (id, row) in rows)
        {
            bool insert = !originals.TryGetValue(id, out var before);
            // Mutation admission already guards every history change by revision.
            // This also covers jobs inserted by PreparedInsert: no second full
            // scalar snapshot/comparison is needed unless that row changed later.
            if (!insert && row.Revision == (long)before![shape.RevisionIndex]!)
                continue;
            object?[] values = shape.Values(row);
            columns[0] = insert ? 'I' : 'U';
            bool changed = false;
            for (int i = 0; i < values.Length; i++)
            {
                bool different = insert || !Equals(before![i], values[i]);
                columns[i + 1] = different ? '1' : '0';
                changed |= different;
            }
            if (!changed) continue;
            if (!insert)
                foreach (int column in overwriteColumns) columns[column + 1] = '1';

            // Most rows share a changed-column shape. The span lookup allocates
            // a key and binding list only for a new shape, independent of row count.
            // A complete pattern also avoids imposing a fixed property-count limit.
            if (!shapes.TryGetValue(columns, out var group))
            {
                group = new(insert, shape.AllColumns.Where(i => columns[i + 1] == '1').ToArray());
                shapes[columns] = group;
            }
            group.Rows.Add((row, values));
        }
        foreach (var group in pending.Values)
        {
            if (Activity.Current is { IsAllDataRequested: true } activity)
                activity.AddEvent(new("history.rows", tags: new ActivityTagsCollection
                {
                    ["table"] = shape.TableName,
                    ["operation"] = group.Insert ? "insert" : "update",
                    ["count"] = group.Rows.Count,
                    ["columns"] = string.Join(',', group.Columns.Select(index => shape.ColumnNames[index])),
                }));
            int[] bindings = group.Insert ? group.Columns : [.. group.Columns, shape.KeyIndex];
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            var transaction = (SqliteTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
            string Column(int i) => shape.ColumnNames[i];
            string sql = group.Insert
                ? $"INSERT INTO {shape.TableName} ({string.Join(", ", group.Columns.Select(Column))}) "
                    + $"VALUES ({string.Join(", ", group.Columns.Select((_, i) => "@p" + i))})"
                : $"UPDATE {shape.TableName} SET "
                    + string.Join(", ", group.Columns.Select((column, i) => $"{Column(column)} = @p{i}"))
                    + $" WHERE {Column(shape.KeyIndex)} = @p{group.Columns.Length}";
            var converters = bindings.Select(column => shape.Properties[column].GetRelationalTypeMapping().Converter).ToArray();
            using var statement = new SqliteWriteStatement(connection, transaction, sql);
            foreach (var change in group.Rows)
            {
                try
                {
                    for (int i = 0; i < bindings.Length; i++)
                    {
                        object? value = change.Values[bindings[i]];
                        value = converters[i] is { } converter ? converter.ConvertToProvider(value) : value;
                        statement.Bind(i, value);
                    }
                    statement.Execute(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Only a failed row enters EF, to reuse the existing exact
                    // mutation attribution. The transaction will be discarded.
                    throw new DbUpdateException("Could not persist history entry.", ex, [context.Entry(change.Row)]);
                }
            }
        }
    }

    private sealed class ChangeGroup(bool insert, int[] columns)
    {
        public bool Insert { get; } = insert;
        public int[] Columns { get; } = columns;
        public List<(T Row, object?[] Values)> Rows { get; } = [];
    }
}
