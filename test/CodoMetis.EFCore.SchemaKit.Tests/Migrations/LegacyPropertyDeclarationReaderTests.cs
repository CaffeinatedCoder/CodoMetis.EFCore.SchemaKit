using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>An earlier package's format on a property, for the tests: a marker, read as a unique guard on that property, named after its path.</summary>
public sealed class OldMarkReader : LegacyPropertyDeclarationReader<TestGuard>
{
    public const string Name = "Old:Marked";

    public override string Annotation => Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string path, IReadOnlyProperty property, object? value)
        => value is true ? [new TestGuard.Unique([new GuardKey(path)]) { Name = "ux_" + path.Replace('.', '_').ToLowerInvariant() }] : [];
}

/// <summary>A second class for the same annotation of a property.</summary>
public sealed class AnotherOldMarkReader : LegacyPropertyDeclarationReader<TestGuard>
{
    public override string Annotation => OldMarkReader.Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string path, IReadOnlyProperty property, object? value) => [];
}

/// <summary>A reader of an annotation of that name on an entity type, which is another annotation.</summary>
public sealed class MarkOnTheEntityReader : LegacyDeclarationReader<TestGuard>
{
    public override string Annotation => OldMarkReader.Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string value) => [];
}

public sealed class NullMarkReader : LegacyPropertyDeclarationReader<TestGuard>
{
    public override string Annotation => OldMarkReader.Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string path, IReadOnlyProperty property, object? value) => null!;
}

public static class MarkedModels
{
    /// <summary>A property of a complex type marked, as the earlier package stored it.</summary>
    public sealed class InTheOldFormat : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Burrow>().ComplexProperty(x => x.Spot, spot => spot.Property(x => x.Name).HasAnnotation(OldMarkReader.Name, true));
    }

    /// <summary>The same guard, declared.</summary>
    public sealed class Declared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Burrow>().ComplexProperty(x => x.Spot);
            modelBuilder.Entity<Burrow>().Metadata.Declare([new TestGuard.Unique([new GuardKey("Spot.Name")]) { Name = "ux_spot_name" }]);
        }
    }

    public sealed class Undeclared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Burrow>().ComplexProperty(x => x.Spot);
    }

    /// <summary>A marker that is not set.</summary>
    public sealed class MarkedFalse : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Burrow>().ComplexProperty(x => x.Spot, spot => spot.Property(x => x.Name).HasAnnotation(OldMarkReader.Name, false));
    }

    /// <summary>A property of the entity type itself, which a derived type inherits.</summary>
    public sealed class OnAnInheritedProperty : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Property(x => x.Name).HasAnnotation(OldMarkReader.Name, true);
            modelBuilder.Entity<Dog>();
        }
    }

    public sealed class AnimalsUndeclared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>();
            modelBuilder.Entity<Dog>();
        }
    }
}

/// <summary>
/// The seam for an earlier package's annotation that sits on a property (#218), as 5.x's marker of
/// an index on a property does. It is asked like the reader of an entity type's annotation: of
/// every model that is not the context's own, refused in that one, and once for each property
/// where the property is declared.
/// </summary>
[TestClass]
public sealed class LegacyPropertyDeclarationReaderTests
{
    // Named unlike the methods of LegacyDeclarationReaderTests: the harness tells one set of
    // services from another by the method's name.
    private static void WithTheMarkReader(IServiceCollection services) => services.AddLegacyDeclarationReader<OldMarkReader>();

    private static void WithTwoMarkReaders(IServiceCollection services)
        => services.AddLegacyDeclarationReader<OldMarkReader>().AddLegacyDeclarationReader<AnotherOldMarkReader>();

    private static void WithAReaderOfTheEntityToo(IServiceCollection services)
        => services.AddLegacyDeclarationReader<OldMarkReader>().AddLegacyDeclarationReader<MarkOnTheEntityReader>();

    private static void WithTheNullMarkReader(IServiceCollection services) => services.AddLegacyDeclarationReader<NullMarkReader>();

    private static List<string> Diff<TFrom, TTo>(Action<IServiceCollection>? more)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = SchemaOperationPlannerTests.Context<TFrom>(more: more);
        using var to = SchemaOperationPlannerTests.Context<TTo>(more: more);
        return SchemaOperationPlannerTests.Describe(
            to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to)));
    }

    /// <summary>The reader is handed the property's path from the entity type, through its complex properties.</summary>
    [TestMethod]
    public void What_a_property_is_marked_with_and_the_model_declares_is_the_same_object()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Diff<MarkedModels.InTheOldFormat, MarkedModels.Declared>(WithTheMarkReader));

    /// <summary>The control: without the reader the marker is nobody's, and the object is created as if it were not there.</summary>
    [TestMethod]
    public void Without_its_reader_the_marker_is_not_seen()
        => Assert.HasCount(1, Diff<MarkedModels.InTheOldFormat, MarkedModels.Declared>(more: null));

    [TestMethod]
    public void What_a_property_is_marked_with_and_the_model_does_not_declare_is_dropped()
        => CollectionAssert.AreEqual(new[] { "DROP INDEX ux_spot_name;" }, Diff<MarkedModels.InTheOldFormat, MarkedModels.Undeclared>(WithTheMarkReader));

    [TestMethod]
    public void The_reader_decides_what_a_value_declares()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Diff<MarkedModels.MarkedFalse, MarkedModels.Undeclared>(WithTheMarkReader));

    [TestMethod]
    public void The_marker_in_the_contexts_own_model_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<MarkedModels.Undeclared, MarkedModels.InTheOldFormat>(WithTheMarkReader));

        StringAssert.Contains(refused.Message, "Burrow carries the annotation 'Old:Marked' in the context's own model");
    }

    /// <summary>
    /// A property a derived type inherits is its base type's, and is read there: read for each
    /// type that has it, one object would be two, and the second dropped while it is declared.
    /// </summary>
    [TestMethod]
    public void A_property_is_read_where_it_is_declared()
        => CollectionAssert.AreEqual(new[] { "DROP INDEX ux_name;" }, Diff<MarkedModels.OnAnInheritedProperty, MarkedModels.AnimalsUndeclared>(WithTheMarkReader));

    /// <summary>One annotation of a property has one reader, as one of an entity type has (#108).</summary>
    [TestMethod]
    public void Two_readers_of_one_annotation_of_a_property_are_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<MarkedModels.InTheOldFormat, MarkedModels.Undeclared>(WithTwoMarkReaders));

        StringAssert.Contains(refused.Message, "Two readers read the annotation 'Old:Marked'");
    }

    /// <summary>An annotation of an entity type under the same name is another annotation, with a reader of its own.</summary>
    [TestMethod]
    public void A_reader_of_an_entity_types_annotation_of_the_same_name_is_another_reader()
        => CollectionAssert.AreEqual(new[] { "DROP INDEX ux_spot_name;" }, Diff<MarkedModels.InTheOldFormat, MarkedModels.Undeclared>(WithAReaderOfTheEntityToo));

    [TestMethod]
    public void A_reader_that_returns_null_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<MarkedModels.InTheOldFormat, MarkedModels.Undeclared>(WithTheNullMarkReader));

        StringAssert.Contains(refused.Message, nameof(NullMarkReader));
    }
}
