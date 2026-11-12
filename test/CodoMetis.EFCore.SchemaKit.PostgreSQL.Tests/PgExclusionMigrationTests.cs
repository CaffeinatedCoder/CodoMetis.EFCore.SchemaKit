using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// What a migration holds for an exclusion constraint, without a server: the statements and their
/// place among EF Core's operations. Whether they do what they say is
/// <see cref="Integration.PgExclusionLiveTests" />'s, which applies each of these pairs.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PgExclusionMigrationTests
{
    // Npgsql writes a name bare where PostgreSQL reads it the same either way, and SchemaKit delimits
    // through the provider: a lower-case name has no quotes, one with a capital has.
    private const string Add =
        """ALTER TABLE role_grants ADD CONSTRAINT ex_role_grant_active_period EXCLUDE USING gist ("GranteeId" WITH =, "RoleId" WITH =, "Period" WITH &&) WHERE ("RevokedOn" IS NULL) DEFERRABLE INITIALLY DEFERRED;""";

    /// <summary>"=" on an integer under GiST has an operator class through this extension only.</summary>
    private const string Extension = "CREATE EXTENSION IF NOT EXISTS btree_gist;";

    private const string Drop = """ALTER TABLE role_grants DROP CONSTRAINT ex_role_grant_active_period;""";

    /// <summary>5.x's statement, clause for clause, without the <c>DROP CONSTRAINT IF EXISTS</c> it put in front.</summary>
    [TestMethod]
    public void The_create_script_adds_the_constraint_after_its_table()
    {
        using var context = Contexts.Of<Models.Scheduling>();
        var script = context.Database.GenerateCreateScript();

        StringAssert.Contains(script, Add);
        Assert.IsTrue(script.IndexOf(Extension, StringComparison.Ordinal) is >= 0 and var extension && extension < script.IndexOf("CREATE TABLE", StringComparison.Ordinal), script);
        Assert.IsTrue(script.IndexOf("CREATE TABLE role_grants", StringComparison.Ordinal) < script.IndexOf(Add, StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("IF EXISTS ex_", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void A_new_constraint_is_one_statement_after_the_extension_it_needs()
        => CollectionAssert.AreEqual(new[] { Extension, Add }, Contexts.Diff<Models.Bare, Models.Scheduling>().Shape());

    /// <summary>A drop names the constraint and says no <c>IF EXISTS</c>: one that is gone already is a database that differs from the snapshot.</summary>
    [TestMethod]
    public void A_constraint_no_longer_declared_is_dropped_by_name()
        => CollectionAssert.AreEqual(new[] { Drop }, Contexts.Diff<Models.Scheduling, Models.Bare>().Shape());

    [TestMethod]
    public void Another_name_is_a_rename_in_place()
        => CollectionAssert.AreEqual(
            new[] { """ALTER TABLE role_grants RENAME CONSTRAINT ex_role_grant_active_period TO ex_renamed;""" },
            Contexts.Diff<Models.Scheduling, Models.Renamed>().Shape());

    /// <summary>PostgreSQL alters the deferrability of a foreign key only, so here it takes a drop and an add.</summary>
    [TestMethod]
    public void Another_deferral_is_a_rebuild()
        => CollectionAssert.AreEqual(
            new[] { Extension, Drop, Add.Replace(" DEFERRABLE INITIALLY DEFERRED", "") },
            Contexts.Diff<Models.Scheduling, Models.Immediate>().Shape());

    /// <summary>
    /// The default name of 5.x: <c>EX</c>, the table, then a column's name and an expression's
    /// letters and digits. An expression is written in parentheses.
    /// </summary>
    [TestMethod]
    public void A_constraint_without_a_name_gets_the_default_one()
        => CollectionAssert.AreEqual(
            new[] { Extension, """ALTER TABLE role_grants ADD CONSTRAINT "EX_role_grants_GranteeId_lowerNote_Period" EXCLUDE USING gist ("GranteeId" WITH =, (lower("Note")) WITH =, "Period" WITH &&);""" },
            Contexts.Diff<Models.Bare, Models.Unnamed>().Shape());

    // ── A table two types share ──

    /// <summary>
    /// Declared on a stay, the constraint would refuse two overlapping holds as well: it is the
    /// table's, and nothing in it says "stay". So it is refused where the model is diffed, with the
    /// type to declare it on; declared there it says what it does.
    /// </summary>
    [TestMethod]
    public void A_constraint_on_a_derived_type_that_shares_its_table_is_refused()
    {
        using var onStay = Contexts.Of<Models.StaysMayNotOverlap>();
        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => onStay.Database.GenerateCreateScript());
        StringAssert.Contains(refusal.Message, "Stay declares Pg.Exclusion gist 'ex_stay'");
        StringAssert.Contains(refusal.Message, "Declare it on Reservation");

        using var onReservation = Contexts.Of<Models.ReservationsMayNotOverlap>();
        StringAssert.Contains(
            onReservation.Database.GenerateCreateScript(),
            """ALTER TABLE reservations ADD CONSTRAINT ex_stay EXCLUDE USING gist ("RoomId" WITH =, "Period" WITH &&);""");

        // A snapshot that still has it on the stay is the same constraint: moving it costs nothing.
        Assert.AreEqual(0, Contexts.Diff<Models.StaysMayNotOverlap, Models.ReservationsMayNotOverlap>().Count);
    }

    // ── btree_gist ──

    private static List<string> Create<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return context.GetService<IMigrationsModelDiffer>().GetDifferences(null, context.Relational()).Shape();
    }

    /// <summary>Once for a migration, however many constraints need it, and before anything else of the migration.</summary>
    [TestMethod]
    public void Two_constraints_that_need_the_extension_get_it_once_and_first()
    {
        var operations = Create<Models.TwoSchemas>();

        Assert.AreEqual(Extension, operations[0], string.Join("\n", operations));
        Assert.AreEqual(1, operations.Count(o => o.Contains("EXTENSION", StringComparison.Ordinal)));
        Assert.AreEqual(2, operations.Count(o => o.Contains("EXCLUDE", StringComparison.Ordinal)));
    }

    /// <summary>
    /// What needs it, by 5.x's rule: a column whose type GiST has no operator class for, and an
    /// expression compared by an operator B-tree has. A range needs nothing, and SP-GiST is not GiST.
    /// </summary>
    [TestMethod]
    public void The_extension_is_added_for_what_needs_it_and_for_nothing_else()
    {
        Assert.IsFalse(Create<Models.RangeOnly>().Any(o => o.Contains("EXTENSION", StringComparison.Ordinal)));
        Assert.IsFalse(Create<Models.RangeOnlySpGist>().Any(o => o.Contains("EXTENSION", StringComparison.Ordinal)));
        // A text column needs it under GiST (NoteAsText), and not here: btree_gist is GiST's.
        Assert.IsFalse(Create<Models.TextSpGist>().Any(o => o.Contains("EXTENSION", StringComparison.Ordinal)));
        Assert.AreEqual(Extension, Create<Models.ExpressionCompared>()[0]);
        Assert.AreEqual(Extension, Create<Models.Scheduling>()[0]);
    }

    [TestMethod]
    [DataRow("integer", false), DataRow("text", false), DataRow("character varying(50)", false), DataRow("uuid", false)]
    [DataRow("daterange", true), DataRow("int4range", true), DataRow("tstzmultirange", true), DataRow("TSTZRANGE", true)]
    [DataRow("point", true), DataRow("tsvector", true), DataRow("geometry(Point, 4326)", true), DataRow("ltree", true)]
    // No array has a GiST operator class of its own, an array of ranges included.
    [DataRow("integer[]", false), DataRow("daterange[]", false)]
    public void A_store_type_has_a_gist_operator_class_of_its_own_or_not(string storeType, bool has)
        => Assert.AreEqual(has, Internal.PgExclusionContributor.HasGistOperatorClass(storeType));

    /// <summary>Declared with <c>HasPostgresExtension</c>, the extension is Npgsql's to create, and it is created once.</summary>
    [TestMethod]
    public void A_model_that_declares_the_extension_itself_gets_no_second_statement()
    {
        var operations = Create<Models.SchedulingWithTheExtensionDeclared>();

        Assert.IsFalse(operations.Contains(Extension), string.Join("\n", operations));
        CollectionAssert.Contains(operations, "AlterDatabaseOperation");
        CollectionAssert.Contains(operations, Add);
    }

    [TestMethod]
    public void A_model_that_suppresses_the_injection_gets_none()
        => CollectionAssert.AreEqual(new[] { "CreateTableOperation", Add }, Create<Models.SchedulingSuppressed>());

    /// <summary>
    /// The extension says how a constraint comes to be, not what it is: a constraint that is there
    /// is the same constraint whether or not this model would install the extension for it.
    /// </summary>
    [TestMethod]
    public void Whether_the_extension_is_installed_for_it_does_not_change_a_constraint()
    {
        Assert.AreEqual(0, Contexts.Diff<Models.Scheduling, Models.SchedulingSuppressed>().Count);
        CollectionAssert.AreEqual(new[] { "AlterDatabaseOperation" }, Contexts.Diff<Models.Scheduling, Models.SchedulingWithTheExtensionDeclared>().Shape());
    }

    /// <summary>
    /// A column's type moves from one that needs the extension to one that does not. The constraint
    /// is the same by name and definition, and PostgreSQL keeps it through the change: were the
    /// need compared, this would drop and add it.
    /// </summary>
    [TestMethod]
    public void A_type_change_that_ends_the_need_for_the_extension_leaves_the_constraint_alone()
    {
        Assert.AreEqual(Extension, Create<Models.NoteAsText>()[0]);
        Assert.IsFalse(Create<Models.NoteAsLtree>().Contains(Extension));

        // Npgsql installs and removes ltree for the column by itself (AlterDatabaseOperation).
        // Nothing of SchemaKit's stands beside EF Core's operations, in either direction.
        foreach (var operations in new[] { Contexts.Diff<Models.NoteAsText, Models.NoteAsLtree>(), Contexts.Diff<Models.NoteAsLtree, Models.NoteAsText>() })
        {
            CollectionAssert.Contains(operations.Shape(), "AlterColumnOperation");
            Assert.AreEqual(0, operations.Sql().Count, string.Join(" | ", operations.Shape()));
        }
    }

    /// <summary>
    /// The switch is 5.x's annotation, key and value: a model that carried it there, or a snapshot
    /// that still does, says the same here.
    /// </summary>
    [TestMethod]
    public void The_suppress_switch_is_the_annotation_5x_wrote()
    {
        using var context = Contexts.Of<Models.SchedulingSuppressed>();

        Assert.AreEqual(true, context.GetService<IDesignTimeModel>().Model.FindAnnotation("CustomTemporal:SuppressAutoExtension")?.Value);
    }

    // ── A model against its own snapshot ──

    /// <summary>
    /// The diff a consumer runs after every <c>migrations add</c>: the model against the snapshot
    /// EF Core scaffolded and compiled. Anything but nothing here is a migration nobody asked for,
    /// on every build.
    /// </summary>
    [TestMethod]
    public void A_model_and_its_compiled_snapshot_differ_in_nothing()
    {
        foreach (var context in new DbContext[] { Contexts.Of<Models.Scheduling>(), Contexts.Of<Models.Unnamed>(), Contexts.Of<Models.TwoSchemas>(), Contexts.Of<Models.SchedulingSuppressed>() })
        {
            using var _ = context;
            var differ = context.GetService<IMigrationsModelDiffer>();
            var (snapshot, code) = Snapshots.Of(context);
            var stored = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true, validationLogger: null).GetRelationalModel();

            Assert.IsTrue(code.Contains("CodoMetis:Pg.Exclusion:", StringComparison.Ordinal), code);
            Assert.AreEqual(0, differ.GetDifferences(stored, context.Relational()).Count, code);
            Assert.AreEqual(0, differ.GetDifferences(context.Relational(), stored).Count, code);
            Assert.IsFalse(differ.HasDifferences(stored, context.Relational()));

            // And the snapshot answers what the model answers about a column's type: from nothing,
            // both lead to the same statements, the extension's among them or not.
            CollectionAssert.AreEqual(differ.GetDifferences(null, context.Relational()).Sql(), differ.GetDifferences(null, stored).Sql(), code);
        }
    }

    [TestMethod]
    public void A_declared_constraint_is_a_pending_change()
    {
        using var bare = Contexts.Of<Models.Bare>();
        using var scheduling = Contexts.Of<Models.Scheduling>();

        Assert.IsTrue(scheduling.GetService<IMigrationsModelDiffer>().HasDifferences(bare.Relational(), scheduling.Relational()));
    }

    // ── Under EF Core's own operations (the plan's section 3.7, as measured on PostgreSQL 16 and 18) ──

    /// <summary>A renamed table and renamed columns take the constraint along: nothing of SchemaKit's is in the migration.</summary>
    [TestMethod]
    public void Renames_of_the_table_and_of_columns_leave_a_named_constraint_alone()
    {
        var operations = Contexts.Diff<Models.Scheduling, Models.SchedulingRenamedAround>();

        Assert.AreEqual(0, operations.Sql().Count, string.Join("\n", operations.Shape()));
        CollectionAssert.IsSubsetOf(new[] { "RenameTableOperation", "RenameColumnOperation" }, operations.Shape());
    }

    /// <summary>
    /// What the condition reads is a dependency like what the elements read. Left out of the
    /// descriptor's list, this rename would not be seen and the constraint would be rebuilt for it.
    /// </summary>
    [TestMethod]
    public void A_renamed_column_of_the_condition_alone_leaves_the_constraint_alone()
        => CollectionAssert.AreEqual(new[] { "RenameColumnOperation" }, Contexts.Diff<Models.Scheduling, Models.SchedulingRevokedRenamed>().Shape());

    /// <summary>The default name is built from the columns, so a renamed column is a renamed constraint, in place and after EF Core's rename.</summary>
    [TestMethod]
    public void A_renamed_column_renames_a_constraint_that_is_named_after_it()
        => CollectionAssert.AreEqual(
            new[]
            {
                "RenameColumnOperation",
                """ALTER TABLE role_grants RENAME CONSTRAINT "EX_role_grants_GranteeId_lowerNote_Period" TO "EX_role_grants_grantee_lowerNote_Period";""",
            },
            Contexts.Diff<Models.Unnamed, Models.UnnamedRenamedAround>().Shape());

    [TestMethod]
    public void An_altered_column_leaves_the_constraint_alone()
        => CollectionAssert.AreEqual(new[] { "AlterColumnOperation" }, Contexts.Diff<Models.Scheduling, Models.SchedulingWithLongGrantee>().Shape());

    /// <summary>
    /// PostgreSQL drops an exclusion constraint with a column that is one of its elements, without
    /// saying so. So there is no drop to write, and one written after the column would fail.
    /// </summary>
    [TestMethod]
    public void A_dropped_element_column_takes_the_constraint_with_it_and_the_new_one_is_added_after()
        => CollectionAssert.AreEqual(
            new[] { Extension, "DropColumnOperation", Add.Replace("\"RoleId\" WITH =, ", "") },
            Contexts.Diff<Models.Scheduling, Models.WithoutRole>().Shape());

    /// <summary>
    /// With the column in its condition only, PostgreSQL refuses to drop the column while the
    /// constraint is there (2BP01). So the constraint goes first.
    /// </summary>
    [TestMethod]
    public void A_dropped_condition_column_needs_the_constraint_out_of_the_way_first()
        => CollectionAssert.AreEqual(
            new[] { Extension, Drop, "DropColumnOperation", Add.Replace(" WHERE (\"RevokedOn\" IS NULL)", "") },
            Contexts.Diff<Models.Scheduling, Models.WithoutRevocation>().Shape());

    /// <summary>A constraint is named within its table, so it is the table that is qualified.</summary>
    [TestMethod]
    public void A_drop_names_the_table_with_its_schema()
        => CollectionAssert.AreEqual(
            new[] { """ALTER TABLE grants.role_grants DROP CONSTRAINT ex_no_overlap;""" },
            Contexts.Diff<Models.TwoSchemas, Models.TwoSchemasOneConstraint>().Shape());
}
