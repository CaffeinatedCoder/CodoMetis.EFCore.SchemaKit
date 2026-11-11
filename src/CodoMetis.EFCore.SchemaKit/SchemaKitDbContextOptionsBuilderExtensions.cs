using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>Registers SchemaKit on a context's options.</summary>
public static class SchemaKitDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Puts SchemaKit into every path that turns this context's model into DDL:
    /// <c>dotnet ef migrations add</c>, <c>EnsureCreated()</c>, <c>GenerateCreateScript()</c>, scripts,
    /// bundles and the pending-model-changes check of <c>Migrate()</c>. It is the only registration;
    /// nothing is added at build time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call it after anything else that replaces <see cref="IMigrationsModelDiffer" /> or
    /// <see cref="IMigrationsSqlGenerator" />. A replacement made before this call keeps working
    /// inside SchemaKit. One made after it with <c>ReplaceService&lt;TService, TImplementation&gt;()</c>
    /// is refused when the context is constructed, because EF Core keeps one replacement per service.
    /// </para>
    /// <para>
    /// With <see cref="DbContextOptionsBuilder.UseInternalServiceProvider" />, call this after it, and
    /// add <see cref="SchemaKitServiceCollectionExtensions.AddEntityFrameworkSchemaKit" /> to that
    /// provider's collection.
    /// </para>
    /// <para>
    /// A model that declares a SchemaKit object without this call fails where the declaration is
    /// written, in <c>OnModelCreating</c>.
    /// </para>
    /// </remarks>
    /// <param name="optionsBuilder">The builder of the context's options.</param>
    /// <returns>The same builder.</returns>
    public static DbContextOptionsBuilder UseSchemaKit(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var core     = optionsBuilder.Options.FindExtension<CoreOptionsExtension>();
        var existing = optionsBuilder.Options.FindExtension<SchemaKitOptionsExtension>();

        // Whatever an earlier ReplaceService put in the entry this call takes over becomes the inner
        // service. On a second call the entry is SchemaKit's own, and the first call's answer stands.
        var extension = new SchemaKitOptionsExtension(
            EarlierReplacement<IMigrationsModelDiffer, SchemaKitModelDiffer>(core, existing?.InnerDiffer),
            EarlierReplacement<IMigrationsSqlGenerator, SchemaKitSqlGenerator>(core, existing?.InnerGenerator));
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        // EF Core applies ReplaceService entries after every extension's ApplyServices, which is what
        // lets SchemaKit stay outermost whatever the order of the other extensions. With a
        // caller-owned provider it applies none and refuses the call;
        // AddEntityFrameworkSchemaKit() wraps that collection's registrations instead.
        if (core?.InternalServiceProvider is null)
        {
            optionsBuilder.ReplaceService<IMigrationsModelDiffer, SchemaKitModelDiffer>();
            optionsBuilder.ReplaceService<IMigrationsSqlGenerator, SchemaKitSqlGenerator>();
        }

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseSchemaKit(DbContextOptionsBuilder)" />
    /// <typeparam name="TContext">The context the options are for.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseSchemaKit<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseSchemaKit();
        return optionsBuilder;
    }

    private static Type? EarlierReplacement<TService, TOurs>(CoreOptionsExtension? core, Type? known)
        => core?.ReplacedServices is { } replaced
           && replaced.TryGetValue((typeof(TService), null), out var type)
           && type != typeof(TOurs)
            ? type
            : known;
}
