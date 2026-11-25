using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests;

/// <summary>
/// SQLite as far as it goes (#119): an index on an expression reaches the database through every
/// path, follows what SQLite carries it through, and is not lost in silence when the provider
/// creates its table again. Every test runs on a database file of its own; none needs a server.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SqliteExpressionIndexTests
{
    private const string Index = """People | CREATE INDEX "IX_people_lower_name" ON "People" (lower("Name"))""";

    private static List<string> Created<TModel>() where TModel : ITestModel
    {
        using var database = new SqliteDatabase();
        using var context = Contexts.Sqlite<TModel>(database.ConnectionString);
        context.Database.EnsureCreated();
        return database.Indexes();
    }

    private static void AssertMigratesTo<TFrom, TTo>(Action<SqliteDatabase>? also = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var migrated = new SqliteDatabase();
        migrated.Create<TFrom>();
        migrated.Migrate<TFrom, TTo>();

        CollectionAssert.AreEqual(Created<TTo>(), migrated.Indexes());
        also?.Invoke(migrated);
    }

    private static List<string> Shape<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Contexts.Sqlite<TFrom>();
        using var to = Contexts.Sqlite<TTo>();
        return from.DiffTo(to).Shape();
    }

    // ── Every path that creates the index ──

    [TestMethod]
    public void EnsureCreated_creates_the_index_on_the_expression()
        => CollectionAssert.AreEqual(new[] { Index }, Created<People.Indexed>());

    [TestMethod]
    public void The_create_script_creates_it()
    {
        using var database = new SqliteDatabase();
        using var context = Contexts.Sqlite<People.Indexed>();

        database.Execute(context.Database.GenerateCreateScript());

        CollectionAssert.AreEqual(new[] { Index }, database.Indexes());
    }

    [TestMethod]
    public void A_first_migration_creates_it()
    {
        using var database = new SqliteDatabase();

        database.Create<People.Indexed>();

        CollectionAssert.AreEqual(new[] { Index }, database.Indexes());
    }

    [TestMethod]
    public void A_default_name_is_built_from_the_table_and_the_expression()
        => CollectionAssert.AreEqual(new[] { """People | CREATE INDEX "IX_People_lowerName" ON "People" (lower("Name"))""" }, Created<People.Unnamed>());

    [TestMethod]
    public void Adding_the_index_to_a_table_that_exists()
        => AssertMigratesTo<People.Bare, People.Indexed>(database => CollectionAssert.AreEqual(new[] { Index }, database.Indexes()));

    [TestMethod]
    public void Dropping_the_index() => AssertMigratesTo<People.Indexed, People.Bare>(database => Assert.AreEqual(0, database.Indexes().Count));

    // ── Behaviour ──

    /// <summary>The planner uses the index for the expression it is on, which is what makes it the index that was declared.</summary>
    [TestMethod]
    public void A_query_on_the_expression_is_answered_from_the_index()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Indexed>();

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """EXPLAIN QUERY PLAN SELECT "Id" FROM "People" WHERE lower("Name") = 'ada'""";
        using var reader = command.ExecuteReader();
        List<string> details = [];
        while (reader.Read()) details.Add(reader.GetString(3));

        StringAssert.Contains(string.Join("\n", details), "INDEX IX_people_lower_name (<expr>=?)");
    }

    [TestMethod]
    public void A_unique_index_on_the_expression_refuses_a_name_that_differs_in_case_only()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Unique>();
        database.Execute("""INSERT INTO "People" ("Name") VALUES ('Ada')""");

        var refused = Assert.ThrowsExactly<SqliteException>(() => database.Execute("""INSERT INTO "People" ("Name") VALUES ('ADA')"""));

        Assert.AreEqual(19, refused.SqliteErrorCode, refused.Message);
        database.Execute("""INSERT INTO "People" ("Name") VALUES ('Grace')""");
    }

    // ── What SQLite carries the index through ──

    /// <summary>SQLite renames no index, so another name is a drop and a create.</summary>
    [TestMethod]
    public void Another_name_is_a_drop_and_a_create()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                """DROP INDEX "IX_people_lower_name";""",
                """CREATE INDEX "IX_people_name_ci" ON "People" (lower("Name"));""",
            },
            Shape<People.Indexed, People.Renamed>());

        AssertMigratesTo<People.Indexed, People.Renamed>(database =>
            CollectionAssert.AreEqual(new[] { Index.Replace(People.Name, "IX_people_name_ci") }, database.Indexes()));
    }

    /// <summary>
    /// <c>ALTER TABLE … RENAME COLUMN</c> rewrites the index's own statement. SchemaKit writes
    /// nothing: the migration is EF Core's rename alone, and the index names the new column.
    /// </summary>
    [TestMethod]
    public void Renaming_the_column_the_expression_reads()
    {
        CollectionAssert.AreEqual(new[] { nameof(RenameColumnOperation) }, Shape<People.Indexed, People.NameColumnRenamed>());

        AssertMigratesTo<People.Indexed, People.NameColumnRenamed>(database =>
            CollectionAssert.AreEqual(new[] { """People | CREATE INDEX "IX_people_lower_name" ON "People" (lower("FullName"))""" }, database.Indexes()));
    }

    /// <summary>
    /// <c>ALTER TABLE … RENAME TO</c> carries the index along. The primary key has a name that
    /// does not say the table's here: by convention it does, EF Core then drops and adds the key
    /// beside the rename, and SQLite's provider creates the table again for that (the next test).
    /// </summary>
    [TestMethod]
    public void Renaming_the_table()
    {
        CollectionAssert.AreEqual(new[] { nameof(RenameTableOperation) }, Shape<People.IndexedWithANamedKey, People.TableRenamedWithANamedKey>());

        AssertMigratesTo<People.IndexedWithANamedKey, People.TableRenamedWithANamedKey>(database =>
            StringAssert.StartsWith(database.Indexes().Single(), "Persons | "));
    }

    // ── The table created again ──

    /// <summary>
    /// SQLite's provider writes a change of a column as a rebuild: a new table, the rows copied,
    /// the old table dropped, and the index with it. The contributor says the index is lost and
    /// the planner would create it again, but SQLite's generator moves the rebuild to the end of
    /// the migration, behind the create. So the migration is refused where it is scaffolded
    /// (#145), with the declaration and the table named, and the database is not touched.
    /// </summary>
    [TestMethod]
    public void A_table_rebuild_under_an_unchanged_index_is_refused_at_the_diff()
        => AssertRefusedAtTheDiff<People.Indexed, People.IndexedWithARequiredEmail>("People");

    /// <summary>
    /// The same for a renamed table whose primary key is named after it, which is every table
    /// with EF Core's default names: <c>DropPrimaryKey</c> and <c>AddPrimaryKey</c> stand beside
    /// the rename, and SQLite rebuilds the table for each. The create names the table as the
    /// target has it.
    /// </summary>
    [TestMethod]
    public void A_renamed_table_whose_key_is_named_after_it_is_a_rebuild_and_is_refused_as_one()
        => AssertRefusedAtTheDiff<People.Indexed, People.TableRenamed>("Persons");

    /// <summary>
    /// The operation that makes the rebuild names the table as it was, and the index names it as
    /// it will be: a check constraint is dropped from <c>People</c>, the table becomes
    /// <c>Persons</c>, and its key keeps its name, so nothing else stands beside the rename. The
    /// rebuild was not taken for one of the index's table: the planner wrote nothing, the
    /// migration applied, and the index was gone (#193).
    /// </summary>
    [TestMethod]
    public void A_rebuild_written_for_the_table_under_its_old_name_is_refused_too()
    {
        CollectionAssert.AreEqual(
            new[] { nameof(DropCheckConstraintOperation), nameof(RenameTableOperation) },
            EfCoreAlone<People.IndexedWithANamedKeyAndACheck, People.TableRenamedWithANamedKey>());

        AssertRefusedAtTheDiff<People.IndexedWithANamedKeyAndACheck, People.TableRenamedWithANamedKey>("Persons");
    }

    // What EF Core's own differ writes for the pair, without SchemaKit's planner in front.
    private static List<string> EfCoreAlone<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        var options = new DbContextOptionsBuilder().UseSqlite(Contexts.OfflineSqlite).Options;
        using var plain = new DbContext(options);
        using var from = Contexts.Sqlite<TFrom>();
        using var to = Contexts.Sqlite<TTo>();
        return [.. plain.GetService<IMigrationsModelDiffer>().GetDifferences(from.Relational(), to.Relational()).Select(operation => operation.GetType().Name)];
    }

    /// <summary>The column the index reads is dropped, and the index of that name moves to another column: a rebuild, and refused.</summary>
    [TestMethod]
    public void A_dropped_column_under_the_index_is_refused_as_a_rebuild_is()
        => AssertRefusedAtTheDiff<People.IndexedByEmail, People.IndexedWithoutTheEmail>("People");

    /// <summary>The column the index reads is altered, which SQLite's provider writes as a rebuild too.</summary>
    [TestMethod]
    public void An_altered_column_under_the_index_is_refused_as_a_rebuild_is()
        => AssertRefusedAtTheDiff<People.IndexedByEmail, People.IndexedByARequiredEmail>("People");

    private static void AssertRefusedAtTheDiff<TFrom, TTo>(string table, Action<ProbeSqliteIndexContributor>? arrange = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var database = new SqliteDatabase();
        database.Create<TFrom>();
        var (indexes, tables) = (database.Indexes(), database.Tables());

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => database.Migrate<TFrom, TTo>(arrange));

        StringAssert.Contains(refused.Message, $"has to create on the table '{table}'");
        StringAssert.Contains(refused.Message, "SQLite's provider writes as a rebuild");
        CollectionAssert.AreEqual(indexes, database.Indexes());
        CollectionAssert.AreEqual(tables, database.Tables());
    }

    /// <summary>
    /// What the answer above is for. A contributor that takes the rebuild for nothing gets a
    /// migration that applies and a database without the index.
    /// </summary>
    [TestMethod]
    public void Answered_as_unaffected_the_rebuild_takes_the_index_without_a_word()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Indexed>();

        database.Migrate<People.Indexed, People.IndexedWithARequiredEmail>(contributor =>
            contributor.ImpactOverride = change => change is BaseChange.TableRebuilt ? new Impact.Unaffected() : null);

        Assert.AreEqual(0, database.Indexes().Count);
    }

    // ── Where the migration applied and the index was gone (#145) ──
    //
    // In each of these the create ran on the old table, under a name that was free at that
    // moment, and the rebuild at the end of the migration dropped the old table: the migration
    // applied and the database lacked the index the model declares. Each is refused now.

    /// <summary>The contract's other answer for an object that cannot stay. On SQLite the drop only cleared the way for the create that was lost.</summary>
    [TestMethod]
    public void An_index_answered_as_rebuilt_around_the_rebuild_is_refused()
        => AssertRefusedAtTheDiff<People.Indexed, People.IndexedWithARequiredEmail>(
            "People", contributor => contributor.ImpactOverride = change => change is BaseChange.TableRebuilt ? new Impact.RebuildAround() : null);

    /// <summary>No answer of a contributor is involved: the index is new, so nothing is asked about it.</summary>
    [TestMethod]
    public void An_index_declared_in_the_migration_that_rebuilds_its_table_is_refused()
        => AssertRefusedAtTheDiff<People.Bare, People.IndexedWithARequiredEmail>("People");

    /// <summary>An index of SQL written by hand names no column, and is on its table all the same: it reads the whole of it.</summary>
    [TestMethod]
    public void An_index_of_hand_written_sql_on_the_rebuilt_table_is_refused()
        => AssertRefusedAtTheDiff<People.Bare, People.IndexedByHandWrittenSqlWithARequiredEmail>("People");

    [TestMethod]
    public void An_index_renamed_in_the_migration_that_rebuilds_its_table_is_refused()
        => AssertRefusedAtTheDiff<People.Indexed, People.RenamedWithARequiredEmail>("People");

    /// <summary>
    /// The way the refusal names, measured: the declaration taken out, the table changed, the
    /// declaration put back, a migration each. The index is there at the end, as
    /// <c>EnsureCreated()</c> builds it for the last model.
    /// </summary>
    [TestMethod]
    public void In_three_migrations_the_table_is_changed_and_the_index_is_there()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Indexed>();

        database.Migrate<People.Indexed, People.Bare>();
        database.Migrate<People.Bare, People.BareWithARequiredEmail>();
        database.Migrate<People.BareWithARequiredEmail, People.IndexedWithARequiredEmail>();

        CollectionAssert.AreEqual(Created<People.IndexedWithARequiredEmail>(), database.Indexes());
        Assert.AreEqual(1, database.Indexes().Count);
    }

    // ── Names: one namespace for the tables and the indexes of a database ──

    private const string Tables =
        """
        CREATE TABLE "People" ("Id" INTEGER PRIMARY KEY, "Name" TEXT, "Email" TEXT CONSTRAINT "CK_shared" CHECK ("Email" <> ''));
        CREATE TABLE "Pets" ("Id" INTEGER PRIMARY KEY, "Name" TEXT CONSTRAINT "CK_shared" CHECK ("Name" <> ''));
        CREATE INDEX "IX_shared" ON "Pets" ("Name");
        """;

    private static string Add(string name, string column = "Name") => $"""CREATE INDEX "{name}" ON "People" (lower("{column}"))""";

    /// <summary>The database's word for where an index name is taken, which is what the probe's dialect and contributor claim.</summary>
    [TestMethod]
    [DataRow("IX_shared", "index IX_shared already exists", DisplayName = "an index of another table")]
    [DataRow("Pets", "there is already a table named Pets", DisplayName = "a table")]
    [DataRow("ix_SHARED", "index ix_SHARED already exists", DisplayName = "an index of another table in another case")]
    public void The_database_refuses_an_index_named_like(string name, string message)
    {
        using var database = new SqliteDatabase();
        database.Execute(Tables);

        var refused = Assert.ThrowsExactly<SqliteException>(() => database.Execute(Add(name)));

        StringAssert.Contains(refused.Message, message);
    }

    /// <summary>A constraint's name is taken nowhere: two tables hold <c>CK_shared</c> already, and an index may be called that too.</summary>
    [TestMethod]
    public void The_database_takes_an_index_named_like_a_check_constraint()
    {
        using var database = new SqliteDatabase();
        database.Execute(Tables);

        database.Execute(Add("CK_shared"));

        Assert.AreEqual(2, database.Indexes().Count);
    }

    private static SchemaValidationException? Collision<TModel>(bool dialect = true) where TModel : ITestModel
    {
        using var context = Contexts.Sqlite<TModel>(dialect: dialect);
        return context.Collisions();
    }

    /// <summary>With SQLite's namespaces said by a dialect, the collision is found where the migration is scaffolded.</summary>
    [TestMethod]
    public void An_index_named_like_an_index_of_another_table_or_like_a_table_is_refused_at_the_diff()
    {
        var index = Collision<People.NamedLikeAnIndexOfAnotherTable>()?.Diagnostics.Single();
        Assert.AreEqual("SK1001", index?.Id);
        Assert.AreEqual("SK1001", Collision<People.NamedLikeATable>()?.Diagnostics.Single().Id);

        // What the consumer is told (#119): SQLite's one namespace, in PostgreSQL's words. SQLite has
        // no schema, no sequence, and no index behind a constraint that takes a name.
        Assert.AreEqual(
            "SK1001: Probe.SqliteIndex index 'IX_shared' on Person and the index 'IX_shared' on 'Pets' both claim the name 'IX_shared' among "
            + "the relations of the default schema (tables, indexes, sequences, and the indexes behind constraints). The database keeps a "
            + "name once there: give one of them another name.",
            index!.Message);
    }

    /// <summary>
    /// A key's name and a check constraint's are kept in the table's own statement and compared
    /// with nothing: an index may be called either, the dialect claims neither, and the database
    /// creates all of it.
    /// </summary>
    [TestMethod]
    public void An_index_named_like_a_key_or_a_check_constraint_of_its_table_is_let_through_and_created()
    {
        Assert.IsNull(Collision<People.NamedLikeItsPrimaryKey>());
        Assert.IsNull(Collision<People.NamedLikeACheckConstraint>());

        CollectionAssert.AreEqual(new[] { Index.Replace(People.Name, "PK_People") }, Created<People.NamedLikeItsPrimaryKey>());
        CollectionAssert.AreEqual(new[] { Index.Replace(People.Name, "CK_People_Name") }, Created<People.NamedLikeACheckConstraint>());
    }

    /// <summary>
    /// The kernel's own dialect, which a provider without a package gets, names an index within
    /// its table. On SQLite that is wrong: the collision passes the diff and the database refuses
    /// the migration.
    /// </summary>
    [TestMethod]
    public void The_kernels_own_scopes_let_the_collision_through_to_the_database()
    {
        Assert.IsNull(Collision<People.NamedLikeAnIndexOfAnotherTable>(dialect: false));

        using var database = new SqliteDatabase();
        using var context = Contexts.Sqlite<People.NamedLikeAnIndexOfAnotherTable>(database.ConnectionString, dialect: false);
        var refused = Assert.ThrowsExactly<SqliteException>(() => context.Database.EnsureCreated());

        StringAssert.Contains(refused.Message, "index IX_shared already exists");
    }

    /// <summary>
    /// SQLite folds the case of ASCII letters and of no others, so these are two names to it. The
    /// contract's comparison that ignores case folds both: the kernel takes the two declarations
    /// for one where they are written, and refuses a model the database holds.
    /// </summary>
    [TestMethod]
    public void Two_names_that_differ_in_the_case_of_an_umlaut_are_two_to_the_database_and_one_to_the_kernel()
    {
        using var database = new SqliteDatabase();
        database.Execute(Tables);
        database.Execute(Add("IX_Ä"));
        database.Execute(Add("IX_ä", "Email"));
        Assert.AreEqual(3, database.Indexes().Count);

        using var context = Contexts.Sqlite<People.TwoIndexesThatDifferInTheCaseOfAnUmlaut>();
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);

        StringAssert.Contains(refused.Message, "declares the same schema object twice: Probe.SqliteIndex index 'IX_Ä' and Probe.SqliteIndex index 'IX_ä'");
    }

    // ── A snapshot ──

    /// <summary>The model against its own compiled snapshot is nothing, both ways, and the snapshot holds the declaration.</summary>
    [TestMethod]
    public void A_snapshot_round_trip_changes_nothing()
    {
        using var context = Contexts.Sqlite<People.Indexed>();
        var snapshot = context.Snapshot();
        var differ = context.GetService<IMigrationsModelDiffer>();

        Assert.AreEqual(0, differ.GetDifferences(snapshot, context.Relational()).Count);
        Assert.AreEqual(0, differ.GetDifferences(context.Relational(), snapshot).Count);
        Assert.AreEqual(1, snapshot.Model.GetEntityTypes().Single().GetDeclarations<Sqlite.ProbeSqliteIndex>().Count);
    }

    /// <summary>The stored form, as it stands in a consumer's snapshot: an expression is stored as <c>sql</c>, a column as <c>column</c>.</summary>
    [TestMethod]
    public void The_declaration_is_one_annotation_in_the_snapshot()
    {
        using var context = Contexts.Sqlite<People.Indexed>();
        var code = CodoMetis.EFCore.SchemaKit.Tests.Harness.Snapshots.Of(context).Code;

        StringAssert.Contains(code, "b.HasAnnotation(\"CodoMetis:Probe.SqliteIndex:");
        StringAssert.Contains(code, """{\"$kind\":\"index\",\"v\":3,\"r\":1,\"name\":\"IX_people_lower_name\",\"sql\":[\"lower(\",{\"col\":\"Name\"},\")\"],\"unique\":false}""");
    }
}
