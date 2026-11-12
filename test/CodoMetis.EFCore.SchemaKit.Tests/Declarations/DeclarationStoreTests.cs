using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Tests.Declarations;

/// <summary>
/// The declaration store: one annotation per declaration, a key that says which declarations are
/// one, and the rule the store exists for: a declaration never replaces another without being told to.
/// </summary>
[TestClass]
public sealed class DeclarationStoreTests
{
    private static readonly TestGuard.Unique Email = new([new GuardKey("Email")])
    {
        Name = "ux_people_email",
        NullsDistinct = false,
        Include = ["Name"],
        Where = new GuardFilter("{DeletedAt} IS NULL AND {Rank} <> 0"),
        Concurrently = true,
    };

    private static IMutableEntityType Entity(bool stamped = true)
    {
        var builder = new ModelBuilder();
        if (stamped) builder.Model.SetAnnotation(SchemaKitModelStamp.Annotation, SchemaKitModelStamp.Value);
        return builder.Entity<Thing>().Metadata;
    }

    private static List<IAnnotation> Stored(IReadOnlyEntityType entity)
        => [.. entity.GetAnnotations().Where(a => a.Name.StartsWith("CodoMetis:", StringComparison.Ordinal)).OrderBy(a => a.Name, StringComparer.Ordinal)];

    private static string Hash(string identity) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];

    // ---- the stored form, which is permanent -------------------------------------------------------

    [TestMethod]
    public void A_declaration_is_stored_as_one_annotation_in_the_documented_form()
    {
        var entity = Entity();

        entity.Declare([Email]);

        var annotation = Stored(entity).Single();
        Assert.AreEqual("CodoMetis:Test.Guard:08dd2f43dd31aea1", annotation.Name);
        Assert.AreEqual(
            """{"$kind":"unique","v":2,"r":1,"name":"ux_people_email","keys":[{"column":"Email","descending":false}],"nullsDistinct":false,"include":["Name"],"where":{"template":"{DeletedAt} IS NULL AND {Rank} <> 0"},"concurrently":true}""",
            annotation.Value);
    }

    [TestMethod]
    public void A_named_declarations_key_is_over_its_kind_and_its_name()
    {
        var entity = Entity();

        entity.Declare([Email]);

        Assert.AreEqual($"CodoMetis:Test.Guard:{Hash("name\nunique\nux_people_email")}", Stored(entity).Single().Name);
    }

    [TestMethod]
    public void An_unnamed_declarations_key_is_over_its_kind_and_everything_that_is_not_operational()
    {
        var entity = Entity();

        entity.Declare([Email with { Name = null }]);

        var identity = """{"keys":[{"column":"Email","descending":false}],"nullsDistinct":false,"include":["Name"],"where":{"template":"{DeletedAt} IS NULL AND {Rank} <> 0"}}""";
        var annotation = Stored(entity).Single();
        Assert.AreEqual($"CodoMetis:Test.Guard:{Hash("content\nunique\n" + identity)}", annotation.Name);
        Assert.AreEqual("CodoMetis:Test.Guard:22068f431e3a1cb9", annotation.Name);
        StringAssert.Contains((string)annotation.Value!, "\"concurrently\":true", "An operational option is stored; it is only left out of the key.");
    }

    /// <summary>
    /// The key of a declaration whose text has a character .NET 9 escaped and .NET 10 does not
    /// (U+A7DB), one no runtime knows yet (U+0378) and one outside the BMP. Each is hashed as
    /// itself, so the key is the one below on every runtime.
    /// </summary>
    [TestMethod]
    public void A_key_does_not_depend_on_what_the_runtime_knows_of_a_character()
    {
        var entity = Entity();

        entity.Declare([new TestGuard.Unique([new GuardKey("Name")]) { Where = new GuardFilter("{Name} <> '\uA7DB\u0378\U0001F600'") }]);

        var identity = "{\"keys\":[{\"column\":\"Name\",\"descending\":false}],\"nullsDistinct\":true,\"include\":[],\"where\":{\"template\":\"{Name} <> '\uA7DB\u0378\U0001F600'\"}}";
        var annotation = Stored(entity).Single();
        Assert.AreEqual($"CodoMetis:Test.Guard:{Hash("content\nunique\n" + identity)}", annotation.Name);
        Assert.AreEqual("CodoMetis:Test.Guard:b2d819d709fd6d1d", annotation.Name);
        StringAssert.Contains((string)annotation.Value!, "'\uA7DB\u0378\U0001F600'");
    }

    // ---- the rule: no declaration replaces another ---------------------------------------------------

    /// <summary>
    /// The defect of the draft's identity (plan, section 3.4): a unique index and a plain one over
    /// the same column had one key, and the second took the first's place without an error.
    /// </summary>
    [TestMethod]
    public void Two_unnamed_declarations_that_differ_in_an_option_are_both_kept()
    {
        var entity = Entity();
        var unique = new TestGuard.Unique([new GuardKey("Code")]) { NullsDistinct = false };
        var plain  = new TestGuard.Unique([new GuardKey("Code")]);

        entity.Declare([unique, plain]);

        CollectionAssert.AreEquivalent(new TestGuard[] { unique, plain }, entity.GetDeclarations<TestGuard>().ToList());
    }

    [TestMethod]
    public void The_same_declaration_twice_in_one_call_is_refused_and_nothing_is_stored()
    {
        var entity = Entity();
        var other = new TestGuard.Trigger("Name");

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => entity.Declare([other, Email, Email with { Include = [] }]));

        StringAssert.Contains(error.Message, "declares the same schema object twice");
        StringAssert.Contains(error.Message, "Test.Guard unique 'ux_people_email'");
        Assert.AreEqual(0, Stored(entity).Count, "A refused call must leave nothing behind, the declarations before the repeated one included.");
    }

    [TestMethod]
    public void A_declaration_that_is_already_there_is_refused_and_the_first_stays()
    {
        var entity = Entity();
        entity.Declare([Email]);

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => entity.Declare([new TestGuard.Trigger("Name"), Email with { NullsDistinct = true }]));

        StringAssert.Contains(error.Message, "already declares Test.Guard unique 'ux_people_email'");
        StringAssert.Contains(error.Message, "Redeclare()");
        Assert.AreEqual(Email, entity.GetDeclarations<TestGuard>().Single(), "The first declaration must be the one still there, unchanged.");
    }

    /// <summary>
    /// The same for a declaration without a name, declared in two calls, by a convention and by
    /// the user for instance. The second differs only in an operational option, so it has the
    /// first's key, and storing it would overwrite the first's options without an error.
    /// </summary>
    [TestMethod]
    public void An_unnamed_declaration_that_is_already_there_is_refused_and_the_first_stays()
    {
        var entity = Entity();
        var index = new TestGuard.Unique([new GuardKey("Code")]);
        entity.Declare([index]);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => entity.Declare([index with { Concurrently = true }]));

        StringAssert.Contains(error.Message, "already declares Test.Guard unique");
        StringAssert.Contains((string)Stored(entity).Single().Value!, "\"concurrently\":false", "The first declaration's options must be the ones still stored.");
    }

    [TestMethod]
    public void Two_declarations_that_differ_only_in_an_operational_option_are_one()
    {
        var entity = Entity();
        var index = new TestGuard.Unique([new GuardKey("Code")]);

        Assert.ThrowsExactly<InvalidOperationException>(() => entity.Declare([index, index with { Concurrently = true }]));
    }

    /// <summary>
    /// Which names collide is the database's rule, per kind: a trigger and an index of one name are
    /// two objects. The store keeps both; whether the name is taken twice is the name claims' to say.
    /// </summary>
    [TestMethod]
    public void The_same_name_on_two_kinds_is_two_declarations()
    {
        var entity = Entity();

        entity.Declare([Email, new TestGuard.Trigger("Email") { Name = "ux_people_email" }]);

        Assert.AreEqual(2, Stored(entity).Count);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => entity.FindDeclaration<TestGuard>("ux_people_email"));
        StringAssert.Contains(error.Message, "more than one Test.Guard declaration named 'ux_people_email'");
    }

    [TestMethod]
    public void Names_are_compared_as_the_database_compares_them()
    {
        var ordinal = Entity();
        ordinal.Declare([new TestGuard.Trigger("A") { Name = "TR" }, new TestGuard.Trigger("A") { Name = "tr" }]);
        Assert.AreEqual(2, Stored(ordinal).Count);
        Assert.IsNull(ordinal.FindDeclaration<TestGuard>("Tr"));

        var loose = Entity();
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => loose.Declare([new LooseGuard("A") { Name = "IX" }, new LooseGuard("B") { Name = "ix" }]));
        StringAssert.Contains(error.Message, "declares the same schema object twice");

        loose.Declare([new LooseGuard("A") { Name = "IX" }]);
        Assert.AreEqual(new LooseGuard("A") { Name = "IX" }, loose.FindDeclaration<LooseGuard>("ix"));
    }

    // ---- the wiring ---------------------------------------------------------------------------------

    [TestMethod]
    public void Declaring_on_a_model_without_the_stamp_fails_at_the_declaration()
    {
        var entity = Entity(stamped: false);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => entity.Declare([Email]));

        StringAssert.Contains(error.Message, "UseSchemaKit() is not on this context's options");
        Assert.AreEqual(0, Stored(entity).Count);
    }

    [TestMethod]
    public void Redeclaring_on_a_model_without_the_stamp_fails_too()
    {
        var entity = Entity(stamped: false);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => entity.Redeclare<TestGuard>(d => d));

        StringAssert.Contains(error.Message, "UseSchemaKit() is not on this context's options");
    }

    // ---- the read model -----------------------------------------------------------------------------

    [TestMethod]
    public void The_read_model_returns_the_values_that_were_declared_and_only_its_owners()
    {
        var entity = Entity();
        var trigger = new TestGuard.Trigger("Name") { Name = "tr_people_name", Epoch = 3 };
        entity.Declare([Email, trigger, new LooseGuard("Name")]);

        CollectionAssert.AreEquivalent(new TestGuard[] { Email, trigger }, entity.GetDeclarations<TestGuard>().ToList());
        Assert.AreEqual(new LooseGuard("Name"), entity.GetDeclarations<LooseGuard>().Single());
        Assert.AreEqual(trigger, entity.FindDeclaration<TestGuard>("tr_people_name"));
        Assert.IsNull(entity.FindDeclaration<TestGuard>("no_such_name"));
    }

    [TestMethod]
    public void The_read_model_is_ordered_by_key_whatever_the_order_of_declaration()
    {
        TestGuard[] declarations = [.. Enumerable.Range(0, 8).Select(TestGuard (i) => new TestGuard.Trigger($"C{i}"))];
        var forward = Entity();
        var backward = Entity();

        forward.Declare(declarations);
        backward.Declare(declarations.Reverse());

        var byKey = declarations.OrderBy(DeclarationCodec.Key, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(byKey, forward.GetDeclarations<TestGuard>().ToList());
        CollectionAssert.AreEqual(byKey, backward.GetDeclarations<TestGuard>().ToList());

        // EF Core's own entity types return their annotations sorted by name, so the two above hold
        // without the store ordering anything. The order is the store's promise, not EF Core's: an
        // entity type that returns them otherwise gets the same answer.
        var unsorted = DispatchProxy.Create<IReadOnlyEntityType, ReversedAnnotations>();
        ((ReversedAnnotations)unsorted).Inner = forward;
        var returned = unsorted.GetAnnotations().Select(a => a.Name).Where(n => n.StartsWith("CodoMetis:Test.Guard:", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreNotEqual(returned.Order(StringComparer.Ordinal).ToList(), returned, "The entity type of this test has to return them out of order.");

        CollectionAssert.AreEqual(byKey, unsorted.GetDeclarations<TestGuard>().ToList());
    }

    /// <summary>An entity type that is another's in everything but the order of its annotations.</summary>
    public class ReversedAnnotations : DispatchProxy
    {
        public IReadOnlyEntityType Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(Inner, args);
            return targetMethod.Name == nameof(IReadOnlyAnnotatable.GetAnnotations)
                ? ((IEnumerable<IAnnotation>)result!).Reverse().ToList()
                : result;
        }
    }

    // ---- Redeclare: the one replacing path ------------------------------------------------------------

    [TestMethod]
    public void Redeclare_changes_a_named_declaration_in_place()
    {
        var entity = Entity();
        entity.Declare([Email, new TestGuard.Trigger("Name")]);
        var key = Stored(entity).Single(a => ((string)a.Value!).Contains("ux_people_email")).Name;

        var changed = entity.Redeclare<TestGuard>(d => d is TestGuard.Unique u ? u with { Where = new GuardFilter("{Rank} > 0") } : d);

        Assert.AreEqual(1, changed);
        Assert.AreEqual(Email with { Where = new GuardFilter("{Rank} > 0") }, entity.FindDeclaration<TestGuard>("ux_people_email"));
        Assert.AreEqual(2, Stored(entity).Count);
        Assert.IsNotNull(entity.FindAnnotation(key), "A named declaration keeps its key when its content changes.");
    }

    [TestMethod]
    public void Redeclare_moves_a_renamed_declaration_to_its_new_key()
    {
        var entity = Entity();
        entity.Declare([Email]);
        var before = Stored(entity).Single().Name;

        var changed = entity.Redeclare<TestGuard>(d => d with { Name = "ux_people_mail" });

        Assert.AreEqual(1, changed);
        Assert.AreNotEqual(before, Stored(entity).Single().Name);
        Assert.AreEqual(Email with { Name = "ux_people_mail" }, entity.GetDeclarations<TestGuard>().Single());
    }

    [TestMethod]
    public void Redeclare_that_changes_nothing_reports_nothing()
    {
        var entity = Entity();
        entity.Declare([Email]);

        Assert.AreEqual(0, entity.Redeclare<TestGuard>(d => d));
        Assert.AreEqual(0, entity.Redeclare<TestGuard>(d => d with { }));
        Assert.AreEqual(Email, entity.GetDeclarations<TestGuard>().Single());
    }

    [TestMethod]
    public void Redeclare_that_would_turn_two_declarations_into_one_is_refused_and_changes_nothing()
    {
        var entity = Entity();
        var first  = new TestGuard.Trigger("A") { Name = "tr_a" };
        var second = new TestGuard.Trigger("B") { Name = "tr_b" };
        entity.Declare([first, second]);
        var before = Stored(entity).Select(a => (a.Name, a.Value)).ToList();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => entity.Redeclare<TestGuard>(d => d with { Name = "tr" }));

        StringAssert.Contains(error.Message, "would turn two declarations into one");
        CollectionAssert.AreEqual(before, Stored(entity).Select(a => (a.Name, a.Value)).ToList());
    }

    /// <summary>A swap passes through a state in which both have one key; only the result counts.</summary>
    [TestMethod]
    public void Redeclare_can_swap_two_names()
    {
        var entity = Entity();
        entity.Declare([new TestGuard.Trigger("A") { Name = "one" }, new TestGuard.Trigger("B") { Name = "two" }]);

        var changed = entity.Redeclare<TestGuard>(d => d with { Name = d.Name == "one" ? "two" : "one" });

        Assert.AreEqual(2, changed);
        Assert.AreEqual(new TestGuard.Trigger("A") { Name = "two" }, entity.FindDeclaration<TestGuard>("two"));
        Assert.AreEqual(new TestGuard.Trigger("B") { Name = "one" }, entity.FindDeclaration<TestGuard>("one"));
    }

    [TestMethod]
    public void Redeclare_leaves_another_owners_declarations_alone()
    {
        var entity = Entity();
        entity.Declare([Email, new LooseGuard("Name") { Name = "ux_people_email" }]);

        entity.Redeclare<TestGuard>(d => d with { Name = "renamed" });

        Assert.AreEqual(new LooseGuard("Name") { Name = "ux_people_email" }, entity.GetDeclarations<LooseGuard>().Single());
    }

    [TestMethod]
    public void Redeclare_refuses_a_function_that_returns_null()
    {
        var entity = Entity();
        entity.Declare([Email]);

        Assert.ThrowsExactly<InvalidOperationException>(() => entity.Redeclare<TestGuard>(_ => null!));
        Assert.AreEqual(Email, entity.GetDeclarations<TestGuard>().Single());
    }
}
