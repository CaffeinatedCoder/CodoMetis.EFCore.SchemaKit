using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

public enum Provider { Npgsql, SqlServer, Sqlite }

public sealed class Thing
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Gadget
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>One context type per test (<typeparamref name="TTest" />), so no test reads a model another cached.</summary>
public sealed class Ctx<TTest>(DbContextOptions<Ctx<TTest>> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Thing>().DeclareTestObject("thing_name");
}

/// <summary>What a context actually uses, read after its model is built.</summary>
public sealed record Observed(string OuterDiffer, string InnerDiffer, string OuterGenerator, string InnerGenerator, string Script)
{
    public bool HasTestObject => Script.Contains("test_thing_name");
    public bool HasOtherPackage => Script.Contains("other-package");
}

internal static class Setup
{
    public static DbContextOptionsBuilder<Ctx<TTest>> Options<TTest>() => new();

    public static DbContextOptionsBuilder<TContext> Use<TContext>(this DbContextOptionsBuilder<TContext> builder, Provider provider, string? connection = null)
        where TContext : DbContext
    {
        ((DbContextOptionsBuilder)builder).Use(provider, connection);
        return builder;
    }

    /// <summary>The provider and the test kit; whether SchemaKit is wired is each test's own line.</summary>
    public static DbContextOptionsBuilder Use(this DbContextOptionsBuilder builder, Provider provider, string? connection = null)
        => builder.UseProvider(provider, connection).UseTestKit();

    /// <summary>The provider alone: a context nothing has contributed to.</summary>
    public static DbContextOptionsBuilder UseProvider(this DbContextOptionsBuilder builder, Provider provider, string? connection = null)
    {
        // Every condition is its own options shape, so its own internal service provider. EF Core
        // turns the twenty-first into an error; a suite that varies the wiring switches that off.
        builder.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        _ = provider switch
        {
            Provider.Npgsql    => builder.UseNpgsql(connection ?? "Host=localhost;Database=schemakit_tests"),
            Provider.SqlServer => builder.UseSqlServer(connection ?? "Server=localhost;Database=schemakit_tests;TrustServerCertificate=true"),
            _                  => builder.UseSqlite(connection ?? "Data Source=schemakit_tests.db"),
        };
        return builder;
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

    /// <summary>Builds the model, which runs the ownership check, then reads what the context uses.</summary>
    public static Observed Observe(DbContext context)
    {
        _ = context.Model;
        var differ    = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        return new Observed(
            differ.GetType().Name,
            (differ as SchemaKitModelDiffer)?.Inner.GetType().Name ?? "",
            generator.GetType().Name,
            (generator as SchemaKitSqlGenerator)?.Inner.GetType().Name ?? "",
            context.Database.GenerateCreateScript());
    }

    public static void AssertOwned(Observed seen, Provider provider, string innerDiffer = "MigrationsModelDiffer")
    {
        Assert.AreEqual(nameof(SchemaKitModelDiffer), seen.OuterDiffer);
        Assert.AreEqual(innerDiffer, seen.InnerDiffer);
        Assert.AreEqual(nameof(SchemaKitSqlGenerator), seen.OuterGenerator);
        Assert.AreEqual(Generator(provider), seen.InnerGenerator);
        Assert.IsTrue(seen.HasTestObject, "The create script lacks the declared object:\n" + seen.Script);
    }
}
