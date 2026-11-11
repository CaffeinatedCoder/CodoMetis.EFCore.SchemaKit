using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Update.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>A stranger's differ, as packages replace the differ today: derived from EF's internal class.</summary>
public sealed class OtherDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies)
    : MigrationsModelDiffer(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider, rowIdentityMapFactory, commandBatchPreparerDependencies)
{
    public const string Marker = "SELECT 1 /* other-package */";

    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => [.. base.GetDifferences(source, target), new SqlOperation { Sql = Marker }];
}

/// <summary>Registers its differ directly in <c>ApplyServices</c>, by type or through a factory.</summary>
public sealed class OtherOptionsExtension(bool throughFactory = false) : IDbContextOptionsExtension
{
    public bool ThroughFactory { get; } = throughFactory;

    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        if (ThroughFactory)
            services.AddScoped<IMigrationsModelDiffer>(provider => ActivatorUtilities.CreateInstance<OtherDiffer>(provider));
        else
            services.AddScoped<IMigrationsModelDiffer, OtherDiffer>();
    }

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private bool Factory => ((OtherOptionsExtension)Extension).ThroughFactory;

        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using OtherPackage ";
        public override int GetServiceProviderHashCode() => Factory ? 2 : 1;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo o && o.Factory == Factory;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Other:Package"] = Factory ? "factory" : "type";
    }
}
