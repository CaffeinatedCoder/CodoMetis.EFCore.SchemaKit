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
}
