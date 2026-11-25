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

    // What the factories return is not SQL until a model is finalized. These tests are about the
    // SQL, so each value is bound as the convention binds it.
    private static KeyTarget Key<T>(Expression<Func<Person, T>> key)
        => KeyTarget.Of(key, SqlSpelling.PostgreSql) is KeyTarget.Template template ? new KeyTarget.Template(Bound(template.Sql)) : KeyTarget.Of(key, SqlSpelling.PostgreSql);

    private static SqlTemplate Bound(SqlTemplate template)
        => template.Bind(lambda => CodoMetis.EFCore.SchemaKit.Metadata.Internal.DeclarationBinder.Bind(lambda, null!));

    private static Predicate Where<T>(Expression<Func<T, bool>> condition) => new(Bound(Predicate.Of(condition, SqlSpelling.PostgreSql).Template));

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
        Assert.AreEqual(new KeyTarget.Template(SqlTemplate.FromPlaceholders("lower({Name})")), Key(x => x.Name.ToLower()));
        Assert.AreEqual(new KeyTarget.Template(SqlTemplate.FromPlaceholders("({Name} || {Address.City})")), Key(x => x.Name + x.Address.City));
        Assert.AreEqual(new KeyTarget.Template(SqlTemplate.FromPlaceholders("coalesce({Tag}, {Name})")), Key(x => x.Tag ?? x.Name));
        Assert.AreEqual(new KeyTarget.Template(SqlTemplate.FromPlaceholders("'{Name}'")), Key(x => "{Name}"));
    }

    [TestMethod]
    public void A_lambda_the_translator_refuses_is_refused_where_it_is_written()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Key(x => x.Name.Normalize()));
        Assert.ThrowsExactly<NotSupportedException>(() => Where<Person>(x => x.Name.Normalize() == "a"));
    }

    [TestMethod]
    public void A_target_or_a_predicate_without_text_cannot_be_made()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new KeyTarget.Column(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new KeyTarget.Column("Name") with { Path = "" });
        Assert.ThrowsExactly<ArgumentException>(() => SqlTemplate.Raw(""));
        Assert.ThrowsExactly<ArgumentException>(() => SqlTemplate.FromPlaceholders(" "));
        Assert.ThrowsExactly<ArgumentException>(() => SqlTemplate.Of([]));
        Assert.ThrowsExactly<ArgumentException>(() => new SqlSegment.Text(""));
        Assert.ThrowsExactly<ArgumentException>(() => new SqlSegment.Raw("x") with { Sql = "" });
        Assert.ThrowsExactly<ArgumentException>(() => new SqlSegment.Column("a b"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new KeyTarget.Template(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new Predicate(SqlTemplate.FromPlaceholders("{Name} IS NULL")) with { Template = null! });
    }

    /// <summary>
    /// The predicate goes through the translator's null compensation: <c>!=</c> on a nullable
    /// member keeps the rows whose member is null, as C# does. Left out, a constraint "among rows
    /// not tagged blocked" stops covering the untagged ones.
    /// </summary>
    [TestMethod]
    public void A_predicate_is_translated_as_EF_Core_writes_it()
    {
        Assert.AreEqual("{RevokedOn} IS NULL", Where<Person>(x => x.RevokedOn == null).Template.ToString());
        Assert.AreEqual("({Tag} <> 'blocked' OR {Tag} IS NULL)", Where<Person>(x => x.Tag != "blocked").Template.ToString());
    }

    /// <summary>A predicate over an interface, for a convention: its paths are the interface's member names.</summary>
    [TestMethod]
    public void A_predicate_may_be_written_over_an_interface()
        => Assert.AreEqual(new Predicate(SqlTemplate.FromPlaceholders("{RevokedOn} IS NULL")), Where<IWithdrawable>(x => x.RevokedOn == null));

    /// <summary>
    /// A lambda is translated for one provider (#132): both factories ask for its spelling, and a
    /// package that has none to pass cannot have a lambda spelled as another provider's by default.
    /// </summary>
    [TestMethod]
    public void Both_factories_ask_for_the_providers_spelling()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => KeyTarget.Of((Expression<Func<Person, string>>)(x => x.Name), null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => Predicate.Of<Person>(x => x.RevokedOn == null, null!));

        foreach (var factory in new[] { typeof(KeyTarget).GetMethod(nameof(KeyTarget.Of))!, typeof(Predicate).GetMethod(nameof(Predicate.Of))! })
        {
            Assert.IsTrue(factory.GetParameters().Any(p => p.ParameterType == typeof(SqlSpelling)), factory.Name);
            Assert.AreEqual("SK9001", factory.GetCustomAttributes(typeof(System.Diagnostics.CodeAnalysis.ExperimentalAttribute), false)
                .Cast<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>().Single().DiagnosticId);
        }
    }

    /// <summary>5.x's rule, so that a convention may run twice: what is already the whole condition, or its last conjunct, is not added again.</summary>
    [TestMethod]
    public void And_adds_a_condition_once()
    {
        var active = new Predicate(SqlTemplate.FromPlaceholders("{RevokedOn} IS NULL"));
        var ranked = new Predicate(SqlTemplate.FromPlaceholders("{Rank} > 0"));

        Assert.AreEqual(active, active.And(active));
        Assert.AreEqual(new Predicate(SqlTemplate.FromPlaceholders("({RevokedOn} IS NULL) AND ({Rank} > 0)")), active.And(ranked));
        Assert.AreEqual(active.And(ranked), active.And(ranked).And(ranked));
        // Only the last conjunct is recognised; the first is added again, as in 5.x.
        Assert.AreEqual(new Predicate(SqlTemplate.FromPlaceholders("(({RevokedOn} IS NULL) AND ({Rank} > 0)) AND ({RevokedOn} IS NULL)")), active.And(ranked).And(active));
        // And three deep: what And wrote is still one group in front of the last conjunct.
        Assert.AreEqual(active.And(ranked).And(active), active.And(ranked).And(active).And(active));
    }

    /// <summary>
    /// A predicate written by hand may end in what looks like a conjunct and is none: AND binds
    /// tighter than OR. Taken for one, the condition stays wider than the one asked for, and a
    /// constraint covers rows it was told to leave out. Nothing fails.
    /// </summary>
    [TestMethod]
    [DataRow("{A} OR {B} AND ({C})", "{C}", "({A} OR {B} AND ({C})) AND ({C})")]
    [DataRow("({A}) OR ({B}) AND ({C})", "{C}", "(({A}) OR ({B}) AND ({C})) AND ({C})")]
    [DataRow("NOT ({A}) AND ({C})", "{C}", "(NOT ({A}) AND ({C})) AND ({C})")]
    // A parenthesis inside a literal closes nothing.
    [DataRow("({A} = ')') OR ({B}) AND ({C})", "{C}", "(({A} = ')') OR ({B}) AND ({C})) AND ({C})")]
    [DataRow("({A} = ')' OR {B}) AND ({C})", "{C}", "({A} = ')' OR {B}) AND ({C})")]
    [DataRow("({A} = 'it''s (') AND ({C})", "{C}", "({A} = 'it''s (') AND ({C})")]
    public void Only_a_conjunct_And_itself_wrote_is_taken_for_one(string template, string added, string expected)
        => Assert.AreEqual(expected, new Predicate(SqlTemplate.FromPlaceholders(template)).And(new Predicate(SqlTemplate.FromPlaceholders(added))).Template.ToString());

    // ---- a lambda between the call and the finalized model ---------------------------------------------

    /// <summary>
    /// A lambda is SQL when its model is finalized (issue 114). Until then the value holds the
    /// lambda: it says so, shows the lambda in a message, and refuses to hand out segments.
    /// </summary>
    [TestMethod]
    public void A_lambda_is_not_sql_where_it_is_written()
    {
        var condition = Predicate.Of<Person>(x => x.RevokedOn == null, SqlSpelling.PostgreSql);
        var expression = (KeyTarget.Template)KeyTarget.Of((Expression<Func<Person, string>>)(x => x.Name.ToLower()), SqlSpelling.PostgreSql);

        Assert.IsFalse(condition.Template.IsBound);
        Assert.IsFalse(expression.Sql.IsBound);
        Assert.AreEqual("x => (x.RevokedOn == null)", condition.Template.ToString());
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => condition.Template.Segments).Message,
            "a lambda becomes SQL when its model is finalized");

        Assert.IsTrue(Bound(condition.Template).IsBound);
        Assert.IsTrue(SqlTemplate.FromPlaceholders("{A}").IsBound);
    }

    /// <summary>
    /// A value only the model can write is a mark in the translation at the call, and a mark is
    /// no SQL: bound without a model, as these tests bind, such a lambda is refused, not stored.
    /// </summary>
    [TestMethod]
    public void A_value_only_the_model_can_write_is_not_bound_without_one()
    {
                var condition = Predicate.Of<Person>(x => x.RevokedOn == new DateOnly(2026, 1, 2), SqlSpelling.PostgreSql);

        Assert.IsFalse(condition.Template.IsBound);
        StringAssert.Contains(Assert.ThrowsExactly<NotSupportedException>(() => Bound(condition.Template)).Message, "only the model can write");
    }

    /// <summary>A lambda that names a property and nothing else is a path, and a path waits for nothing.</summary>
    [TestMethod]
    public void A_lambda_that_is_one_property_is_a_column_at_once()
        => Assert.AreEqual(new KeyTarget.Column("Name"), KeyTarget.Of((Expression<Func<Person, string>>)(x => x.Name), SqlSpelling.PostgreSql));

    /// <summary>
    /// Two lambdas are one value when their trees are, which is what lets a declaration said twice
    /// be refused where it is written. A value read from outside the lambda is part of the tree
    /// as the value it had at the call.
    /// </summary>
    [TestMethod]
    public void Two_lambdas_that_say_the_same_are_one_value()
    {
        var tag = "blocked";
        var literal = Predicate.Of<Person>(x => x.Tag != "blocked", SqlSpelling.PostgreSql);
        var captured = Predicate.Of<Person>(x => x.Tag != tag, SqlSpelling.PostgreSql);

        Assert.AreEqual(literal, captured);
        Assert.AreEqual(literal.GetHashCode(), captured.GetHashCode());
        Assert.AreNotEqual(literal, Predicate.Of<Person>(x => x.Tag != "other", SqlSpelling.PostgreSql));

        // The local changes after the call; the declaration does not.
        tag = "other";
        Assert.AreEqual(literal, captured);
        Assert.AreEqual("({Tag} <> 'blocked' OR {Tag} IS NULL)", Bound(captured.Template).ToString());
    }

    /// <summary>A convention adds a condition to one that is not bound yet, and adding it again changes nothing.</summary>
    [TestMethod]
    public void And_adds_to_a_lambda_that_is_not_bound()
    {
        var active = Predicate.Of<Person>(x => x.RevokedOn == null, SqlSpelling.PostgreSql);
        var ranked = Predicate.Of<Person>(x => x.Rank > 0, SqlSpelling.PostgreSql);
        var byHand = new Predicate(SqlTemplate.FromPlaceholders("{Tag} IS NOT NULL"));

        Assert.AreEqual(active, active.And(Predicate.Of<Person>(x => x.RevokedOn == null, SqlSpelling.PostgreSql)));
        Assert.AreEqual(active.And(ranked), active.And(ranked).And(ranked));
        Assert.AreEqual(active.And(byHand), active.And(byHand).And(byHand));
        Assert.AreNotEqual(active.And(ranked), active.And(ranked).And(active));

        Assert.IsFalse(active.And(byHand).Template.IsBound);
        Assert.AreEqual("({RevokedOn} IS NULL) AND ({Rank} > 0)", Bound(active.And(ranked).Template).ToString());
        Assert.AreEqual("({RevokedOn} IS NULL) AND ({Tag} IS NOT NULL)", Bound(active.And(byHand).Template).ToString());
    }

    /// <summary>
    /// SQL written by hand is never taken for what <c>And</c> wrote, whatever it reads like: its
    /// text is another kind of segment. The same characters from a package's own text are.
    /// </summary>
    [TestMethod]
    public void Hand_written_sql_that_reads_like_a_conjunct_is_none()
    {
        var added = new Predicate(SqlTemplate.FromPlaceholders("{C}"));
        var byHand = new Predicate(SqlTemplate.Raw("({A}) AND ({C})"));
        var written = new Predicate(SqlTemplate.FromPlaceholders("({A}) AND ({C})"));

        Assert.AreEqual("(({A}) AND ({C})) AND ({C})", byHand.And(added).Template.ToString());
        Assert.AreEqual(written, written.And(added));
        Assert.AreNotEqual(byHand, written);
    }

    /// <summary>What a template is made of: neighbouring text of one kind is one segment, so two ways of writing one SQL are one value.</summary>
    [TestMethod]
    public void A_template_is_its_segments()
    {
        var parsed = SqlTemplate.FromPlaceholders("lower({Email.Value}) <> 'it''s {Name}' AND tags = '{{a,b}}' {not a path} {} {Name");

        CollectionAssert.AreEqual(
            new SqlSegment[]
            {
                new SqlSegment.Text("lower("),
                new SqlSegment.Column("Email.Value"),
                new SqlSegment.Text(") <> 'it''s {Name}' AND tags = '{{a,b}}' {not a path} {} {Name"),
            },
            parsed.Segments.ToList());
        Assert.AreEqual(parsed, SqlTemplate.Of([new SqlSegment.Text("low"), new SqlSegment.Text("er("), new SqlSegment.Column("Email.Value"), .. parsed.Segments.Skip(2)]));

        CollectionAssert.AreEqual(
            new SqlSegment[] { new SqlSegment.Column("A"), new SqlSegment.Raw(" > b") },
            SqlTemplate.Raw("{A} > b").Segments.ToList());
        Assert.AreEqual("{A} > b", SqlTemplate.Raw("{A} > b").ToString());
    }
}
