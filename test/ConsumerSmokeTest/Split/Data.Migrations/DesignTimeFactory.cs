using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Shop;
using Smoke;

namespace Data.Migrations;

/// <summary>How `dotnet ef` builds the context here: a factory, with the migrations in this assembly.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ShopContext>
{
    public ShopContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ShopContext>()
            .UsePostgres(o => o.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options);
}
