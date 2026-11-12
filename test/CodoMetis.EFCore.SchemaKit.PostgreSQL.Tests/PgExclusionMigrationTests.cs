using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    private const string Drop = """ALTER TABLE role_grants DROP CONSTRAINT ex_role_grant_active_period;""";

    /// <summary>5.x's statement, clause for clause, without the <c>DROP CONSTRAINT IF EXISTS</c> it put in front.</summary>
    [TestMethod]
    public void The_create_script_adds_the_constraint_after_its_table()
    {
        using var context = Contexts.Of<Models.Scheduling>();
        var script = context.Database.GenerateCreateScript();

        StringAssert.Contains(script, Add);
        Assert.IsTrue(script.IndexOf("CREATE TABLE role_grants", StringComparison.Ordinal) < script.IndexOf(Add, StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("IF EXISTS ex_", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void A_new_constraint_is_one_statement()
        => CollectionAssert.AreEqual(new[] { Add }, Contexts.Diff<Models.Bare, Models.Scheduling>().Shape());

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
            new[] { Drop, Add.Replace(" DEFERRABLE INITIALLY DEFERRED", "") },
            Contexts.Diff<Models.Scheduling, Models.Immediate>().Shape());

    /// <summary>
    /// The default name of 5.x: <c>EX</c>, the table, then a column's name and an expression's
    /// letters and digits. An expression is written in parentheses.
    /// </summary>
    [TestMethod]
    public void A_constraint_without_a_name_gets_the_default_one()
        => CollectionAssert.AreEqual(
            new[] { """ALTER TABLE role_grants ADD CONSTRAINT "EX_role_grants_GranteeId_lowerNote_Period" EXCLUDE USING gist ("GranteeId" WITH =, (lower("Note")) WITH =, "Period" WITH &&);""" },
            Contexts.Diff<Models.Bare, Models.Unnamed>().Shape());

    // ── A model against its own snapshot ──

    /// <summary>
    /// The diff a consumer runs after every <c>migrations add</c>: the model against the snapshot
    /// EF Core scaffolded and compiled. Anything but nothing here is a migration nobody asked for,
    /// on every build.
    /// </summary>
    [TestMethod]
    public void A_model_and_its_compiled_snapshot_differ_in_nothing()
    {
        foreach (var context in new DbContext[] { Contexts.Of<Models.Scheduling>(), Contexts.Of<Models.Unnamed>(), Contexts.Of<Models.TwoSchemas>() })
        {
            using var _ = context;
            var differ = context.GetService<IMigrationsModelDiffer>();
            var (snapshot, code) = Snapshots.Of(context);
            var stored = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true, validationLogger: null).GetRelationalModel();

            Assert.IsTrue(code.Contains("CodoMetis:Pg.Exclusion:", StringComparison.Ordinal), code);
            Assert.AreEqual(0, differ.GetDifferences(stored, context.Relational()).Count, code);
            Assert.AreEqual(0, differ.GetDifferences(context.Relational(), stored).Count, code);
            Assert.IsFalse(differ.HasDifferences(stored, context.Relational()));
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
            new[] { "DropColumnOperation", Add.Replace("\"RoleId\" WITH =, ", "") },
            Contexts.Diff<Models.Scheduling, Models.WithoutRole>().Shape());

    /// <summary>
    /// With the column in its condition only, PostgreSQL refuses to drop the column while the
    /// constraint is there (2BP01). So the constraint goes first.
    /// </summary>
    [TestMethod]
    public void A_dropped_condition_column_needs_the_constraint_out_of_the_way_first()
        => CollectionAssert.AreEqual(
            new[] { Drop, "DropColumnOperation", Add.Replace(" WHERE (\"RevokedOn\" IS NULL)", "") },
            Contexts.Diff<Models.Scheduling, Models.WithoutRevocation>().Shape());

    /// <summary>A constraint is named within its table, so it is the table that is qualified.</summary>
    [TestMethod]
    public void A_drop_names_the_table_with_its_schema()
        => CollectionAssert.AreEqual(
            new[] { """ALTER TABLE grants.role_grants DROP CONSTRAINT ex_no_overlap;""" },
            Contexts.Diff<Models.TwoSchemas, Models.TwoSchemasOneConstraint>().Shape());
}
