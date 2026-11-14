using CodoMetis.EFCore.SchemaKit;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;
using Smoke;

// The consumer's own namespace and types, unchanged by the move: the scaffolded files name them.
// The model is test/Fixtures/ComplexIndexes5x/Generator/Model.cs written with SchemaKit, as
// MovedModels.Declared in the PostgreSQL package's tests (Legacy/LegacyFixture.cs) writes it.
namespace Legacy5x;

public sealed class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<DateOnly> During { get; set; }
}

public sealed class Shift
{
    public int Id { get; set; }
    public int WorkerId { get; set; }
    public int SiteId { get; set; }
    public string Kind { get; set; } = "";
    public NpgsqlRange<DateOnly> During { get; set; }
    public DateOnly? CancelledOn { get; set; }
}

public sealed class Term
{
    public NpgsqlRange<DateOnly> Span { get; set; }
}

public sealed class Lease
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public Term Term { get; set; } = new();
}

public sealed class Slot
{
    public int Id { get; set; }
    public NpgsqlRange<DateOnly> During { get; set; }
}

public sealed class LegacyContext(DbContextOptions<LegacyContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>(booking =>
        {
            booking.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.During))]);

            // SMOKE_LEGACY=note is one more change after the move: a column nothing declared reads.
            if (Environment.GetEnvironmentVariable("SMOKE_LEGACY") == "note")
                booking.Property<string>("Note");
        });

        modelBuilder.Entity<Shift>(shift =>
        {
            shift.ToTable("shifts", "ops");
            shift.Property(x => x.WorkerId).HasColumnName("worker_id");
            shift.Property(x => x.SiteId).HasColumnName("site_id");
            shift.Property(x => x.Kind).HasColumnName("kind");
            shift.Property(x => x.During).HasColumnName("during");
            shift.Property(x => x.CancelledOn).HasColumnName("cancelled_on");
            shift.DeclarePostgres(pg =>
            [
                pg.Exclusion(
                    pg.Equal(x => x.WorkerId).Overlaps(x => x.During),
                    where: x => x.CancelledOn == null,
                    deferral: Deferral.InitiallyDeferred,
                    name: "ex_shift_active"),
                pg.Exclusion(
                    pg.Equal(x => x.WorkerId).Equal(x => x.SiteId).Overlaps(x => x.During),
                    where: x => x.Kind != "draft",
                    name: "ex_shift_site"),
            ]);
        });

        modelBuilder.Entity<Lease>(lease =>
        {
            lease.ComplexProperty(x => x.Term);
            lease.DeclarePostgres(pg =>
                [pg.Exclusion(pg.Equal(x => x.Code.ToLower()).Overlaps(x => x.Term.Span), deferral: Deferral.InitiallyImmediate)]);
        });

        modelBuilder.Entity<Slot>().DeclarePostgres(_ =>
            [new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Column("During"), ExclusionOperator.Overlaps))]);
    }
}

/// <summary>The factory the 5.x project had, with SchemaKit's registration in place of 5.x's.</summary>
public sealed class LegacyContextFactory : IDesignTimeDbContextFactory<LegacyContext>
{
    public LegacyContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<LegacyContext>().UsePostgres().Options);
}

public static class Program
{
    public static void Main()
    {
    }
}
