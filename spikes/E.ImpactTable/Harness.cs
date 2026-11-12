using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace SchemaKit.Spikes.E;

/// <summary>What an operation did to an object that depends on what it changed.</summary>
public enum Outcome
{
    /// <summary>The object is there, with the definition it had, and works.</summary>
    Unaffected,

    /// <summary>The object is there and works; its definition names the new column, table or schema.</summary>
    Follows,

    /// <summary>The operation succeeded and the object is gone.</summary>
    Lost,

    /// <summary>The operation failed because of the object.</summary>
    Blocks,

    /// <summary>The operation succeeded and the object is in the catalog, but it fails or no longer fires when it runs.</summary>
    Stale,
}

public sealed record Measured(Outcome Outcome, string Detail)
{
    public override string ToString() => Detail.Length == 0 ? Outcome.ToString() : $"{Outcome} ({Detail})";
}

/// <summary>One kind of object: how it is created on the table <c>{s}.t</c> under the name <c>o</c>.</summary>
public sealed record ObjectKind(string Name, string Create, bool IsTrigger = false);

/// <summary>One operation of EF Core's, on the column <c>a</c> or the table <c>t</c> of a schema.</summary>
public sealed record Operation(string Name, Func<Server, string, IReadOnlyList<MigrationOperation>> Build);

/// <summary>A server a cell is measured on. A cell gets a schema of its own.</summary>
public abstract class Server : IDisposable
{
    private readonly DbContext _context;
    private int _cells;

    protected Server(string name, DbConnection connection, DbContext context)
    {
        Name = name;
        Connection = connection;
        _context = context;
    }

    public string Name { get; }

    protected DbConnection Connection { get; }

    public abstract IReadOnlyList<ObjectKind> Objects { get; }

    public IReadOnlyList<Operation> Operations =>
    [
        new("RenameColumn", (_, s) => [new RenameColumnOperation { Schema = s, Table = "t", Name = "a", NewName = "a2" }]),
        new("AlterColumn (type)", (server, s) => [server.Alter(s, typeof(long), server.BigInt, nullable: false)]),
        new("AlterColumn (nullability)", (server, s) => [server.Alter(s, typeof(int), server.Int, nullable: true)]),
        new("DropColumn", (_, s) => [new DropColumnOperation { Schema = s, Table = "t", Name = "a" }]),
        new("RenameTable", (_, s) => [new RenameTableOperation { Schema = s, Name = "t", NewSchema = s, NewName = "t2" }]),
        new("Schema move", (_, s) =>
        [
            new EnsureSchemaOperation { Name = s + "_moved" },
            new RenameTableOperation { Schema = s, Name = "t", NewSchema = s + "_moved", NewName = "t" },
        ]),
        new("DropTable", (_, s) => [new DropTableOperation { Schema = s, Name = "t" }]),
    ];

    protected abstract string Int { get; }

    protected abstract string BigInt { get; }

    /// <summary>Creates the cell's schema with the table <c>t</c>, a log table and what the triggers call.</summary>
    protected abstract void CreateTable(string schema);

    /// <summary>A handle to the cell's table, which survives its rename and its move to another schema.</summary>
    protected abstract object Find(string schema);

    /// <summary>
    /// The definition of the object <c>o</c> on the table as the catalog has it now, and what the
    /// catalog identifies it by; null when it is gone. The object is looked up by its name and not
    /// by that identity: a server may rebuild an object under a new one, and that is not a loss.
    /// </summary>
    protected abstract (string Definition, string Identity)? Definition(object handle, ObjectKind kind);

    /// <summary>The statements EF Core's generator writes for an operation, for the record.</summary>
    public IEnumerable<string> Statements(Operation operation)
        => _context.GetService<IMigrationsSqlGenerator>().Generate(operation.Build(this, "s")).Select(c => c.CommandText.Trim());

    /// <summary>Inserts and updates a row in the cell's table wherever it is now; null when the table is gone.</summary>
    protected abstract string? Probe(string schema);

    protected abstract long LogCount(string schema);

    /// <summary>What an operation does to the table when nothing depends on the column: it has to succeed.</summary>
    public string? Baseline(Operation operation)
    {
        var schema = NewSchema();
        CreateTable(schema);
        return Run(operation.Build(this, schema));
    }

    public Measured Measure(ObjectKind kind, Operation operation)
    {
        var schema = NewSchema();
        CreateTable(schema);
        foreach (var statement in kind.Create.Replace("{s}", schema).Split("\nGO\n"))
            Execute(statement);

        var handle = Find(schema);
        var before = Definition(handle, kind) ?? throw new InvalidOperationException($"{kind.Name} was not created.");

        if (Run(operation.Build(this, schema)) is { } refused)
            return new Measured(Outcome.Blocks, refused);

        if (Definition(handle, kind) is not { } after)
            return new Measured(Outcome.Lost, "");

        var rebuilt = after.Identity == before.Identity ? "" : "rebuilt by the server";

        var fired = LogCount(schema);
        string? broken;
        try
        {
            broken = Probe(schema);
        }
        catch (DbException e)
        {
            return new Measured(Outcome.Stale, Short(e));
        }

        if (broken is not null) return new Measured(Outcome.Stale, broken);
        if (kind.IsTrigger && LogCount(schema) == fired) return new Measured(Outcome.Stale, "it no longer fires");

        return new Measured(after.Definition == before.Definition ? Outcome.Unaffected : Outcome.Follows, rebuilt);
    }

    private AlterColumnOperation Alter(string schema, Type clrType, string storeType, bool nullable) => new()
    {
        Schema = schema,
        Table = "t",
        Name = "a",
        ClrType = clrType,
        ColumnType = storeType,
        IsNullable = nullable,
        OldColumn = new AddColumnOperation { ClrType = typeof(int), ColumnType = Int, IsNullable = false },
    };

    /// <summary>Runs the statements EF Core's own generator writes for the operations; the error when one fails.</summary>
    private string? Run(IReadOnlyList<MigrationOperation> operations)
    {
        try
        {
            foreach (var command in _context.GetService<IMigrationsSqlGenerator>().Generate(operations))
                Execute(command.CommandText);
            return null;
        }
        catch (DbException e)
        {
            return Short(e);
        }
    }

    protected void Execute(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    protected T? Scalar<T>(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() is { } value and not DBNull ? (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)) : default;
    }

    protected List<(string Name, string Type)> Rows(string sql)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        List<(string, string)> rows = [];
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    protected abstract string Short(DbException exception);

    private string NewSchema() => $"c{++_cells}";

    public void Dispose()
    {
        _context.Dispose();
        Connection.Dispose();
    }

    /// <summary>
    /// The connection string of a server, from an environment variable. Without it a local run goes
    /// inconclusive; when <c>CI</c> is set it fails, so that a missing server cannot retire the spike
    /// while the build reports green.
    /// </summary>
    public static string ConnectionString(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value)) return value;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
            Assert.Fail($"{variable} is not set, and this is CI: the spike must run here.");
        Assert.Inconclusive($"{variable} is not set; no server to measure on.");
        return "";
    }
}
