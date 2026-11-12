using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Legacy5x;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// What 5.x could store and the fixture does not hold. Each annotation is written as 5.x's
/// serializer wrote it: a null left out, a deferral that is false left out.
/// </summary>
[TestClass]
public sealed class ComplexIndexes5xExclusionReaderTests
{
    private const string Annotation = "CustomExclusion:Constraints";

    private static List<string> Diff<TModel>(string json)
        where TModel : ITestModel
        => LegacyFixture.DiffFrom<TModel>(source => source.Entity<Slot>().HasAnnotation(Annotation, json)).Shape();

    [TestMethod]
    public void An_empty_annotation_holds_no_constraint()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Diff<MovedModels.SlotAlone>(""));

    [TestMethod]
    public void An_empty_list_holds_no_constraint()
        => CollectionAssert.AreEqual(Array.Empty<string>(), Diff<MovedModels.SlotAlone>("[]"));

    /// <summary>
    /// 5.x wrote the method as it was given, and wrote INITIALLY DEFERRED for a deferrable
    /// constraint only. Both are read as the database has them.
    /// </summary>
    [TestMethod]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}]}]""")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"method":"gist"}]""")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"method":"GIST"}]""")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"initiallyDeferred":true}]""")]
    public void The_same_constraint_however_5x_stored_it_is_no_operations(string json)
        => CollectionAssert.AreEqual(Array.Empty<string>(), Diff<MovedModels.SlotUnderGist>(json));

    [TestMethod]
    public void A_deferrable_constraint_is_one_checked_at_once()
    {
        const string deferrable = """[{"parts":[{"path":"During","op":"&&"}],"deferrable":true}]""";

        CollectionAssert.AreEqual(Array.Empty<string>(), Diff<MovedModels.SlotUnderGistDeferrable>(deferrable));
        CollectionAssert.AreEqual(
            new[]
            {
                """ALTER TABLE "Slot" DROP CONSTRAINT "EX_Slot_During";""",
                """ALTER TABLE "Slot" ADD CONSTRAINT "EX_Slot_During" EXCLUDE USING gist ("During" WITH &&);""",
            },
            Diff<MovedModels.SlotUnderGist>(deferrable));
    }

    [TestMethod]
    public void A_deferrable_constraint_checked_at_commit_is_not_one_checked_at_once()
        => Assert.AreEqual(2, Diff<MovedModels.SlotUnderGistDeferrable>(
            """[{"parts":[{"path":"During","op":"&&"}],"deferrable":true,"initiallyDeferred":true}]""").Count);

    /// <summary>The constraint is in the database. A reader that passed over it would have the next migration create it again.</summary>
    [TestMethod]
    [DataRow("""[{"parts":[{"path":"Id","op":"="}]}]""", "whose elements are all compared with '='")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"method":"btree"}]""", "uses the access method 'btree'")]
    [DataRow("""[{"parts":[{"path":"Id","op":"="},{"path":"During","op":"&&"}],"method":"spgist"}]""", "uses the access method 'spgist'")]
    public void What_PgExclusion_cannot_say_is_refused_by_what_it_is(string json, string expected)
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<MovedModels.SlotAlone>(json));

        StringAssert.Contains(refused.Message, "exclusion constraint of Slot");
        StringAssert.Contains(refused.Message, expected);
        StringAssert.Contains(refused.Message, "in the 5.x project");
    }

    /// <summary>Nothing 5.x wrote looks like these. Each would otherwise be read as a constraint other than the one stored.</summary>
    [TestMethod]
    [DataRow("nonsense", "cannot be read")]
    [DataRow("{}", "it is not a list")]
    [DataRow("[[]]", "a constraint is not an object")]
    [DataRow("[{}]", "a constraint has no parts")]
    [DataRow("""[{"parts":[]}]""", "a constraint has no parts")]
    [DataRow("""[{"parts":"During"}]""", "a constraint holds 'parts'")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"include":["Id"]}]""", "a constraint holds 'include'")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"name":null}]""", "'name' is not text")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&"}],"deferrable":"yes"}]""", "'deferrable' is neither true nor false")]
    [DataRow("""[{"parts":["During"]}]""", "a part is not an object")]
    [DataRow("""[{"parts":[{"path":"During","op":"&&","opclass":"range_ops"}]}]""", "a part holds 'opclass'")]
    [DataRow("""[{"parts":[{"path":"During","expr":"\"During\"","op":"&&"}]}]""", "exactly one of 'path' and 'expr'")]
    [DataRow("""[{"parts":[{"op":"&&"}]}]""", "exactly one of 'path' and 'expr'")]
    [DataRow("""[{"parts":[{"path":"During"}]}]""", "a part has no 'op'")]
    [DataRow("""[{"parts":[{"path":"During","op":" "}]}]""", "a part has no 'op'")]
    public void An_annotation_5x_did_not_write_is_refused(string json, string expected)
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<MovedModels.SlotAlone>(json));

        StringAssert.Contains(refused.Message, "The model snapshot holds the exclusion constraints of Slot");
        StringAssert.Contains(refused.Message, expected);
    }

    /// <summary>
    /// 5.x gave every table of a TPC hierarchy a copy of the root's constraint. Read as one
    /// constraint, the copies would stay in the database with nothing that knows them (#43).
    /// </summary>
    [TestMethod]
    public void A_constraint_on_the_root_of_a_TPC_hierarchy_is_refused()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => LegacyFixture.DiffFrom<MovedModels.SlotAlone>(source =>
        {
            source.Entity<Vehicle>().UseTpcMappingStrategy().HasAnnotation(Annotation, """[{"parts":[{"path":"During","op":"&&"}]}]""");
            source.Entity<Car>();
            source.Entity<Truck>();
        }));

        StringAssert.Contains(refused.Message, "on Vehicle, the root of a TPC hierarchy");
    }
}
