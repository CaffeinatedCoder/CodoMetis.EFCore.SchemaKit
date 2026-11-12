using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SchemaKit.Spikes.E;

public sealed class SqlServer : Server
{
    private SqlServer(string name, SqlConnection connection, DbContext context) : base(name, connection, context)
    {
    }

    /// <summary>Connects to the server and gives the spike a database of its own, made anew.</summary>
    public static SqlServer Open(string name, string variable)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString(variable));
        using (var admin = new SqlConnection(builder.ConnectionString))
        {
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = "IF DB_ID('spike_e') IS NOT NULL BEGIN ALTER DATABASE spike_e SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE spike_e; END";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE DATABASE spike_e";
            command.ExecuteNonQuery();
        }

        builder.InitialCatalog = "spike_e";
        builder.Pooling = false;
        var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        return new SqlServer(name, connection, new DbContext(new DbContextOptionsBuilder().UseSqlServer(builder.ConnectionString).Options));
    }

    public override IReadOnlyList<ObjectKind> Objects =>
    [
        new("index (a)", "CREATE INDEX o ON {s}.t (a)"),
        new("index (id) INCLUDE (a)", "CREATE INDEX o ON {s}.t (id) INCLUDE (a)"),
        new("index (id) WHERE a > 0", "CREATE INDEX o ON {s}.t (id) WHERE a > 0"),
        new("unique index (a) WHERE a IS NOT NULL", "CREATE UNIQUE INDEX o ON {s}.t (a) WHERE a IS NOT NULL"),
        new("trigger, no column", "CREATE TRIGGER {s}.o ON {s}.t AFTER INSERT, UPDATE AS INSERT INTO {s}.log DEFAULT VALUES", IsTrigger: true),
        new("trigger IF UPDATE(a)", "CREATE TRIGGER {s}.o ON {s}.t AFTER INSERT, UPDATE AS IF UPDATE(a) INSERT INTO {s}.log DEFAULT VALUES", IsTrigger: true),
        new("trigger, body reads inserted.a",
            "CREATE TRIGGER {s}.o ON {s}.t AFTER INSERT, UPDATE AS BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE a < 0) THROW 50000, 'negative', 1; INSERT INTO {s}.log DEFAULT VALUES; END",
            IsTrigger: true),
    ];

    protected override string Int => "int";

    protected override string BigInt => "bigint";

    protected override void CreateTable(string schema)
    {
        Execute($"CREATE SCHEMA {schema}");
        Execute($"CREATE TABLE {schema}.t (id int IDENTITY PRIMARY KEY, a int NOT NULL, b nvarchar(50) NOT NULL, other int NULL)");
        Execute($"CREATE TABLE {schema}.log (at datetime2 DEFAULT sysutcdatetime())");
    }

    protected override object Find(string schema) => Scalar<long>($"SELECT OBJECT_ID('{schema}.t')");

    protected override (string Definition, string Identity)? Definition(object handle, ObjectKind kind)
    {
        var table = (long)handle;
        // An index's definition is its table, its columns and its filter. The columns' types are
        // left out: the index is the same index when a column under it becomes nullable.
        var row = Rows(kind.IsTrigger
            ? $"SELECT CONCAT(OBJECT_SCHEMA_NAME(object_id), '.', name, ' ON ', OBJECT_SCHEMA_NAME(parent_id), '.', OBJECT_NAME(parent_id), ' ', OBJECT_DEFINITION(object_id)), " +
              $"CAST(object_id AS nvarchar(20)) FROM sys.triggers WHERE parent_id = {table} AND name = 'o'"
            : $"SELECT CONCAT(OBJECT_SCHEMA_NAME(i.object_id), '.', OBJECT_NAME(i.object_id), ' (', " +
              $"(SELECT STRING_AGG(CONCAT(c.name, CASE WHEN ic.is_included_column = 1 THEN ' include' ELSE '' END), ', ') WITHIN GROUP (ORDER BY ic.index_column_id) " +
              $"FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
              $"WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id), ') ', i.filter_definition), CAST(i.index_id AS nvarchar(20)) " +
              $"FROM sys.indexes i WHERE i.object_id = {table} AND i.name = 'o'");
        return row.Count == 0 ? null : row[0];
    }

    protected override string? Probe(string schema)
    {
        var table = Scalar<string>(
            $"SELECT CONCAT(QUOTENAME(SCHEMA_NAME(schema_id)), '.', QUOTENAME(name)) FROM sys.tables " +
            $"WHERE name IN ('t', 't2') AND SCHEMA_NAME(schema_id) IN ('{schema}', '{schema}_moved')");
        if (table is null) return null;

        var columns = Rows(
            $"SELECT QUOTENAME(name), TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND is_identity = 0 ORDER BY column_id");
        var values = columns.Select(c => c.Type switch
        {
            "int" or "bigint" => "1",
            "nvarchar"        => "N'x'",
            var other         => throw new InvalidOperationException($"No value for a {other} column."),
        });

        Execute($"INSERT INTO {table} ({string.Join(", ", columns.Select(c => c.Name))}) VALUES ({string.Join(", ", values)})");
        Execute($"UPDATE {table} SET {string.Join(", ", columns.Select(c => $"{c.Name} = {c.Name}"))}");
        return null;
    }

    protected override long LogCount(string schema) => Scalar<long>($"SELECT COUNT(*) FROM {schema}.log");

    protected override string Short(DbException exception)
        => exception is SqlException sql ? $"Msg {sql.Number}: {sql.Message.Split('\n')[0].Trim()}" : exception.Message;
}
