using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>A rewriter that takes every annotation 5.x stamped on an operation, as #28 will bring them.</summary>
internal sealed class Replays5x : IMigrationOperationRewriter
{
    public IReadOnlyList<MigrationOperation> Rewrite(IReadOnlyList<MigrationOperation> operations, IModel? model)
    {
        foreach (var operation in operations)
        foreach (var annotation in LegacyOperationAnnotationTests.Stamped)
            operation.RemoveAnnotation(annotation);

        return operations;
    }
}

/// <summary>A rewriter that takes one of the two annotations of a temporal foreign key and leaves the other.</summary>
internal sealed class ReplaysTheDependentPeriodOnly : IMigrationOperationRewriter
{
    public IReadOnlyList<MigrationOperation> Rewrite(IReadOnlyList<MigrationOperation> operations, IModel? model)
    {
        foreach (var operation in operations)
            operation.RemoveAnnotation("CustomTemporal:ForeignKeyDependentPeriod");

        return operations;
    }
}

/// <summary>
/// An operation of a migration <c>EFCore.ComplexIndexes</c> 5.x scaffolded that carries an
/// annotation only 5.x's SQL generators rendered (#110). The provider's generator writes the
/// operation as if the annotation were not there: a plain unique constraint under the temporal
/// one's name. Until a rewriter replays it (#28) the generator refuses it, on every provider.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LegacyOperationAnnotationTests
{
    /// <summary>The list, written down a second time: what 5.x's source says, not what the guard holds.</summary>
    internal static readonly string[] Stamped =
    [
        "CustomTemporal:WithoutOverlaps",
        "CustomTemporal:ForeignKeyDependentPeriod",
        "CustomTemporal:ForeignKeyPrincipalPeriod",
        "CustomIndex:IndexParts",
        "CustomIndex:AfterTableRebuild",
    ];

    private sealed class Plain(DbContextOptions<Plain> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private static Plain Context(Provider provider, Action<IServiceCollection>? rewriters = null)
    {
        var builder = new DbContextOptionsBuilder<Plain>().UseProvider(provider);
        if (rewriters is not null) ServicesExtension.Add(builder, rewriters.Method.Name, rewriters);
        return new Plain(builder.UseSchemaKit().Options);
    }

    private static void WithTheRewriter(IServiceCollection services) => services.AddScoped<IMigrationOperationRewriter, Replays5x>();

    private static void WithHalfARewriter(IServiceCollection services) => services.AddScoped<IMigrationOperationRewriter, ReplaysTheDependentPeriodOnly>();

    /// <summary>The operation of the issue: what 5.0.1 scaffolded for <c>HasTemporalConstraint</c>.</summary>
    private static AddUniqueConstraintOperation Unique(string? schema = null)
        => new() { Schema = schema, Table = "Tariff", Name = "AK_Tariff_ProductId_Valid", Columns = ["ProductId", "Valid"] };

    private static AddForeignKeyOperation ForeignKey() => new()
    {
        Table = "Price",
        Name = "FK_Price_Tariff_ProductId_During",
        Columns = ["ProductId", "During"],
        PrincipalTable = "Tariff",
        PrincipalColumns = ["ProductId", "Valid"],
    };

    /// <summary>An expression index as 5.x scaffolded it: the parts' text as columns, and the sentinel.</summary>
    private static CreateIndexOperation ExpressionIndex() => new()
    {
        Table = "Customer",
        Name = "IX_Customer_Name_Lower",
        Columns = ["lower(\"Name\")", "__requires_UseNpgsqlComplexIndexes__"],
    };

    /// <summary>An index on a table SQLite rebuilds, as 5.5.0 scaffolded it: the marker as its filter.</summary>
    private static CreateIndexOperation IndexAfterRebuild() => new()
    {
        Table = "Customer",
        Name = "IX_Customer_Address_City",
        Columns = ["Address_City"],
        Filter = "__requires_UseComplexIndexes__()",
    };

    /// <summary>Each case: the operation without what 5.x stamped on it, and the stamps.</summary>
    private static (MigrationOperation Operation, (string Name, object? Value)[] Stamps) Case(string name) => name switch
    {
        "unique"           => (Unique(), [("CustomTemporal:WithoutOverlaps", "Valid")]),
        "foreign key"      => (ForeignKey(), [("CustomTemporal:ForeignKeyDependentPeriod", "During"), ("CustomTemporal:ForeignKeyPrincipalPeriod", "Valid")]),
        "dependent alone"  => (ForeignKey(), [("CustomTemporal:ForeignKeyDependentPeriod", "During")]),
        "principal alone"  => (ForeignKey(), [("CustomTemporal:ForeignKeyPrincipalPeriod", "Valid")]),
        "expression index" => (ExpressionIndex(), [("CustomIndex:IndexParts", """[{"Value":"lower(\"Name\")","IsExpression":true,"Descending":false,"NullSort":0}]""")]),
        "after rebuild"    => (IndexAfterRebuild(), [("CustomIndex:AfterTableRebuild", true)]),
        _                  => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static MigrationOperation Stamped5x(string name)
    {
        var (operation, stamps) = Case(name);
        foreach (var (annotation, value) in stamps) operation.AddAnnotation(annotation, value);
        return operation;
    }

    /// <summary>What a generator answers: its SQL, or the exception it throws. SQLite refuses a constraint added without a model.</summary>
    private static string Answer(IMigrationsSqlGenerator generator, params MigrationOperation[] operations)
    {
        try
        {
            return string.Join("\n", generator.Generate(operations).Select(c => c.CommandText));
        }
        catch (Exception refused)
        {
            return $"{refused.GetType().Name}: {refused.Message}";
        }
    }

    public static IEnumerable<object[]> Refused =>
        from row in new[]
        {
            ("unique", "AddUniqueConstraintOperation 'AK_Tariff_ProductId_Valid' on table 'Tariff'", "CustomTemporal:WithoutOverlaps", "a unique constraint WITHOUT OVERLAPS"),
            ("foreign key", "AddForeignKeyOperation 'FK_Price_Tariff_ProductId_During' on table 'Price'", "CustomTemporal:ForeignKeyDependentPeriod", "a temporal foreign key (PERIOD)"),
            ("dependent alone", "AddForeignKeyOperation 'FK_Price_Tariff_ProductId_During' on table 'Price'", "CustomTemporal:ForeignKeyDependentPeriod", "a temporal foreign key (PERIOD)"),
            ("principal alone", "AddForeignKeyOperation 'FK_Price_Tariff_ProductId_During' on table 'Price'", "CustomTemporal:ForeignKeyPrincipalPeriod", "a temporal foreign key (PERIOD)"),
            ("expression index", "CreateIndexOperation 'IX_Customer_Name_Lower' on table 'Customer'", "CustomIndex:IndexParts", "an index over the expressions and the null ordering"),
            ("after rebuild", "CreateIndexOperation 'IX_Customer_Address_City' on table 'Customer'", "CustomIndex:AfterTableRebuild", "the index after SQLite's table rebuild"),
        }
        from provider in Enum.GetValues<Provider>()
        select new object[] { row.Item1, row.Item2, row.Item3, row.Item4, provider };

    public static IEnumerable<object[]> Cases =>
        from name in new[] { "unique", "foreign key", "dependent alone", "principal alone", "expression index", "after rebuild" }
        from provider in Enum.GetValues<Provider>()
        select new object[] { name, provider };

    [TestMethod, DynamicData(nameof(Refused))]
    public void An_operation_only_5x_rendered_is_refused(string name, string operation, string annotation, string rendered, Provider provider)
    {
        using var context = Context(provider);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => generator.Generate(
        [
            new SqlOperation { Sql = "SELECT 1" },
            Stamped5x(name),
        ]));

        StringAssert.Contains(refused.Message, $"The migration holds {operation} with the annotation '{annotation}' of EFCore.ComplexIndexes 5.x.");
        StringAssert.Contains(refused.Message, rendered);
        StringAssert.Contains(refused.Message, "This version of SchemaKit cannot replay the operation");
        StringAssert.Contains(refused.Message, "https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/28");
    }

    /// <summary>
    /// The premise, and why the refusal is needed: the provider's own generator takes the stamped
    /// operation without a word and writes what it would write for the bare one.
    /// </summary>
    [TestMethod, DynamicData(nameof(Cases))]
    public void The_providers_generator_writes_it_as_if_nothing_were_stamped_on_it(string name, Provider provider)
    {
        using var context = Context(provider);
        var inner = ((SchemaKitSqlGenerator)context.GetService<IMigrationsSqlGenerator>()).Inner;

        Assert.AreEqual(Answer(inner, Case(name).Operation), Answer(inner, Stamped5x(name)));
    }

    /// <summary>The issue's statement, verbatim: a plain unique constraint under the temporal one's name.</summary>
    [TestMethod]
    public void On_postgres_that_is_a_plain_unique_constraint_under_the_temporal_ones_name()
    {
        using var context = Context(Provider.Npgsql);
        var inner = ((SchemaKitSqlGenerator)context.GetService<IMigrationsSqlGenerator>()).Inner;

        Assert.AreEqual(
            """ALTER TABLE "Tariff" ADD CONSTRAINT "AK_Tariff_ProductId_Valid" UNIQUE ("ProductId", "Valid");""",
            Answer(inner, Stamped5x("unique")).Trim());
    }

    [TestMethod, DynamicData(nameof(Cases))]
    public void The_same_operation_without_the_annotation_is_generated_as_before(string name, Provider provider)
    {
        using var context = Context(provider);
        var generator = (SchemaKitSqlGenerator)context.GetService<IMigrationsSqlGenerator>();

        var expected = Answer(generator.Inner, Case(name).Operation);

        Assert.AreEqual(expected, Answer(generator, Case(name).Operation));
        Assert.IsFalse(expected.Contains("EFCore.ComplexIndexes"), expected);
    }

    /// <summary>
    /// A rewriter leaves the guard by taking the annotation off, as a reader leaves the snapshot's
    /// guard by being registered. The guard runs after the rewriters: before them, this is refused.
    /// </summary>
    [TestMethod, DynamicData(nameof(Cases))]
    public void An_operation_whose_annotation_a_rewriter_took_is_generated(string name, Provider provider)
    {
        using var context = Context(provider, WithTheRewriter);
        var generator = (SchemaKitSqlGenerator)context.GetService<IMigrationsSqlGenerator>();

        Assert.AreEqual(Answer(generator.Inner, Case(name).Operation), Answer(generator, Stamped5x(name)));
    }

    /// <summary>5.x rendered a temporal foreign key from two annotations; a rewriter that takes one has not replayed it.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void An_annotation_a_rewriter_left_is_still_refused(Provider provider)
    {
        using var context = Context(provider, WithHalfARewriter);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => generator.Generate([Stamped5x("foreign key")]));

        StringAssert.Contains(refused.Message, "with the annotation 'CustomTemporal:ForeignKeyPrincipalPeriod'");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_table_is_named_with_its_schema(Provider provider)
    {
        using var context = Context(provider);
        var operation = Unique(schema: "billing");
        operation.AddAnnotation("CustomTemporal:WithoutOverlaps", "Valid");

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsSqlGenerator>().Generate([operation]));

        StringAssert.Contains(refused.Message, "AddUniqueConstraintOperation 'AK_Tariff_ProductId_Valid' on table 'billing.Tariff'");
    }

    /// <summary>
    /// An annotation of 5.x in a shape 5.x never wrote, a value it did not read or an operation it
    /// did not stamp, is not passed through either: what it was meant to say cannot be told.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void An_annotation_in_a_shape_5x_never_wrote_is_refused_too(Provider provider)
    {
        using var context = Context(provider);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var unread = IndexAfterRebuild();
        unread.AddAnnotation("CustomIndex:AfterTableRebuild", false);
        var elsewhere = new DropUniqueConstraintOperation { Table = "Tariff", Name = "AK_Tariff_ProductId_Valid" };
        elsewhere.AddAnnotation("CustomIndex:IndexParts", null);

        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => generator.Generate([unread])).Message,
            "CreateIndexOperation 'IX_Customer_Address_City' on table 'Customer' with the annotation 'CustomIndex:AfterTableRebuild'");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => generator.Generate([elsewhere])).Message,
            "DropUniqueConstraintOperation on table 'Tariff' with the annotation 'CustomIndex:IndexParts'");
    }

    /// <summary>
    /// What the published 5.6.0 scaffolds carries none of them: its temporal constraint is SQL
    /// written at design time, and the indexes the fixture holds are plain. Read from the files a
    /// consumer has (<c>test/Fixtures/ComplexIndexes5x</c>), which the PostgreSQL package's tests
    /// compile and run through the real generator.
    /// </summary>
    [TestMethod, DataRow("Scaffolded"), DataRow("ScaffoldedOtherKinds")]
    public void A_migration_5_6_0_scaffolded_carries_none_of_them(string folder)
    {
        var migration = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "test", "Fixtures", "ComplexIndexes5x", folder, "Initial.cs"));

        foreach (var annotation in Stamped)
            Assert.IsFalse(migration.Contains(annotation, StringComparison.Ordinal), $"{folder}/Initial.cs holds '{annotation}'.");

        StringAssert.Contains(migration, "migrationBuilder.Sql(");
    }

    /// <summary>The operations of that migration's temporal constraint and its indexes, on every provider.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void What_5_6_0_scaffolds_for_the_same_objects_is_generated(Provider provider)
    {
        using var context = Context(provider);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // ScaffoldedOtherKinds/Initial.cs, lines 60 to 71.
        var commands = generator.Generate(
        [
            new CreateIndexOperation { Name = "IX_Customer_Address_City", Table = "Customer", Columns = ["Address_City"] },
            new CreateIndexOperation { Name = "IX_Supplier_Address_Zip", Table = "Supplier", Columns = ["Address_Zip"], IsUnique = true },
            new SqlOperation { Sql = "ALTER TABLE \"Tariff\" ADD CONSTRAINT \"AK_Tariff_ProductId_Valid\" UNIQUE (\"ProductId\", \"Valid\" WITHOUT OVERLAPS);" },
        ]);

        Assert.HasCount(3, commands);
        StringAssert.Contains(commands[2].CommandText, "WITHOUT OVERLAPS");
    }
}
