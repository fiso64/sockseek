using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Persistence.Sqlite;

namespace Sockseek.Persistence.Tests;

[TestClass]
public sealed class SqliteWriteStatementTests
{
    [TestMethod]
    public void RepeatedRowsPreserveConstantsScalarChangesAndCopiedBuffers()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sample (id INTEGER PRIMARY KEY, number INTEGER, real REAL, text TEXT, identity TEXT, data BLOB, missing TEXT)";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using var writer = new SqliteWriteStatement(connection, transaction,
            "INSERT INTO sample VALUES ($id,$number,$real,$text,$identity,$data,$missing)");
        Guid identity = Guid.NewGuid();
        writer.Bind("$identity", identity);
        byte[] data = [1, 2];
        writer.BindValues([1, 5_000_000_000L, 1.25, @"Peer\é/δ"]);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(@"Peer\é/δ");
        writer.BindUtf8Text(3, text);
        writer.Bind("$data", data);
        writer.Bind("$missing", DBNull.Value);
        data[0] = 3; // Binding copies caller-owned buffers immediately.
        text[0] = (byte)'X';
        Assert.AreEqual(1, writer.Execute(CancellationToken.None));
        writer.Bind(0, 2);
        writer.Bind(4, identity);
        writer.Bind(1, (long?)long.MinValue);
        writer.Bind("$data", data);
        writer.Bind("$missing", null);
        Assert.AreEqual(1, writer.Execute(CancellationToken.None));
        writer.BindValues([3, 7, 2.5, "changed"]);
        writer.Bind(1, (int?)null);
        writer.Bind("$data", Array.Empty<byte>());
        writer.Bind("$missing", "known");
        Assert.AreEqual(1, writer.Execute(CancellationToken.None));
        writer.Bind(0, 4);
        writer.Bind(1, (int?)7);
        writer.BindUtf8Text(3, []);
        Assert.AreEqual(1, writer.Execute(CancellationToken.None));
        transaction.Commit();
        command.CommandText = "SELECT number,real,text,identity,hex(data),missing,typeof(data),typeof(text) FROM sample ORDER BY id";
        using var rows = command.ExecuteReader();
        for (int index = 0; index < 4; index++)
        {
            Assert.IsTrue(rows.Read());
            if (index == 2) Assert.IsTrue(rows.IsDBNull(0));
            else Assert.AreEqual(index == 0 ? 5_000_000_000L : index == 1 ? long.MinValue : 7, rows.GetInt64(0));
            Assert.AreEqual(index >= 2 ? 2.5 : 1.25, rows.GetDouble(1));
            Assert.AreEqual(index == 3 ? "" : index == 2 ? "changed" : @"Peer\é/δ", rows.GetString(2));
            Assert.AreEqual(identity.ToString().ToUpperInvariant(), rows.GetString(3));
            Assert.AreEqual(index == 0 ? "0102" : index == 1 ? "0302" : "", rows.GetString(4));
            Assert.AreEqual(index < 2, rows.IsDBNull(5));
            Assert.AreEqual("blob", rows.GetString(6));
            Assert.AreEqual("text", rows.GetString(7));
        }
        Assert.IsFalse(rows.Read());
    }

    [TestMethod]
    public void FailedBindingOrStatementDoesNotCorruptLaterRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sample (id INTEGER PRIMARY KEY, value TEXT)";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using var writer = new SqliteWriteStatement(connection, transaction,
            "INSERT INTO sample VALUES ($id,$value)");
        writer.BindValues([1, "first"]);
        writer.Execute(CancellationToken.None);
        Assert.ThrowsExactly<SqliteException>(() => writer.Execute(CancellationToken.None));
        writer.Bind(0, 2);
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Bind(1, double.NaN));
        writer.BindValues([2, "last"]);
        writer.Execute(CancellationToken.None);
        command.Transaction = transaction;
        command.CommandText = "SELECT group_concat(value, ',') FROM (SELECT value FROM sample ORDER BY id)";
        Assert.AreEqual("first,last", command.ExecuteScalar());
    }

    [TestMethod]
    public void ReturningWritesRecoverAfterFailureAndRetainNullSemantics()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sample (value TEXT UNIQUE)";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using var writer = new SqliteWriteStatement(connection, transaction,
            "INSERT INTO sample VALUES ($value) RETURNING value");
        writer.Bind(0, "first");
        Assert.AreEqual("first", writer.ExecuteScalarText(CancellationToken.None));
        Assert.ThrowsExactly<SqliteException>(() => writer.ExecuteScalarText(CancellationToken.None));
        writer.Bind(0, null);
        Assert.IsNull(writer.ExecuteScalarText(CancellationToken.None));
        writer.Bind(0, DBNull.Value);
        Assert.IsNull(writer.ExecuteScalarText(CancellationToken.None));
        writer.Bind(0, "last");
        Assert.AreEqual("last", writer.ExecuteScalarText(CancellationToken.None));
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sample";
        Assert.AreEqual(4L, command.ExecuteScalar());
    }

    [TestMethod]
    public void WritesRequireCompleteBindingsAndTheOriginalActiveTransaction()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sample (id INTEGER, value TEXT)";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        using var writer = new SqliteWriteStatement(connection, transaction,
            "INSERT INTO sample VALUES ($id,$value)");
        writer.Bind(0, 1);
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Execute(CancellationToken.None));
        writer.Bind(1, "first");
        Assert.ThrowsExactly<OperationCanceledException>(() => writer.Execute(new CancellationToken(true)));
        Assert.AreEqual(1, writer.Execute(CancellationToken.None));
        transaction.Commit();
        using var next = connection.BeginTransaction();
        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Execute(CancellationToken.None));
        command.Transaction = next;
        command.CommandText = "SELECT COUNT(*) FROM sample";
        Assert.AreEqual(1L, command.ExecuteScalar());
    }
}
