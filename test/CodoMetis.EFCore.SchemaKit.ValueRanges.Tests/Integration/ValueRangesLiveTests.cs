using System.Data.Common;
using CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;
using CodoMetis.ValueRanges;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Integration;

/// <summary>
/// The oracles of the plan's section 4 for a column that CodoMetis.ValueRanges maps (#4), on a
/// live server: a migration leaves the catalog <c>EnsureCreated()</c> builds, a row that overlaps
/// is refused and one that does not is taken, and the planner takes the index for the query EF
/// Core writes. Beside each, the same model over Npgsql's own range leaves the same catalog.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ValueRangesLiveTests
{
    private static DateRange Days(int from, int to) => DateRange.CreateFinite(new DateOnly(2026, 1, from), new DateOnly(2026, 1, to));

    private static LocalDateRange LocalDays(int from, int to) => LocalDateRange.CreateFinite(new LocalDate(2026, 1, from), new LocalDate(2026, 1, to));

    // From the first hour up to, and without, the second: a range over a continuous type.
    private static TimeRange Hours(int from, int to) => TimeRange.CreateFinite(new TimeOnly(from, 0), new TimeOnly(to, 0));

    private static void Save<TModel>(LiveDatabase database, object row) where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>(database.ConnectionString);
        context.Add(row);
        context.SaveChanges();
    }

    private static void AssertRefused<TModel>(LiveDatabase database, object row, string constraint) where TModel : ITestModel
    {
        var refusal = Assert.ThrowsExactly<DbUpdateException>(() => Save<TModel>(database, row));
        var said = (PostgresException)refusal.InnerException!;
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, said.SqlState);
        Assert.AreEqual(constraint, said.ConstraintName);
    }

    private static int Count<TModel, TEntity>(LiveDatabase database)
        where TModel : ITestModel
        where TEntity : class
    {
        using var context = Contexts.Of<TModel>(database.ConnectionString);
        return context.Set<TEntity>().Count();
    }

    /// <summary>
    /// A table that exists gets the constraint by a migration, on a database without any extension,
    /// and holds what <c>EnsureCreated()</c> builds for the target model: the constraint as the
    /// server writes it, the column's type, and <c>btree_gist</c>, which the "=" element needs.
    /// </summary>
    private static LiveDatabase Migrated<TBare, TDeclared>(string constraint)
        where TBare : ITestModel
        where TDeclared : ITestModel
    {
        var migrated = new LiveDatabase();
        try
        {
            migrated.Create<TBare>();
            Assert.AreEqual(0, migrated.Extensions().Count);
            Assert.AreEqual(0, migrated.Exclusions().Count);

            migrated.Migrate<TBare, TDeclared>();

            using var created = new LiveDatabase();
            created.EnsureCreated<TDeclared>();

            CollectionAssert.AreEqual(new[] { constraint }, migrated.Exclusions());
            CollectionAssert.AreEqual(new[] { "btree_gist" }, migrated.Extensions());
            CollectionAssert.AreEqual(created.Catalog(), migrated.Catalog());
            return migrated;
        }
        catch
        {
            migrated.Dispose();
            throw;
        }
    }

    /// <summary>What <c>EnsureCreated()</c> builds for a model, as the catalog says it.</summary>
    private static List<string> Created<TModel>() where TModel : ITestModel
    {
        using var database = new LiveDatabase();
        database.EnsureCreated<TModel>();
        return database.Catalog();
    }

    // ── DateRange: a daterange column ──

    private const string BookingConstraint =
        """public.bookings | ex_booking_room_during | index ex_booking_room_during | EXCLUDE USING gist ("RoomId" WITH =, "During" WITH &&) WHERE (("CancelledOn" IS NULL))""";

    [TestMethod]
    public void A_constraint_over_a_DateRange_is_migrated_to_what_EnsureCreated_builds_and_refuses_an_overlap()
    {
        using var database = Migrated<Bookings.Bare, Bookings.Declared>(BookingConstraint);

        Save<Bookings.Declared>(database, new Booking { RoomId = 1, During = Days(1, 10) });

        // The same room over days that overlap: the one thing the constraint is for.
        AssertRefused<Bookings.Declared>(database, new Booking { RoomId = 1, During = Days(5, 15) }, Bookings.Constraint);

        // Each of these differs from the refused row in one thing.
        Save<Bookings.Declared>(database, new Booking { RoomId = 2, During = Days(5, 15) });                                         // another room
        Save<Bookings.Declared>(database, new Booking { RoomId = 1, During = Days(11, 20) });                                        // adjacent: a date range is closed, and canonical in the database
        Save<Bookings.Declared>(database, new Booking { RoomId = 1, During = Days(5, 15), CancelledOn = new DateOnly(2026, 1, 2) }); // cancelled: outside the condition

        Assert.AreEqual(4, Count<Bookings.Declared, Booking>(database));
    }

    // ── RangeSet<DateRange, DateOnly>: a datemultirange column ──

    private const string ClosureConstraint =
        """public.closures | ex_closure_room_days | index ex_closure_room_days | EXCLUDE USING gist ("RoomId" WITH =, "Days" WITH &&)""";

    [TestMethod]
    public void A_constraint_over_a_RangeSet_is_migrated_to_what_EnsureCreated_builds_and_refuses_an_overlap()
    {
        using var database = Migrated<Closures.Bare, Closures.Declared>(ClosureConstraint);

        Save<Closures.Declared>(database, new Closure { RoomId = 1, Days = RangeSet<DateRange, DateOnly>.From(Days(1, 3), Days(8, 10)) });

        // One of its ranges overlaps the second range of the row that is there.
        AssertRefused<Closures.Declared>(database, new Closure { RoomId = 1, Days = RangeSet<DateRange, DateOnly>.From(Days(9, 12), Days(20, 21)) }, Closures.Constraint);

        Save<Closures.Declared>(database, new Closure { RoomId = 2, Days = RangeSet<DateRange, DateOnly>.From(Days(9, 12), Days(20, 21)) }); // another room
        Save<Closures.Declared>(database, new Closure { RoomId = 1, Days = RangeSet<DateRange, DateOnly>.From(Days(4, 7), Days(11, 12)) });  // in the gap and behind: no day in common

        Assert.AreEqual(3, Count<Closures.Declared, Closure>(database));
    }

    // ── TimeRange: a timerange column, a type the model creates ──

    private const string ShiftConstraint =
        """public.shifts | ex_shift_desk_hours | index ex_shift_desk_hours | EXCLUDE USING gist ("DeskId" WITH =, "Hours" WITH &&)""";

    private static void AssertShiftsHold<TModel>(LiveDatabase database) where TModel : ITestModel
    {
        Save<TModel>(database, new Shift { DeskId = 1, Hours = Hours(8, 12) });

        AssertRefused<TModel>(database, new Shift { DeskId = 1, Hours = Hours(11, 13) }, Shifts.Constraint);

        Save<TModel>(database, new Shift { DeskId = 2, Hours = Hours(11, 13) }); // another desk
        Save<TModel>(database, new Shift { DeskId = 1, Hours = Hours(12, 16) }); // adjacent: the first ends before its upper bound

        Assert.AreEqual(3, Count<TModel, Shift>(database));
    }

    [TestMethod]
    public void A_constraint_over_a_TimeRange_is_migrated_to_what_EnsureCreated_builds_and_refuses_an_overlap()
    {
        using var database = Migrated<Shifts.Bare, Shifts.Declared>(ShiftConstraint);

        CollectionAssert.AreEqual(new[] { "public.timerange | time without time zone | timemultirange" }, database.RangeTypes());
        AssertShiftsHold<Shifts.Declared>(database);
    }

    /// <summary>
    /// The type, the table with a column of it and the constraint in one migration: the statement
    /// that adds the constraint runs after the one that creates the type, or this fails (42704).
    /// </summary>
    [TestMethod]
    public void One_migration_creates_the_range_type_the_table_and_the_constraint()
    {
        using var database = new LiveDatabase();
        database.Create<Shifts.Nothing>();
        Assert.AreEqual(0, database.RangeTypes().Count);

        database.Migrate<Shifts.Nothing, Shifts.Declared>();

        CollectionAssert.AreEqual(new[] { ShiftConstraint }, database.Exclusions());
        CollectionAssert.AreEqual(Created<Shifts.Declared>(), database.Catalog());
        AssertShiftsHold<Shifts.Declared>(database);
    }

    /// <summary>
    /// With <c>timerange</c> mapped to Npgsql's own range beside ValueRanges, which is what lets a
    /// snapshot of the model be diffed both ways, the database is the same and holds the same.
    /// </summary>
    [TestMethod]
    public void With_timerange_mapped_to_Npgsqls_range_too_the_constraint_is_the_same_and_holds()
    {
        using var database = Migrated<Shifts.Bare, Shifts.DeclaredWithTimeRangeMapped>(ShiftConstraint);

        CollectionAssert.AreEqual(Created<Shifts.Declared>(), database.Catalog());
        AssertShiftsHold<Shifts.DeclaredWithTimeRangeMapped>(database);
    }

    // ── LocalDateRange: a daterange column through the NodaTime packages ──

    private const string StayConstraint =
        """public.stays | ex_stay_room_period | index ex_stay_room_period | EXCLUDE USING gist ("RoomId" WITH =, "Period" WITH &&)""";

    [TestMethod]
    public void A_constraint_over_a_LocalDateRange_is_migrated_to_what_EnsureCreated_builds_and_refuses_an_overlap()
    {
        using var database = Migrated<Stays.Bare, Stays.Declared>(StayConstraint);

        Save<Stays.Declared>(database, new Stay { RoomId = 1, Period = LocalDays(1, 10) });

        AssertRefused<Stays.Declared>(database, new Stay { RoomId = 1, Period = LocalDays(5, 15) }, Stays.Constraint);

        Save<Stays.Declared>(database, new Stay { RoomId = 2, Period = LocalDays(5, 15) });  // another room
        Save<Stays.Declared>(database, new Stay { RoomId = 1, Period = LocalDays(11, 20) }); // adjacent

        Assert.AreEqual(3, Count<Stays.Declared, Stay>(database));
    }

    // ── A range alone: no extension, as for a range of Npgsql's own ──

    /// <summary>
    /// A range and a multirange have a GiST operator class of their own. SchemaKit tells them by
    /// the column's store type, so for each of the four the migration installs nothing, and the
    /// server takes the constraint without the extension.
    /// </summary>
    [TestMethod]
    public void A_constraint_over_the_range_alone_applies_on_a_database_without_any_extension()
    {
        using (var bookings = new LiveDatabase())
        {
            bookings.Create<Bookings.DuringAlone>();
            Assert.AreEqual(0, bookings.Extensions().Count);
            CollectionAssert.AreEqual(new[] { """public.bookings | ex_booking_during | index ex_booking_during | EXCLUDE USING gist ("During" WITH &&)""" }, bookings.Exclusions());
        }

        using (var closures = new LiveDatabase())
        {
            closures.Create<Closures.DaysAlone>();
            Assert.AreEqual(0, closures.Extensions().Count);
            CollectionAssert.AreEqual(new[] { """public.closures | ex_closure_days | index ex_closure_days | EXCLUDE USING gist ("Days" WITH &&)""" }, closures.Exclusions());
        }

        using (var shifts = new LiveDatabase())
        {
            shifts.Create<Shifts.HoursAlone>();
            Assert.AreEqual(0, shifts.Extensions().Count);
            CollectionAssert.AreEqual(new[] { """public.shifts | ex_shift_hours | index ex_shift_hours | EXCLUDE USING gist ("Hours" WITH &&)""" }, shifts.Exclusions());
        }

        using (var stays = new LiveDatabase())
        {
            stays.Create<Stays.PeriodAlone>();
            Assert.AreEqual(0, stays.Extensions().Count);
            CollectionAssert.AreEqual(new[] { """public.stays | ex_stay_period | index ex_stay_period | EXCLUDE USING gist ("Period" WITH &&)""" }, stays.Exclusions());
        }
    }

    // ── Against Npgsql's own range ──

    /// <summary>
    /// The twin of each model has Npgsql's own range where the model has a type of ValueRanges,
    /// and the typed <c>Overlaps</c> where the model has <c>WithUncheckedOperator</c>. The
    /// databases the two build are the same to the catalog: types, columns, constraints, indexes.
    /// </summary>
    [TestMethod]
    public void A_column_of_ValueRanges_and_one_of_Npgsqls_own_are_the_same_database()
    {
        CollectionAssert.AreEqual(Created<Bookings.Twin>(), Created<Bookings.Declared>());
        CollectionAssert.AreEqual(Created<Closures.Twin>(), Created<Closures.Declared>());
        CollectionAssert.AreEqual(Created<Stays.Twin>(), Created<Stays.Declared>());
        CollectionAssert.AreEqual(Created<Bookings.TwinIndexed>(), Created<Bookings.Indexed>());

        // The comparison is of something: the constraint and the index are in what was compared.
        Assert.IsTrue(Created<Bookings.Declared>().Contains("exclusion " + BookingConstraint));
        Assert.IsTrue(Created<Bookings.Indexed>().Contains("index " + BookingIndex));
    }

    // ── A GiST index ──

    private const string BookingIndex = """public.bookings | CREATE INDEX ix_booking_during ON public.bookings USING gist ("During")""";

    [TestMethod]
    public void A_GiST_index_over_a_DateRange_is_migrated_to_what_EnsureCreated_builds_without_an_extension()
    {
        using var database = new LiveDatabase();
        database.Create<Bookings.Bare>();
        Assert.AreEqual(0, database.Indexes().Count);

        database.Migrate<Bookings.Bare, Bookings.Indexed>();

        CollectionAssert.AreEqual(new[] { BookingIndex }, database.Indexes());
        Assert.AreEqual(0, database.Extensions().Count);
        CollectionAssert.AreEqual(Created<Bookings.Indexed>(), database.Catalog());
    }

    /// <summary>The README's sample: the constraint and the index in one migration, and the database <c>EnsureCreated()</c> builds.</summary>
    [TestMethod]
    public void The_declaration_the_README_shows_is_migrated_to_what_EnsureCreated_builds()
    {
        using var database = new LiveDatabase();
        database.Create<Bookings.Bare>();

        database.Migrate<Bookings.Bare, Bookings.AsInTheReadme>();

        CollectionAssert.AreEqual(
            new[] { """public.bookings | ex_booking_room_during | index ex_booking_room_during | EXCLUDE USING gist ("RoomId" WITH =, "During" WITH &&)""" },
            database.Exclusions());
        CollectionAssert.Contains(database.Indexes(), BookingIndex);
        CollectionAssert.AreEqual(Created<Bookings.AsInTheReadme>(), database.Catalog());

        Save<Bookings.AsInTheReadme>(database, new Booking { RoomId = 1, During = Days(1, 10) });
        AssertRefused<Bookings.AsInTheReadme>(database, new Booking { RoomId = 1, During = Days(10, 12) }, Bookings.Constraint);
    }

    /// <summary>The plan of a query as EF Core sends it, with its parameters, on a table that is not read in sequence where an index can serve.</summary>
    private static string Plan<TEntity>(DbContext context, IQueryable<TEntity> query)
    {
        context.Database.OpenConnection();

        // An empty table is read in sequence whatever indexes it has; without that choice the plan says whether the index can serve the query.
        context.Database.ExecuteSqlRaw("SET enable_seqscan = off");

        using var command = query.CreateDbCommand();
        command.CommandText = "EXPLAIN " + command.CommandText;
        using DbDataReader reader = command.ExecuteReader();
        List<string> lines = [];
        while (reader.Read()) lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// What the index is for: <c>Overlaps</c> in a query is <c>&amp;&amp;</c> on the column, as
    /// ValueRanges translates it, and the server's planner names the index for it.
    /// </summary>
    [TestMethod]
    public void The_planner_takes_the_index_for_the_overlap_query_EF_Core_writes()
    {
        var january = Days(1, 31);

        using var database = new LiveDatabase();
        database.Create<Bookings.Indexed>();
        using var context = Contexts.Of<Bookings.Indexed>(database.ConnectionString);

        var overlapping = context.Set<Booking>().Where(b => b.During.Overlaps(january));
        var sql = overlapping.ToQueryString();
        StringAssert.Contains(sql, """b."During" && @""", sql);
        StringAssert.Contains(Plan(context, overlapping), Bookings.Index, sql);

        // The control: a query that does not read the range is not served by it.
        var byRoom = context.Set<Booking>().Where(b => b.RoomId == 1);
        Assert.IsFalse(Plan(context, byRoom).Contains(Bookings.Index, StringComparison.Ordinal), byRoom.ToQueryString());

        // And on a table without the index the same query is read in sequence.
        using var without = new LiveDatabase();
        without.Create<Bookings.Bare>();
        using var bare = Contexts.Of<Bookings.Bare>(without.ConnectionString);
        var plan = Plan(bare, bare.Set<Booking>().Where(b => b.During.Overlaps(january)));
        Assert.IsFalse(plan.Contains(Bookings.Index, StringComparison.Ordinal), plan);
        StringAssert.Contains(plan, "Seq Scan", plan);
    }

    /// <summary>
    /// The constraint's own index serves the query too, for the room and the days together, which
    /// is what 5.x's consumers had without declaring an index beside the constraint.
    /// </summary>
    [TestMethod]
    public void The_planner_takes_the_constraints_index_for_a_query_by_room_and_overlap()
    {
        var january = Days(1, 31);

        using var database = new LiveDatabase();
        database.Create<Bookings.Declared>();
        using var context = Contexts.Of<Bookings.Declared>(database.ConnectionString);

        var query = context.Set<Booking>().Where(b => b.RoomId == 1 && b.CancelledOn == null && b.During.Overlaps(january));
        StringAssert.Contains(Plan(context, query), Bookings.Constraint, query.ToQueryString());
    }
}
