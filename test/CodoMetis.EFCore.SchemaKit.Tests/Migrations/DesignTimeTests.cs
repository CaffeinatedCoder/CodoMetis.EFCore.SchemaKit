using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

public sealed class DesignTimeContext(DbContextOptions<DesignTimeContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>().DeclareTestObject("thing_name");
}

/// <summary>
/// <c>dotnet ef migrations add</c>, in process: EF Core's own design-time composition
/// (<see cref="DesignTimeServicesBuilder" />) over a context wired with nothing but
/// <c>UseSchemaKit()</c>. There is no <c>.targets</c> and no design-time services reference; the
/// design-time differ is the context's because EF Core seeds its services from the context.
/// </summary>
/// <remarks>
/// This holds the composition order. That a packed package behaves the same from a consumer outside
/// the repository is the consumer smoke test's to hold (spikes/B.DesignTime until it exists).
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class DesignTimeTests
{
    private static IServiceProvider DesignTimeServices(DbContext context)
        => new DesignTimeServicesBuilder(
                typeof(DesignTimeTests).Assembly, typeof(DesignTimeTests).Assembly, new OperationReporter(handler: null), [])
            .Build(context);

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_design_time_differ_is_the_contexts(Provider provider)
    {
        using var context = new DesignTimeContext(new DbContextOptionsBuilder<DesignTimeContext>().Use(provider).UseSchemaKit().Options);

        var differ = DesignTimeServices(context).GetRequiredService<IMigrationsModelDiffer>();

        Assert.IsInstanceOfType<SchemaKitModelDiffer>(differ);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_scaffolded_migration_carries_the_declared_object(Provider provider)
    {
        using var context = new DesignTimeContext(new DbContextOptionsBuilder<DesignTimeContext>().Use(provider).UseSchemaKit().Options);

        var migration = DesignTimeServices(context).GetRequiredService<IMigrationsScaffolder>()
            .ScaffoldMigration("Initial", rootNamespace: "Scaffolded");

        // Up creates the index after its table; Down drops it before the table.
        var up   = migration.MigrationCode.IndexOf("CREATE INDEX", StringComparison.Ordinal);
        var down = migration.MigrationCode.IndexOf("DROP INDEX", StringComparison.Ordinal);
        Assert.IsTrue(up > migration.MigrationCode.IndexOf("CreateTable", StringComparison.Ordinal), migration.MigrationCode);
        Assert.IsTrue(down > up && down < migration.MigrationCode.IndexOf("DropTable", StringComparison.Ordinal), migration.MigrationCode);

        // The snapshot records the declaration and the stamp; both are what the next diff reads.
        StringAssert.Contains(migration.SnapshotCode, $"HasAnnotation(\"{TestKit.Declared}\", \"thing_name\")");
        StringAssert.Contains(migration.SnapshotCode, $"HasAnnotation(\"{SchemaKitModelStamp.Annotation}\", \"{SchemaKitModelStamp.Value}\")");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Without_use_schemakit_scaffolding_fails_at_the_declaration(Provider provider)
    {
        using var context = new DesignTimeContext(new DbContextOptionsBuilder<DesignTimeContext>().Use(provider).Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => DesignTimeServices(context).GetRequiredService<IMigrationsScaffolder>().ScaffoldMigration("Initial", rootNamespace: "Scaffolded"));

        StringAssert.Contains(error.Message, "UseSchemaKit() is not on this context's options");
    }
}
