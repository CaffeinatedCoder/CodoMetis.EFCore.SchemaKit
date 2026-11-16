using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;

// A context with migrations, so that EF Core's own Migrator writes the scripts: the history table,
// the batches, the transactions and, for an idempotent script, the IF NOT EXISTS around every
// command. Each migration holds what the differ scaffolds for its step and the compiled snapshot
// of the model it leads to, which is what a migration scaffolded by `dotnet ef` holds.

/// <summary>The context the three migrations below belong to. Its model is the last one's.</summary>
public sealed class Scripted(DbContextOptions<Scripted> options) : DbContext(options)
{
    public const string Orders = "20261116000001_Orders";

    public const string OpenOrders = "20261116000002_OpenOrders";

    public const string SentAt = "20261116000003_SentAt";

    /// <summary>Breaks a rule of the contributor in every migration, for a test that shows what the rule is for.</summary>
    internal static Action<ProbeIndexContributor>? Arrange { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => Sales.FilterColumnRenamed.Build(modelBuilder);

    internal static Scripted Of(string? connection = null)
        => new(new DbContextOptionsBuilder<Scripted>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .UseSqlServer(connection ?? Contexts.OfflineSqlServer, o => o.UseSchemaKitProbe())
            .Options);
}

/// <summary>A model without a table: where the first migration starts.</summary>
public sealed class Nothing : ITestModel
{
    public static void Build(ModelBuilder modelBuilder)
    {
    }
}

/// <summary>One migration: what the differ scaffolds to get from one model to the other.</summary>
public abstract class Step<TFrom, TTo> : Migration
    where TFrom : ITestModel
    where TTo : ITestModel
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var from = Contexts.SqlServer<TFrom>();
        using var to = Contexts.SqlServer<TTo>();
        Scripted.Arrange?.Invoke(to.Contributor<ProbeIndexContributor>());
        migrationBuilder.Operations.AddRange(from.DiffTo(to));
    }

    /// <summary>The snapshot as it was scaffolded for this step's model, compiled.</summary>
    public override IModel TargetModel
    {
        get
        {
            using var to = Contexts.SqlServer<TTo>();
            return field ??= Snapshots.Of(to).Model;
        }
    }
}

[DbContext(typeof(Scripted))]
[Migration(Scripted.Orders)]
public sealed class OrdersMigration : Step<Nothing, Sales.Bare>;

/// <summary>Adds the filtered index.</summary>
[DbContext(typeof(Scripted))]
[Migration(Scripted.OpenOrders)]
public sealed class OpenOrdersMigration : Step<Sales.Bare, Sales.Indexed>;

/// <summary>Renames the column the filter reads: the index is dropped before and created after.</summary>
[DbContext(typeof(Scripted))]
[Migration(Scripted.SentAt)]
public sealed class SentAtMigration : Step<Sales.Indexed, Sales.FilterColumnRenamed>;
