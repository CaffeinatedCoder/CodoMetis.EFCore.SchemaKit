using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace Legacy5x;

// The model as a 5.x consumer wrote it. Every way 5.x could store an exclusion constraint is here
// once: the two-argument form with its default name, a lambda filter, a string filter with a
// placeholder, an explicit name, both kinds of deferral, a verbatim expression, a path through a
// complex type, a table in a schema with mapped column names, and SP-GiST.
//
// test/…PostgreSQL.Tests/Legacy/LegacyFixture.cs declares the same model with SchemaKit. The two
// are kept alike by hand; LegacyHandoverTests fails when they are not.

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
        modelBuilder.Entity<Booking>().HasExclusionConstraint(x => x.RoomId, x => x.During);

        modelBuilder.Entity<Shift>(shift =>
        {
            shift.ToTable("shifts", "ops");
            shift.Property(x => x.WorkerId).HasColumnName("worker_id");
            shift.Property(x => x.SiteId).HasColumnName("site_id");
            shift.Property(x => x.Kind).HasColumnName("kind");
            shift.Property(x => x.During).HasColumnName("during");
            shift.Property(x => x.CancelledOn).HasColumnName("cancelled_on");

            shift.HasExclusionConstraint(c => c
                .WithEquality(x => x.WorkerId)
                .WithOverlaps(x => x.During)
                .HasFilter(x => x.CancelledOn == null)
                .HasName("ex_shift_active")
                .IsDeferrable(initiallyDeferred: true));

            shift.HasExclusionConstraint(
                x => new { x.WorkerId, x.SiteId },
                x => x.During,
                filter: "{Kind} <> 'draft'",
                name: "ex_shift_site");
        });

        modelBuilder.Entity<Lease>(lease =>
        {
            lease.ComplexProperty(x => x.Term);
            lease.HasExclusionConstraint(c => c
                .WithExpression("lower(\"Code\")", "=")
                .WithOverlaps(x => x.Term.Span)
                .IsDeferrable());
        });

        modelBuilder.Entity<Slot>().HasExclusionConstraint(c => c
            .WithOverlaps(x => x.During)
            .UseMethod("spgist"));
    }
}

public sealed class LegacyContextFactory : IDesignTimeDbContextFactory<LegacyContext>
{
    // migrations add reaches no server; the connection string only has to parse.
    public LegacyContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<LegacyContext>()
            .UseNpgsql("Host=localhost;Database=legacy5x")
            .UseNpgsqlComplexIndexes()
            .Options);
}

public static class Program
{
    public static void Main()
    {
    }
}
