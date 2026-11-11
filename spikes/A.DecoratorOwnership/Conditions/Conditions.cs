#pragma warning disable EF1001 // typeof(MigrationsModelDiffer), only to play a stranger's type-specific ReplaceService

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SchemaKit.Spikes.A.OtherPackage;
using SchemaKit.Spikes.A.SpikeKit;

namespace SchemaKit.Spikes.A.Conditions;

public enum Provider { Npgsql, SqlServer, Sqlite }

public sealed class Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>One context type per test (<typeparamref name="TTest"/>), so no test reads a model another cached.</summary>
public sealed class Ctx<TTest>(DbContextOptions<Ctx<TTest>> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Thing>().DeclareSpike("thing_name");
}

public sealed record Observed(string OuterDiffer, string InnerDiffer, string OuterGenerator, string InnerGenerator, string Script)
{
    public bool HasSpikeObject => Script.Contains("spike:thing_name");
    public bool HasOtherPackage => Script.Contains("other-package");
}

internal static class Setup
{
    public static DbContextOptionsBuilder<TContext> Use<TContext>(this DbContextOptionsBuilder<TContext> builder, Provider provider)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).Use(provider);
        return builder;
    }

    public static DbContextOptionsBuilder Use(this DbContextOptionsBuilder builder, Provider provider)
    {
        // Every condition is its own options shape, so its own internal service provider. EF turns
        // the twenty-first into an error; a suite that varies the wiring has to switch that off.
        builder.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return provider switch
        {
            Provider.Npgsql    => builder.UseNpgsql("Host=localhost;Database=spike"),
            Provider.SqlServer => builder.UseSqlServer("Server=localhost;Database=spike;TrustServerCertificate=true"),
            _                  => builder.UseSqlite("Data Source=spike.db"),
        };
    }

    public static IServiceCollection AddProvider(this IServiceCollection services, Provider provider) => provider switch
    {
        Provider.Npgsql    => services.AddEntityFrameworkNpgsql(),
        Provider.SqlServer => services.AddEntityFrameworkSqlServer(),
        _                  => services.AddEntityFrameworkSqlite(),
    };

    public static string Generator(Provider provider) => provider switch
    {
        Provider.Npgsql    => "NpgsqlMigrationsSqlGenerator",
        Provider.SqlServer => "SqlServerMigrationsSqlGenerator",
        _                  => "SqliteMigrationsSqlGenerator",
    };

    public static DbContextOptionsBuilder<Ctx<TTest>> Options<TTest>() => new();

    /// <summary>Builds the model (which runs the ownership check), then reads what the context actually uses.</summary>
    public static Observed Observe(DbContext context)
    {
        _ = context.Model;
        var differ    = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        return new Observed(
            differ.GetType().Name,
            (differ as SpikeDiffer)?.Inner.GetType().Name ?? "",
            generator.GetType().Name,
            (generator as SpikeGenerator)?.Inner.GetType().Name ?? "",
            context.Database.GenerateCreateScript());
    }

    public static void AssertOwned(Observed seen, Provider provider, string innerDiffer = "MigrationsModelDiffer")
    {
        Assert.AreEqual(nameof(SpikeDiffer), seen.OuterDiffer);
        Assert.AreEqual(innerDiffer, seen.InnerDiffer);
        Assert.AreEqual(nameof(SpikeGenerator), seen.OuterGenerator);
        Assert.AreEqual(Generator(provider), seen.InnerGenerator);
        Assert.IsTrue(seen.HasSpikeObject, "The create script lacks the declared object:\n" + seen.Script);
    }
}

[TestClass]
[DoNotParallelize]
public sealed class Conditions
{
    private sealed class T01; private sealed class T02; private sealed class T03; private sealed class T04;
    private sealed class T05; private sealed class T06; private sealed class T07; private sealed class T08;
    private sealed class T09; private sealed class T10; private sealed class T11; private sealed class T12;
    private sealed class T13; private sealed class T14; private sealed class T15; private sealed class T16;
    private sealed class T17; private sealed class T18; private sealed class T19; private sealed class T20;
    private sealed class T21;

    // ---- the plain case, both call orders -------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C01_provider_then_kit(Provider provider)
    {
        using var context = new Ctx<T01>(Setup.Options<T01>().Use(provider).UseSpikeKit().Options);
        Setup.AssertOwned(Setup.Observe(context), provider);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C02_kit_then_provider(Provider provider)
    {
        var builder = Setup.Options<T02>();
        builder.UseSpikeKit();
        using var context = new Ctx<T02>(builder.Use(provider).Options);
        Setup.AssertOwned(Setup.Observe(context), provider);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C03_no_kit_fails_at_the_declaration(Provider provider)
    {
        using var context = new Ctx<T03>(Setup.Options<T03>().Use(provider).Options);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);
        StringAssert.Contains(error.Message, "UseSpikeKit() is not on this context's options");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C04_has_differences_sees_the_declaration(Provider provider)
    {
        using var context = new Ctx<T04>(Setup.Options<T04>().Use(provider).UseSpikeKit().Options);
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        Assert.IsTrue(context.GetService<IMigrationsModelDiffer>().HasDifferences(null, model));
        Assert.IsFalse(context.GetService<IMigrationsModelDiffer>().HasDifferences(model, model));
    }

    // ---- another package's ReplaceService ---------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C05_replace_service_before_the_kit_becomes_the_inner(Provider provider)
    {
        var builder = Setup.Options<T05>().Use(provider);
        builder.ReplaceService<IMigrationsModelDiffer, OtherDiffer>();
        using var context = new Ctx<T05>(builder.UseSpikeKit().Options);

        var seen = Setup.Observe(context);
        Setup.AssertOwned(seen, provider, innerDiffer: nameof(OtherDiffer));
        Assert.IsTrue(seen.HasOtherPackage, "The other package's operation was lost.");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C06_replace_service_after_the_kit_is_refused(Provider provider)
    {
        var builder = Setup.Options<T06>().Use(provider);
        builder.UseSpikeKit();
        builder.ReplaceService<IMigrationsModelDiffer, OtherDiffer>();

        // Validate runs when the context is constructed: the constructor asks for the internal
        // service provider, and EF validates the options before building it.
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new Ctx<T06>(builder.Options));
        StringAssert.Contains(error.Message, "was called after UseSpikeKit() and displaced it");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C07_type_specific_replace_before_the_kit_becomes_the_inner(Provider provider)
    {
        var builder = Setup.Options<T07>().Use(provider);
        builder.ReplaceService<IMigrationsModelDiffer, MigrationsModelDiffer, OtherDiffer>();
        using var context = new Ctx<T07>(builder.UseSpikeKit().Options);

        var seen = Setup.Observe(context);
        Setup.AssertOwned(seen, provider, innerDiffer: nameof(OtherDiffer));
        Assert.IsTrue(seen.HasOtherPackage);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C08_type_specific_replace_after_the_kit_becomes_the_inner(Provider provider)
    {
        var builder = Setup.Options<T08>().Use(provider);
        builder.UseSpikeKit();
        builder.ReplaceService<IMigrationsModelDiffer, MigrationsModelDiffer, OtherDiffer>();
        using var context = new Ctx<T08>(builder.Options);

        var seen = Setup.Observe(context);
        Setup.AssertOwned(seen, provider, innerDiffer: nameof(OtherDiffer));
        Assert.IsTrue(seen.HasOtherPackage);
    }

    // ---- another extension that registers a differ directly ---------------------------------------

    [TestMethod]
    [DataRow(Provider.Npgsql, false), DataRow(Provider.SqlServer, false), DataRow(Provider.Sqlite, false)]
    [DataRow(Provider.Npgsql, true), DataRow(Provider.SqlServer, true), DataRow(Provider.Sqlite, true)]
    public void C09_direct_registration_after_the_kit_becomes_the_inner(Provider provider, bool throughFactory)
    {
        var builder = Setup.Options<T09>().Use(provider);
        builder.UseSpikeKit();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new OtherOptionsExtension(throughFactory));
        using var context = new Ctx<T09>(builder.Options);

        var seen = Setup.Observe(context);
        Setup.AssertOwned(seen, provider, innerDiffer: nameof(OtherDiffer));
        Assert.IsTrue(seen.HasOtherPackage);
    }

    [TestMethod]
    [DataRow(Provider.Npgsql, false), DataRow(Provider.SqlServer, false), DataRow(Provider.Sqlite, false)]
    [DataRow(Provider.Npgsql, true), DataRow(Provider.SqlServer, true), DataRow(Provider.Sqlite, true)]
    public void C10_direct_registration_before_the_provider_becomes_the_inner(Provider provider, bool throughFactory)
    {
        var builder = Setup.Options<T10>();
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new OtherOptionsExtension(throughFactory));
        using var context = new Ctx<T10>(builder.Use(provider).UseSpikeKit().Options);

        var seen = Setup.Observe(context);
        Setup.AssertOwned(seen, provider, innerDiffer: nameof(OtherDiffer));
        Assert.IsTrue(seen.HasOtherPackage);
    }

    // ---- how the context is registered ------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C11_add_db_context(Provider provider)
    {
        using var services = new ServiceCollection()
            .AddDbContext<Ctx<T11>>(o => o.Use(provider).UseSpikeKit())
            .BuildServiceProvider();

        for (var i = 0; i < 2; i++)
        {
            using var scope = services.CreateScope();
            Setup.AssertOwned(Setup.Observe(scope.ServiceProvider.GetRequiredService<Ctx<T11>>()), provider);
        }
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C12_pooled_context_reused(Provider provider)
    {
        using var services = new ServiceCollection()
            .AddDbContextPool<Ctx<T12>>(o => o.Use(provider).UseSpikeKit(), poolSize: 1)
            .BuildServiceProvider();

        DbContext? first = null;
        for (var i = 0; i < 3; i++)
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<Ctx<T12>>();
            first ??= context;
            Setup.AssertOwned(Setup.Observe(context), provider);
            if (i > 0) Assert.AreSame(first, context, "The pool handed out a new instance; the reuse case was not exercised.");
        }
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C13_context_factory(Provider provider)
    {
        using var services = new ServiceCollection()
            .AddDbContextFactory<Ctx<T13>>(o => o.Use(provider).UseSpikeKit())
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<Ctx<T13>>>();

        for (var i = 0; i < 2; i++)
        {
            using var context = factory.CreateDbContext();
            Setup.AssertOwned(Setup.Observe(context), provider);
        }
    }

    // ---- UseInternalServiceProvider -----------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C14_internal_service_provider_with_the_kit_added(Provider provider)
    {
        using var internalProvider = new ServiceCollection().AddProvider(provider).AddEntityFrameworkSpikeKit().BuildServiceProvider();
        var builder = Setup.Options<T14>().Use(provider);
        builder.UseInternalServiceProvider(internalProvider);
        using var context = new Ctx<T14>(builder.UseSpikeKit().Options);

        Setup.AssertOwned(Setup.Observe(context), provider);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C15_internal_service_provider_without_the_kit_is_refused(Provider provider)
    {
        using var internalProvider = new ServiceCollection().AddProvider(provider).BuildServiceProvider();
        var builder = Setup.Options<T15>().Use(provider);
        builder.UseInternalServiceProvider(internalProvider);
        builder.UseSpikeKit();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new Ctx<T15>(builder.Options));
        StringAssert.Contains(error.Message, "has no SpikeKit services");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C16_kit_called_before_use_internal_service_provider_is_refused_by_ef(Provider provider)
    {
        using var internalProvider = new ServiceCollection().AddProvider(provider).AddEntityFrameworkSpikeKit().BuildServiceProvider();
        var builder = Setup.Options<T16>().Use(provider);
        builder.UseSpikeKit();                                  // records ReplaceService entries
        builder.UseInternalServiceProvider(internalProvider);   // which EF refuses beside this

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new Ctx<T16>(builder.Options));
        StringAssert.Contains(error.Message, "ReplaceService");
    }

    // ---- what only the resolve-time check catches ---------------------------------------------------

    /// <summary>
    /// With a caller-owned provider there is no ReplaceService pass to put SpikeKit back on top: a
    /// registration added after AddEntityFrameworkSpikeKit() wins, and Validate cannot see it.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C20_internal_service_provider_with_a_later_registration_is_refused_at_model_build(Provider provider)
    {
        using var internalProvider = new ServiceCollection()
            .AddProvider(provider)
            .AddEntityFrameworkSpikeKit()
            .AddScoped<IMigrationsModelDiffer, OtherDiffer>()
            .BuildServiceProvider();
        var builder = Setup.Options<T20>().Use(provider);
        builder.UseInternalServiceProvider(internalProvider);
        using var context = new Ctx<T20>(builder.UseSpikeKit().Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);
        StringAssert.Contains(error.Message, "IMigrationsModelDiffer is 'OtherDiffer', not SpikeKit's");
    }

    /// <summary>A replacement keyed on SpikeKit's own type rewrites SpikeKit's registration.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C21_replace_service_keyed_on_the_kits_type_is_refused_at_model_build(Provider provider)
    {
        var builder = Setup.Options<T21>().Use(provider);
        builder.UseSpikeKit();
        builder.ReplaceService<IMigrationsModelDiffer, SpikeDiffer, OtherDiffer>();
        using var context = new Ctx<T21>(builder.Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);
        StringAssert.Contains(error.Message, "IMigrationsModelDiffer is 'OtherDiffer', not SpikeKit's");
    }

    // ---- the model cache ------------------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C17_cache_hit_skips_the_check_but_not_the_decorators(Provider provider)
    {
        var options = Setup.Options<T17>().Use(provider).UseSpikeKit().Options;

        var before = StampConvention.Checks;
        using (var first = new Ctx<T17>(options)) Setup.AssertOwned(Setup.Observe(first), provider);
        var afterFirst = StampConvention.Checks;
        using (var second = new Ctx<T17>(options)) Setup.AssertOwned(Setup.Observe(second), provider);

        Assert.AreEqual(1, afterFirst - before, "The check should run once when the model is built.");
        Assert.AreEqual(afterFirst, StampConvention.Checks, "A cache hit should not build the model again.");
    }

    /// <summary>
    /// The residual, measured: a model cache shared between options with and without the kit hands
    /// the stamped model to the context without it. Nothing of SpikeKit runs there.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C18_shared_memory_cache_carries_a_stamped_model_to_unwired_options(Provider provider)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var wired   = Setup.Options<T18>().Use(provider).UseSpikeKit().UseMemoryCache(cache).Options;
        var unwired = Setup.Options<T18>().Use(provider).UseMemoryCache(cache).Options;

        using (var first = new Ctx<T18>(wired)) Setup.AssertOwned(Setup.Observe(first), provider);

        using var second = new Ctx<T18>(unwired);
        var seen = Setup.Observe(second);                       // no exception: the cached model carries the stamp
        Assert.AreNotEqual(nameof(SpikeDiffer), seen.OuterDiffer);
        Assert.IsFalse(seen.HasSpikeObject, "Expected the silent omission this condition documents.");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void C19_use_spike_kit_twice_is_the_same_as_once(Provider provider)
    {
        var builder = Setup.Options<T19>().Use(provider);
        builder.ReplaceService<IMigrationsModelDiffer, OtherDiffer>();
        builder.UseSpikeKit();
        using var context = new Ctx<T19>(builder.UseSpikeKit().Options);

        Setup.AssertOwned(Setup.Observe(context), provider, innerDiffer: nameof(OtherDiffer));
    }
}
