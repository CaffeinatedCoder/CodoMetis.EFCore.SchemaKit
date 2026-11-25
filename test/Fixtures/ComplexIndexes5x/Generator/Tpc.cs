using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace Legacy5x;

// Exclusion constraints on the root of a TPC hierarchy (CodoMetis.EFCore.SchemaKit#189). From
// 5.5.0 on, 5.x gives every table of the hierarchy below the declaring type its copy. Two
// hierarchies: a root with a table of its own, with a constraint under a name and one under the
// default name, and an abstract root, which has no table, with a constraint under a name.
//
// test/…PostgreSQL.Tests/Legacy/LegacyFixture.cs declares the same model with SchemaKit. The two
// are kept alike by hand; LegacyHandoverTests fails when they are not.

public class Rental
{
    public int Id { get; set; }
    public int UnitId { get; set; }
    public NpgsqlRange<DateOnly> During { get; set; }
}

public sealed class CarRental : Rental;

public sealed class BoatRental : Rental;

public abstract class Permit
{
    public int Id { get; set; }
    public int HolderId { get; set; }
    public NpgsqlRange<DateOnly> During { get; set; }
}

public sealed class ParkingPermit : Permit;

public sealed class FishingPermit : Permit;

public sealed class TpcContext(DbContextOptions<TpcContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Rental>(rental =>
        {
            rental.UseTpcMappingStrategy().ToTable("rentals");

            rental.HasExclusionConstraint(c => c
                .WithEquality(x => x.UnitId)
                .WithOverlaps(x => x.During)
                .HasName("ex_rental"));

            rental.HasExclusionConstraint(c => c.WithOverlaps(x => x.During));
        });
        modelBuilder.Entity<CarRental>().ToTable("car_rentals");
        modelBuilder.Entity<BoatRental>().ToTable("boat_rentals");

        modelBuilder.Entity<Permit>(permit =>
        {
            permit.UseTpcMappingStrategy();

            permit.HasExclusionConstraint(c => c
                .WithEquality(x => x.HolderId)
                .WithOverlaps(x => x.During)
                .HasName("ex_permit"));
        });
        modelBuilder.Entity<ParkingPermit>().ToTable("parking_permits");
        modelBuilder.Entity<FishingPermit>().ToTable("fishing_permits");
    }
}

public sealed class TpcContextFactory : IDesignTimeDbContextFactory<TpcContext>
{
    public TpcContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<TpcContext>()
            .UseNpgsql("Host=localhost;Database=legacy5x")
            .UseNpgsqlComplexIndexes()
            .Options);
}
