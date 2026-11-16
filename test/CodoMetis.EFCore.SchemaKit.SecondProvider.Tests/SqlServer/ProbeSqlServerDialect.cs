using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;

/// <summary>
/// SQL Server's dialect, as far as a package outside the repository can write one: by deriving
/// from <see cref="SchemaDialect" />, since none of the values a dialect returns can be made
/// anywhere else.
/// </summary>
internal sealed class ProbeSqlServerDialect(SchemaDialectDependencies dependencies, int maxIdentifierLength)
    : SchemaDialect(dependencies, maxIdentifierLength)
{
    /// <summary>Every kind and name the kernel asked about, for the tests of what it does not ask.</summary>
    public List<(SchemaModelObject Kind, string Name)> Asked { get; } = [];

    // A table without a schema is created in the default schema of the user that runs the
    // migration, which is dbo for the owner of a database. A user with another default schema is
    // a database the model does not describe, as a search_path is on PostgreSQL.
    public override Identifier? DefaultSchema => field ??= Identifier("dbo");

    // An index is named within its table, and nothing else is. A table, a sequence and every
    // constraint are objects of the schema: one name, one object, whatever its kind. A primary key
    // and a unique constraint are built on an index of their name, so they take that name among
    // their table's indexes as well.
    //
    // Audit: "an object of the schema" is written as NameClaim.Relation, which is PostgreSQL's
    // word and is described to the consumer in PostgreSQL's words (SK1001). NameClaim.Constraint
    // and NameClaim.Trigger are named within a table and have no reading here.
    public override IEnumerable<NameClaim> Claims(SchemaModelObject kind, TableRef table, Identifier name)
    {
        Asked.Add((kind, name.Name));

        return kind switch
        {
            SchemaModelObject.Index => [new NameClaim.TableIndex(table, name)],
            SchemaModelObject.PrimaryKey or SchemaModelObject.UniqueConstraint =>
                [new NameClaim.Relation(table.Schema, name), new NameClaim.TableIndex(table, name)],
            _ => [new NameClaim.Relation(table.Schema, name)],
        };
    }
}

/// <summary>What the probe adds to a context's services beside the kernel's.</summary>
/// <param name="dialect">Whether SQL Server's dialect replaces the kernel's own, which is what a provider without a package gets.</param>
internal sealed class ProbeSqlServerOptionsExtension(bool dialect) : IDbContextOptionsExtension
{
    public bool Dialect => dialect;

    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // As the PostgreSQL package does it: Replace, since EF Core does not promise which of the
        // two extensions is applied first. The identifier limit is the design-time model's, so
        // this is a diff-time service.
        if (dialect)
        {
            services.Replace(ServiceDescriptor.Scoped<ISchemaDialect>(provider => new ProbeSqlServerDialect(
                new SchemaDialectDependencies(
                    provider.GetRequiredService<ISqlGenerationHelper>(),
                    provider.GetRequiredService<IRelationalTypeMappingSource>()),
                provider.GetRequiredService<IDesignTimeModel>().Model.GetMaxIdentifierLength())));
        }

        services.AddSchemaContributor<ProbeIndexContributor>();
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(ProbeSqlServerOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using SchemaKit.Probe.SqlServer ";

        public override int GetServiceProviderHashCode() => extension.Dialect ? 1 : 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info && ((ProbeSqlServerOptionsExtension)info.Extension).Dialect == extension.Dialect;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["CodoMetis:SchemaKit:Probe:SqlServer"] = extension.Dialect ? "dialect" : "kernel's dialect";
    }
}

/// <summary>The registration a consumer would write: <c>UseSqlServer(connectionString, o => o.UseSchemaKit())</c>, in the real package.</summary>
internal static class ProbeSqlServerRegistration
{
    /// <param name="optionsBuilder">SQL Server's builder.</param>
    /// <param name="dialect">False to leave the kernel's own dialect in place, as on a provider without a package.</param>
    public static SqlServerDbContextOptionsBuilder UseSchemaKitProbe(this SqlServerDbContextOptionsBuilder optionsBuilder, bool dialect = true)
    {
        var core = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;
        core.UseSchemaKit();
        ((IDbContextOptionsBuilderInfrastructure)core).AddOrUpdateExtension(new ProbeSqlServerOptionsExtension(dialect));
        return optionsBuilder;
    }
}
