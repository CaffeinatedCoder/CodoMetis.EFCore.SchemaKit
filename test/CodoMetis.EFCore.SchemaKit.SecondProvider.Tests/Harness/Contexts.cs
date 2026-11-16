using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;

/// <summary>One model of a test. A context type per model, so no test reads a model another cached.</summary>
public interface ITestModel
{
    static abstract void Build(ModelBuilder modelBuilder);
}

public sealed class Db<TModel>(DbContextOptions<Db<TModel>> options) : DbContext(options)
    where TModel : ITestModel
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => TModel.Build(modelBuilder);
}

internal static class Contexts
{
    public const string OfflineSqlServer = "Server=localhost;Database=schemakit_tests;TrustServerCertificate=true";

    public const string OfflineSqlite = "Data Source=schemakit_tests.db";

    /// <summary>
    /// A context on SQL Server, wired the way a consumer would wire the real package. Without a
    /// connection string it builds models and scripts and reaches no server.
    /// </summary>
    /// <param name="connection">The database, or null for none.</param>
    /// <param name="dialect">False for the kernel's own dialect: what a provider without a package gets.</param>
    public static Db<TModel> SqlServer<TModel>(string? connection = null, bool dialect = true)
        where TModel : ITestModel
        => new(Options<TModel>().UseSqlServer(connection ?? OfflineSqlServer, o => o.UseSchemaKitProbe(dialect)).Options);

    /// <inheritdoc cref="SqlServer{TModel}" />
    public static Db<TModel> Sqlite<TModel>(string? connection = null, bool dialect = true)
        where TModel : ITestModel
        => new(Options<TModel>().UseSqlite(connection ?? OfflineSqlite, o => o.UseSchemaKitProbe(dialect)).Options);

    private static DbContextOptionsBuilder<Db<TModel>> Options<TModel>()
        where TModel : ITestModel
        => new DbContextOptionsBuilder<Db<TModel>>().ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    public static IRelationalModel Relational(this DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    /// <summary>What <c>migrations add</c> would scaffold to get from the one context's model to the other's.</summary>
    public static IReadOnlyList<MigrationOperation> DiffTo(this DbContext from, DbContext to)
        => to.GetService<IMigrationsModelDiffer>().GetDifferences(from.Relational(), to.Relational());

    /// <summary>What the first migration of a context's model holds.</summary>
    public static IReadOnlyList<MigrationOperation> FirstMigration(this DbContext to)
        => to.GetService<IMigrationsModelDiffer>().GetDifferences(null, to.Relational());

    /// <summary>The collisions of names the diff refuses a context's model for, or null when it refuses nothing.</summary>
    public static SchemaValidationException? Collisions(this DbContext context)
    {
        try
        {
            _ = context.FirstMigration();
            return null;
        }
        catch (SchemaValidationException refused)
        {
            return refused;
        }
    }

    /// <summary>
    /// The model as the next diff reads it: scaffolded into a snapshot by EF Core's design-time
    /// services, compiled, loaded and initialized. A property bag without CLR types.
    /// </summary>
    public static IRelationalModel Snapshot(this DbContext context)
        => context.GetService<IModelRuntimeInitializer>()
            .Initialize(Snapshots.Of(context).Model, designTime: true, validationLogger: null)
            .GetRelationalModel();

    /// <summary>Each operation in its order: SchemaKit's as its SQL, EF Core's as its type.</summary>
    public static List<string> Shape(this IEnumerable<MigrationOperation> operations)
        => [.. operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name)];

    /// <summary>The contributor a context's differ asks, for a test that breaks one of its rules.</summary>
    public static TContributor Contributor<TContributor>(this DbContext context)
        where TContributor : ISchemaContributor
        => context.GetService<IEnumerable<ISchemaContributor>>().OfType<TContributor>().Single();
}
