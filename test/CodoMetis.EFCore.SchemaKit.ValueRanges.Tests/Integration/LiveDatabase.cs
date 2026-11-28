using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Integration;

/// <summary>A database of a test's own on the server of <c>SCHEMAKIT_TEST_PG</c>, dropped when the test is done.</summary>
internal sealed class LiveDatabase : IDisposable
{
    private readonly string _name = $"sk_{Guid.NewGuid():N}";

    public LiveDatabase()
    {
        using var server = PostgresServer.Open();
        server.Execute($"CREATE DATABASE \"{_name}\"");
        ConnectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresServer.Variable)) { Database = _name, Pooling = false }.ToString();
    }

    public string ConnectionString { get; }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Every exclusion constraint the database holds: its table, its name, its backing index and
    /// its definition as the server writes it. No OID: a rebuilt constraint is the same constraint.
    /// </summary>
    public List<string> Exclusions()
    {
        using var connection = Open();
        return connection.Column(
            """
            SELECT n.nspname || '.' || t.relname || ' | ' || c.conname || ' | index ' || i.relname || ' | ' || pg_get_constraintdef(c.oid)
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class i ON i.oid = c.conindid
            WHERE c.contype = 'x'
            ORDER BY 1
            """);
    }

    /// <summary>
    /// Every index the database holds that is no primary key: its table and its definition as the
    /// server writes it, which names the index and its schema.
    /// </summary>
    public List<string> Indexes()
    {
        using var connection = Open();
        return connection.Column(
            """
            SELECT n.nspname || '.' || t.relname || ' | ' || pg_get_indexdef(i.indexrelid)
            FROM pg_index i
            JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE NOT i.indisprimary AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY 1
            """);
    }

    /// <summary>Every column of every table: its type as the server writes it, and whether it takes a null.</summary>
    public List<string> Columns()
    {
        using var connection = Open();
        return connection.Column(
            """
            SELECT n.nspname || '.' || t.relname || '.' || a.attname || ' | ' || format_type(a.atttypid, a.atttypmod) || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END
            FROM pg_attribute a
            JOIN pg_class t ON t.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE t.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY 1
            """);
    }

    public List<string> Extensions()
    {
        using var connection = Open();
        return connection.Column("SELECT extname::text FROM pg_extension WHERE extname <> 'plpgsql' ORDER BY 1");
    }

    /// <summary>Every range type the database holds that it did not ship with, with its subtype and its multirange.</summary>
    public List<string> RangeTypes()
    {
        using var connection = Open();
        return connection.Column(
            """
            SELECT n.nspname || '.' || t.typname || ' | ' || format_type(r.rngsubtype, NULL) || ' | ' || m.typname
            FROM pg_range r
            JOIN pg_type t ON t.oid = r.rngtypid
            JOIN pg_type m ON m.oid = r.rngmultitypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1
            """);
    }

    /// <summary>All of the above as one list: what two databases are compared by.</summary>
    public List<string> Catalog() =>
    [
        .. RangeTypes().Select(type => "type " + type),
        .. Extensions().Select(extension => "extension " + extension),
        .. Columns().Select(column => "column " + column),
        .. Exclusions().Select(exclusion => "exclusion " + exclusion),
        .. Indexes().Select(index => "index " + index),
    ];

    /// <summary>What <c>EnsureCreated()</c> builds for the model.</summary>
    public void EnsureCreated<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>(ConnectionString);
        context.Database.EnsureCreated();
    }

    /// <summary>Applies what the differ scaffolds for a new database: the first migration.</summary>
    public void Create<TModel>() where TModel : ITestModel => Apply<TModel>(source: null);

    /// <summary>Applies what the differ scaffolds to get from one model to the other, through the provider's generator.</summary>
    public void Migrate<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Contexts.Of<TFrom>();
        Apply<TTo>(from.Relational());
    }

    private void Apply<TTo>(IRelationalModel? source) where TTo : ITestModel
    {
        using var context = Contexts.Of<TTo>(ConnectionString);
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(source, context.Relational());
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.GetService<IDesignTimeModel>().Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());

        // A migration that creates a type leaves Npgsql with the types it read before: Npgsql's
        // migrator reads them again after such a migration, and so does this, which stands in for it.
        context.Database.OpenConnection();
        ((NpgsqlConnection)context.Database.GetDbConnection()).ReloadTypes();
    }

    public void Dispose()
    {
        using var server = PostgresServer.Open();
        server.Execute($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }
}
