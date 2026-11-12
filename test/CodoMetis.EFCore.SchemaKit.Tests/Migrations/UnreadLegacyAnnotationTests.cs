using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

public sealed class Spot
{
    public string Name { get; set; } = "";
}

public sealed class Burrow
{
    public int Id { get; set; }
    public Spot Spot { get; set; } = new();
}

/// <summary>A reader for 5.x's entity-level indexes, as a later version will bring one.</summary>
public sealed class IndexesReader : LegacyDeclarationReader<TestGuard>
{
    public override string Annotation => "CustomIndex:CompositeIndexes";

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string value)
        => [new TestGuard.Unique([new GuardKey("Name")]) { Name = "ux_animal" }];
}

/// <summary>
/// A snapshot that holds what <c>EFCore.ComplexIndexes</c> 5.x declared and nothing here reads
/// (#93). The target declares nothing in every test: the snapshot alone says the objects exist.
/// Where 5.x puts each annotation is held against a real snapshot by the PostgreSQL package's
/// <c>UnreadLegacyFixtureTests</c>.
/// </summary>
[TestClass]
public sealed class UnreadLegacyAnnotationTests
{
    private static void WithTheIndexesReader(IServiceCollection services) => services.AddLegacyDeclarationReader<IndexesReader>();

    private static List<string> DiffFrom(Action<ModelBuilder> source, Provider provider = Provider.Npgsql, Action<IServiceCollection>? more = null)
    {
        using var context = SchemaOperationPlannerTests.Context<LegacyModels.Undeclared>(provider, more);
        var builder = new ModelBuilder(context.GetService<IConventionSetBuilder>().CreateConventionSet());
        source(builder);
        var from = context.GetService<IModelRuntimeInitializer>().Initialize(builder.FinalizeModel(), designTime: true, validationLogger: null);

        return SchemaOperationPlannerTests.Describe(
            context.GetService<IMigrationsModelDiffer>().GetDifferences(from.GetRelationalModel(), SchemaOperationPlannerTests.Model(context)));
    }

    public static IEnumerable<object[]> OnEntityTypes =>
        from annotation in new[]
        {
            ("CustomIndex:CompositeIndexes", "indexes of Animal", "issues/27"),
            ("CustomTemporal:Constraints", "temporal constraints of Animal", "issues/18"),
            ("CustomTemporal:ForeignKeys", "temporal foreign keys of Animal", "issues/18"),
            ("CustomExclusion:Constraints", "exclusion constraints of Animal", "call UseSchemaKit() on the options builder of UseNpgsql"),
        }
        from provider in Enum.GetValues<Provider>()
        select new object[] { annotation.Item1, annotation.Item2, annotation.Item3, provider };

    [TestMethod, DynamicData(nameof(OnEntityTypes))]
    public void What_5x_declared_on_an_entity_type_is_refused_when_nothing_reads_it(string annotation, string what, string advice, Provider provider)
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(
            () => DiffFrom(source => source.Entity<Animal>().HasAnnotation(annotation, """[{"paths":["Name"]}]"""), provider));

        StringAssert.Contains(refused.Message, $"The model snapshot holds {what} as EFCore.ComplexIndexes 5.x stored them ('{annotation}')");
        StringAssert.Contains(refused.Message, advice);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void An_index_5x_declared_on_a_property_of_a_complex_type_is_refused(Provider provider)
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => DiffFrom(
            source => source.Entity<Burrow>().ComplexProperty(x => x.Spot, spot => spot.Property(x => x.Name).HasAnnotation("CustomIndex:IsIndexed", true)),
            provider));

        StringAssert.Contains(refused.Message, "The model snapshot holds an index on Spot.Name of Burrow");
        StringAssert.Contains(refused.Message, "'CustomIndex:IsIndexed'");
        StringAssert.Contains(refused.Message, "issues/27");
    }

    /// <summary>5.x read these as no declaration, and left the annotation behind when the last one went.</summary>
    [TestMethod]
    [DataRow("CustomIndex:CompositeIndexes", "")]
    [DataRow("CustomIndex:CompositeIndexes", "[]")]
    [DataRow("CustomTemporal:Constraints", " [] ")]
    public void An_annotation_that_holds_nothing_is_no_declaration(string annotation, string value)
        => CollectionAssert.AreEqual(Array.Empty<string>(), DiffFrom(source => source.Entity<Animal>().HasAnnotation(annotation, value)));

    [TestMethod]
    public void A_property_marked_as_not_indexed_is_no_declaration()
    {
        var operations = DiffFrom(source
            => source.Entity<Burrow>().ComplexProperty(x => x.Spot, spot => spot.Property(x => x.Name).HasAnnotation("CustomIndex:IsIndexed", false)));

        // The tables differ, which is EF Core's to say; nothing was refused.
        CollectionAssert.Contains(operations, "DropTableOperation");
    }

    /// <summary>What a later version does by bringing a reader: the guard needs no edit.</summary>
    [TestMethod]
    public void An_annotation_with_a_reader_is_read_and_not_refused()
    {
        var statements = DiffFrom(source => source.Entity<Animal>().HasAnnotation("CustomIndex:CompositeIndexes", """[{"paths":["Name"]}]"""), more: WithTheIndexesReader);

        Assert.HasCount(1, statements);
        StringAssert.Contains(statements[0], "ux_animal");
    }

    [TestMethod]
    public void What_declares_nothing_is_not_refused()
        => CollectionAssert.AreEqual(Array.Empty<string>(), DiffFrom(source =>
        {
            source.HasAnnotation("CustomIndex:RenderingVersion", 3).HasAnnotation("CustomTemporal:SuppressAutoExtension", true);
            source.Entity<Animal>();
        }));
}
