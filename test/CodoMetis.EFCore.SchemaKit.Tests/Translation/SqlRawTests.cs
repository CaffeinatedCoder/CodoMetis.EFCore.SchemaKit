using System.Linq.Expressions;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit.Tests.Translation;

// This project has a namespace `Sql`, which is nearer than the kernel's class of that name.
using Sql = CodoMetis.EFCore.SchemaKit.Sql;

/// <summary>
/// <see cref="Sql.Raw{T}" /> in a lambda (issue 220): SQL written by hand comes out of the
/// translator as its own kind of segment, as written when it is the whole lambda and in
/// parentheses as a part of one, and nothing is derived from its text.
/// </summary>
[TestClass]
public sealed class SqlRawTests
{
    /// <summary>A template with its kinds of segment told apart: SQL written by hand between « and ».</summary>
    private static string Show(SqlTemplate template)
    {
        StringBuilder text = new();
        foreach (var segment in template.Segments)
        {
            _ = segment switch
            {
                SqlSegment.Raw raw       => text.Append('«').Append(raw.Sql).Append('»'),
                SqlSegment.Column column => text.Append('{').Append(column.Path).Append('}'),
                SqlSegment.Text written  => text.Append(written.Sql),
                _                        => throw new InvalidOperationException(),
            };
        }

        return text.ToString();
    }

    private static string E<T>(Expression<Func<Doc, T>> lambda)
        => Show(DeclarationBinder.Bind(UnboundLambda.Of(lambda, Shipped.Spelling, isPredicate: false), entityType: null));

    private static string P(Expression<Func<Doc, bool>> lambda)
        => Show(DeclarationBinder.Bind(UnboundLambda.Of(lambda, Shipped.Spelling, isPredicate: true), entityType: null));

    private static string Refused(Action declare) => Assert.ThrowsExactly<NotSupportedException>(declare).Message;

    /// <summary>
    /// Alone it is what the contract's own factory makes of the same text, which is what a 5.x
    /// reader makes of a filter and of an expression: the three have to be one object.
    /// </summary>
    [TestMethod]
    public void As_the_whole_lambda_it_is_the_SQL_as_written()
    {
        Assert.AreEqual("«lower(»{Code}«)»", E(x => Sql.Raw<string>("lower({Code})")));
        Assert.AreEqual("{CreatedAt}« IS NULL»", P(x => Sql.Raw<bool>("{CreatedAt} IS NULL")));
        Assert.AreEqual("«a > 0 OR b»", P(x => Sql.Raw<bool>("a > 0 OR b")));

        Assert.AreEqual(
            SqlTemplate.Raw("lower({Code}) || {Address.City}"),
            DeclarationBinder.Bind(UnboundLambda.Of((Expression<Func<Doc, string>>)(x => Sql.Raw<string>("lower({Code}) || {Address.City}")), Shipped.Spelling, isPredicate: false), entityType: null));
        Assert.AreEqual("«lower(»{Code}«)»", E<object>(x => Sql.Raw<string>("lower({Code})")), "Under the conversion a key of object adds.");
    }

    /// <summary>
    /// A placeholder and nothing else is the column, as the property is: one index however it was
    /// said, and no key in parentheses of its own for it.
    /// </summary>
    [TestMethod]
    public void A_placeholder_alone_is_the_column()
    {
        Assert.AreEqual(new KeyTarget.Column("Code"), KeyTarget.Of((Expression<Func<Doc, string>>)(x => Sql.Raw<string>("{Code}")), Shipped.Spelling));
        Assert.IsInstanceOfType<KeyTarget.Template>(KeyTarget.Of((Expression<Func<Doc, string>>)(x => Sql.Raw<string>("{Code} ")), Shipped.Spelling));
    }

    /// <summary>Nothing knows what the SQL binds as, so it never stands bare beside an operator.</summary>
    [TestMethod]
    public void As_a_part_it_stands_in_parentheses()
    {
        Assert.AreEqual("({Rank} > 0 AND («a > 0 OR b»))", P(x => x.Rank > 0 && Sql.Raw<bool>("a > 0 OR b")));
        Assert.AreEqual("(«a + 1») > {Rank}", P(x => Sql.Raw<int>("a + 1") > x.Rank));
        Assert.AreEqual("lower((«a || b»))", E(x => Sql.Raw<string>("a || b").ToLower()));
        Assert.AreEqual("({Code} || coalesce((«a»), ''))", E(x => x.Code + Sql.Raw<string>("a")));
        Assert.AreEqual("coalesce({Name}, («a»))", E(x => x.Name ?? Sql.Raw<string>("a")));
    }

    /// <summary>A condition written by hand is negated as a whole, also when the negation is pushed through.</summary>
    [TestMethod]
    public void A_negation_is_written_around_it()
    {
        Assert.AreEqual("NOT («a > 0»)", P(x => !Sql.Raw<bool>("a > 0")));
        Assert.AreEqual("({Name} = 'x' OR NOT («a > 0»))", P(x => !(x.Name != "x" && Sql.Raw<bool>("a > 0"))));
    }

    /// <summary>
    /// What <c>T</c> says of null is all there is: a value type cannot be null, its
    /// <c>Nullable&lt;T&gt;</c> can, and a reference type is taken to be, since a tree does not
    /// tell <c>string</c> from <c>string?</c>.
    /// </summary>
    [TestMethod]
    public void Its_type_says_whether_it_can_be_null()
    {
        Assert.AreEqual("(«a») <> 1", P(x => Sql.Raw<int>("a") != 1));
        Assert.AreEqual("((«a») <> 1 OR («a») IS NULL)", P(x => Sql.Raw<int?>("a") != 1));
        Assert.AreEqual("((«a») <> 'x' OR («a») IS NULL)", P(x => Sql.Raw<string>("a") != "x"));
        Assert.AreEqual("(«a») IS NULL", P(x => Sql.Raw<string>("a") == null));
        Assert.AreEqual("(«a») IS NOT NULL", P(x => Sql.Raw<int?>("a").HasValue));
        Assert.AreEqual("((«a») = {Name} OR ((«a») IS NULL AND {Name} IS NULL))", P(x => Sql.Raw<string>("a") == x.Name));
    }

    /// <summary>
    /// A placeholder is a property path outside a literal, by the grammar of every template. A
    /// quote in the SQL does not move where the next mark is looked for.
    /// </summary>
    [TestMethod]
    public void A_placeholder_is_a_column_and_a_literal_is_left_alone()
    {
        Assert.AreEqual("{Code}« = 'it''s {Name}' AND »{Address.Zip}« IS NULL»", P(x => Sql.Raw<bool>("{Code} = 'it''s {Name}' AND {Address.Zip} IS NULL")));
        Assert.AreEqual("(((«'») AND («'»)) AND {Rank} = 1)", P(x => Sql.Raw<bool>("'") && Sql.Raw<bool>("'") && x.Rank == 1), "An unbalanced quote stays in its own SQL.");
        Assert.AreEqual("{Code} = '﷐a﷑'", P(x => x.Code == "﷐a﷑"), "A value that holds the marks is a literal.");
    }

    /// <summary>
    /// The marker reads no parameter, so the visitor that reads the captured values took it for
    /// one and called it. What it is handed is a value, and is read where the lambda is written.
    /// </summary>
    [TestMethod]
    public void The_SQL_is_read_where_the_lambda_is_written_and_the_marker_is_never_called()
    {
        var sql = "a > 0";
        Expression<Func<Doc, bool>> lambda = x => Sql.Raw<bool>(sql);
        var unbound = UnboundLambda.Of(lambda, Shipped.Spelling, isPredicate: true);
        sql = "a < 0";

        Assert.AreEqual("«a > 0»", Show(DeclarationBinder.Bind(unbound, entityType: null)));
        Assert.AreEqual(unbound, UnboundLambda.Of((Expression<Func<Doc, bool>>)(x => Sql.Raw<bool>("a > 0")), Shipped.Spelling, isPredicate: true), "Said twice, the two are one.");

        // Without the visitor too, as the translator's own tests call it.
        var limit = 3;
        StringAssert.Contains(LambdaTranslator.Predicate((Expression<Func<Doc, bool>>)(x => Sql.Raw<int>("a") > limit), Shipped.Spelling), "> 3");
    }

    [TestMethod]
    public void Called_it_throws()
        => StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => Sql.Raw<bool>("a > 0")).Message, "cannot be called");

    [TestMethod]
    public void What_is_no_SQL_is_refused_where_the_lambda_is_written()
    {
        StringAssert.Contains(Refused(() => P(x => Sql.Raw<bool>(x.Code))), "is read from the entity");
        StringAssert.Contains(Refused(() => P(x => Sql.Raw<bool>(" "))), "holds no SQL");
        StringAssert.Contains(Refused(() => P(x => Sql.Raw<bool>(null!))), "holds no SQL");
        StringAssert.Contains(Refused(() => P(x => Sql.Raw<bool>("a = '﷐'"))), "U+FDD0");
        StringAssert.Contains(Refused(() => P(x => Sql.Raw<bool>(Sql.Raw<string>("a")))), "is read from the entity");
    }

    /// <summary>What stands around it is still checked: a conversion that can change the value has no spelling here either.</summary>
    [TestMethod]
    public void A_conversion_of_it_is_held_to_the_same_table()
    {
        Assert.AreEqual("(«a») = 5", P(x => Sql.Raw<short>("a") == 5));
        StringAssert.Contains(Refused(() => P(x => (int)Sql.Raw<decimal>("a") == 2)), "converts Decimal to Int32");
        StringAssert.Contains(Refused(() => P(x => Sql.Raw<DocStatus>("a") == DocStatus.Final)), "compares an enum");
    }
}
