using CodoMetis.EFCore.SchemaKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
#if ANNOTATED
using Microsoft.EntityFrameworkCore.Migrations;
#endif
#if NPGSQL
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
#endif

namespace Consumer;

public sealed class Thing
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>
/// One table and one declared object. The context is built the way <c>dotnet ef</c> and a bundle
/// build it without help: a parameterless constructor and <c>OnConfiguring</c>.
/// </summary>
public sealed class SpikeContext : DbContext
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // No design-time path connects: a migration is scaffolded, scripted and bundled without a
        // server. The judge and the bundle are handed the database they write to.
        var connection = Environment.GetEnvironmentVariable("SPIKE_D_CONNECTION");

#if ANNOTATED
        // Before UseSchemaKit(), which keeps an earlier replacement as the generator it wraps: this
        // one stands where the provider's generator stands and writes down what reaches it.
        options.ReplaceService<IMigrationsSqlGenerator, RecordingGenerator>();
#endif

#if NPGSQL
        options.UseNpgsql(connection ?? "Host=localhost;Database=spike_d_offline", o => o.UseSchemaKit());
#elif SQLSERVER
        options.UseSqlServer(connection ?? "Server=localhost;Database=spike_d_offline;TrustServerCertificate=true").UseSchemaKit();
#else
        options.UseSqlite(connection ?? "Data Source=spike_d_offline.db").UseSchemaKit();
#endif

        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new SpikeExtension());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var thing = modelBuilder.Entity<Thing>();
        thing.Property(t => t.Name).HasMaxLength(100);
        thing.Metadata.Declare([new SpikeObject(Forms.Current, nameof(Thing.Name)) { Name = Forms.ObjectName }]);
    }
}

/// <summary>How a package adds its contributor: an options extension of its own.</summary>
public sealed class SpikeExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) => services.AddSchemaContributor<SpikeContributor>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using spike D ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Spike:D"] = "1";
    }
}
