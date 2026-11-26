using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Legacy5x;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// Coming from <c>EFCore.ComplexIndexes</c> 5.x (#92, #18): the snapshot the published 5.6.0
/// scaffolded, against the same model declared with SchemaKit. Whether "no operations" is also
/// right, the database holding what the model says, is <c>LegacyHandoverLiveTests</c>.
/// </summary>
[TestClass]
public sealed class LegacyHandoverTests
{
    [TestMethod]
    public void The_fixture_is_what_5x_wrote()
    {
        // The reader is tested against this file. Were it scaffolded without 5.x's differ, it would
        // hold tables only, and every test below would compare nothing with nothing.
        var snapshot = File.ReadAllText(Path.Combine(LegacyFixture.Folder, "LegacyContextModelSnapshot.cs"));

        Assert.AreEqual(4, snapshot.Split("\"CustomExclusion:Constraints\"").Length - 1);
        StringAssert.Contains(snapshot, "\"CustomIndex:RenderingVersion\", 3");
        StringAssert.Contains(snapshot, "\"ProductVersion\", \"10.0.");
        Assert.AreEqual(5, LegacyFixture.Up.Sql().Count(sql => sql.Contains("EXCLUDE USING")));
    }

    [TestMethod]
    public void The_5x_snapshot_against_the_same_model_declared_here_is_no_operations()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), LegacyFixture.DiffTo<MovedModels.Declared>().Shape());

        using var context = LegacyFixture.Context<MovedModels.Declared>();
        Assert.IsFalse(context.GetService<IMigrationsModelDiffer>().HasDifferences(LegacyFixture.Snapshot(context), context.Relational()));
    }

    /// <summary>
    /// An expression 5.x took as SQL, kept as SQL in the model with <c>Sql.Raw</c>: the same
    /// constraint under the same default name, whichever side the diff starts from (#220).
    /// </summary>
    [TestMethod]
    public void An_expression_of_5x_kept_as_SQL_is_no_operations()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), LegacyFixture.DiffTo<MovedModels.DeclaredWithTheSqlOf5x>().Shape());

        using var context = LegacyFixture.Context<MovedModels.DeclaredWithTheSqlOf5x>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(context.Relational(), LegacyFixture.Snapshot(context)).Shape());
    }

    // ── A constraint on a type of a TPC hierarchy: a copy in every table (#189) ──

    [TestMethod]
    public void The_TPC_fixture_is_what_5x_wrote()
    {
        var snapshot = File.ReadAllText(Path.Combine(LegacyFixture.TpcFolder, "TpcContextModelSnapshot.cs"));

        // One annotation per declaring type, and nothing of the copies: 5.x made them at the diff.
        Assert.AreEqual(2, snapshot.Split("\"CustomExclusion:Constraints\"").Length - 1);
        StringAssert.Contains(snapshot, "\"CustomIndex:RenderingVersion\", 3");
        Assert.AreEqual(8, LegacyFixture.TpcUp.Sql().Count(sql => sql.Contains("EXCLUDE USING")));
    }

    /// <summary>
    /// The copies are 5.x's objects under 5.x's names, and a declaration here lands on the same
    /// tables under the same names: nothing to do, from the snapshot to the model and back.
    /// </summary>
    [TestMethod]
    public void The_5x_snapshot_of_a_TPC_hierarchy_against_the_same_model_declared_here_is_no_operations()
    {
        using var context = LegacyFixture.Context<MovedModels.TpcDeclared>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var (snapshot, model) = (LegacyFixture.TpcSnapshot(context), context.Relational());

        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(snapshot, model).Shape());
        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(model, snapshot).Shape());
        Assert.IsFalse(differ.HasDifferences(snapshot, model));
    }

    /// <summary>The control: every copy is seen, under the name 5.x gave it, and dropped by it.</summary>
    [TestMethod]
    public void Against_a_model_that_declares_nothing_every_copy_of_the_snapshot_is_dropped()
    {
        using var context = LegacyFixture.Context<MovedModels.TpcNothingDeclared>();

        CollectionAssert.AreEquivalent(
            new[]
            {
                """ALTER TABLE rentals DROP CONSTRAINT ex_rental;""",
                """ALTER TABLE boat_rentals DROP CONSTRAINT ex_rental_boat_rentals;""",
                """ALTER TABLE car_rentals DROP CONSTRAINT ex_rental_car_rentals;""",
                """ALTER TABLE rentals DROP CONSTRAINT "EX_rentals_During";""",
                """ALTER TABLE boat_rentals DROP CONSTRAINT "EX_boat_rentals_During";""",
                """ALTER TABLE car_rentals DROP CONSTRAINT "EX_car_rentals_During";""",
                """ALTER TABLE fishing_permits DROP CONSTRAINT ex_permit_fishing_permits;""",
                """ALTER TABLE parking_permits DROP CONSTRAINT ex_permit_parking_permits;""",
            },
            context.GetService<IMigrationsModelDiffer>().GetDifferences(LegacyFixture.TpcSnapshot(context), context.Relational()).Shape());
    }

    /// <summary>
    /// The control of the test above, and what a consumer gets who has not declared anything yet:
    /// every constraint of the snapshot is seen, under the name 5.x gave it, and is dropped by it.
    /// </summary>
    [TestMethod]
    public void Against_a_model_that_declares_nothing_every_constraint_of_the_snapshot_is_dropped()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                """ALTER TABLE "Slot" DROP CONSTRAINT "EX_Slot_During";""",
                """ALTER TABLE ops.shifts DROP CONSTRAINT ex_shift_site;""",
                """ALTER TABLE ops.shifts DROP CONSTRAINT ex_shift_active;""",
                """ALTER TABLE "Lease" DROP CONSTRAINT "EX_Lease_lowerCode_Term_Span";""",
                """ALTER TABLE "Booking" DROP CONSTRAINT "EX_Booking_RoomId_During";""",
            },
            LegacyFixture.DiffTo<MovedModels.NothingDeclared>().Shape());
    }

    [TestMethod]
    public void A_constraint_declared_under_another_name_is_renamed_in_place()
    {
        CollectionAssert.AreEqual(
            new[] { """ALTER TABLE ops.shifts RENAME CONSTRAINT ex_shift_site TO ex_shift_per_site;""" },
            LegacyFixture.DiffTo<MovedModels.OneRenamed>().Shape());
    }

    /// <summary>A difference is never taken for none: the constraint the model declares replaces the one 5.x built.</summary>
    [TestMethod]
    public void A_constraint_declared_with_another_condition_is_rebuilt()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                // What is created is created as for any model: with the extension "=" on an integer needs.
                """CREATE EXTENSION IF NOT EXISTS btree_gist;""",
                """ALTER TABLE ops.shifts DROP CONSTRAINT ex_shift_site;""",
                """ALTER TABLE ops.shifts ADD CONSTRAINT ex_shift_site EXCLUDE USING gist (worker_id WITH =, site_id WITH =, during WITH &&);""",
            },
            LegacyFixture.DiffTo<MovedModels.OneWithoutItsCondition>().Shape());
    }

    /// <summary>The moved model with one change of its own, as a first migration after the move holds one.</summary>
    public sealed class DeclaredWithANewColumn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            MovedModels.Declared.Build(modelBuilder);
            modelBuilder.Entity<Slot>().Property<string>("Note");
        }
    }

    /// <summary>
    /// What <c>migrations add</c> asks for when the first migration after the move holds a change:
    /// Up from the snapshot to the model, and, since Up is not empty, Down from the model to the
    /// snapshot (#98). A diff of no operations never asks for Down, which is why the test above
    /// did not see it.
    /// </summary>
    [TestMethod]
    public void The_first_migration_after_the_move_scaffolds_with_a_change_of_its_own()
    {
        using var context = LegacyFixture.Context<DeclaredWithANewColumn>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var snapshot = LegacyFixture.Snapshot(context);

        CollectionAssert.AreEqual(new[] { "AddColumnOperation" }, differ.GetDifferences(snapshot, context.Relational()).Shape());
        CollectionAssert.AreEqual(new[] { "DropColumnOperation" }, differ.GetDifferences(context.Relational(), snapshot).Shape());
    }

    /// <summary>What <c>migrations remove</c> asks of the last migration 5.x scaffolded: two snapshots, neither the context's model.</summary>
    [TestMethod]
    public void The_last_migration_of_5x_can_be_removed()
    {
        using var context = LegacyFixture.Context<MovedModels.Declared>();
        var snapshot = LegacyFixture.Snapshot(context);

        Assert.IsFalse(context.GetService<IMigrationsModelDiffer>().HasDifferences(snapshot, snapshot));
    }

    [TestMethod]
    public void The_5x_annotation_in_the_contexts_own_model_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => LegacyFixture.DiffTo<MovedModels.DeclaredAndTheOldAnnotationKept>());

        StringAssert.Contains(refused.Message, "Slot carries the annotation 'CustomExclusion:Constraints' in the context's own model");
    }
}
