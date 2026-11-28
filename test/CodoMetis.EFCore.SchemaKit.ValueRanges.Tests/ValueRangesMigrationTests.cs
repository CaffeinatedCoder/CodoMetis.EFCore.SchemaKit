using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests;

/// <summary>
/// What a migration holds for an exclusion constraint and a GiST index over a column that
/// CodoMetis.ValueRanges maps (#4), without a server. The two libraries share no code: SchemaKit
/// reads the column's store type, which ValueRanges' mapping states. Whether the statements do
/// what they say is <see cref="Integration.ValueRangesLiveTests" />'s.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ValueRangesMigrationTests
{
    /// <summary>"=" on an integer under GiST has an operator class through this extension only.</summary>
    private const string Extension = "CREATE EXTENSION IF NOT EXISTS btree_gist;";

    internal const string AddBooking =
        """ALTER TABLE bookings ADD CONSTRAINT ex_booking_room_during EXCLUDE USING gist ("RoomId" WITH =, "During" WITH &&) WHERE ("CancelledOn" IS NULL);""";

    internal const string AddClosure =
        """ALTER TABLE closures ADD CONSTRAINT ex_closure_room_days EXCLUDE USING gist ("RoomId" WITH =, "Days" WITH &&);""";

    internal const string AddShift =
        """ALTER TABLE shifts ADD CONSTRAINT ex_shift_desk_hours EXCLUDE USING gist ("DeskId" WITH =, "Hours" WITH &&);""";

    internal const string AddStay =
        """ALTER TABLE stays ADD CONSTRAINT ex_stay_room_period EXCLUDE USING gist ("RoomId" WITH =, "Period" WITH &&);""";

    internal const string CreateIndex = """CREATE INDEX ix_booking_during ON bookings USING gist ("During");""";

    // ── The column each type gets: the one thing SchemaKit reads of ValueRanges ──

    private static string? StoreType<TModel>(string table, string column) where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return context.Relational().FindTable(table, null)!.FindColumn(column)!.StoreType;
    }

    /// <summary>
    /// The contract between the two libraries is this name. SchemaKit takes a column for one that
    /// GiST has an operator class for by its ending, so a mapping that named its column otherwise
    /// would get <c>btree_gist</c> and still apply; one that named a scalar's type would not be a range.
    /// </summary>
    [TestMethod]
    public void Each_type_of_ValueRanges_is_a_column_of_the_range_type_its_documentation_names()
    {
        Assert.AreEqual("daterange", StoreType<Bookings.Declared>("bookings", "During"));
        Assert.AreEqual("datemultirange", StoreType<Closures.Declared>("closures", "Days"));
        Assert.AreEqual("timerange", StoreType<Shifts.Declared>("shifts", "Hours"));
        Assert.AreEqual("daterange", StoreType<Stays.Declared>("stays", "Period"));
    }

    // ── An exclusion constraint: one statement, after the extension its "=" element needs ──

    [TestMethod]
    public void A_constraint_over_a_DateRange_is_one_statement_after_the_extension()
        => CollectionAssert.AreEqual(new[] { Extension, AddBooking }, Contexts.Diff<Bookings.Bare, Bookings.Declared>().Shape());

    [TestMethod]
    public void A_constraint_over_a_RangeSet_is_one_statement_after_the_extension()
        => CollectionAssert.AreEqual(new[] { Extension, AddClosure }, Contexts.Diff<Closures.Bare, Closures.Declared>().Shape());

    [TestMethod]
    public void A_constraint_over_a_TimeRange_is_one_statement_after_the_extension()
        => CollectionAssert.AreEqual(new[] { Extension, AddShift }, Contexts.Diff<Shifts.Bare, Shifts.Declared>().Shape());

    [TestMethod]
    public void A_constraint_over_a_LocalDateRange_is_one_statement_after_the_extension()
        => CollectionAssert.AreEqual(new[] { Extension, AddStay }, Contexts.Diff<Stays.Bare, Stays.Declared>().Shape());

    /// <summary>A constraint no longer declared is dropped by its name, and nothing else is written.</summary>
    [TestMethod]
    public void A_constraint_no_longer_declared_is_dropped_by_name()
    {
        CollectionAssert.AreEqual(new[] { "ALTER TABLE bookings DROP CONSTRAINT ex_booking_room_during;" }, Contexts.Diff<Bookings.Declared, Bookings.Bare>().Shape());
        CollectionAssert.AreEqual(new[] { "ALTER TABLE closures DROP CONSTRAINT ex_closure_room_days;" }, Contexts.Diff<Closures.Declared, Closures.Bare>().Shape());
        CollectionAssert.AreEqual(new[] { "ALTER TABLE shifts DROP CONSTRAINT ex_shift_desk_hours;" }, Contexts.Diff<Shifts.Declared, Shifts.Bare>().Shape());
        CollectionAssert.AreEqual(new[] { "ALTER TABLE stays DROP CONSTRAINT ex_stay_room_period;" }, Contexts.Diff<Stays.Declared, Stays.Bare>().Shape());
    }

    /// <summary>
    /// <c>timerange</c> has to exist before a table has a column of it, and before the constraint.
    /// The model creates it (<c>HasPostgresRange</c>), which EF Core writes as an operation of the
    /// database; SchemaKit's constraint comes after EF Core's operations, the type's among them.
    /// </summary>
    [TestMethod]
    public void The_custom_range_type_is_created_before_the_table_and_the_constraint()
    {
        string[] expected = [Extension, "AlterDatabaseOperation", "CreateTableOperation", AddShift];

        CollectionAssert.AreEqual(expected, Contexts.Initial<Shifts.Declared>().Shape());
        CollectionAssert.AreEqual(expected, Contexts.Diff<Shifts.Nothing, Shifts.Declared>().Shape());
    }

    // ── btree_gist: decided by the column's store type, as for a column of Npgsql's own ──

    /// <summary>
    /// A range and a multirange have a GiST operator class of their own, so a constraint over
    /// one alone installs nothing. Each of the four columns is taken for what it is: with a
    /// store type SchemaKit did not know for a range, the extension's statement would be here.
    /// </summary>
    [TestMethod]
    public void A_constraint_over_the_range_alone_installs_nothing()
    {
        CollectionAssert.AreEqual(
            new[] { """ALTER TABLE bookings ADD CONSTRAINT ex_booking_during EXCLUDE USING gist ("During" WITH &&);""" },
            Contexts.Diff<Bookings.Bare, Bookings.DuringAlone>().Shape());
        CollectionAssert.AreEqual(
            new[] { """ALTER TABLE closures ADD CONSTRAINT ex_closure_days EXCLUDE USING gist ("Days" WITH &&);""" },
            Contexts.Diff<Closures.Bare, Closures.DaysAlone>().Shape());
        CollectionAssert.AreEqual(
            new[] { """ALTER TABLE shifts ADD CONSTRAINT ex_shift_hours EXCLUDE USING gist ("Hours" WITH &&);""" },
            Contexts.Diff<Shifts.Bare, Shifts.HoursAlone>().Shape());
        CollectionAssert.AreEqual(
            new[] { """ALTER TABLE stays ADD CONSTRAINT ex_stay_period EXCLUDE USING gist ("Period" WITH &&);""" },
            Contexts.Diff<Stays.Bare, Stays.PeriodAlone>().Shape());
    }

    // ── Against Npgsql's own range: the same migration ──

    /// <summary>
    /// The twin of each model has Npgsql's own range where the model has a type of ValueRanges,
    /// and the typed <c>Overlaps</c> where the model has <c>WithUncheckedOperator</c>. Both lead to
    /// one first migration, EF Core's operations and SchemaKit's statements alike.
    /// </summary>
    [TestMethod]
    public void A_column_of_ValueRanges_and_one_of_Npgsqls_own_lead_to_the_same_migration()
    {
        CollectionAssert.AreEqual(Contexts.Initial<Bookings.Twin>().Shape(), Contexts.Initial<Bookings.Declared>().Shape());
        CollectionAssert.AreEqual(Contexts.Initial<Closures.Twin>().Shape(), Contexts.Initial<Closures.Declared>().Shape());
        CollectionAssert.AreEqual(Contexts.Initial<Stays.Twin>().Shape(), Contexts.Initial<Stays.Declared>().Shape());
        CollectionAssert.AreEqual(Contexts.Initial<Bookings.TwinIndexed>().Shape(), Contexts.Initial<Bookings.Indexed>().Shape());

        // And the scripts, which also say each column's type.
        Assert.AreEqual(CreateScript<Bookings.Twin>(), CreateScript<Bookings.Declared>());
        Assert.AreEqual(CreateScript<Closures.Twin>(), CreateScript<Closures.Declared>());
        Assert.AreEqual(CreateScript<Stays.Twin>(), CreateScript<Stays.Declared>());
        Assert.AreEqual(CreateScript<Bookings.TwinIndexed>(), CreateScript<Bookings.Indexed>());
    }

    private static string CreateScript<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return context.Database.GenerateCreateScript();
    }

    /// <summary>SchemaKit's dialect is put in place of the kernel's whichever library is registered first.</summary>
    [TestMethod]
    public void The_order_of_the_two_registrations_changes_nothing()
    {
        CollectionAssert.AreEqual(Contexts.Initial<Bookings.Declared>().Shape(), Contexts.Initial<Bookings.DeclaredValueRangesFirst>().Shape());
        CollectionAssert.AreEqual(new[] { Extension, AddBooking }, Contexts.Diff<Bookings.Bare, Bookings.DeclaredValueRangesFirst>().Shape());
    }

    // ── A GiST index ──

    /// <summary>A range has a GiST operator class, so the index needs no extension.</summary>
    [TestMethod]
    public void A_GiST_index_over_a_DateRange_is_one_statement_and_needs_no_extension()
        => CollectionAssert.AreEqual(new[] { CreateIndex }, Contexts.Diff<Bookings.Bare, Bookings.Indexed>().Shape());

    [TestMethod]
    public void An_index_no_longer_declared_is_dropped_by_name()
        => CollectionAssert.AreEqual(new[] { "DROP INDEX ix_booking_during;" }, Contexts.Diff<Bookings.Indexed, Bookings.Bare>().Shape());

    /// <summary>
    /// The README's sample for a range of ValueRanges, and the statements it shows beside it: the
    /// extension first, then the index, then the constraint.
    /// </summary>
    [TestMethod]
    public void The_declaration_the_README_shows_is_the_statements_it_shows()
        => CollectionAssert.AreEqual(
            new[]
            {
                Extension,
                CreateIndex,
                """ALTER TABLE bookings ADD CONSTRAINT ex_booking_room_during EXCLUDE USING gist ("RoomId" WITH =, "During" WITH &&);""",
            },
            Contexts.Diff<Bookings.Bare, Bookings.AsInTheReadme>().Shape());

    // ── A model against its own snapshot ──

    private static (IRelationalModel Stored, string Code) Snapshot(DbContext context)
    {
        var (snapshot, code) = Snapshots.Of(context);
        return (context.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true, validationLogger: null).GetRelationalModel(), code);
    }

    private static void AssertNoDifferenceFromItsSnapshot<TModel>(string storedUnder) where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var (stored, code) = Snapshot(context);

        StringAssert.Contains(code, storedUnder, code);
        Assert.AreEqual(0, differ.GetDifferences(stored, context.Relational()).Count, code);
        Assert.AreEqual(0, differ.GetDifferences(context.Relational(), stored).Count, code);
        Assert.IsFalse(differ.HasDifferences(stored, context.Relational()), code);
        Assert.IsFalse(differ.HasDifferences(context.Relational(), stored), code);

        // And the snapshot answers what the model answers about the column's type: from nothing,
        // both lead to the same operations, the extension's statement among them or not.
        var fromTheModel = differ.GetDifferences(null, context.Relational()).Shape();
        CollectionAssert.AreEqual(fromTheModel, differ.GetDifferences(null, stored).Shape(), code);
        Assert.IsTrue(fromTheModel.Any(statement => statement.Contains("USING gist", StringComparison.Ordinal)), string.Join("\n", fromTheModel));
    }

    /// <summary>
    /// The diff a consumer runs after every <c>migrations add</c>: the model against the snapshot
    /// EF Core scaffolded and compiled. A snapshot is a property bag without converters: it holds
    /// the property as the <c>NpgsqlRange&lt;T&gt;</c> ValueRanges converts it to and the column's
    /// type as text, so on that side the mapping is Npgsql's own and on the model's it is
    /// ValueRanges'. Anything but nothing here is a migration nobody asked for.
    /// </summary>
    [TestMethod]
    public void A_model_and_its_compiled_snapshot_differ_in_nothing()
    {
        AssertNoDifferenceFromItsSnapshot<Bookings.Declared>("CodoMetis:Pg.Exclusion:");
        AssertNoDifferenceFromItsSnapshot<Closures.Declared>("CodoMetis:Pg.Exclusion:");
        AssertNoDifferenceFromItsSnapshot<Stays.Declared>("CodoMetis:Pg.Exclusion:");
        AssertNoDifferenceFromItsSnapshot<Bookings.Indexed>("CodoMetis:Pg.Index:");
        AssertNoDifferenceFromItsSnapshot<Bookings.AsInTheReadme>("CodoMetis:Pg.Index:");
    }

    /// <summary>The constraints over a range alone, whose need for no extension is read from the store type on both sides.</summary>
    [TestMethod]
    public void A_range_alone_and_its_compiled_snapshot_differ_in_nothing()
    {
        AssertNoDifferenceFromItsSnapshot<Bookings.DuringAlone>("CodoMetis:Pg.Exclusion:");
        AssertNoDifferenceFromItsSnapshot<Closures.DaysAlone>("CodoMetis:Pg.Exclusion:");
        AssertNoDifferenceFromItsSnapshot<Stays.PeriodAlone>("CodoMetis:Pg.Exclusion:");
    }

    /// <summary>
    /// What a snapshot holds for each property: the type on the other side of ValueRanges'
    /// converter, which is Npgsql's, and never a type of ValueRanges. So a snapshot compiles
    /// without ValueRanges, and what maps its property is whatever maps that pair.
    /// </summary>
    [TestMethod]
    public void A_snapshot_holds_the_property_as_Npgsqls_range_and_the_column_type_as_text()
    {
        StringAssert.Contains(SnapshotCode<Bookings.Declared>(), """b.Property<NpgsqlRange<DateOnly>>("During")""");
        StringAssert.Contains(SnapshotCode<Closures.Declared>(), """b.Property<NpgsqlRange<DateOnly>[]>("Days")""");
        StringAssert.Contains(SnapshotCode<Shifts.Declared>(), """b.Property<NpgsqlRange<TimeOnly>>("Hours")""");
        StringAssert.Contains(SnapshotCode<Stays.Declared>(), """b.Property<NpgsqlRange<LocalDate>>("Period")""");

        foreach (var code in new[] { SnapshotCode<Bookings.Declared>(), SnapshotCode<Closures.Declared>(), SnapshotCode<Shifts.Declared>(), SnapshotCode<Stays.Declared>() })
            Assert.IsFalse(code.Contains("CodoMetis.ValueRanges;", StringComparison.Ordinal), code);
    }

    private static string SnapshotCode<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return Snapshots.Of(context).Code;
    }

    // ── A TimeRange column: a defect that is not SchemaKit's, held here until it is gone ──

    /// <summary>
    /// The snapshot of a <c>TimeRange</c> column is <c>NpgsqlRange&lt;TimeOnly&gt;</c> with the
    /// column type <c>timerange</c>, and in a context wired as ValueRanges documents nothing maps
    /// that pair: ValueRanges maps <c>TimeRange</c>, and Npgsql knows no <c>timerange</c>. EF
    /// Core's own differ then throws a <see cref="NullReferenceException" /> when the snapshot is
    /// the diff's target, which is the Down of every later <c>migrations add</c> that has
    /// operations. It does so without SchemaKit in the context, which is the first half of this
    /// test; SchemaKit's differ hands the two models on and changes nothing about it.
    /// </summary>
    /// <remarks>
    /// Measured against ValueRanges 11.0.0-preview.1. When this test fails because nothing is
    /// thrown, the defect is gone: move the two models of <see cref="Shifts" /> into
    /// <see cref="A_model_and_its_compiled_snapshot_differ_in_nothing" /> and
    /// <see cref="A_range_alone_and_its_compiled_snapshot_differ_in_nothing" />, and take this
    /// test and the paragraph of the guide that names it out.
    /// </remarks>
    [TestMethod]
    public void The_snapshot_of_a_TimeRange_column_fails_as_the_target_of_a_diff_with_or_without_SchemaKit()
    {
        using (var withoutSchemaKit = Contexts.Of<Shifts.BareWithoutSchemaKit>())
        {
            var differ = withoutSchemaKit.GetService<IMigrationsModelDiffer>();
            var (stored, code) = Snapshot(withoutSchemaKit);

            Assert.AreEqual("Microsoft.EntityFrameworkCore.Relational", differ.GetType().Assembly.GetName().Name);
            Assert.AreEqual(0, differ.GetDifferences(stored, withoutSchemaKit.Relational()).Count, code);
            Assert.ThrowsExactly<NullReferenceException>(() => differ.GetDifferences(withoutSchemaKit.Relational(), stored), code);
        }

        foreach (var context in new DbContext[] { Contexts.Of<Shifts.Declared>(), Contexts.Of<Shifts.HoursAlone>() })
        {
            using var _ = context;
            var differ = context.GetService<IMigrationsModelDiffer>();
            var (stored, code) = Snapshot(context);

            // The way a consumer meets a snapshot first, as the source of a diff, is as for every other column.
            StringAssert.Contains(code, "CodoMetis:Pg.Exclusion:", code);
            Assert.AreEqual(0, differ.GetDifferences(stored, context.Relational()).Count, code);
            Assert.IsFalse(differ.HasDifferences(stored, context.Relational()), code);

            var thrown = Assert.ThrowsExactly<NullReferenceException>(() => differ.GetDifferences(context.Relational(), stored), code);
            Assert.AreEqual("Microsoft.EntityFrameworkCore.Relational", thrown.TargetSite?.DeclaringType?.Assembly.GetName().Name, thrown.ToString());
        }
    }

    /// <summary>
    /// The way round it that was measured: <c>MapRange&lt;TimeOnly&gt;("timerange")</c> beside
    /// <c>UseValueRanges()</c>. Npgsql then maps the snapshot's property, ValueRanges still maps
    /// the model's, and the two differ in nothing, both ways.
    /// </summary>
    [TestMethod]
    public void With_timerange_mapped_to_Npgsqls_range_too_the_model_and_its_snapshot_differ_in_nothing()
    {
        AssertNoDifferenceFromItsSnapshot<Shifts.DeclaredWithTimeRangeMapped>("CodoMetis:Pg.Exclusion:");

        // The mapping of the model's own property is still ValueRanges': the migration is the one of the other wiring.
        CollectionAssert.AreEqual(Contexts.Initial<Shifts.Declared>().Shape(), Contexts.Initial<Shifts.DeclaredWithTimeRangeMapped>().Shape());
        Assert.AreEqual(CreateScript<Shifts.Declared>(), CreateScript<Shifts.DeclaredWithTimeRangeMapped>());
    }

    [TestMethod]
    public void A_declared_constraint_is_a_pending_change()
    {
        using var bare = Contexts.Of<Bookings.Bare>();
        using var declared = Contexts.Of<Bookings.Declared>();

        Assert.IsTrue(declared.GetService<IMigrationsModelDiffer>().HasDifferences(bare.Relational(), declared.Relational()));
    }
}
