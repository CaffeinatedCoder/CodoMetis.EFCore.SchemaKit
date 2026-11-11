using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SchemaKit.Spikes.A.SpikeKit;

/// <summary>What <c>UseSchemaKit()</c> would do, in miniature.</summary>
public static class SpikeKitExtensions
{
    public const string Stamp = "Spike:Wired";
    public const string Declared = "Spike:Object";

    /// <summary>
    /// The one registration. It takes over the <c>ReplaceService</c> entry of both services, keeping
    /// whatever an earlier call put there as the inner service.
    /// </summary>
    public static DbContextOptionsBuilder UseSpikeKit(this DbContextOptionsBuilder builder)
    {
        var core     = builder.Options.FindExtension<CoreOptionsExtension>();
        var existing = builder.Options.FindExtension<SpikeOptionsExtension>();

        var extension = new SpikeOptionsExtension(
            Earlier<IMigrationsModelDiffer, SpikeDiffer>(core, existing?.InnerDiffer),
            Earlier<IMigrationsSqlGenerator, SpikeGenerator>(core, existing?.InnerGenerator));
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(extension);

        // With a caller-owned provider EF applies no ReplaceService at all and refuses the call;
        // AddEntityFrameworkSpikeKit() decorates that collection instead.
        if (core?.InternalServiceProvider is null)
        {
            builder.ReplaceService<IMigrationsModelDiffer, SpikeDiffer>();
            builder.ReplaceService<IMigrationsSqlGenerator, SpikeGenerator>();
        }

        return builder;
    }

    /// <summary>The generic builder's overload, as EF's own options methods have one.</summary>
    public static DbContextOptionsBuilder<TContext> UseSpikeKit<TContext>(this DbContextOptionsBuilder<TContext> builder)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).UseSpikeKit();
        return builder;
    }

    private static Type? Earlier<TService, TOurs>(CoreOptionsExtension? core, Type? known)
        => core?.ReplacedServices is { } replaced
           && replaced.TryGetValue((typeof(TService), null), out var type)
           && type != typeof(TOurs)
            ? type
            : known;

    /// <summary>
    /// For <c>UseInternalServiceProvider</c>: called on the caller's collection after the provider's
    /// <c>AddEntityFramework…()</c>. Wraps the last registration of both services.
    /// </summary>
    public static IServiceCollection AddEntityFrameworkSpikeKit(this IServiceCollection services)
    {
        var inner = new InnerServices();
        inner.Capture<IMigrationsModelDiffer>(services);
        inner.Capture<IMigrationsSqlGenerator>(services);
        services.AddSingleton(inner);
        services.AddScoped<IMigrationsModelDiffer, SpikeDiffer>();
        services.AddScoped<IMigrationsSqlGenerator, SpikeGenerator>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConventionSetPlugin, StampPlugin>());
        return services;
    }

    /// <summary>The declaration API: it refuses a model that <c>UseSpikeKit()</c> did not stamp.</summary>
    public static EntityTypeBuilder<TEntity> DeclareSpike<TEntity>(this EntityTypeBuilder<TEntity> builder, string name)
        where TEntity : class
    {
        if (builder.Metadata.Model.FindAnnotation(Stamp) is null)
            throw new InvalidOperationException(
                $"'{name}' is declared on '{builder.Metadata.DisplayName()}', but UseSpikeKit() is not on this context's options. " +
                "Without it the declaration would be left out of migrations and EnsureCreated() without an error.");

        builder.HasAnnotation(Declared, name);
        return builder;
    }
}

public sealed class SpikeOptionsExtension(Type? innerDiffer, Type? innerGenerator) : IDbContextOptionsExtension
{
    /// <summary>What an earlier <c>ReplaceService</c> had put in the entry this extension took over.</summary>
    public Type? InnerDiffer { get; } = innerDiffer;
    public Type? InnerGenerator { get; } = innerGenerator;

    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        services.TryAddSingleton(new InnerServices());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConventionSetPlugin, StampPlugin>());

        // Plain Add, not TryAdd: a registration of its own, so a type-specific
        // ReplaceService<IMigrationsModelDiffer, TCurrent, TOther>() rewrites the provider's
        // descriptor and not this one. If the provider's extension is applied after this one, its
        // TryAdd finds these and adds nothing.
        services.AddScoped<IMigrationsModelDiffer, SpikeDiffer>();
        services.AddScoped<IMigrationsSqlGenerator, SpikeGenerator>();
    }

    public void Validate(IDbContextOptions options)
    {
        var core = options.FindExtension<CoreOptionsExtension>();

        if (core?.InternalServiceProvider is { } provider)
        {
            using var scope = provider.CreateScope();
            if (!scope.ServiceProvider.GetServices<IConventionSetPlugin>().OfType<StampPlugin>().Any())
                throw new InvalidOperationException(
                    "UseSpikeKit() is on the options, but the service provider passed to UseInternalServiceProvider() " +
                    "has no SpikeKit services. Call AddEntityFrameworkSpikeKit() on its collection, after the provider's AddEntityFramework…().");
            return;
        }

        Owns<IMigrationsModelDiffer, SpikeDiffer>(core);
        Owns<IMigrationsSqlGenerator, SpikeGenerator>(core);
    }

    private static void Owns<TService, TOurs>(CoreOptionsExtension? core)
    {
        if (core?.ReplacedServices is { } replaced
            && replaced.TryGetValue((typeof(TService), null), out var type)
            && type != typeof(TOurs))
            throw new InvalidOperationException(
                $"ReplaceService<{typeof(TService).Name}, {type.Name}>() was called after UseSpikeKit() and displaced it. " +
                "Call UseSpikeKit() last: it keeps an earlier replacement as its inner service.");
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        private SpikeOptionsExtension Kit => (SpikeOptionsExtension)Extension;

        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using SpikeKit ";

        public override int GetServiceProviderHashCode() => HashCode.Combine(Kit.InnerDiffer, Kit.InnerGenerator);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && o.Kit.InnerDiffer == Kit.InnerDiffer && o.Kit.InnerGenerator == Kit.InnerGenerator;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["Spike:Kit"] = $"{Kit.InnerDiffer?.Name}|{Kit.InnerGenerator?.Name}";
    }
}

/// <summary>Adds the convention that stamps the model and checks who owns the two services.</summary>
public sealed class StampPlugin(IServiceProvider services) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelInitializedConventions.Add(new StampConvention(services));
        return conventionSet;
    }
}

public sealed class StampConvention(IServiceProvider services) : IModelInitializedConvention
{
    /// <summary>How often the ownership check ran, for the model-cache condition.</summary>
    public static int Checks;

    public void ProcessModelInitialized(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        // Resolved while the model is being built. Neither service may need the model, or this cycles.
        Outermost<IMigrationsModelDiffer, SpikeDiffer>();
        Outermost<IMigrationsSqlGenerator, SpikeGenerator>();
        Interlocked.Increment(ref Checks);

        modelBuilder.HasAnnotation(SpikeKitExtensions.Stamp, "1");
    }

    private void Outermost<TService, TOurs>() where TService : notnull
    {
        var actual = services.GetRequiredService<TService>();
        if (actual is not TOurs)
            throw new InvalidOperationException(
                $"{typeof(TService).Name} is '{actual.GetType().Name}', not SpikeKit's. SpikeKit declarations would be " +
                "left out of migrations without an error. Register the other package before UseSpikeKit(), which keeps it as its inner service.");
    }
}
