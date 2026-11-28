using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;

/// <summary>
/// One model of a test, and the registrations of the context that builds it. A context type per
/// model, so no test reads a model another cached.
/// </summary>
public interface ITestModel
{
    static abstract void Build(ModelBuilder modelBuilder);

    /// <summary>What a consumer writes inside <c>UseNpgsql</c>. <see cref="Wiring" /> has each form.</summary>
    static abstract void Wire(NpgsqlDbContextOptionsBuilder npgsql);
}

/// <summary>The registrations a consumer of both libraries writes, and those of the twins that use Npgsql's own ranges.</summary>
public static class Wiring
{
    /// <summary>
    /// SchemaKit and ValueRanges. <c>EnableUnmappedTypes</c> is ValueRanges' second opt-in for a
    /// <c>TimeRange</c>, whose type the database does not ship: Npgsql sends it by its name.
    /// </summary>
    public static void Both(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseSchemaKit();
        npgsql.UseValueRanges();
        npgsql.ConfigureDataSource(dataSource => dataSource.EnableUnmappedTypes());
    }

    /// <summary>
    /// <see cref="Both" />, and <c>timerange</c> mapped to Npgsql's own range as well. ValueRanges
    /// does not ask for it. A snapshot holds a <c>TimeRange</c> property as the
    /// <c>NpgsqlRange&lt;TimeOnly&gt;</c> ValueRanges converts it to, and this is what maps that.
    /// </summary>
    public static void BothWithTimeRangeMapped(NpgsqlDbContextOptionsBuilder npgsql)
    {
        Both(npgsql);
        npgsql.MapRange<TimeOnly>("timerange");
    }

    /// <summary>ValueRanges without SchemaKit, as its documentation wires a <c>TimeRange</c>: the differ is EF Core's own.</summary>
    public static void ValueRangesAlone(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseValueRanges();
        npgsql.ConfigureDataSource(dataSource => dataSource.EnableUnmappedTypes());
    }

    /// <summary>The same two, ValueRanges first: no order of the two calls is the right one.</summary>
    public static void BothValueRangesFirst(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseValueRanges();
        npgsql.ConfigureDataSource(dataSource => dataSource.EnableUnmappedTypes());
        npgsql.UseSchemaKit();
    }

    /// <summary>SchemaKit and ValueRanges' NodaTime package, which turns on Npgsql's NodaTime plugin and ValueRanges itself.</summary>
    public static void BothWithNodaTime(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseSchemaKit();
        npgsql.UseValueRangesNodaTime();
    }

    /// <summary>SchemaKit alone, for a model whose ranges are Npgsql's own.</summary>
    public static void SchemaKitAlone(NpgsqlDbContextOptionsBuilder npgsql) => npgsql.UseSchemaKit();

    /// <summary>SchemaKit and Npgsql's NodaTime plugin, for a model whose ranges are Npgsql's own over NodaTime's types.</summary>
    public static void SchemaKitWithNodaTime(NpgsqlDbContextOptionsBuilder npgsql)
    {
        npgsql.UseSchemaKit();
        npgsql.UseNodaTime();
    }
}

public sealed class Db<TModel>(DbContextOptions<Db<TModel>> options) : DbContext(options)
    where TModel : ITestModel
{
    // No extension is declared here: "=" on an integer under GiST needs btree_gist, and that the
    // migration brings it is what the tests on a live server measure.
    protected override void OnModelCreating(ModelBuilder modelBuilder) => TModel.Build(modelBuilder);
}

internal static class Contexts
{
    public const string Offline = "Host=localhost;Database=schemakit_tests";

    /// <summary>A context wired the way a consumer wires it. Without a connection string it can build models and scripts, and reach no server.</summary>
    public static Db<TModel> Of<TModel>(string? connection = null)
        where TModel : ITestModel
        => new(new DbContextOptionsBuilder<Db<TModel>>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .UseNpgsql(connection ?? Offline, TModel.Wire)
            .Options);

    public static IRelationalModel Relational(this DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    /// <summary>What <c>migrations add</c> would scaffold to get from one model to the other.</summary>
    public static IReadOnlyList<MigrationOperation> Diff<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Of<TFrom>();
        using var to = Of<TTo>();
        return to.GetService<IMigrationsModelDiffer>().GetDifferences(from.Relational(), to.Relational());
    }

    /// <summary>What the first migration of a model holds.</summary>
    public static IReadOnlyList<MigrationOperation> Initial<TModel>()
        where TModel : ITestModel
    {
        using var context = Of<TModel>();
        return context.GetService<IMigrationsModelDiffer>().GetDifferences(null, context.Relational());
    }

    /// <summary>The statements SchemaKit wrote, among EF Core's operations, in their order.</summary>
    public static List<string> Sql(this IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql)];

    /// <summary>Each operation in its order: SchemaKit's as its SQL, EF Core's as its type.</summary>
    public static List<string> Shape(this IEnumerable<MigrationOperation> operations)
        => [.. operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name)];
}
