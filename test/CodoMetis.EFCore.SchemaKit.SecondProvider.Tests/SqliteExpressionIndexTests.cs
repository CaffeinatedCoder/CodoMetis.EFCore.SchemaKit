using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
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
    /// A column the index does not read is altered, and SQLite's provider creates the table again
    /// without the index. The contributor says the index is lost and the planner creates it again,
    /// but SQLite's generator moves the rebuild to the end of the migration, so the create meets
    /// the index that is still there. The migration fails and changes nothing: that is as far as
    /// this version gets (plan, section 3.12), and it is loud.
    /// </summary>
    [TestMethod]
    public void A_table_rebuild_under_an_unchanged_index_fails_the_migration_and_loses_nothing()
    {
        CollectionAssert.AreEqual(
            new[] { nameof(AlterColumnOperation), """CREATE INDEX "IX_people_lower_name" ON "People" (lower("Name"));""" },
            Shape<People.Indexed, People.IndexedWithARequiredEmail>());

        AssertRefusedAndUnchanged<People.Indexed, People.IndexedWithARequiredEmail>();
    }

    /// <summary>
    /// The same for a renamed table whose primary key is named after it, which is every table
    /// with EF Core's default names: <c>DropPrimaryKey</c> and <c>AddPrimaryKey</c> stand beside
    /// the rename, and SQLite rebuilds the table for each.
    /// </summary>
    [TestMethod]
    public void A_renamed_table_whose_key_is_named_after_it_is_a_rebuild_and_fails_as_one()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(DropPrimaryKeyOperation), nameof(RenameTableOperation), nameof(AddPrimaryKeyOperation),
                """CREATE INDEX "IX_people_lower_name" ON "Persons" (lower("Name"));""",
            },
            Shape<People.Indexed, People.TableRenamed>());

        AssertRefusedAndUnchanged<People.Indexed, People.TableRenamed>();
    }

    /// <summary>
    /// The column the index reads is dropped, and the index of that name moves to another column.
    /// SQLite's provider rebuilds the table for the drop, so it is the same case: the create
    /// meets the index that is still there. Were the dropped column answered as a reason to take
    /// the index away first, the create would find the name free and the rebuild would take the
    /// new index (#145).
    /// </summary>
    [TestMethod]
    public void A_dropped_column_under_the_index_fails_the_migration_as_a_rebuild_does()
    {
        using var database = new SqliteDatabase();
        database.Create<People.IndexedByEmail>();
        var before = database.Indexes();

        var refused = Assert.ThrowsExactly<SqliteException>(() => database.Migrate<People.IndexedByEmail, People.IndexedWithoutTheEmail>());

        StringAssert.Contains(refused.Message, "index IX_people_lower_name already exists");
        CollectionAssert.AreEqual(before, database.Indexes());
    }

    private static void AssertRefusedAndUnchanged<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var database = new SqliteDatabase();
        database.Create<TFrom>();
        var tables = database.Tables();

        var refused = Assert.ThrowsExactly<SqliteException>(() => database.Migrate<TFrom, TTo>());

        StringAssert.Contains(refused.Message, "index IX_people_lower_name already exists");
        CollectionAssert.AreEqual(new[] { Index }, database.Indexes());
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

    // ── Where it is not loud yet (#145) ──
    //
    // Each of these applies and leaves the database without the index the model declares: the
    // create runs on the old table, under a name that is free at that moment, and the rebuild at
    // the end of the migration drops the old table. They are held as they are today, and each
    // turns over with the fix of #145: the migration is then refused, or the index is there.

    /// <summary>The contract's other answer for an object that cannot stay. On SQLite the drop only clears the way for the create that is lost.</summary>
    [TestMethod]
    public void Today_an_index_answered_as_rebuilt_around_the_rebuild_is_lost_without_a_word()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Indexed>();

        database.Migrate<People.Indexed, People.IndexedWithARequiredEmail>(contributor =>
            contributor.ImpactOverride = change => change is BaseChange.TableRebuilt ? new Impact.RebuildAround() : null);

        Assert.AreEqual(0, database.Indexes().Count, "#145 is fixed when this fails: the index is there, or the migration is refused.");
    }

    /// <summary>No answer of a contributor is involved: the index is new, so nothing is asked about it.</summary>
    [TestMethod]
    public void Today_an_index_declared_in_the_migration_that_rebuilds_its_table_is_lost_without_a_word()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Bare>();

        database.Migrate<People.Bare, People.IndexedWithARequiredEmail>();

        Assert.AreEqual(0, database.Indexes().Count, "#145 is fixed when this fails: the index is there, or the migration is refused.");
        CollectionAssert.AreEqual(new[] { Index }, Created<People.IndexedWithARequiredEmail>(), "EnsureCreated() builds the index for the same model.");
    }

    [TestMethod]
    public void Today_an_index_renamed_in_the_migration_that_rebuilds_its_table_is_lost_without_a_word()
    {
        using var database = new SqliteDatabase();
        database.Create<People.Indexed>();

        database.Migrate<People.Indexed, People.RenamedWithARequiredEmail>();

        Assert.AreEqual(0, database.Indexes().Count, "#145 is fixed when this fails: the index is there, or the migration is refused.");
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
}
