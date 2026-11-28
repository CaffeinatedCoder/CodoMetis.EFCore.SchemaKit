using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using CodoMetis.ValueRanges;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;

// Each entity over a type of ValueRanges has a twin over Npgsql's own range, with the same table,
// columns and names: what the two models differ in is the property's type and nothing else, so a
// difference in a migration or in the catalog is that type's.

/// <summary>A room is booked over days: a <c>daterange</c> column.</summary>
public sealed class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public DateRange During { get; set; } = DateRange.Empty;
    public DateOnly? CancelledOn { get; set; }
}

public sealed class NpgsqlBooking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<DateOnly> During { get; set; }
    public DateOnly? CancelledOn { get; set; }
}

/// <summary>A room is closed on several stretches of days at once: a <c>datemultirange</c> column.</summary>
public sealed class Closure
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public RangeSet<DateRange, DateOnly> Days { get; set; } = RangeSet<DateRange, DateOnly>.Empty;
}

public sealed class NpgsqlClosure
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<DateOnly>[] Days { get; set; } = [];
}

/// <summary>A desk is staffed over hours of the day: a <c>timerange</c> column, a type the database does not ship.</summary>
public sealed class Shift
{
    public int Id { get; set; }
    public int DeskId { get; set; }
    public TimeRange Hours { get; set; } = TimeRange.Empty;
}

/// <summary>A room is stayed in over days, as NodaTime says them: a <c>daterange</c> column again.</summary>
public sealed class Stay
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public LocalDateRange Period { get; set; } = LocalDateRange.Empty;
}

public sealed class NpgsqlStay
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<LocalDate> Period { get; set; }
}

/// <summary>A <see cref="DateRange" />: no two bookings of a room overlap, among those not cancelled.</summary>
public static class Bookings
{
    public const string Constraint = "ex_booking_room_during";
    public const string Index = "ix_booking_during";

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Booking> Table(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Booking>().ToTable("bookings");

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<NpgsqlBooking> TwinTable(ModelBuilder modelBuilder)
        => modelBuilder.Entity<NpgsqlBooking>().ToTable("bookings");

    private static void Declare(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(pg =>
    [
        pg.Exclusion(
            pg.Equal(x => x.RoomId).WithUncheckedOperator(x => x.During, "&&"),
            where: x => x.CancelledOn == null,
            name: Constraint),
    ]);

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    public sealed class Declared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    /// <summary><see cref="Declared" /> in a context that registers ValueRanges before SchemaKit.</summary>
    public sealed class DeclaredValueRangesFirst : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.BothValueRangesFirst(npgsql);
    }

    /// <summary><see cref="Declared" /> over Npgsql's own range, with the typed <c>Overlaps</c>.</summary>
    public sealed class Twin : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => TwinTable(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Exclusion(
                pg.Equal(x => x.RoomId).Overlaps(x => x.During),
                where: x => x.CancelledOn == null,
                name: Constraint),
        ]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.SchemaKitAlone(npgsql);
    }

    /// <summary>
    /// The range and nothing else, which the lambda factory cannot say for a type of ValueRanges:
    /// the elements are written as records, the property by its name.
    /// </summary>
    public sealed class DuringAlone : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(_ =>
            [new PgExclusion.Gist([new ExclusionElement(new KeyTarget.Column(nameof(Booking.During)), ExclusionOperator.Overlaps)]) { Name = "ex_booking_during" }]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    /// <summary>The declaration the README shows: the constraint without a condition, and a GiST index beside it.</summary>
    public sealed class AsInTheReadme : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Exclusion(pg.Equal(x => x.RoomId).WithUncheckedOperator(x => x.During, "&&"), name: Constraint),
            pg.Gist(x => x.During, name: Index),
        ]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    /// <summary>A GiST index over the range, which is what serves <c>&amp;&amp;</c> in a query.</summary>
    public sealed class Indexed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(pg => [pg.Gist(x => x.During, name: Index)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    /// <summary><see cref="Indexed" /> over Npgsql's own range.</summary>
    public sealed class TwinIndexed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => TwinTable(modelBuilder).DeclarePostgres(pg => [pg.Gist(x => x.During, name: Index)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.SchemaKitAlone(npgsql);
    }
}

/// <summary>A <see cref="RangeSet{TRange, T}" />, a multirange column: no two closures of a room overlap.</summary>
public static class Closures
{
    public const string Constraint = "ex_closure_room_days";

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Closure> Table(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Closure>().ToTable("closures");

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    public sealed class Declared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.RoomId).WithUncheckedOperator(x => x.Days, "&&"), name: Constraint)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    /// <summary><see cref="Declared" /> over Npgsql's own multirange, an array of ranges.</summary>
    public sealed class Twin : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<NpgsqlClosure>().ToTable("closures").DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Days), name: Constraint)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.SchemaKitAlone(npgsql);
    }

    /// <summary>The multirange and nothing else.</summary>
    public sealed class DaysAlone : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(_ =>
            [new PgExclusion.Gist([new ExclusionElement(new KeyTarget.Column(nameof(Closure.Days)), ExclusionOperator.Overlaps)]) { Name = "ex_closure_days" }]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }
}

/// <summary>
/// A <see cref="TimeRange" />: no two shifts at a desk overlap. The model creates the type, as
/// ValueRanges says to (<c>HasPostgresRange</c>), and EF Core writes that as an operation of its own.
/// </summary>
public static class Shifts
{
    public const string Constraint = "ex_shift_desk_hours";

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Shift> Table(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresRange("timerange", "time");
        return modelBuilder.Entity<Shift>().ToTable("shifts");
    }

    /// <summary>Neither the type nor the table: what the first migration of the others starts from in a database that has other tables.</summary>
    public sealed class Nothing : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) { }

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    public sealed class Declared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.DeskId).WithUncheckedOperator(x => x.Hours, "&&"), name: Constraint)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }

    /// <summary><see cref="Bare" /> in a context that has ValueRanges and no SchemaKit.</summary>
    public sealed class BareWithoutSchemaKit : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.ValueRangesAlone(npgsql);
    }

    /// <summary><see cref="Declared" /> in a context that maps <c>timerange</c> to Npgsql's own range too.</summary>
    public sealed class DeclaredWithTimeRangeMapped : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declared.Build(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.BothWithTimeRangeMapped(npgsql);
    }

    /// <summary>The range and nothing else.</summary>
    public sealed class HoursAlone : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(_ =>
            [new PgExclusion.Gist([new ExclusionElement(new KeyTarget.Column(nameof(Shift.Hours)), ExclusionOperator.Overlaps)]) { Name = "ex_shift_hours" }]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.Both(npgsql);
    }
}

/// <summary>A <see cref="LocalDateRange" />, through ValueRanges' NodaTime package: no two stays in a room overlap.</summary>
public static class Stays
{
    public const string Constraint = "ex_stay_room_period";

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Stay> Table(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Stay>().ToTable("stays");

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.BothWithNodaTime(npgsql);
    }

    public sealed class Declared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.RoomId).WithUncheckedOperator(x => x.Period, "&&"), name: Constraint)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.BothWithNodaTime(npgsql);
    }

    /// <summary><see cref="Declared" /> over Npgsql's own range of NodaTime's dates.</summary>
    public sealed class Twin : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<NpgsqlStay>().ToTable("stays").DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: Constraint)]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.SchemaKitWithNodaTime(npgsql);
    }

    /// <summary>The range and nothing else.</summary>
    public sealed class PeriodAlone : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Table(modelBuilder).DeclarePostgres(_ =>
            [new PgExclusion.Gist([new ExclusionElement(new KeyTarget.Column(nameof(Stay.Period)), ExclusionOperator.Overlaps)]) { Name = "ex_stay_period" }]);

        public static void Wire(NpgsqlDbContextOptionsBuilder npgsql) => Wiring.BothWithNodaTime(npgsql);
    }
}
