using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// The snapshot the published 5.6.0 scaffolded for an index on a property, an index on an entity
/// type and a temporal constraint (#93): where 5.x put each declaration. The kernel's guard looks
/// in those places; were one of them wrong, a real snapshot would pass it. The indexes are read
/// since #218; the temporal constraint is what nothing here reads, and a diff from the snapshot
/// is refused for it.
/// </summary>
[TestClass]
public sealed class UnreadLegacyFixtureTests
{
    [TestMethod]
    public void An_index_on_a_property_of_a_complex_type_is_marked_on_that_property()
    {
        var city = LegacyFixture.OtherKinds.FindEntityType("Legacy5x.Customer")!.FindComplexProperty("Address")!.ComplexType.FindProperty("City")!;

        Assert.AreEqual(true, city.FindAnnotation("CustomIndex:IsIndexed")?.Value);
    }

    [TestMethod]
    [DataRow("Legacy5x.Supplier", "CustomIndex:CompositeIndexes", """[{"paths":["Address.Zip"],"unique":true}]""")]
    [DataRow("Legacy5x.Tariff", "CustomTemporal:Constraints", """[{"keys":["ProductId"],"period":"Valid"}]""")]
    public void A_declaration_on_an_entity_type_is_an_annotation_of_it(string entityType, string annotation, string value)
        => Assert.AreEqual(value, LegacyFixture.OtherKinds.FindEntityType(entityType)!.FindAnnotation(annotation)?.Value);

    [TestMethod]
    public void A_diff_from_the_snapshot_is_refused_for_what_nothing_reads()
    {
        using var context = LegacyFixture.Context<MovedModels.SlotAlone>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(LegacyFixture.Prepare(context, LegacyFixture.OtherKinds), context.Relational()));

        StringAssert.Contains(refused.Message, "The model snapshot holds temporal constraints of Tariff as EFCore.ComplexIndexes 5.x stored them");
    }

    /// <summary><c>has-pending-model-changes</c> runs the same plan, so it does not answer "no" over them.</summary>
    [TestMethod]
    public void HasDifferences_refuses_it_too()
    {
        using var context = LegacyFixture.Context<MovedModels.SlotAlone>();

        Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>()
            .HasDifferences(LegacyFixture.Prepare(context, LegacyFixture.OtherKinds), context.Relational()));
    }
}
