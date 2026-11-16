using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;

/// <summary>
/// The SQL Server the integration tests run against: <c>SCHEMAKIT_TEST_MSSQL</c>, a SqlClient
/// connection string to a server on which the tests may create and drop databases.
/// </summary>
/// <remarks>
/// Without it a local run goes inconclusive. When <c>CI</c> is set it fails instead: an unreachable
/// server must not quietly retire the tests that touch a database while the build reports green.
/// </remarks>
internal static class SqlServerInstance
{
    public const string Variable = "SCHEMAKIT_TEST_MSSQL";

    public static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                Assert.Fail($"{Variable} is not set, and this is CI: the integration tests must run here.");
            Assert.Inconclusive($"{Variable} is not set; no SQL Server to test against.");
        }

        return connectionString;
    }

    public static SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString());
        connection.Open();
        return connection;
    }

    public static void Execute(this SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public static List<string> Column(this SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        return values;
    }
}

/// <summary>A database of a test's own on the server of <c>SCHEMAKIT_TEST_MSSQL</c>, dropped when the test is done.</summary>
internal sealed class SqlServerDatabase : IDisposable
{
    private readonly string _name = $"sk_{Guid.NewGuid():N}";

    /// <param name="collation">The database's collation, which is how its names compare; null for the server's own.</param>
    public SqlServerDatabase(string? collation = null)
    {
        using var server = SqlServerInstance.Open();
        server.Execute($"CREATE DATABASE [{_name}]" + (collation is null ? "" : $" COLLATE {collation}"));
        ConnectionString = new SqlConnectionStringBuilder(SqlServerInstance.ConnectionString()) { InitialCatalog = _name, Pooling = false }.ToString();
    }

    public string ConnectionString { get; }

    public SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public void Execute(string sql)
    {
        using var connection = Open();
        connection.Execute(sql);
    }

    public List<string> Column(string sql)
    {
        using var connection = Open();
        return connection.Column(sql);
    }

    /// <summary>
    /// Runs a script batch by batch, a line that says <c>GO</c> ending each, on one connection of
    /// SqlClient. A statement that cannot share a batch with its neighbour, or a batch that does
    /// not compile, fails here.
    /// </summary>
    /// <remarks>
    /// The batches are <c>sqlcmd</c>'s, the session is not. SqlClient connects with
    /// <c>QUOTED_IDENTIFIER</c> on, as Management Studio does; <c>sqlcmd</c> has it off unless it
    /// is started with <c>-I</c>, and then SQL Server refuses to create a filtered index, this
    /// contributor's and EF Core's own alike
    /// (<c>SqlServerScriptLiveTests.With_QUOTED_IDENTIFIER_off_a_filtered_index_is_refused</c>).
    /// </remarks>
    public void ExecuteScript(string script)
    {
        using var connection = Open();
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                Run();
                continue;
            }

            batch.Append(line).Append('\n');
        }

        Run();

        void Run()
        {
            if (!string.IsNullOrWhiteSpace(batch.ToString())) connection.Execute(batch.ToString());
            batch.Clear();
        }
    }

    /// <summary>
    /// Every index the database holds but the heaps and EF Core's history table: its table, its
    /// name, what it is, its key columns, its included columns and its filter, as the catalog has them.
    /// </summary>
    public List<string> Indexes()
    {
        using var connection = Open();

        Dictionary<(int Object, int Index), (List<string> Keys, List<string> Included)> columns = [];
        using (var command = new SqlCommand(
                   """
                   SELECT ic.object_id, ic.index_id, c.name, ic.is_included_column, ic.is_descending_key
                   FROM sys.index_columns ic
                   JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   ORDER BY ic.object_id, ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
                   """, connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetInt32(0), reader.GetInt32(1));
                if (!columns.TryGetValue(key, out var lists)) columns[key] = lists = ([], []);
                if (reader.GetBoolean(3)) lists.Included.Add(reader.GetString(2));
                else lists.Keys.Add(reader.GetString(2) + (reader.GetBoolean(4) ? " DESC" : ""));
            }
        }

        List<string> indexes = [];
        using (var command = new SqlCommand(
                   """
                   SELECT s.name, t.name, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition, i.object_id, i.index_id
                   FROM sys.indexes i
                   JOIN sys.tables t ON t.object_id = i.object_id
                   JOIN sys.schemas s ON s.schema_id = t.schema_id
                   WHERE i.type > 0 AND t.is_ms_shipped = 0 AND t.name <> N'__EFMigrationsHistory'
                   """, connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var (keys, included) = columns[(reader.GetInt32(8), reader.GetInt32(9))];
                var what = reader.GetString(3)
                           + (reader.GetBoolean(5) ? " PRIMARY KEY" : reader.GetBoolean(6) ? " UNIQUE CONSTRAINT" : reader.GetBoolean(4) ? " UNIQUE" : "");
                indexes.Add(
                    $"{reader.GetString(0)}.{reader.GetString(1)} | {reader.GetString(2)} | {what} ({string.Join(", ", keys)})"
                    + (included.Count > 0 ? $" INCLUDE ({string.Join(", ", included)})" : "")
                    + (reader.IsDBNull(7) ? "" : $" WHERE {reader.GetString(7)}"));
            }
        }

        indexes.Sort(StringComparer.Ordinal);
        return indexes;
    }

    /// <summary>The declared indexes alone: every index that backs no key of EF Core's.</summary>
    public List<string> Declared() => [.. Indexes().Where(index => !index.Contains(" PRIMARY KEY (", StringComparison.Ordinal))];

    /// <summary>
    /// What tells an index that was renamed from one that was dropped and created: the identity of
    /// its storage. A rename keeps it; a create allocates another.
    /// </summary>
    public string StorageOf(string index)
        => Column($"SELECT p.hobt_id FROM sys.indexes i JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id WHERE i.name = N'{index}'").Single();

    /// <summary>Applies what the differ scaffolds for a new database: the first migration.</summary>
    public void Create<TModel>(bool dialect = true) where TModel : ITestModel => Apply<TModel>(source: null, dialect, arrange: null);

    /// <summary>Applies what the differ scaffolds to get from one model to the other, through the provider's generator.</summary>
    /// <param name="arrange">Breaks a rule of the contributor, for a test that shows what the rule is for.</param>
    /// <param name="dialect">False for the kernel's own dialect.</param>
    public void Migrate<TFrom, TTo>(Action<ProbeIndexContributor>? arrange = null, bool dialect = true)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Contexts.SqlServer<TFrom>(dialect: dialect);
        Apply<TTo>(from.Relational(), dialect, arrange);
    }

    /// <summary>The same from the model as a consumer's next diff reads it: its compiled snapshot.</summary>
    public void MigrateFromSnapshot<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Contexts.SqlServer<TFrom>();
        Apply<TTo>(from.Snapshot(), dialect: true, arrange: null);
    }

    private void Apply<TTo>(IRelationalModel? source, bool dialect, Action<ProbeIndexContributor>? arrange) where TTo : ITestModel
    {
        using var context = Contexts.SqlServer<TTo>(ConnectionString, dialect);
        arrange?.Invoke(context.Contributor<ProbeIndexContributor>());

        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(source, context.Relational());
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.GetService<IDesignTimeModel>().Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
    }

    public void Dispose()
    {
        using var server = SqlServerInstance.Open();
        server.Execute($"IF DB_ID(N'{_name}') IS NOT NULL BEGIN ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]; END");
    }
}
