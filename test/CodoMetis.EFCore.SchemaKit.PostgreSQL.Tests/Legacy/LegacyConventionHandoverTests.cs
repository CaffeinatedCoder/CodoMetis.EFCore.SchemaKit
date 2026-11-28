using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// A condition a convention adds, coming from 5.x (#249). 5.x had
/// <c>AddComplexIndexFilter</c> and <c>AddExclusionConstraintFilter</c> on the entity type, which
/// took a template, and a convention called them in a loop over entity types it knew only when it
/// ran. Here the condition is a value, <c>PgSchema.Where</c>, with <c>Sql.Raw</c> for a property
/// no lambda can name, and <c>Redeclare</c> with <c>AndWhere</c> adds it. The snapshot is the one
/// the published 5.6.0 scaffolded for a model with every way a declaration can have a condition
/// of its own (<c>test/Fixtures/ComplexIndexes5x/ScaffoldedConvention</c>).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LegacyConventionHandoverTests
{
    private static IReadOnlyList<MigrationOperation> Up<TModel>() where TModel : ITestModel
    {
        using var context = LegacyFixture.Context<TModel>();
        return context.GetService<IMigrationsModelDiffer>().GetDifferences(LegacyFixture.ConventionSnapshot(context), context.Relational());
    }

    private static IReadOnlyList<MigrationOperation> Down<TModel>() where TModel : ITestModel
    {
        using var context = LegacyFixture.Context<TModel>();
        return context.GetService<IMigrationsModelDiffer>().GetDifferences(context.Relational(), LegacyFixture.ConventionSnapshot(context));
    }

    [TestMethod]
    public void The_premise_the_snapshot_holds_each_way_a_condition_was_joined()
    {
        var snapshot = File.ReadAllText(Path.Combine(LegacyFixture.ConventionFolder, "ConventionContextModelSnapshot.cs"));

        // On a property and on the entity type; alone, after SQL, after a lambda's one comparison
        // and after a lambda's two.
        StringAssert.Contains(snapshot, """".HasAnnotation("CustomIndex:Filter", "{DeletedOn} IS NULL")"""");
        StringAssert.Contains(snapshot, """".HasAnnotation("CustomIndex:Filter", "({ClosedOn} IS NULL) AND ({DeletedOn} IS NULL)")"""");
        StringAssert.Contains(snapshot, """"\"filter\":\"(\\u0022ClosedOn\\u0022 IS NULL) AND ({DeletedOn} IS NULL)"""");
        StringAssert.Contains(snapshot, """"\"filter\":\"(({ClosedOn} IS NULL AND {QueueId} \\u003E 0)) AND ({DeletedOn} IS NULL)\",\"name\":\"ux_ticket_owner_title_open"""");
        StringAssert.Contains(snapshot, """"\"filter\":\"(({ClosedOn} IS NULL AND {QueueId} \\u003E 0)) AND ({DeletedOn} IS NULL)\",\"name\":\"ex_ticket_owner_queue_open"""");
        // What the convention passed over: an index that is not unique, a type without the property.
        StringAssert.Contains(snapshot, """"\"unique\":false,\"filter\":\"{ClosedOn} IS NULL\",\"name\":\"ix_ticket_queue_open"""");
        StringAssert.Contains(snapshot, """"\"unique\":true,\"name\":\"ux_memo_text"""");
    }

    [TestMethod]
    public void The_model_with_the_convention_is_no_operations_both_ways()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), Up<MovedConvention.Declared>().Shape());
        CollectionAssert.AreEqual(Array.Empty<string>(), Down<MovedConvention.Declared>().Shape());

        using var context = LegacyFixture.Context<MovedConvention.Declared>();
        Assert.IsFalse(context.GetService<IMigrationsModelDiffer>().HasDifferences(LegacyFixture.ConventionSnapshot(context), context.Relational()));
    }

    /// <summary>What <c>Redeclare</c> returns is how many declarations changed: the six unique indexes of eight, and the three constraints.</summary>
    [TestMethod]
    public void The_convention_changes_the_unique_indexes_and_the_constraints_of_the_type_that_has_the_property()
    {
        using var context = LegacyFixture.Context<MovedConvention.Declared>();
        _ = context.Model;

        Assert.AreEqual((6, 3), MovedConvention.Changed);
    }

    /// <summary>
    /// <c>AndWhere</c> does not add a condition that is the declaration's last conjunct already,
    /// as 5.x's did not: a convention that runs once per module of an application runs twice.
    /// </summary>
    [TestMethod]
    public void The_convention_run_twice_changes_nothing_the_second_time()
    {
        using (var context = LegacyFixture.Context<MovedConvention.DeclaredWithTheConventionTwice>())
        {
            _ = context.Model;
            Assert.AreEqual((0, 0), MovedConvention.Changed);
        }

        CollectionAssert.AreEqual(Array.Empty<string>(), Up<MovedConvention.DeclaredWithTheConventionTwice>().Shape());
        CollectionAssert.AreEqual(Array.Empty<string>(), Down<MovedConvention.DeclaredWithTheConventionTwice>().Shape());
    }

    /// <summary>
    /// The control. Without the convention every object that had its condition is another object:
    /// nine of them are dropped and created, and the four it passed over are left alone.
    /// </summary>
    [TestMethod]
    public void Without_the_convention_the_nine_objects_it_changed_are_rebuilt()
    {
        var sql = Up<MovedConvention.DeclaredWithoutTheConvention>().Sql();

        string[] changed =
        [
            "ux_ticket_label_name", "ux_ticket_label_code_open", "ux_ticket_code", "ux_ticket_queue_code_open", "ux_ticket_owner_title_open",
            "ux_ticket_title_lower_open", "ex_ticket_owner", "ex_ticket_queue_open", "ex_ticket_owner_queue_open",
        ];
        // The extension first, since constraints are created; then nine drops and nine creates.
        Assert.AreEqual("CREATE EXTENSION IF NOT EXISTS btree_gist;", sql[0]);
        Assert.HasCount(19, sql, string.Join("\n", sql));
        foreach (var name in changed)
            Assert.AreEqual(2, sql.Count(statement => statement.Contains(name + " ", StringComparison.Ordinal) || statement.Contains(name + ";", StringComparison.Ordinal)), name);
        Assert.IsFalse(sql.Any(statement => statement.Contains("DeletedOn", StringComparison.Ordinal)), "The model without the convention has no such condition.");
    }
}
