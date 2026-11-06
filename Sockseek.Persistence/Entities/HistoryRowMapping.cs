using System.Data.Common;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Entities;

/// <summary>
/// One model-owned scalar mapping for history reads and writes. Column order,
/// provider conversions and NULL handling cannot drift between them. Query
/// execution preserves EF diagnostics/interceptors without LINQ compilation.
/// </summary>
internal sealed class HistoryRowMapping<T> where T : class, IRevisionedHistoryRow
{
    private static readonly ConditionalWeakTable<IModel, HistoryRowMapping<T>> Mappings = new();
    public static HistoryRowMapping<T> For(SockseekDbContext context)
        => Mappings.GetValue(context.Model, model => new(model.FindEntityType(typeof(T))!));

    public string SelectColumns(string alias)
        => string.Join(", ", ColumnNames.Select(column => alias + "." + column));

    public Task<List<T>> ReadAsync(SockseekDbContext context, string sql,
        IEnumerable<object> parameters, CancellationToken cancellationToken)
        => SqliteReadQuery.ReadAsync(context, sql, parameters, Read, cancellationToken);

    public IProperty[] Properties { get; }
    public IProperty Key { get; }
    public int KeyIndex { get; }
    public int RevisionIndex { get; }
    public int[] AllColumns { get; }
    public string[] ColumnNames { get; }
    public string TableName { get; }
    public string InsertBatchSql { get; }
    public ValueConverter?[] Converters { get; }
    public JsonSerializerOptions InsertJson { get; }
    public Func<DbDataReader, T> Read { get; }
    public Func<T, Guid> Identity { get; }

    private HistoryRowMapping(IEntityType entity)
    {
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        TableName = $"\"{table.Name}\"";
        Properties = entity.GetProperties().ToArray();
        Key = entity.FindPrimaryKey()!.Properties.Single();
        KeyIndex = Array.IndexOf(Properties, Key);
        RevisionIndex = Array.FindIndex(Properties,
            property => property.Name == nameof(IRevisionedHistoryRow.Revision));
        AllColumns = Enumerable.Range(0, Properties.Length).ToArray();
        ColumnNames = Properties.Select(property => $"\"{property.GetColumnName(table)}\"").ToArray();
        Converters = Properties.Select(property => property.GetRelationalTypeMapping().Converter).ToArray();
        Read = CreateReader();
        var row = Expression.Parameter(typeof(T), "row");
        Identity = Expression.Lambda<Func<T, Guid>>(
            Expression.Property(row, Key.PropertyInfo!), row).Compile();
        InsertJson = new() { Converters = { new BatchJsonConverter(this) } };
        // Decode the batch once. Binary row values let each mapped column
        // extract its scalar without reparsing the row's JSON text. Keep one
        // statement so forward/cyclic references retain their FK semantics.
        InsertBatchSql = $"INSERT INTO {TableName} ({string.Join(", ", ColumnNames)}) SELECT "
            + string.Join(", ", AllColumns.Select(index => $"json_extract(value, '$[{index}]')"))
            + " FROM jsonb_each(jsonb($rows))";
    }

    private Func<DbDataReader, T> CreateReader()
    {
        var reader = Expression.Parameter(typeof(DbDataReader), "reader");
        var bindings = Properties.Select((property, ordinal) =>
        {
            var mapping = property.GetRelationalTypeMapping();
            var method = mapping.GetDataReaderMethod();
            Expression target = method.DeclaringType!.IsAssignableFrom(reader.Type)
                ? reader : Expression.Convert(reader, method.DeclaringType);
            Expression value = mapping.CustomizeDataReaderExpression(
                Expression.Call(target, method, Expression.Constant(ordinal)));
            if (mapping.Converter is { } converter)
                value = Expression.Invoke(converter.ConvertFromProviderExpression, value);
            value = Expression.Convert(value, property.ClrType);
            if (property.IsNullable)
                value = Expression.Condition(
                    Expression.Call(reader, nameof(DbDataReader.IsDBNull), null, Expression.Constant(ordinal)),
                    Expression.Default(property.ClrType), value);
            return Expression.Bind(property.PropertyInfo!, value);
        });
        // These history entities are scalar rows. Provider getters and model
        // converters retain GUID, numeric and NULL semantics without a second
        // handwritten field list; compile once per existing model lifetime.
        return Expression.Lambda<Func<DbDataReader, T>>(
            Expression.MemberInit(Expression.New(typeof(T)), bindings), reader).Compile();
    }

    public object?[] Values(T row)
    {
        var values = new object?[Properties.Length];
        for (int i = 0; i < values.Length; i++) values[i] = Properties[i].GetGetter().GetClrValue(row);
        return values;
    }

    // Serialize through JsonSerializer's pooled buffer; an insert must not
    // allocate a growing UTF-8 scratch buffer for every transaction.
    private sealed class BatchJsonConverter(HistoryRowMapping<T> shape) : JsonConverter<(Guid Key, object?[] Values)[]>
    {
        public override (Guid Key, object?[] Values)[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => throw new NotSupportedException("Batch insert encoding is write-only.");

        public override void Write(Utf8JsonWriter json, (Guid Key, object?[] Values)[] entries, JsonSerializerOptions options)
        {
            json.WriteStartArray();
            foreach (var entry in entries)
            {
                json.WriteStartArray();
                for (int i = 0; i < entry.Values.Length; i++)
                {
                    object? value = entry.Values[i];
                    var converter = shape.Converters[i];
                    WriteScalar(json, converter is null ? value : converter.ConvertToProvider(value));
                }
                json.WriteEndArray();
            }
            json.WriteEndArray();
        }
    }

    private static void WriteScalar(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case string text: json.WriteStringValue(text); break;
            case int number: json.WriteNumberValue(number); break;
            case long number: json.WriteNumberValue(number); break;
            case bool boolean: json.WriteBooleanValue(boolean); break;
            case Guid id:
                Span<byte> formatted = stackalloc byte[36];
                id.TryFormat(formatted, out _, "D");
                Ascii.ToUpperInPlace(formatted, out _);
                json.WriteStringValue(formatted);
                break;
            default: throw new InvalidOperationException("A bulk history insert requires model-converted text or integer scalars.");
        }
    }
}
