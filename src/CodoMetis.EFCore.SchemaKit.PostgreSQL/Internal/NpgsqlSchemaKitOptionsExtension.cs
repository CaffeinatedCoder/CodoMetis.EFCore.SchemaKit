using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>
/// What the PostgreSQL package adds to a context's services beside the kernel's: its dialect and
/// its contributors. It touches neither of the two EF Core services the kernel decorates.
/// </summary>
internal sealed class NpgsqlSchemaKitOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) => AddServices(services);

    public void Validate(IDbContextOptions options)
    {
    }

    public static void AddServices(IServiceCollection services)
    {
        // The kernel adds its own dialect with TryAdd, and EF Core does not promise which of the two
        // extensions is applied first. Replace is right in both orders: it takes the kernel's out
        // when that is there already, and stands in its way when it is not.
        //
        // The identifier limit is the design-time model's, so like the kernel's this is a diff-time
        // service: nothing resolved while a model is built may ask for it.
        services.Replace(ServiceDescriptor.Scoped<ISchemaDialect>(provider => new NpgsqlSchemaDialect(
            new SchemaDialectDependencies(
                provider.GetRequiredService<ISqlGenerationHelper>(),
                provider.GetRequiredService<IRelationalTypeMappingSource>()),
            provider.GetRequiredService<IDesignTimeModel>().Model.GetMaxIdentifierLength())));

        services.AddSchemaContributor<PgExclusionContributor>();
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using SchemaKit.PostgreSQL ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["CodoMetis:SchemaKit:PostgreSQL"] = "1";
    }
}
