using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>Registers SchemaKit in a service collection the caller owns.</summary>
public static class SchemaKitServiceCollectionExtensions
{
    /// <summary>
    /// Adds SchemaKit's services to the collection of a provider passed to
    /// <see cref="DbContextOptionsBuilder.UseInternalServiceProvider" />. Only that case needs it:
    /// otherwise <see cref="SchemaKitDbContextOptionsBuilderExtensions.UseSchemaKit(DbContextOptionsBuilder)" />
    /// is the whole registration.
    /// </summary>
    /// <remarks>
    /// Call it after the provider's <c>AddEntityFramework…()</c> and after any other package that
    /// registers <see cref="IMigrationsModelDiffer" /> or <see cref="IMigrationsSqlGenerator" />: it
    /// wraps the registration that is last at this point. One added after it is refused when the
    /// model is built.
    /// </remarks>
    /// <param name="services">The collection the internal service provider is built from.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddEntityFrameworkSchemaKit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var inner = new InnerServices();
        inner.Capture<IMigrationsModelDiffer>(services);
        inner.Capture<IMigrationsSqlGenerator>(services);
        services.AddSingleton(inner);

        services.AddScoped<IMigrationsModelDiffer, SchemaKitModelDiffer>();
        services.AddScoped<IMigrationsSqlGenerator, SchemaKitSqlGenerator>();
        AddShared(services);
        return services;
    }

    /// <summary>What both registration paths add besides the two decorators.</summary>
    internal static void AddShared(IServiceCollection services)
    {
        services.TryAddScoped<ISchemaOperationPlanner, NoSchemaOperations>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConventionSetPlugin, SchemaKitConventionSetPlugin>());
    }
}
