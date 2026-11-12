using EFCore.ComplexIndexes;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace Legacy5x;

// What 5.x stores besides exclusion constraints, one kind per entity type: an index declared on a
// property of a complex type, an index declared on the entity type, and a temporal constraint.
// (A temporal foreign key is in 5.x's source and in no published version.) No version of
// SchemaKit reads these yet. This snapshot is what says where
// 5.x put each of them, for the guard that refuses a snapshot holding one (#93).

public sealed class Address
{
    public string City { get; set; } = "";
    public string Zip { get; set; } = "";
}

public sealed class Customer
{
    public int Id { get; set; }
    public Address Address { get; set; } = new();
}

public sealed class Supplier
{
    public int Id { get; set; }
    public Address Address { get; set; } = new();
}

public sealed class Tariff
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public NpgsqlRange<DateOnly> Valid { get; set; }
}

public sealed class OtherKindsContext(DbContextOptions<OtherKindsContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().ComplexProperty(x => x.Address, address => address.Property(x => x.City).HasComplexIndex());

        modelBuilder.Entity<Supplier>(supplier =>
        {
            supplier.ComplexProperty(x => x.Address);
            supplier.HasComplexIndex(x => x.Address.Zip, isUnique: true);
        });

        modelBuilder.Entity<Tariff>().HasTemporalConstraint(x => x.ProductId, x => x.Valid);
    }
}

public sealed class OtherKindsContextFactory : IDesignTimeDbContextFactory<OtherKindsContext>
{
    public OtherKindsContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<OtherKindsContext>()
            .UseNpgsql("Host=localhost;Database=legacy5x")
            .UseNpgsqlComplexIndexes()
            .Options);
}
