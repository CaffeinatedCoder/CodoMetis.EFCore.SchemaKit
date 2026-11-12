using System.Linq.Expressions;

namespace CodoMetis.EFCore.SchemaKit.Tests.Declarations;

/// <summary>
/// The two values every provider's declarations are made of. They are where a lambda becomes what
/// is stored, so what they make of one is what a snapshot holds.
/// </summary>
[TestClass]
public sealed class DeclarationValueTests
{
    private sealed class Address
    {
        public string City { get; set; } = "";
    }

    private interface IWithdrawable
    {
        DateOnly? RevokedOn { get; }
    }

    private sealed class Person : IWithdrawable
    {
        public string Name { get; set; } = "";
        public string? Tag { get; set; }
        public int? Rank { get; set; }
        public Address Address { get; set; } = new();
        public DateOnly? RevokedOn { get; set; }
    }

    private static KeyTarget Key<T>(Expression<Func<Person, T>> key) => KeyTarget.Of(key);

    [TestMethod]
    public void A_lambda_that_names_a_property_is_a_column()
    {
        Assert.AreEqual(new KeyTarget.Column("Name"), Key(x => x.Name));
        Assert.AreEqual(new KeyTarget.Column("Address.City"), Key(x => x.Address.City));
        // As in EF Core's queries, the value of a nullable is the column itself.
        Assert.AreEqual(new KeyTarget.Column("Rank"), Key(x => x.Rank!.Value));
    }

    /// <summary>
    /// Anything the translator wrote around or beside a placeholder is SQL: it is rendered in
    /// parentheses and names the object by its letters and digits, so taking it for a column, or a
    /// column for it, would render and name another object.
    /// </summary>
    [TestMethod]
    public void Anything_more_than_a_property_is_a_template()
    {
        Assert.AreEqual(new KeyTarget.Template("lower({Name})"), Key(x => x.Name.ToLower()));
        Assert.AreEqual(new KeyTarget.Template("({Name} || {Address.City})"), Key(x => x.Name + x.Address.City));
        Assert.AreEqual(new KeyTarget.Template("coalesce({Tag}, {Name})"), Key(x => x.Tag ?? x.Name));
        Assert.AreEqual(new KeyTarget.Template("'{Name}'"), Key(x => "{Name}"));
    }

    [TestMethod]
    public void A_lambda_the_translator_refuses_is_refused_where_it_is_written()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Key(x => x.Name.Normalize()));
        Assert.ThrowsExactly<NotSupportedException>(() => Predicate.Of<Person>(x => x.Name.Normalize() == "a"));
    }

    [TestMethod]
    public void A_target_or_a_predicate_without_text_cannot_be_made()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new KeyTarget.Column(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new KeyTarget.Template(""));
        Assert.ThrowsExactly<ArgumentException>(() => new KeyTarget.Column("Name") with { Path = "" });
        Assert.ThrowsExactly<ArgumentException>(() => new Predicate(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new Predicate("{Name} IS NULL") with { Template = "" });
    }

    /// <summary>
    /// The predicate goes through the translator's null compensation: <c>!=</c> on a nullable
    /// member keeps the rows whose member is null, as C# does. Left out, a constraint "among rows
    /// not tagged blocked" stops covering the untagged ones.
    /// </summary>
    [TestMethod]
    public void A_predicate_is_translated_as_EF_Core_writes_it()
    {
        Assert.AreEqual("{RevokedOn} IS NULL", Predicate.Of<Person>(x => x.RevokedOn == null).Template);
        Assert.AreEqual("({Tag} <> 'blocked' OR {Tag} IS NULL)", Predicate.Of<Person>(x => x.Tag != "blocked").Template);
    }

    /// <summary>A predicate over an interface, for a convention: its paths are the interface's member names.</summary>
    [TestMethod]
    public void A_predicate_may_be_written_over_an_interface()
        => Assert.AreEqual(new Predicate("{RevokedOn} IS NULL"), Predicate.Of<IWithdrawable>(x => x.RevokedOn == null));

    /// <summary>5.x's rule, so that a convention may run twice: what is already the whole condition, or its last conjunct, is not added again.</summary>
    [TestMethod]
    public void And_adds_a_condition_once()
    {
        var active = new Predicate("{RevokedOn} IS NULL");
        var ranked = new Predicate("{Rank} > 0");

        Assert.AreEqual(active, active.And(active));
        Assert.AreEqual(new Predicate("({RevokedOn} IS NULL) AND ({Rank} > 0)"), active.And(ranked));
        Assert.AreEqual(active.And(ranked), active.And(ranked).And(ranked));
        // Only the last conjunct is recognised; the first is added again, as in 5.x.
        Assert.AreEqual(new Predicate("(({RevokedOn} IS NULL) AND ({Rank} > 0)) AND ({RevokedOn} IS NULL)"), active.And(ranked).And(active));
    }
}
