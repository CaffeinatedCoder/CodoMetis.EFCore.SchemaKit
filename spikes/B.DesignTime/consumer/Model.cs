using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SchemaKit.Spikes.A.SpikeKit;

namespace Consumer;

public sealed class Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Gadget
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

public sealed class SpikeContext : DbContext
{
#if CTOR
    // The way `dotnet ef` builds a context when there is neither a factory nor a host.
    public SpikeContext() { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured) Config.Apply(optionsBuilder);
    }
#endif

    public SpikeContext(DbContextOptions<SpikeContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Thing>().DeclareSpike("thing_name");

        // SPIKE_SECOND=1 is "the model changed, and only a kit declaration did": Gadget's table is
        // in the model either way.
        var gadget = modelBuilder.Entity<Gadget>();
        if (Config.Is("SPIKE_SECOND")) gadget.DeclareSpike("gadget_label");
    }
}

public static class Config
{
    public static bool Is(string variable) => Environment.GetEnvironmentVariable(variable) == "1";

    public static string Connection => Environment.GetEnvironmentVariable("SPIKE_CONNECTION")
        ?? throw new InvalidOperationException("SPIKE_CONNECTION is not set.");

    /// <summary>The options every path uses. SPIKE_NO_KIT=1 leaves the kit out, to see that it is loud.</summary>
    public static void Apply(DbContextOptionsBuilder builder, bool kit = true)
    {
#if SQLITE
        builder.UseSqlite(Connection);
#elif NPGSQL
        builder.UseNpgsql(Connection);
#elif SQLSERVER
        builder.UseSqlServer(Connection);
#endif
        if (kit && !Is("SPIKE_NO_KIT")) builder.UseSpikeKit();
    }
}

#if FACTORY
/// <summary>SPIKE_FACTORY_NO_KIT=1 is the factory that forgot the kit while the application has it.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SpikeContext>
{
    public SpikeContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SpikeContext>();
        Config.Apply(builder, kit: !Config.Is("SPIKE_FACTORY_NO_KIT"));
        return new SpikeContext(builder.Options);
    }
}
#endif

#if DTS
#pragma warning disable EF1001
/// <summary>
/// A user's own design-time services that register a differ. EF adds these last, so this differ,
/// not the context's, scaffolds the migration: the residual no plugin can guard.
/// </summary>
public sealed class UserDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
        => services.AddScoped<IMigrationsModelDiffer, Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer>();
}
#endif
