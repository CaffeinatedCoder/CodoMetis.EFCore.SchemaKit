using System.Runtime.CompilerServices;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>Registers SchemaKit on a context that uses Npgsql.</summary>
public static class SchemaKitNpgsqlDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Puts SchemaKit and its PostgreSQL objects into every path that turns this context's model
    /// into DDL: <c>options.UseNpgsql(connectionString, o => o.UseSchemaKit())</c>. It is the whole
    /// registration: the kernel's, PostgreSQL's dialect, and what renders an exclusion constraint.
    /// </summary>
    /// <remarks>
    /// A model that declares a PostgreSQL object without this call fails where the declaration is
    /// written, in <c>OnModelCreating</c>. With
    /// <see cref="DbContextOptionsBuilder.UseInternalServiceProvider" />, add
    /// <see cref="SchemaKitNpgsqlServiceCollectionExtensions.AddEntityFrameworkSchemaKitNpgsql" />
    /// to that provider's collection as well.
    /// </remarks>
    /// <param name="optionsBuilder">Npgsql's builder, as <c>UseNpgsql</c> hands it to its callback.</param>
    /// <returns>The same builder.</returns>
    public static NpgsqlDbContextOptionsBuilder UseSchemaKit(this NpgsqlDbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // First, and from a method that calls nothing else of the kernel: beside a kernel of another
        // version this is the message, where the calls below would be a MissingMethodException.
        SchemaKitPackages.RequireKernel(typeof(SchemaKitNpgsqlDbContextOptionsBuilderExtensions).Assembly);
        return Register(optionsBuilder);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NpgsqlDbContextOptionsBuilder Register(NpgsqlDbContextOptionsBuilder optionsBuilder)
    {
        var core = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;
        core.UseSchemaKit();

        if (core.Options.FindExtension<NpgsqlSchemaKitOptionsExtension>() is null)
            ((IDbContextOptionsBuilderInfrastructure)core).AddOrUpdateExtension(new NpgsqlSchemaKitOptionsExtension());

        return optionsBuilder;
    }
}

/// <summary>Registers SchemaKit for PostgreSQL in a service collection the caller owns.</summary>
public static class SchemaKitNpgsqlServiceCollectionExtensions
{
    /// <summary>
    /// Adds SchemaKit's services and its PostgreSQL ones to the collection of a provider passed to
    /// <see cref="DbContextOptionsBuilder.UseInternalServiceProvider" />. Only that case needs it.
    /// Call it after <c>AddEntityFrameworkNpgsql()</c>, and in place of
    /// <see cref="SchemaKitServiceCollectionExtensions.AddEntityFrameworkSchemaKit" />, not beside it.
    /// </summary>
    /// <param name="services">The collection the internal service provider is built from.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddEntityFrameworkSchemaKitNpgsql(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        SchemaKitPackages.RequireKernel(typeof(SchemaKitNpgsqlServiceCollectionExtensions).Assembly);
        return Register(services);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IServiceCollection Register(IServiceCollection services)
    {
        services.AddEntityFrameworkSchemaKit();
        NpgsqlSchemaKitOptionsExtension.AddServices(services);
        return services;
    }
}
