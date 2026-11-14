using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;

/// <summary>The scheduling shape of the design: one grantee holds a role over a period, until it is revoked.</summary>
public sealed class RoleGrant
{
    public int Id { get; set; }
    public int GranteeId { get; set; }
    public int RoleId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
    public DateOnly? RevokedOn { get; set; }
    public string? Note { get; set; }
}

public sealed class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
}

/// <summary>A stay and a hold are both reservations of a room, kept in one table.</summary>
public class Reservation
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
}

public sealed class Stay : Reservation;

public sealed class Hold : Reservation;

/// <summary>One model of a test. A context type per model, so no test reads a model another cached.</summary>
public interface ITestModel
{
    static abstract void Build(ModelBuilder modelBuilder);
}

public sealed class Db<TModel>(DbContextOptions<Db<TModel>> options) : DbContext(options)
    where TModel : ITestModel
{
    public DbSet<RoleGrant> Grants => Set<RoleGrant>();

    // No extension is declared here: "=" on an integer under GiST needs btree_gist, and that the
    // migration brings it is what the tests on a live server measure.
    protected override void OnModelCreating(ModelBuilder modelBuilder) => TModel.Build(modelBuilder);
}

internal static class Contexts
{
    public const string Offline = "Host=localhost;Database=schemakit_tests";

    /// <summary>A context wired the way a consumer wires it. Without a connection string it can build models and scripts, and reach no server.</summary>
    public static Db<TModel> Of<TModel>(string? connection = null)
        where TModel : ITestModel
        => new(new DbContextOptionsBuilder<Db<TModel>>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .UseNpgsql(connection ?? Offline, o => o.UseSchemaKit())
            .Options);

    public static IRelationalModel Relational(this DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    /// <summary>What <c>migrations add</c> would scaffold to get from one model to the other.</summary>
    public static IReadOnlyList<MigrationOperation> Diff<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Of<TFrom>();
        using var to = Of<TTo>();
        return to.GetService<IMigrationsModelDiffer>().GetDifferences(from.Relational(), to.Relational());
    }

    /// <summary>The statements SchemaKit wrote, among EF Core's operations, in their order.</summary>
    public static List<string> Sql(this IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql)];

    /// <summary>Each operation in its order: SchemaKit's as its SQL, EF Core's as its type.</summary>
    public static List<string> Shape(this IEnumerable<MigrationOperation> operations)
        => [.. operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name)];
}

public static class Models
{
    public const string Name = "ex_role_grant_active_period";

    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    /// <summary>The design's fourth declaration: no overlapping periods per grantee and role among grants not revoked, checked at commit.</summary>
    public static readonly PgExclusion.Gist ActivePeriod = Pg.Exclusion(
        Pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
        where: x => x.RevokedOn == null,
        deferral: Deferral.InitiallyDeferred,
        name: Name);

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<RoleGrant> Grants(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoleGrant>().ToTable("role_grants");

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder);
    }

    public sealed class Scheduling : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [ActivePeriod]);
    }

    /// <summary><see cref="Scheduling" /> in a model that declares the extension itself, as 5.x's <c>UseBtreeGist()</c> did.</summary>
    public sealed class SchedulingWithTheExtensionDeclared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.HasPostgresExtension("btree_gist");
            Grants(modelBuilder).DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary><see cref="Scheduling" /> in a model that says the extension is installed some other way.</summary>
    public sealed class SchedulingSuppressed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.SuppressBtreeGistInjection();
            Grants(modelBuilder).DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary>A range and nothing else: GiST has an operator class for it without any extension.</summary>
    public sealed class RangeOnly : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), name: "ex_range")]);
    }

    /// <summary>The same range under SP-GiST, which btree_gist has nothing for.</summary>
    public sealed class RangeOnlySpGist : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ =>
            [new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Column("Period"), ExclusionOperator.Overlaps)) { Name = "ex_range" }]);
    }

    /// <summary>A text column under SP-GiST, which has an operator class for text of its own.</summary>
    public sealed class TextSpGist : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ =>
            [new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Column("Note"), new ExclusionOperator("<>"))) { Name = "ex_note" }]);
    }

    /// <summary>A range and an expression compared by an operator B-tree has: the expression's type is unknown, its operator is not.</summary>
    public sealed class ExpressionCompared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), name: "ex_note")]);
    }

    /// <summary>A text column compared by <c>=</c>: it needs the extension.</summary>
    public sealed class NoteAsText : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.Note).Overlaps(x => x.Period), name: "ex_note")]);
    }

    /// <summary><see cref="NoteAsText" /> with the column as an <c>ltree</c>, which GiST has an operator class for: the same constraint, needing nothing.</summary>
    public sealed class NoteAsLtree : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasColumnType("ltree");
            grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.Note).Overlaps(x => x.Period), name: "ex_note")]);
        }
    }

    public sealed class Renamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [ActivePeriod with { Name = "ex_renamed" }]);
    }

    /// <summary>Checked at each statement. PostgreSQL has no statement that changes this on an exclusion constraint.</summary>
    public sealed class Immediate : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [ActivePeriod with { Deferral = Deferral.NotDeferrable }]);
    }

    /// <summary>No name of its own, and an expression among its elements.</summary>
    public sealed class Unnamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period))]);
    }

    /// <summary><see cref="Scheduling" />, and EF Core renames the table and a column of each kind: an element's and the condition's.</summary>
    public sealed class SchedulingRenamedAround : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = modelBuilder.Entity<RoleGrant>().ToTable("grants");
            grants.Property(x => x.GranteeId).HasColumnName("grantee");
            grants.Property(x => x.RevokedOn).HasColumnName("revoked");
            grants.DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary><see cref="Scheduling" />, and EF Core renames the one column that only the condition reads.</summary>
    public sealed class SchedulingRevokedRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.RevokedOn).HasColumnName("revoked");
            grants.DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary><see cref="Unnamed" />, and EF Core renames a column its default name is built from.</summary>
    public sealed class UnnamedRenamedAround : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnName("grantee");
            grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period))]);
        }
    }

    /// <summary><see cref="Scheduling" />, and EF Core alters a column that is an element.</summary>
    public sealed class SchedulingWithLongGrantee : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnType("bigint");
            grants.DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary><see cref="Scheduling" />, and a column that is an element becomes a computed one: Npgsql drops it and adds it again.</summary>
    public sealed class SchedulingWithAComputedGrantee : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasComputedColumnSql("\"RoleId\" * 10", stored: true);
            grants.DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary><see cref="Scheduling" />, and the column only the condition reads becomes a computed one.</summary>
    public sealed class SchedulingWithAComputedRevocation : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.RevokedOn).HasComputedColumnSql("NULL::date", stored: true);
            grants.DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary>The constraint under its name without the role, and EF Core drops the role's column: an element of the constraint there was.</summary>
    public sealed class WithoutRole : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.RoleId);
            grants.DeclarePostgres(pg =>
                [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null, deferral: Deferral.InitiallyDeferred, name: Name)]);
        }
    }

    public const string OverNotes = "ex_role_grant_note";

    /// <summary>An expression over the note among the elements, under a name: <c>(lower("Note")) WITH =</c>.</summary>
    public sealed class WithAnExpressionOverTheNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), name: OverNotes)]);
    }

    /// <summary><see cref="WithAnExpressionOverTheNote" /> without that element, and EF Core drops the note: a column an expression read.</summary>
    public sealed class WithoutTheNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.Note);
            grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), name: OverNotes)]);
        }
    }

    /// <summary><see cref="WithAnExpressionOverTheNote" />, and the note becomes a computed column: Npgsql drops it and adds it again.</summary>
    public sealed class WithAnExpressionOverAComputedNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasComputedColumnSql("'n'::text", stored: true);
            grants.DeclarePostgres(pg =>
                [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), name: OverNotes)]);
        }
    }

    /// <summary><see cref="WithAnExpressionOverTheNote" />, and EF Core renames the note.</summary>
    public sealed class WithAnExpressionOverARenamedNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasColumnName("memo");
            grants.DeclarePostgres(pg =>
                [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), name: OverNotes)]);
        }
    }

    /// <summary><see cref="WithAnExpressionOverTheNote" />, and EF Core alters the note's type.</summary>
    public sealed class WithAnExpressionOverAShortNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasMaxLength(40);
            grants.DeclarePostgres(pg =>
                [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), name: OverNotes)]);
        }
    }

    /// <summary>The note as an element by itself and inside the expression of another: both ways of reading one column.</summary>
    public sealed class WithTheNoteBothWays : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.Note).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), name: OverNotes)]);
    }

    /// <summary><see cref="WithTheNoteBothWays" /> with neither element, and EF Core drops the note.</summary>
    public sealed class WithoutTheNoteAtAll : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.Note);
            grants.DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), name: OverNotes)]);
        }
    }

    /// <summary>The grantee as an element by itself, and read by the condition too.</summary>
    public sealed class WithTheGranteeInTheConditionToo : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.GranteeId > 0, name: OverNotes)]);
    }

    /// <summary>The grantee as an element by itself, beside an expression written as SQL that names no column through a placeholder: it may read any of them.</summary>
    public sealed class WithTheGranteeBesideSqlThatNamesNoColumn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
        [
            new PgExclusion.Gist(
            [
                .. pg.Equal(x => x.GranteeId).Overlaps(x => x.Period).Elements,
                new ExclusionElement(new KeyTarget.Template("lower(\"Note\")"), new ExclusionOperator("=")),
            ]) { Name = OverNotes },
        ]);
    }

    /// <summary>Either of the two without the grantee, whose column EF Core drops: the period alone.</summary>
    public sealed class WithoutTheGrantee : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.GranteeId);
            grants.DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), name: OverNotes)]);
        }
    }

    /// <summary>The constraint under its name without its condition, and EF Core drops the column only the condition read.</summary>
    public sealed class WithoutRevocation : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.RevokedOn);
            grants.DeclarePostgres(_ => [ActivePeriod with { Where = null }]);
        }
    }

    /// <summary>No two stays of a room overlap, declared on the stay: but holds are in the same table.</summary>
    public sealed class StaysMayNotOverlap : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Reservation>().ToTable("reservations");
            modelBuilder.Entity<Hold>();
            modelBuilder.Entity<Stay>().DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: "ex_stay")]);
        }
    }

    private static void ReservationsInTablesOfTheirOwn(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reservation>().UseTpcMappingStrategy().ToTable("reservations");
        modelBuilder.Entity<Stay>().ToTable("stays");
        modelBuilder.Entity<Hold>().ToTable("holds");
    }

    /// <summary>Reservations, stays and holds in a table each (TPC), with nothing declared.</summary>
    public sealed class ReservationsInThreeTables : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => ReservationsInTablesOfTheirOwn(modelBuilder);
    }

    /// <summary>The constraint declared on the root of that hierarchy, which has a table, as have its derived types.</summary>
    public sealed class ReservationsInThreeTablesDeclaredOnTheRoot : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            ReservationsInTablesOfTheirOwn(modelBuilder);
            modelBuilder.Entity<Reservation>().DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: "ex_reservation")]);
        }
    }

    /// <summary>The constraint declared on a type without derived types: no two stays of a room overlap, in the stays' own table.</summary>
    public sealed class StaysInTheirOwnTableMayNotOverlap : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            ReservationsInTablesOfTheirOwn(modelBuilder);
            modelBuilder.Entity<Stay>().DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: "ex_stay")]);
        }
    }

    /// <summary>No two reservations of a room overlap, which is what the constraint in the table does.</summary>
    public sealed class ReservationsMayNotOverlap : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Reservation>().ToTable("reservations")
                .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: "ex_stay")]);
            modelBuilder.Entity<Hold>();
            modelBuilder.Entity<Stay>();
        }
    }

    /// <summary>The scalar after the range: every element counts for the extension, not the first.</summary>
    public sealed class ScalarSecond : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Overlaps(x => x.Period).Equal(x => x.GranteeId), name: "ex_second")]);
    }

    /// <summary>An expression compared by an operator B-tree has that is not <c>=</c>.</summary>
    public sealed class ExpressionNotEqual : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Overlaps(x => x.Period).WithUncheckedOperator(x => x.Note!.ToLower(), "<>"), name: "ex_note")]);
    }

    /// <summary>An expression compared by an operator B-tree does not have: nothing says it is a scalar.</summary>
    public sealed class ExpressionOverlapping : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Overlaps(x => x.Period).WithUncheckedOperator(x => x.Note!.ToLower(), "&&"), name: "ex_note")]);
    }

    /// <summary><see cref="Scheduling" /> under its name with one more condition.</summary>
    public sealed class SchedulingWithAnotherCondition : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [ActivePeriod.AndWhere(new Predicate("{Note} IS NOT NULL"))]);
    }

    /// <summary><see cref="Scheduling" /> under its name with another operator on the period: adjacent, not overlapping.</summary>
    public sealed class SchedulingAdjacent : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ =>
            [ActivePeriod with { Elements = [.. ActivePeriod.Elements.Take(2), new ExclusionElement(new KeyTarget.Column("Period"), new ExclusionOperator("-|-"))] }]);
    }

    /// <summary><see cref="Scheduling" />, and EF Core moves the table to another schema.</summary>
    public sealed class SchedulingMoved : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(_ => [ActivePeriod]);
    }

    /// <summary><see cref="Unnamed" />, and EF Core moves the table to another schema: the default name has no schema in it.</summary>
    public sealed class UnnamedMoved : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period))]);
    }

    /// <summary>Two constraints without a name that differ in their condition alone.</summary>
    public sealed class TwoUnnamedThatDifferInTheCondition : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period)),
            pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null),
        ]);
    }

    /// <summary>One constraint name on two tables in two schemas.</summary>
    public sealed class TwoSchemas : ITestModel
    {
        public const string Shared = "ex_no_overlap";

        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants")
                .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), name: Shared)]);
            modelBuilder.Entity<Booking>().ToTable("bookings", "rooms")
                .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: Shared)]);
        }
    }

    /// <summary><see cref="TwoSchemas" /> without the constraint on the grants.</summary>
    public sealed class TwoSchemasOneConstraint : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants");
            modelBuilder.Entity<Booking>().ToTable("bookings", "rooms")
                .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: TwoSchemas.Shared)]);
        }
    }

    // ── A name a rename frees or takes in the migration that renames (#99) ──

    /// <summary>The default name <see cref="Unnamed" /> has, before <see cref="UnnamedRenamedAround" /> renames a column it is built from.</summary>
    public const string UnnamedName = "EX_role_grants_GranteeId_lowerNote_Period";

    /// <summary><see cref="Renamed" />, and EF Core creates an index under the name the constraint gives up.</summary>
    public sealed class RenamedAndItsOldNameReused : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.HasIndex(x => x.RoleId).HasDatabaseName(Name);
            grants.DeclarePostgres(_ => [ActivePeriod with { Name = "ex_renamed" }]);
        }
    }

    /// <summary><see cref="UnnamedRenamedAround" />, and EF Core creates an index under the default name the constraint gives up.</summary>
    public sealed class UnnamedRenamedAroundAndItsOldNameReused : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnName("grantee");
            grants.HasIndex(x => x.RoleId).HasDatabaseName(UnnamedName);
            grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period))]);
        }
    }

    /// <summary><see cref="Scheduling" /> beside an index of EF Core's that has the name <see cref="Renamed" /> gives the constraint.</summary>
    public sealed class SchedulingBesideAnIndexNamedLikeItsNextName : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.HasIndex(x => x.RoleId).HasDatabaseName("ex_renamed");
            grants.DeclarePostgres(_ => [ActivePeriod]);
        }
    }

    /// <summary>The constraint takes the name EF Core's index gives up, which EF Core renames.</summary>
    public sealed class RenamedIntoTheNameAnIndexGaveUp : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.HasIndex(x => x.RoleId).HasDatabaseName("ix_role");
            grants.DeclarePostgres(_ => [ActivePeriod with { Name = "ex_renamed" }]);
        }
    }

    /// <summary>
    /// <see cref="Renamed" /> while EF Core moves its table to another schema, and an index of EF
    /// Core's in the schema the table leaves takes the constraint's old name. The constraint's
    /// index has moved with its table by the time the index is created: nothing collides.
    /// </summary>
    public sealed class RenamedOnAMovedTableAndItsOldNameReusedWhereItWas : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "ops").DeclarePostgres(_ => [ActivePeriod with { Name = "ex_renamed" }]);
            modelBuilder.Entity<Booking>().ToTable("bookings").HasIndex(x => x.RoomId).HasDatabaseName(Name);
        }
    }

    /// <summary>The same move, and the index in the schema the table moves to: there the old name is still held.</summary>
    public sealed class RenamedOnAMovedTableAndItsOldNameReusedWhereItGoes : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "ops").DeclarePostgres(_ => [ActivePeriod with { Name = "ex_renamed" }]);
            modelBuilder.Entity<Booking>().ToTable("bookings", "ops").HasIndex(x => x.RoomId).HasDatabaseName(Name);
        }
    }

    /// <summary>
    /// The same table rename, and an index of EF Core's on the renamed table under the
    /// constraint's old name: the index and the constraint's index are relations of one schema,
    /// whatever their tables are called.
    /// </summary>
    public sealed class RenamedOnARenamedTableAndItsOldNameReused : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = modelBuilder.Entity<RoleGrant>().ToTable("grants");
            grants.HasIndex(x => x.RoleId).HasDatabaseName(Name);
            grants.DeclarePostgres(_ => [ActivePeriod with { Name = "ex_renamed" }]);
        }
    }

    private static PgExclusion OnGrants(PgSchema<RoleGrant> pg, string name) => pg.Exclusion(pg.Overlaps(x => x.Period), name: name);

    private static PgExclusion OnGrantsByRole(PgSchema<RoleGrant> pg, string name) => pg.Exclusion(pg.Equal(x => x.RoleId).Overlaps(x => x.Period), name: name);

    private static PgExclusion OnBookings(PgSchema<Booking> pg, string name) => pg.Exclusion(pg.Overlaps(x => x.Period), name: name);

    /// <summary>Two tables of one schema, a constraint on each: their indexes are relations of that schema.</summary>
    public sealed class TwoTables<TNames> : ITestModel where TNames : INames
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(pg => [OnGrants(pg, TNames.First)]);
            modelBuilder.Entity<Booking>().ToTable("bookings").DeclarePostgres(pg => [OnBookings(pg, TNames.Second)]);
        }
    }

    /// <summary>Two constraints on one table.</summary>
    public sealed class OneTable<TNames> : ITestModel where TNames : INames
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [OnGrants(pg, TNames.First), OnGrantsByRole(pg, TNames.Second)]);
    }

    public interface INames
    {
        static abstract string First { get; }

        static abstract string Second { get; }
    }

    public sealed class AB : INames
    {
        public static string First => "ex_a";

        public static string Second => "ex_b";
    }

    public sealed class BA : INames
    {
        public static string First => "ex_b";

        public static string Second => "ex_a";
    }

    public sealed class BC : INames
    {
        public static string First => "ex_b";

        public static string Second => "ex_c";
    }
}
