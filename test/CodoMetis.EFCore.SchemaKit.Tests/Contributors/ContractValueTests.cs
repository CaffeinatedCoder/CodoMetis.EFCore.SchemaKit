using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Contributors;

/// <summary>The values of the contract: what compares how, and which cases each union has.</summary>
[TestClass]
public sealed class ContractValueTests
{
    [TestMethod]
    public void A_value_list_compares_by_its_elements_in_order()
    {
        ValueList<string> list = ["a", "b"];

        Assert.AreEqual(list, ["a", "b"]);
        Assert.AreEqual(list.GetHashCode(), ((ValueList<string>)["a", "b"]).GetHashCode());
        Assert.AreNotEqual(list, ["b", "a"]);
        Assert.AreNotEqual(list, ["a"]);
        Assert.AreEqual(ValueList<string>.Empty, []);
        Assert.AreEqual("[a, b]", list.ToString());
    }

    [TestMethod]
    public void An_operational_value_is_carried_and_never_compared()
    {
        var on = new Operational<bool>(true);
        var off = new Operational<bool>(false);

        Assert.AreEqual(on, off);
        Assert.AreEqual(on.GetHashCode(), off.GetHashCode());
        Assert.IsTrue(on.Value);
        Assert.IsFalse(off.Value);
    }

    /// <summary>
    /// A public closed union is closed for the major: a case added to one breaks every exhaustive
    /// switch of a contributor that builds with warnings as errors. Adding one here is a decision,
    /// and this is where it is noticed.
    /// </summary>
    [TestMethod]
    [DataRow(typeof(SchemaChange), "InPlace,None,Rebuild")]
    [DataRow(typeof(Impact), "Follows,Lost,RebuildAround,Unaffected")]
    [DataRow(typeof(BaseChange), "ColumnAltered,ColumnDropped,ColumnRenamed,TableDropped,TableMoved,TableRebuilt,TableRenamed")]
    [DataRow(typeof(Dependency), "Column,Extension,SchemaObject,WholeTable")]
    [DataRow(typeof(NameClaim), "Constraint,Relation,TableIndex,Trigger")]
    [DataRow(typeof(SchemaScope), "Database,Schema,Table")]
    public void A_closed_union_has_the_cases_it_was_decided_with(Type union, string cases)
    {
        var found = union.GetNestedTypes(BindingFlags.Public).Where(union.IsAssignableFrom).Select(t => t.Name).Order(StringComparer.Ordinal);

        Assert.AreEqual(cases, string.Join(",", found));
    }

    [TestMethod]
    public void Before_tables_comes_before_every_placement_after_them()
    {
        Assert.IsTrue(SchemaPlacement.BeforeTables.Rank < SchemaPlacement.AfterTables(int.MinValue).Rank);
        Assert.IsTrue(SchemaPlacement.AfterTables(10).Rank < SchemaPlacement.AfterTables(30).Rank);
        Assert.AreEqual(SchemaPlacement.AfterTables(10), SchemaPlacement.AfterTables(10));
        Assert.AreEqual(SchemaPlacement.BeforeTables, SchemaPlacement.BeforeTables);
    }
}
