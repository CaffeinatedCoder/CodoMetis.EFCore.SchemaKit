using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>
/// A stand-in for the planning layer that does not exist yet: a declaration API that asks for the
/// stamp as the real ones will, a planner that turns each declaration into a real index, and a
/// rewriter for one made-up legacy operation. The host is what is under test, not these.
/// </summary>
public static class TestKit
{
    public const string Declared = "Test:Object";
    public const string Legacy = "Test:Legacy";

    public static EntityTypeBuilder<TEntity> DeclareTestObject<TEntity>(this EntityTypeBuilder<TEntity> builder, string name)
        where TEntity : class
    {
        SchemaKitModelStamp.Require(builder.Metadata.Model, builder.Metadata.DisplayName());
        builder.HasAnnotation(Declared, name);
        return builder;
    }

    /// <summary>Adds the planner, the rewriter and a counter of model builds to the context's services.</summary>
    public static DbContextOptionsBuilder UseTestKit(this DbContextOptionsBuilder builder)
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new TestKitExtension());
        return builder;
    }

    public static IServiceCollection AddTestKit(this IServiceCollection services)
    {
        services.AddScoped<ISchemaOperationPlanner, TestPlanner>();
        services.AddScoped<IMigrationOperationRewriter, TestRewriter>();
        services.AddScoped<IConventionSetPlugin, ModelBuildCounter>();
        return services;
    }

    private sealed class TestKitExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);
        public void ApplyServices(IServiceCollection services) => services.AddTestKit();
        public void Validate(IDbContextOptions options) { }

        private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "using TestKit ";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Test:Kit"] = "1";
        }
    }
}

/// <summary>
/// Drops before, creates after: the order the real planner will produce, written through the real
/// statement builder.
/// </summary>
/// <remarks>
/// It takes the dialect in its constructor on purpose. The dialect reads the design-time model, so
/// a host that built its planner while the model was being built would cycle; that it does not is
/// what every test that uses this planner shows.
/// </remarks>
internal sealed class TestPlanner(ISchemaDialect dialect, IDatabaseProvider provider) : ISchemaOperationPlanner
{
    public SchemaOperationPlan Plan(IRelationalModel? source, IRelationalModel? target, IReadOnlyList<MigrationOperation> inner)
    {
        var before = Declarations(source);
        var after  = Declarations(target);
        var drops  = new SchemaSql(dialect);
        var creates = new SchemaSql(dialect);

        foreach (var (_, d) in before.Where(d => !after.ContainsKey(d.Key)))
        {
            // The database's rule, not a preference: SQL Server names an index within its table,
            // PostgreSQL and SQLite within a schema.
            if (provider.Name.Contains("SqlServer"))
                drops.Statement($"DROP INDEX {d.Index} ON {d.Table}");
            else
                drops.Statement($"DROP INDEX {d.Table.Qualify(d.Index)}");
        }

        foreach (var (_, d) in after.Where(d => !before.ContainsKey(d.Key)))
            creates.Statement($"CREATE INDEX {d.Index} ON {d.Table} ({d.Column})");

        return new SchemaOperationPlan(drops.Operations, creates.Operations);
    }

    private Dictionary<string, (Identifier Index, TableRef Table, Identifier Column)> Declarations(IRelationalModel? model)
    {
        Dictionary<string, (Identifier, TableRef, Identifier)> found = [];
        if (model is null) return found;

        foreach (var entity in model.Model.GetEntityTypes())
        {
            if (entity.FindAnnotation(TestKit.Declared)?.Value is not string name) continue;
            var table  = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
            var column = entity.GetProperties().First(p => !p.IsPrimaryKey()).GetColumnName(table)!;
            found[name] = (
                dialect.Identifier($"test_{name}"),
                new TableRef(table.Schema is null ? null : dialect.Identifier(table.Schema), dialect.Identifier(table.Name)),
                dialect.Identifier(column));
        }

        return found;
    }
}

/// <summary>Turns a <see cref="CreateIndexOperation" /> marked <see cref="TestKit.Legacy" /> into SQL, in place.</summary>
internal sealed class TestRewriter : IMigrationOperationRewriter
{
    public IReadOnlyList<MigrationOperation> Rewrite(IReadOnlyList<MigrationOperation> operations, IModel? model)
        => [.. operations.Select(o => o is CreateIndexOperation index && index.FindAnnotation(TestKit.Legacy) is not null
            ? new SqlOperation { Sql = $"SELECT 1 /* legacy:{index.Name} */" }
            : o)];
}

/// <summary>Counts how often a model is created, for the model-cache test.</summary>
public sealed class ModelBuildCounter : IConventionSetPlugin, IModelInitializedConvention
{
    public static int Builds;

    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelInitializedConventions.Add(this);
        return conventionSet;
    }

    public void ProcessModelInitialized(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        => Interlocked.Increment(ref Builds);
}
