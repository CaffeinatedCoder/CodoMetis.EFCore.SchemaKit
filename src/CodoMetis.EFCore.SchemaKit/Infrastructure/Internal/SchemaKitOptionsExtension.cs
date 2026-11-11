using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

/// <summary>
/// SchemaKit's place on a context's options. Its registration takes three parts, each measured as
/// necessary (design/schemakit-core-plan.md, section 3.2): the ReplaceService entries taken over in
/// <c>UseSchemaKit()</c>, the registrations added here, and the ownership checks.
/// </summary>
internal sealed class SchemaKitOptionsExtension(Type? innerDiffer, Type? innerGenerator) : IDbContextOptionsExtension
{
    /// <summary>What an earlier <c>ReplaceService</c> had put in the entry <c>UseSchemaKit()</c> took over.</summary>
    public Type? InnerDiffer { get; } = innerDiffer;

    /// <inheritdoc cref="InnerDiffer" />
    public Type? InnerGenerator { get; } = innerGenerator;

    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.TryAddSingleton(new InnerServices());

        // Registrations of SchemaKit's own, with Add and not TryAdd. Without them the provider's
        // registration is the only one, and a replacement keyed on its type
        // (ReplaceService<IMigrationsModelDiffer, MigrationsModelDiffer, X>()) rewrites exactly that
        // one: EF Core looks up the type-specific entry first and never reaches SchemaKit's. When the
        // provider's extension is applied after this one, its TryAdd finds these and adds nothing.
        services.AddScoped<IMigrationsModelDiffer, SchemaKitModelDiffer>();
        services.AddScoped<IMigrationsSqlGenerator, SchemaKitSqlGenerator>();

        SchemaKitServiceCollectionExtensions.AddShared(services);
    }

    /// <summary>
    /// Runs when a context is constructed, before the service provider is built. It sees what the
    /// options show; what they do not show is the convention's to catch.
    /// </summary>
    public void Validate(IDbContextOptions options)
    {
        var core = options.FindExtension<CoreOptionsExtension>();

        if (core?.InternalServiceProvider is { } provider)
        {
            using var scope = provider.CreateScope();
            if (!scope.ServiceProvider.GetServices<IConventionSetPlugin>().OfType<SchemaKitConventionSetPlugin>().Any())
                throw new InvalidOperationException(SchemaKitStrings.InternalServiceProviderWithoutServices);
            return;
        }

        ThrowIfDisplaced<IMigrationsModelDiffer, SchemaKitModelDiffer>(core);
        ThrowIfDisplaced<IMigrationsSqlGenerator, SchemaKitSqlGenerator>(core);
    }

    private static void ThrowIfDisplaced<TService, TOurs>(CoreOptionsExtension? core)
    {
        if (core?.ReplacedServices is { } replaced
            && replaced.TryGetValue((typeof(TService), null), out var type)
            && type != typeof(TOurs))
            throw new InvalidOperationException(SchemaKitStrings.DisplacedByReplaceService(typeof(TService), type));
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private new SchemaKitOptionsExtension Extension => (SchemaKitOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using SchemaKit ";

        // The inner types decide what the decorators wrap, so options that differ in them must not
        // share an internal service provider.
        public override int GetServiceProviderHashCode() => HashCode.Combine(Extension.InnerDiffer, Extension.InnerGenerator);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o
               && o.Extension.InnerDiffer == Extension.InnerDiffer
               && o.Extension.InnerGenerator == Extension.InnerGenerator;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["CodoMetis:SchemaKit"] = $"{Extension.InnerDiffer?.FullName}|{Extension.InnerGenerator?.FullName}";
    }
}
