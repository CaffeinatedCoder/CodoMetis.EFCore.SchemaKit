using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

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

    public void Execute(string sql)
    {
        using var connection = Open();
        connection.Execute(sql);
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

    public string ConstraintOid(string name)
    {
        using var connection = Open();
        return connection.Column($"SELECT oid::text FROM pg_constraint WHERE conname = '{name}'").Single();
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
    }

    public void Dispose()
    {
        using var server = PostgresServer.Open();
        server.Execute($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }
}

/// <summary>
/// The oracles of the plan's section 4 for exclusion constraints, on a live server: what the
/// catalog holds after each path that creates the constraint, whether the rows it must refuse are
/// refused, and whether a migration leaves the catalog <c>EnsureCreated()</c> would have built.
/// A statement that reads correctly proves none of it.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class PgExclusionLiveTests
{
    private const string Scheduling =
        """public.role_grants | ex_role_grant_active_period | index ex_role_grant_active_period | EXCLUDE USING gist ("GranteeId" WITH =, "RoleId" WITH =, "Period" WITH &&) WHERE (("RevokedOn" IS NULL)) DEFERRABLE INITIALLY DEFERRED""";

    // From the first day up to, and without, the second: PostgreSQL's own form of a date range.
    private static NpgsqlRange<DateOnly> Days(int from, int to)
        => new(new DateOnly(2026, 1, from), lowerBoundIsInclusive: true, new DateOnly(2026, 1, to), upperBoundIsInclusive: false);

    private static RoleGrant Grant(int grantee, int role, int from, int to, bool revoked = false)
        => new() { GranteeId = grantee, RoleId = role, Period = Days(from, to), RevokedOn = revoked ? new DateOnly(2026, 2, 1) : null };

    // ── Every path that creates the constraint ──

    [TestMethod]
    public void EnsureCreated_creates_the_constraint()
    {
        using var database = new LiveDatabase();
        using var context = Contexts.Of<Models.Scheduling>(database.ConnectionString);

        context.Database.EnsureCreated();

        CollectionAssert.AreEqual(new[] { Scheduling }, database.Exclusions());
    }

    /// <summary>The script is applied, not read: one that lacks a terminator, or orders the constraint before its table, fails here.</summary>
    [TestMethod]
    public void The_create_script_creates_the_constraint()
    {
        using var database = new LiveDatabase();
        using var context = Contexts.Of<Models.Scheduling>();

        database.Execute(context.Database.GenerateCreateScript());

        CollectionAssert.AreEqual(new[] { Scheduling }, database.Exclusions());
    }

    [TestMethod]
    public void A_first_migration_creates_the_constraint()
    {
        using var database = new LiveDatabase();

        database.Create<Models.Scheduling>();

        CollectionAssert.AreEqual(new[] { Scheduling }, database.Exclusions());
    }

    [TestMethod]
    public void A_default_name_and_an_expression_reach_the_catalog()
    {
        using var database = new LiveDatabase();

        database.Create<Models.Unnamed>();

        CollectionAssert.AreEqual(
            new[] { """public.role_grants | EX_role_grants_GranteeId_lowerNote_Period | index EX_role_grants_GranteeId_lowerNote_Period | EXCLUDE USING gist ("GranteeId" WITH =, lower("Note") WITH =, "Period" WITH &&)""" },
            database.Exclusions());
    }

    // ── btree_gist ──

    private static List<string> Extensions(LiveDatabase database)
    {
        using var connection = database.Open();
        return connection.Column("SELECT extname::text FROM pg_extension WHERE extname <> 'plpgsql' ORDER BY 1");
    }

    /// <summary>
    /// The premise of every other test here, said once: the database of a test has no extension,
    /// and the model declares none. Each path that created the constraint installed it.
    /// </summary>
    [TestMethod]
    public void A_database_without_the_extension_gets_it_from_the_migration()
    {
        using var database = new LiveDatabase();
        Assert.AreEqual(0, Extensions(database).Count);

        database.Create<Models.Scheduling>();

        CollectionAssert.AreEqual(new[] { "btree_gist" }, Extensions(database));
    }

    [TestMethod]
    public void A_constraint_on_a_range_alone_installs_nothing()
    {
        using var database = new LiveDatabase();

        database.Create<Models.RangeOnly>();

        Assert.AreEqual(0, Extensions(database).Count);
        Assert.AreEqual(1, database.Exclusions().Count);
    }

    /// <summary>An expression compared by <c>=</c> needs it as a column does: without the statement this add fails.</summary>
    [TestMethod]
    public void A_constraint_with_an_expression_compared_by_equality_gets_the_extension()
    {
        using var database = new LiveDatabase();

        database.Create<Models.ExpressionCompared>();

        CollectionAssert.AreEqual(new[] { "btree_gist" }, Extensions(database));
    }

    [TestMethod]
    public void A_model_that_declares_the_extension_itself_applies()
    {
        using var database = new LiveDatabase();

        database.Create<Models.SchedulingWithTheExtensionDeclared>();

        CollectionAssert.AreEqual(new[] { Scheduling }, database.Exclusions());
    }

    /// <summary>
    /// What the switch means, and why the injection exists: suppressed, the migration installs
    /// nothing, and on a database that lacks the extension PostgreSQL refuses the constraint. With
    /// the extension installed by other means the same migration applies.
    /// </summary>
    [TestMethod]
    public void Suppressed_the_migration_needs_the_extension_to_be_there()
    {
        using var without = new LiveDatabase();
        var refusal = Assert.ThrowsExactly<PostgresException>(without.Create<Models.SchedulingSuppressed>);
        Assert.AreEqual(PostgresErrorCodes.UndefinedObject, refusal.SqlState);
        StringAssert.Contains(refusal.MessageText, "no default operator class for access method \"gist\"");

        using var with = new LiveDatabase();
        with.Execute("CREATE EXTENSION btree_gist");
        with.Create<Models.SchedulingSuppressed>();
        CollectionAssert.AreEqual(new[] { Scheduling }, with.Exclusions());
    }

    /// <summary>A constraint is added to a table that exists, on a database that was created without the extension.</summary>
    [TestMethod]
    public void A_later_migration_installs_the_extension_too()
    {
        using var database = new LiveDatabase();
        database.Create<Models.RangeOnly>();

        database.Migrate<Models.RangeOnly, Models.Scheduling>();

        CollectionAssert.AreEqual(new[] { "btree_gist" }, Extensions(database));
        CollectionAssert.AreEqual(new[] { Scheduling }, database.Exclusions());
    }

    /// <summary>
    /// A column's type changes to one GiST has no operator class for. PostgreSQL rebuilds the
    /// constraint's index under it, and without the extension that fails (42704), so the migration
    /// brings it although it creates nothing (#81).
    /// </summary>
    /// <remarks>
    /// The other direction is not SchemaKit's to make work: PostgreSQL casts no text to ltree by
    /// itself (42804), so Npgsql's own statement for the column fails, with or without a constraint.
    /// </remarks>
    [TestMethod]
    public void A_type_change_that_begins_the_need_for_the_extension_applies()
    {
        using var database = new LiveDatabase();
        database.Create<Models.NoteAsLtree>();
        CollectionAssert.AreEqual(new[] { "ltree" }, Extensions(database));

        database.Migrate<Models.NoteAsLtree, Models.NoteAsText>();
        CollectionAssert.Contains(Extensions(database), "btree_gist");
        Assert.AreEqual(1, database.Exclusions().Count);
    }

    // ── Behaviour: what must fail does, what must pass does ──

    [TestMethod]
    public void Overlapping_rows_are_refused_and_rows_outside_the_filter_are_not()
    {
        using var database = new LiveDatabase();
        database.Create<Models.Immediate>();

        void Save(RoleGrant grant)
        {
            using var context = Contexts.Of<Models.Immediate>(database.ConnectionString);
            context.Grants.Add(grant);
            context.SaveChanges();
        }

        Save(Grant(grantee: 1, role: 1, from: 1, to: 10));

        // The same grantee and role over days that overlap: the one thing the constraint is for.
        var refusal = Assert.ThrowsExactly<DbUpdateException>(() => Save(Grant(1, 1, 5, 15)));
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, ((PostgresException)refusal.InnerException!).SqlState);
        Assert.AreEqual(Models.Name, ((PostgresException)refusal.InnerException!).ConstraintName);

        // Each of these differs from the refused row in one thing.
        Save(Grant(2, 1, 5, 15));                  // another grantee
        Save(Grant(1, 2, 5, 15));                  // another role
        Save(Grant(1, 1, 10, 20));                 // adjacent: a range ends before its upper bound
        Save(Grant(1, 1, 5, 15, revoked: true));   // revoked: outside the filter
        Save(Grant(1, 1, 5, 15, revoked: true));   // and a second one like it

        using var read = Contexts.Of<Models.Immediate>(database.ConnectionString);
        Assert.AreEqual(6, read.Grants.Count());
    }

    /// <summary><c>INITIALLY DEFERRED</c>: rows may overlap inside a transaction, and not when it commits.</summary>
    [TestMethod]
    public void A_deferred_constraint_is_checked_at_commit()
    {
        using var database = new LiveDatabase();
        database.Create<Models.Scheduling>();

        using var context = Contexts.Of<Models.Scheduling>(database.ConnectionString);
        var first = Grant(1, 1, 1, 10);
        context.Grants.Add(first);
        context.SaveChanges();

        // Overlapping for a moment, and put right before the commit.
        using (var transaction = context.Database.BeginTransaction())
        {
            context.Grants.Add(Grant(1, 1, 5, 15));
            context.SaveChanges();
            first.RevokedOn = new DateOnly(2026, 1, 5);
            context.SaveChanges();
            transaction.Commit();
        }

        // Left overlapping: the insert passes and the commit does not.
        using (var transaction = context.Database.BeginTransaction())
        {
            context.Grants.Add(Grant(1, 1, 8, 12));
            context.SaveChanges();
            var refusal = Assert.ThrowsExactly<PostgresException>(transaction.Commit);
            Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, refusal.SqlState);
        }
    }

    // ── Catalog equivalence: a migration leaves what EnsureCreated would have built ──

    private static void AssertMigratesTo<TFrom, TTo>(Action<LiveDatabase, LiveDatabase>? also = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var migrated = new LiveDatabase();
        using var created = new LiveDatabase();
        using (var context = Contexts.Of<TTo>(created.ConnectionString)) context.Database.EnsureCreated();

        migrated.Create<TFrom>();
        var before = migrated.Exclusions();
        migrated.Migrate<TFrom, TTo>();

        CollectionAssert.AreEqual(created.Exclusions(), migrated.Exclusions(), "Before the migration: " + string.Join("\n", before));
        also?.Invoke(migrated, created);
    }

    [TestMethod]
    public void Adding_a_constraint_to_a_table_that_exists()
        => AssertMigratesTo<Models.Bare, Models.Scheduling>((migrated, _) => CollectionAssert.AreEqual(new[] { Scheduling }, migrated.Exclusions()));

    [TestMethod]
    public void Dropping_a_constraint()
        => AssertMigratesTo<Models.Scheduling, Models.Bare>((migrated, _) => Assert.AreEqual(0, migrated.Exclusions().Count));

    /// <summary>In place: the constraint that is there afterwards is the one that was there before, and its index took the name too.</summary>
    [TestMethod]
    public void Renaming_a_constraint_keeps_the_constraint()
    {
        using var database = new LiveDatabase();
        database.Create<Models.Scheduling>();
        var oid = database.ConstraintOid(Models.Name);

        database.Migrate<Models.Scheduling, Models.Renamed>();

        Assert.AreEqual(oid, database.ConstraintOid("ex_renamed"));
        CollectionAssert.AreEqual(new[] { Scheduling.Replace(Models.Name, "ex_renamed") }, database.Exclusions());
    }

    [TestMethod]
    public void Changing_the_deferral() => AssertMigratesTo<Models.Scheduling, Models.Immediate>();

    [TestMethod]
    public void Changing_the_condition() => AssertMigratesTo<Models.Scheduling, Models.SchedulingWithAnotherCondition>(
        (migrated, _) => StringAssert.Contains(migrated.Exclusions().Single(), "\"Note\" IS NOT NULL"));

    [TestMethod]
    public void Changing_an_operator() => AssertMigratesTo<Models.Scheduling, Models.SchedulingAdjacent>(
        (migrated, _) => StringAssert.Contains(migrated.Exclusions().Single(), "WITH -|-"));

    [TestMethod]
    public void Changing_the_access_method() => AssertMigratesTo<Models.RangeOnly, Models.RangeOnlySpGist>(
        (migrated, _) => StringAssert.Contains(migrated.Exclusions().Single(), "USING spgist"));

    /// <summary>The constraint moves with its table; the catalog has it in the new schema, under its name.</summary>
    [TestMethod]
    public void Moving_the_table_to_another_schema() => AssertMigratesTo<Models.Scheduling, Models.SchedulingMoved>(
        (migrated, _) => StringAssert.StartsWith(migrated.Exclusions().Single(), "grants.role_grants | ex_role_grant_active_period"));

    [TestMethod]
    public void Moving_the_table_of_a_default_named_constraint() => AssertMigratesTo<Models.Unnamed, Models.UnnamedMoved>();

    /// <summary>The scalar stands after the range, and the constraint is created: the extension was installed for it.</summary>
    [TestMethod]
    public void A_scalar_after_the_range_gets_the_extension()
    {
        using var database = new LiveDatabase();

        database.Create<Models.ScalarSecond>();

        CollectionAssert.AreEqual(new[] { "btree_gist" }, Extensions(database));
    }

    /// <summary>The constraint follows EF Core's renames of its table and its columns by itself; a statement of SchemaKit's would have nothing left to do, or fail.</summary>
    [TestMethod]
    public void Renaming_the_table_and_columns_under_a_constraint() => AssertMigratesTo<Models.Scheduling, Models.SchedulingRenamedAround>();

    [TestMethod]
    public void Renaming_a_column_only_the_condition_reads() => AssertMigratesTo<Models.Scheduling, Models.SchedulingRevokedRenamed>();

    [TestMethod]
    public void Renaming_a_column_a_default_name_is_built_from() => AssertMigratesTo<Models.Unnamed, Models.UnnamedRenamedAround>();

    [TestMethod]
    public void Altering_the_type_of_an_element_column() => AssertMigratesTo<Models.Scheduling, Models.SchedulingWithLongGrantee>();

    /// <summary>
    /// The migration holds no drop of the constraint and adds one under the same name. That it
    /// applies is the measurement: the server took the old one away with the column.
    /// </summary>
    [TestMethod]
    public void Dropping_a_column_that_is_an_element() => AssertMigratesTo<Models.Scheduling, Models.WithoutRole>();

    /// <summary>
    /// Npgsql drops the column and adds it again, and PostgreSQL drops the constraint with it. The
    /// constraint is there afterwards, as <c>EnsureCreated()</c> builds it: before #80 this
    /// migration applied and left none, and no later diff saw it missing.
    /// </summary>
    [TestMethod]
    public void Making_an_element_column_a_computed_one() => AssertMigratesTo<Models.Scheduling, Models.SchedulingWithAComputedGrantee>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    [TestMethod]
    public void Making_a_computed_element_column_a_plain_one() => AssertMigratesTo<Models.SchedulingWithAComputedGrantee, Models.Scheduling>(
        (migrated, _) => CollectionAssert.AreEqual(new[] { Scheduling }, migrated.Exclusions()));

    [TestMethod]
    public void Making_the_conditions_column_a_computed_one() => AssertMigratesTo<Models.Scheduling, Models.SchedulingWithAComputedRevocation>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    /// <summary>On a type of a TPC hierarchy without derived types the constraint is in that type's table, over the columns it inherits.</summary>
    [TestMethod]
    public void A_constraint_on_a_leaf_of_TPC_is_in_its_own_table() => AssertMigratesTo<Models.ReservationsInThreeTables, Models.StaysInTheirOwnTableMayNotOverlap>(
        (migrated, _) => StringAssert.StartsWith(migrated.Exclusions().Single(), "public.stays | ex_stay"));

    /// <summary>The column's drop fails while the constraint is there (2BP01), so the migration applies only with the constraint dropped first.</summary>
    [TestMethod]
    public void Dropping_a_column_only_the_condition_reads() => AssertMigratesTo<Models.Scheduling, Models.WithoutRevocation>();

    /// <summary>What the server does when a column is dropped by hand, with the constraint in place: gone with it, or the drop refused.</summary>
    private static string DroppingByHand<TModel>(string column) where TModel : ITestModel
    {
        using var database = new LiveDatabase();
        database.Create<TModel>();

        try
        {
            database.Execute($"ALTER TABLE role_grants DROP COLUMN \"{column}\";");
        }
        catch (PostgresException refused)
        {
            return $"refused {refused.SqlState}, {database.Exclusions().Count} left";
        }

        return $"dropped, {database.Exclusions().Count} left";
    }

    /// <summary>
    /// The measurement behind <c>PgExclusionDescriptor.GoesWith</c> (#102), one row for each way a
    /// constraint reads a column. A column that is an element by itself goes, and the constraint
    /// with it, whatever else reads it. A column read only through an element's expression, or
    /// only by the condition, is held: the drop is refused while the constraint is there.
    /// </summary>
    [TestMethod]
    public void The_server_drops_the_constraint_with_an_elements_column_and_holds_every_other_column_it_reads()
    {
        const string Gone = "dropped, 0 left";
        const string Held = "refused 2BP01, 1 left";

        Assert.AreEqual(Gone, DroppingByHand<Models.WithAnExpressionOverTheNote>("GranteeId"), "an element by itself");
        Assert.AreEqual(Held, DroppingByHand<Models.WithAnExpressionOverTheNote>("Note"), "in an element's expression only");
        Assert.AreEqual(Held, DroppingByHand<Models.Scheduling>("RevokedOn"), "in the condition only");
        Assert.AreEqual(Gone, DroppingByHand<Models.WithTheNoteBothWays>("Note"), "an element, and in another element's expression");
        Assert.AreEqual(Gone, DroppingByHand<Models.WithTheGranteeInTheConditionToo>("GranteeId"), "an element, and in the condition");
        Assert.AreEqual(Gone, DroppingByHand<Models.WithTheGranteeBesideSqlThatNamesNoColumn>("GranteeId"), "an element, beside SQL that names no column");
        Assert.AreEqual(Held, DroppingByHand<Models.WithTheGranteeBesideSqlThatNamesNoColumn>("Note"), "read by SQL that names no column");
    }

    /// <summary>So the migration applies only with the constraint dropped first (#102): answered as lost, the column's drop failed.</summary>
    [TestMethod]
    public void Dropping_a_column_an_elements_expression_reads() => AssertMigratesTo<Models.WithAnExpressionOverTheNote, Models.WithoutTheNote>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    [TestMethod]
    public void Making_a_column_of_an_elements_expression_a_computed_one() => AssertMigratesTo<Models.WithAnExpressionOverTheNote, Models.WithAnExpressionOverAComputedNote>(
        (migrated, _) => StringAssert.Contains(migrated.Exclusions().Single(), "lower("));

    [TestMethod]
    public void Dropping_a_column_that_is_an_element_and_in_an_expression() => AssertMigratesTo<Models.WithTheNoteBothWays, Models.WithoutTheNoteAtAll>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    [TestMethod]
    public void Dropping_an_element_column_the_condition_reads_too() => AssertMigratesTo<Models.WithTheGranteeInTheConditionToo, Models.WithoutTheGrantee>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    [TestMethod]
    public void Dropping_an_element_column_beside_SQL_that_names_no_column() => AssertMigratesTo<Models.WithTheGranteeBesideSqlThatNamesNoColumn, Models.WithoutTheGrantee>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    /// <summary>The column that SQL reads, which only the server knows: the constraint is out of its way before the drop.</summary>
    [TestMethod]
    public void Dropping_a_column_that_SQL_which_names_no_column_reads() => AssertMigratesTo<Models.WithTheGranteeBesideSqlThatNamesNoColumn, Models.WithoutTheNote>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    [TestMethod]
    public void Renaming_a_column_an_elements_expression_reads() => AssertMigratesTo<Models.WithAnExpressionOverTheNote, Models.WithAnExpressionOverARenamedNote>(
        (migrated, _) => StringAssert.Contains(migrated.Exclusions().Single(), "memo"));

    [TestMethod]
    public void Altering_the_type_of_a_column_an_elements_expression_reads() => AssertMigratesTo<Models.WithAnExpressionOverTheNote, Models.WithAnExpressionOverAShortNote>(
        (migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));

    /// <summary>
    /// One constraint name on two tables in two schemas, and one of them dropped: the other is
    /// still there. A constraint is named within its table, so the table's schema is what tells
    /// the two apart.
    /// </summary>
    [TestMethod]
    public void Dropping_a_constraint_leaves_its_namesake_in_another_schema()
        => AssertMigratesTo<Models.TwoSchemas, Models.TwoSchemasOneConstraint>((migrated, _) =>
            CollectionAssert.AreEqual(
                new[] { """rooms.bookings | ex_no_overlap | index ex_no_overlap | EXCLUDE USING gist ("RoomId" WITH =, "Period" WITH &&)""" },
                migrated.Exclusions()));

    /// <summary>
    /// No <c>DROP CONSTRAINT IF EXISTS</c> in front of the add, as 5.x had it: a constraint of that
    /// name that is there already is a collision, and it fails instead of being replaced.
    /// </summary>
    [TestMethod]
    public void A_name_that_is_taken_fails_and_replaces_nothing()
    {
        using var database = new LiveDatabase();
        database.Create<Models.Bare>();
        database.Execute($"""ALTER TABLE role_grants ADD CONSTRAINT {Models.Name} EXCLUDE USING gist ("Period" WITH &&)""");

        var refusal = Assert.ThrowsExactly<PostgresException>(database.Migrate<Models.Bare, Models.Scheduling>);

        // The name is taken twice over, as a constraint of the table and as a relation of the schema.
        // The server reports the relation: the constraint's index.
        Assert.AreEqual(PostgresErrorCodes.DuplicateTable, refusal.SqlState);
        CollectionAssert.AreEqual(
            new[] { """public.role_grants | ex_role_grant_active_period | index ex_role_grant_active_period | EXCLUDE USING gist ("Period" WITH &&)""" },
            database.Exclusions());
    }
}
