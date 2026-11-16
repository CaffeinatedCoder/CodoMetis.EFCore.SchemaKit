using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Integration;

/// <summary>
/// The generated scripts, applied batch by batch on a connection of SqlClient. A script is where
/// a statement meets what EF Core puts around it: the batch of its migration, a transaction, and
/// in an idempotent script an <c>IF NOT EXISTS (…) BEGIN … END</c> around every command.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SqlServerScriptLiveTests
{
    private const string Open = "dbo.Orders | IX_orders_open | NONCLUSTERED (CustomerId) INCLUDE (Total) WHERE ([ShippedAt] IS NULL)";

    /// <summary>What the three migrations lead to: the index on the renamed column.</summary>
    private static readonly string[] Last = [Open.Replace("[ShippedAt]", "[SentAt]")];

    private static List<string> Created<TModel>() where TModel : ITestModel
    {
        using var database = new SqlServerDatabase();
        using var context = Contexts.SqlServer<TModel>(database.ConnectionString);
        context.Database.EnsureCreated();
        return database.Indexes();
    }

    private static string Script(string? from = null, string? to = null, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        using var context = Scripted.Of();
        return context.GetService<IMigrator>().GenerateScript(from, to, options);
    }

    /// <summary>The script is applied, not read: one that lacks a terminator, or puts the index before its table, fails here.</summary>
    [TestMethod]
    public void The_create_script_creates_the_index()
    {
        using var database = new SqlServerDatabase();
        using var context = Contexts.SqlServer<Sales.Indexed>();

        database.ExecuteScript(context.Database.GenerateCreateScript());

        CollectionAssert.AreEqual(Created<Sales.Indexed>(), database.Indexes());
        CollectionAssert.AreEqual(new[] { Open }, database.Declared());
    }

    /// <summary>Three migrations as one script: a table, the index, and a renamed column with the index out of its way.</summary>
    [TestMethod]
    public void The_migrations_script_leaves_what_EnsureCreated_builds()
    {
        using var database = new SqlServerDatabase();
        var script = Script();
        StringAssert.Contains(script, "\nGO\n");

        database.ExecuteScript(script);

        CollectionAssert.AreEqual(Created<Sales.FilterColumnRenamed>(), database.Indexes());
        CollectionAssert.AreEqual(Last, database.Declared());
        CollectionAssert.AreEqual(
            new[] { Scripted.Orders, Scripted.OpenOrders, Scripted.SentAt },
            database.Column("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"));
    }

    /// <summary>
    /// An idempotent script is run against a database at any migration, which is what it is for:
    /// here a new one, one at the second migration, and one that has them all.
    /// </summary>
    [TestMethod]
    public void The_idempotent_script_applies_to_a_database_at_any_migration()
    {
        var script = Script(options: MigrationsSqlGenerationOptions.Idempotent);
        var expected = Created<Sales.FilterColumnRenamed>();

        using var empty = new SqlServerDatabase();
        empty.ExecuteScript(script);
        CollectionAssert.AreEqual(expected, empty.Indexes());
        CollectionAssert.AreEqual(Last, empty.Declared());

        using var halfway = new SqlServerDatabase();
        halfway.ExecuteScript(Script(to: Scripted.OpenOrders));
        CollectionAssert.AreEqual(new[] { Open }, halfway.Declared());
        halfway.ExecuteScript(script);
        CollectionAssert.AreEqual(expected, halfway.Indexes());

        // The second run does nothing, and has to get past every batch to find that out.
        empty.ExecuteScript(script);
        CollectionAssert.AreEqual(expected, empty.Indexes());
    }

    /// <summary>
    /// What <c>EXEC</c> around the create is for. EF Core writes a migration as one batch, and
    /// SQL Server binds a filter's columns when it compiles the batch: the third migration renames
    /// the column and creates the index on the new name, so written as the statement it is, the
    /// create does not compile (Msg 207). The same migration applies by commands
    /// (<c>SqlServerIndexLiveTests.Renaming_a_column_the_filter_reads</c>): only a script shows it.
    /// </summary>
    [TestMethod]
    [DataRow(MigrationsSqlGenerationOptions.Default, DisplayName = "the migrations script")]
    [DataRow(MigrationsSqlGenerationOptions.Idempotent, DisplayName = "the idempotent script")]
    public void Written_as_a_plain_statement_the_create_fails_the_script(MigrationsSqlGenerationOptions options)
    {
        Scripted.Arrange = contributor => contributor.CreateAsAPlainStatement = true;
        try
        {
            var script = Script(options: options);
            StringAssert.Contains(script, "CREATE NONCLUSTERED INDEX [IX_orders_open] ON [Orders] ([CustomerId]) INCLUDE ([Total]) WHERE [SentAt] IS NULL;");
            using var database = new SqlServerDatabase();

            var refused = Assert.ThrowsExactly<SqlException>(() => database.ExecuteScript(script));

            Assert.AreEqual(207, refused.Number, refused.Message);
            StringAssert.Contains(refused.Message, "SentAt");
        }
        finally
        {
            Scripted.Arrange = null;
        }
    }

    /// <summary>
    /// The premise, with EF Core's own operations and nothing of SchemaKit's: a column renamed and
    /// a filtered index created on its new name, as one batch. EF Core writes the index through
    /// <c>EXEC</c> in an idempotent script, which applies, and as it is in every other, which
    /// does not compile.
    /// </summary>
    [TestMethod]
    public void EF_Cores_own_filtered_index_is_written_through_EXEC_in_an_idempotent_script_alone()
    {
        using var context = Contexts.SqlServer<Sales.Bare>();
        Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation[] operations =
        [
            new Microsoft.EntityFrameworkCore.Migrations.Operations.RenameColumnOperation { Table = "Orders", Name = "ShippedAt", NewName = "SentAt" },
            new Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation { Table = "Orders", Name = "IX_own", Columns = ["CustomerId"], Filter = "[SentAt] IS NULL" },
        ];

        string Batch(MigrationsSqlGenerationOptions options)
            => string.Join("\n", context.GetService<IMigrationsSqlGenerator>().Generate(operations, null, options).Select(command => command.CommandText));

        StringAssert.Contains(Batch(MigrationsSqlGenerationOptions.Idempotent), "EXEC(N'CREATE INDEX [IX_own]");
        Assert.IsFalse(Batch(MigrationsSqlGenerationOptions.Script).Contains("EXEC(N'CREATE", StringComparison.Ordinal));

        using var idempotent = new SqlServerDatabase();
        idempotent.Create<Sales.Bare>();
        idempotent.Execute(Batch(MigrationsSqlGenerationOptions.Idempotent));

        using var plain = new SqlServerDatabase();
        plain.Create<Sales.Bare>();
        Assert.AreEqual(207, Assert.ThrowsExactly<SqlException>(() => plain.Execute(Batch(MigrationsSqlGenerationOptions.Script))).Number);
    }

    /// <summary>
    /// What these tests do not show: the scripts under <c>sqlcmd</c>'s own settings. It runs with
    /// <c>QUOTED_IDENTIFIER</c> off unless it is given <c>-I</c>, and SQL Server creates no
    /// filtered index in such a session (Msg 1934). The migrations script stops at its second
    /// migration; EF Core's own filtered index is refused the same way, and an index without a
    /// filter is not. It is the server's rule for the object, and no statement of a contributor's
    /// goes around it: <c>SET QUOTED_IDENTIFIER ON</c> would have to stand in the batch.
    /// </summary>
    [TestMethod]
    public void With_QUOTED_IDENTIFIER_off_a_filtered_index_is_refused()
    {
        const string AsSqlcmdStarts = "SET QUOTED_IDENTIFIER OFF;\nGO\n";

        using var database = new SqlServerDatabase();
        var refused = Assert.ThrowsExactly<SqlException>(() => database.ExecuteScript(AsSqlcmdStarts + Script()));
        Assert.AreEqual(1934, refused.Number, refused.Message);
        CollectionAssert.AreEqual(new[] { Scripted.Orders }, database.Column("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"));

        Assert.AreEqual(1934, Assert.ThrowsExactly<SqlException>(() =>
            database.ExecuteScript(AsSqlcmdStarts + "CREATE INDEX [IX_own] ON [Orders] ([CustomerId]) WHERE [ShippedAt] IS NULL;")).Number);
        database.ExecuteScript(AsSqlcmdStarts + "CREATE INDEX [IX_unfiltered] ON [Orders] ([CustomerId]);");
    }

    /// <summary>The runtime's path: <c>Migrate()</c> applies the three migrations and records them.</summary>
    [TestMethod]
    public void Migrate_applies_the_migrations()
    {
        using var database = new SqlServerDatabase();
        using var context = Scripted.Of(database.ConnectionString);

        context.Database.Migrate();

        CollectionAssert.AreEqual(Created<Sales.FilterColumnRenamed>(), database.Indexes());
        CollectionAssert.AreEqual(Last, database.Declared());
        Assert.AreEqual(0, context.Database.GetPendingMigrations().Count());
    }
}
