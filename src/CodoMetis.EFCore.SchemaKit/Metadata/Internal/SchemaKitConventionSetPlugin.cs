using System.Reflection;
using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Metadata.Internal;

/// <summary>Adds <see cref="SchemaKitWiringConvention" /> to every model the context builds.</summary>
internal sealed class SchemaKitConventionSetPlugin(IServiceProvider services) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelInitializedConventions.Add(new SchemaKitWiringConvention(services));
        return conventionSet;
    }
}

/// <summary>
/// Runs when a model is created, before <c>OnModelCreating</c>: checks that SchemaKit owns the two
/// services it decorates, then stamps the model.
/// </summary>
/// <remarks>
/// The check resolves both services while the model is being built. Neither needs the model, so it
/// does not cycle (measured on Npgsql, SQL Server and SQLite). It is what catches a displacement
/// the options do not show: a registration added to a caller-owned provider after SchemaKit's, and
/// a replacement keyed on SchemaKit's own type. It runs once per model build; on a model-cache hit
/// the decorators are still there, since they are resolved per context.
/// </remarks>
internal sealed class SchemaKitWiringConvention(IServiceProvider services) : IModelInitializedConvention
{
    public void ProcessModelInitialized(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ThrowUnlessOutermost<IMigrationsModelDiffer, SchemaKitModelDiffer>();
        ThrowUnlessOutermost<IMigrationsSqlGenerator, SchemaKitSqlGenerator>();

        // EF Core reads design-time service references from the startup assembly and the one that
        // holds the migrations; the context's own and the entry assembly are the two this can see.
        LegacyPackageGuard.ThrowIfReferenced(
            services.GetService<ICurrentDbContext>()?.Context.GetType().Assembly,
            Assembly.GetEntryAssembly());

        // The registrations are plain values: a contributor itself may need the dialect, which reads
        // the design-time model, and must not be resolved while a model is being built.
        SchemaKitModelStamp.Stamp(
            (IMutableModel)modelBuilder.Metadata,
            services.GetServices<SchemaContributorRegistration>().Select(r => r.Owner));
    }

    private void ThrowUnlessOutermost<TService, TOurs>() where TService : notnull
    {
        var actual = services.GetRequiredService<TService>();
        if (actual is not TOurs)
            throw new InvalidOperationException(SchemaKitStrings.NotOutermost(typeof(TService), actual.GetType()));
    }
}
