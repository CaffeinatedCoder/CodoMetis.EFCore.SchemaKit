using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>An earlier package's format, for the tests: the annotation's value is the column of a unique guard named <c>ux_animal</c>.</summary>
public sealed class OldGuardReader : LegacyDeclarationReader<TestGuard>
{
    public const string Name = "Old:Guard";

    public override string Annotation => Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string value)
        => [new TestGuard.Unique([new GuardKey(value)]) { Name = "ux_animal" }];
}

public sealed class NullReader : LegacyDeclarationReader<TestGuard>
{
    public override string Annotation => OldGuardReader.Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string value) => null!;
}

public sealed class ReaderOfNulls : LegacyDeclarationReader<TestGuard>
{
    public override string Annotation => OldGuardReader.Name;

    protected override IEnumerable<TestGuard> Read(IReadOnlyEntityType entityType, string value) => [null!];
}

/// <summary>A declaration no contributor of the tests' contexts renders.</summary>
public sealed record Orphan : ISchemaDeclaration<Orphan>
{
    public static string OwnerId => "Test.Orphan";

    public string Owner => OwnerId;

    public string? Name => "orphan";

    public string Kind => "orphan";

    public int Epoch => 1;

    public void Write(SchemaJsonWriter writer)
    {
    }

    public static Orphan Read(SchemaDeclarationHeader header, SchemaJsonReader reader) => new();
}

public sealed class OrphanReader : LegacyDeclarationReader<Orphan>
{
    public override string Annotation => OldGuardReader.Name;

    protected override IEnumerable<Orphan> Read(IReadOnlyEntityType entityType, string value) => [new Orphan()];
}

public static class LegacyModels
{
    /// <summary>The guard as the earlier package stored it.</summary>
    public sealed class InTheOldFormat : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Animal>().HasAnnotation(OldGuardReader.Name, "Name");
    }

    /// <summary>The same guard, declared.</summary>
    public sealed class Declared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Animal>().Metadata.Declare([new TestGuard.Unique([new GuardKey("Name")]) { Name = "ux_animal" }]);
    }

    public sealed class Undeclared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Animal>();
    }

    public sealed class WithAValueThatIsNoText : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Animal>().HasAnnotation(OldGuardReader.Name, 3);
    }
}

/// <summary>
/// The seam a package reads an earlier package's annotations through (#92): asked where a
/// migration starts, refused where it leads, and never an object left out.
/// </summary>
[TestClass]
public sealed class LegacyDeclarationReaderTests
{
    private static void WithTheReader(IServiceCollection services) => services.AddLegacyDeclarationReader<OldGuardReader>();

    private static void WithTheNullReader(IServiceCollection services) => services.AddLegacyDeclarationReader<NullReader>();

    private static void WithTheReaderOfNulls(IServiceCollection services) => services.AddLegacyDeclarationReader<ReaderOfNulls>();

    private static void WithTheOrphanReader(IServiceCollection services) => services.AddLegacyDeclarationReader<OrphanReader>();

    private static List<string> Diff<TFrom, TTo>(Action<IServiceCollection>? more)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = SchemaOperationPlannerTests.Context<TFrom>(more: more);
        using var to = SchemaOperationPlannerTests.Context<TTo>(more: more);
        return SchemaOperationPlannerTests.Describe(
            to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to)));
    }

    [TestMethod]
    public void What_the_old_format_holds_and_the_model_declares_is_the_same_object()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Diff<LegacyModels.InTheOldFormat, LegacyModels.Declared>(WithTheReader));

    /// <summary>The control: without the reader the annotation is nobody's, and the object is created as if it were not there.</summary>
    [TestMethod]
    public void Without_its_reader_the_old_format_is_not_seen()
        => Assert.HasCount(1, Diff<LegacyModels.InTheOldFormat, LegacyModels.Declared>(more: null));

    [TestMethod]
    public void What_the_old_format_holds_and_the_model_does_not_declare_is_dropped()
    {
        var statements = Diff<LegacyModels.InTheOldFormat, LegacyModels.Undeclared>(WithTheReader);

        Assert.HasCount(1, statements);
        StringAssert.Contains(statements[0], "DROP");
        StringAssert.Contains(statements[0], "ux_animal");
    }

    [TestMethod]
    public void The_old_format_where_the_migration_leads_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<LegacyModels.Undeclared, LegacyModels.InTheOldFormat>(WithTheReader));

        StringAssert.Contains(refused.Message, "Animal carries the annotation 'Old:Guard' in the model the migration leads to");
    }

    [TestMethod]
    public void A_value_that_is_no_text_is_refused_and_not_skipped()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<LegacyModels.WithAValueThatIsNoText, LegacyModels.Undeclared>(WithTheReader));

        StringAssert.Contains(refused.Message, "with a value of type Int32");
    }

    [TestMethod]
    public void A_reader_that_returns_null_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<LegacyModels.InTheOldFormat, LegacyModels.Undeclared>(WithTheNullReader));
        StringAssert.Contains(refused.Message, "NullReader'.Read returned null");

        refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<LegacyModels.InTheOldFormat, LegacyModels.Undeclared>(WithTheReaderOfNulls));
        StringAssert.Contains(refused.Message, "ReaderOfNulls'.Read returned null");
    }

    [TestMethod]
    public void A_reader_whose_declaration_has_no_contributor_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<LegacyModels.InTheOldFormat, LegacyModels.Undeclared>(WithTheOrphanReader));

        StringAssert.Contains(refused.Message, "Animal carries a declaration of 'Test.Orphan', and no contributor with that owner is registered");
    }

    [TestMethod]
    public void A_class_that_is_no_reader_is_refused_where_it_is_added()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => new ServiceCollection().AddLegacyDeclarationReader<Animal>());

        StringAssert.Contains(refused.Message, "does not derive from LegacyDeclarationReader<TDeclaration>");
    }

    [TestMethod]
    public void A_reader_added_twice_is_asked_once()
    {
        // Asked twice, the object would be in the source twice and dropped twice.
        static void Twice(IServiceCollection services) => services.AddLegacyDeclarationReader<OldGuardReader>().AddLegacyDeclarationReader<OldGuardReader>();

        Assert.HasCount(1, Diff<LegacyModels.InTheOldFormat, LegacyModels.Undeclared>(Twice));
    }
}
