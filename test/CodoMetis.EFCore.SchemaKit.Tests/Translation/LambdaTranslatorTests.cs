using System.Globalization;
using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit.Tests.Translation;

public enum DocStatus { Draft, Final }

public sealed class DocAddress
{
    public string City { get; set; } = "";
    public string? Zip { get; set; }
}

/// <summary>One property of each shape the translator tells apart: nullable or not, by annotation and by type.</summary>
public sealed class Doc
{
    public string Code { get; set; } = "";
    public string? Name { get; set; }
    public int Rank { get; set; }
    public int? Score { get; set; }
    public decimal Price { get; set; }
    public bool Active { get; set; }
    public bool? Flag { get; set; }
    public char Grade { get; set; }
    public DocStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DocAddress Address { get; set; } = new();

    // One of each numeric type, for the conversions: C# promotes the operands of a comparison.
    public sbyte Signed { get; set; }
    public byte Tiny { get; set; }
    public short Small { get; set; }
    public ushort Port { get; set; }
    public uint Count { get; set; }
    public long Big { get; set; }
    public ulong Huge { get; set; }
    public float Ratio { get; set; }
    public double Weight { get; set; }
    public decimal? Discount { get; set; }
}

/// <summary>
/// The lambda translator, rule by rule. The rules are 5.x's, and each was written after a failure:
/// an index no query used, a unique index that let duplicates in, a migration that failed at apply.
/// What these tests hold is the template; that the template is what EF Core's own queries write is
/// the parity sweep's to hold (issue 26).
/// </summary>
[TestClass]
public sealed class LambdaTranslatorTests
{
    private const string Whitespace = @"E' \t\n\r'";

    private static string E<T>(Expression<Func<Doc, T>> lambda) => LambdaTranslator.Expression(lambda);

    private static string P(Expression<Func<Doc, bool>> lambda) => LambdaTranslator.Predicate(lambda);

    private static string Refused(Func<string> translate) => Assert.ThrowsExactly<NotSupportedException>(() => translate()).Message;

    // ---- expressions ------------------------------------------------------------------------------------

    [TestMethod]
    public void A_property_path_stays_a_placeholder_for_the_resolver()
    {
        Assert.AreEqual("{Code}", E(x => x.Code));
        Assert.AreEqual("{Address.City}", E(x => x.Address.City));
        Assert.AreEqual("{Score}", E(x => x.Score!.Value), "Nullable<T>.Value is the column itself, as in EF Core's queries.");
    }

    /// <summary>
    /// Spelled as Npgsql's query translation spells them, character for character. <c>btrim(x)</c>
    /// alone trims spaces: a unique index on it let 'Y\t' and 'Y' coexist where Trim() says equal.
    /// And <c>substring</c>, not <c>substr</c>: they are different functions to the planner.
    /// </summary>
    [TestMethod]
    public void String_methods_are_written_as_the_providers_queries_write_them()
    {
        Assert.AreEqual("lower({Code})", E(x => x.Code.ToLower()));
        Assert.AreEqual("upper({Code})", E(x => x.Code.ToUpper()));
        Assert.AreEqual($"btrim({{Code}}, {Whitespace})", E(x => x.Code.Trim()));
        Assert.AreEqual($"ltrim({{Code}}, {Whitespace})", E(x => x.Code.TrimStart()));
        Assert.AreEqual($"rtrim({{Code}}, {Whitespace})", E(x => x.Code.TrimEnd()));
        Assert.AreEqual("replace({Code}, 'a', 'b')", E(x => x.Code.Replace("a", "b")));
        Assert.AreEqual("length({Code})", E(x => x.Code.Length));
        Assert.AreEqual("lower(replace({Address.City}, ' ', ''))", E(x => x.Address.City.Replace(" ", "").ToLower()));
    }

    /// <summary>.NET counts from 0 and SQL from 1. A start known at the declaration is folded, as EF Core folds it.</summary>
    [TestMethod]
    public void A_substring_starts_one_later()
    {
        var start = 2;

        Assert.AreEqual("substring({Code}, 2)", E(x => x.Code.Substring(1)));
        Assert.AreEqual("substring({Code}, 3, 4)", E(x => x.Code.Substring(start, 4)));
        Assert.AreEqual("substring({Code}, ({Rank} + 1))", E(x => x.Code.Substring(x.Rank)));
    }

    /// <summary>
    /// C# concatenates null as an empty string and PostgreSQL's <c>||</c> returns NULL. An index on
    /// <c>"Code" || "Name"</c> held NULL for every row without a name: it served no EF Core query
    /// and, unique, checked none of those rows.
    /// </summary>
    [TestMethod]
    public void A_concatenation_coalesces_what_can_be_null()
    {
        string? none = null;
        var prefix = "p-";

        Assert.AreEqual("({Code} || {Address.City})", E(x => x.Code + x.Address.City));
        Assert.AreEqual("({Code} || coalesce({Name}, ''))", E(x => x.Code + x.Name));
        Assert.AreEqual("({Code} || {Rank})", E(x => x.Code + x.Rank));
        Assert.AreEqual("({Code} || coalesce({Score}::text, ''))", E(x => x.Code + x.Score), "COALESCE needs one type.");
        Assert.AreEqual("('p-' || {Code})", E(x => prefix + x.Code));
        Assert.AreEqual("({Code} || '')", E(x => x.Code + none));
    }

    [TestMethod]
    public void A_null_coalescing_is_a_coalesce()
        => Assert.AreEqual("coalesce({Name}, 'none')", E(x => x.Name ?? "none"));

    /// <summary>A captured value is known where the lambda is written: it is evaluated and written as a literal.</summary>
    [TestMethod]
    public void A_literal_is_written_so_that_it_is_the_same_everywhere()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // A German thread must never turn 1.5 into 1,5.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var rate = 1.5m;
            var holder = new Doc { Code = "it's" };

            Assert.AreEqual("{Price} > 1.5", P(x => x.Price > rate));
            Assert.AreEqual("coalesce({Name}, 'it''s')", E(x => x.Name ?? holder.Code), "A member of a captured object is a value, not a path.");
            Assert.AreEqual("coalesce({Name}, '{draft}')", E(x => x.Name ?? "{draft}"), "Inside quotes a brace is no placeholder and needs no escape.");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [TestMethod]
    public void What_has_no_translation_is_refused_where_the_lambda_is_written()
    {
        var id = Guid.NewGuid();
        var at = new DateTime(2026, 1, 1);
        Expression<Func<Doc, Doc, string>> two = (a, b) => a.Code;

        // A member of a scalar is no property the model maps: it passed as a path once and failed at `migrations add`.
        StringAssert.Contains(Refused(() => E(x => x.CreatedAt.Year)), "reads Year of a DateTime");
        StringAssert.Contains(Refused(() => E(x => x.Code.PadLeft(3))), "has no translation in a declaration");
        StringAssert.Contains(Refused(() => E(x => x.Code.Trim('x'))), "has no translation in a declaration");
        StringAssert.Contains(Refused(() => E(x => x.Rank + 1)), "has no translation in a declaration");

        // A DateTime or a Guid is IFormattable too, and came out as bare text that is not SQL.
        StringAssert.Contains(Refused(() => E(x => id)), "A value of type 'Guid' cannot be written as a SQL literal");
        StringAssert.Contains(Refused(() => P(x => x.CreatedAt > at)), "A value of type 'DateTime' cannot be written as a SQL literal");

        StringAssert.Contains(Refused(() => LambdaTranslator.Expression(two)), "takes 2 parameters");
        StringAssert.Contains(Refused(() => LambdaTranslator.Predicate(two)), "takes 2 parameters");
    }

    // ---- predicates: comparisons and null ------------------------------------------------------------------

    [TestMethod]
    public void A_comparison_of_what_cannot_be_null_is_written_plainly()
    {
        Assert.AreEqual("{Rank} > 5", P(x => x.Rank > 5));
        Assert.AreEqual("{Rank} >= 5", P(x => x.Rank >= 5));
        Assert.AreEqual("{Rank} < 5", P(x => x.Rank < 5));
        Assert.AreEqual("{Rank} <= 5", P(x => x.Rank <= 5));
        Assert.AreEqual("{Code} = 'a'", P(x => x.Code == "a"));
        Assert.AreEqual("{Code} <> 'a'", P(x => x.Code != "a"));
        Assert.AreEqual("{Score} > 5", P(x => x.Score > 5));
        Assert.AreEqual("length({Code}) > 3", P(x => x.Code.Length > 3));
    }

    /// <summary>
    /// C# compares with two-valued logic: <c>x.Name != "a"</c> is true for a null name. SQL's
    /// <c>&lt;&gt;</c> is NULL there, and a partial index or a constraint whose predicate is NULL
    /// leaves the row out. A unique index "among rows that are not blocked" stopped covering the
    /// rows whose tag is null. So the comparison is written as EF Core writes the same lambda.
    /// </summary>
    [TestMethod]
    public void A_comparison_of_what_can_be_null_keeps_the_rows_csharp_keeps()
    {
        Assert.AreEqual("({Name} <> 'a' OR {Name} IS NULL)", P(x => x.Name != "a"));
        Assert.AreEqual("{Name} = 'a'", P(x => x.Name == "a"), "An equality with a value that is not null is false for null on both sides.");
        Assert.AreEqual("({Name} = {Address.Zip} OR ({Name} IS NULL AND {Address.Zip} IS NULL))", P(x => x.Name == x.Address.Zip));
        Assert.AreEqual(
            "(({Name} <> {Address.Zip} OR {Name} IS NULL OR {Address.Zip} IS NULL) AND ({Name} IS NOT NULL OR {Address.Zip} IS NOT NULL))",
            P(x => x.Name != x.Address.Zip));

        // A function the translator accepts is strict: lower(x) is null exactly when x is, so the test is on the column.
        Assert.AreEqual("(lower({Name}) <> 'a' OR {Name} IS NULL)", P(x => x.Name!.ToLower() != "a"));
        Assert.AreEqual("({Name} <> {Code} OR {Name} IS NULL)", P(x => x.Name != x.Code));
    }

    [TestMethod]
    public void A_comparison_with_null_is_a_null_test()
    {
        string? none = null;

        Assert.AreEqual("{Name} IS NULL", P(x => x.Name == null));
        Assert.AreEqual("{Name} IS NOT NULL", P(x => x.Name != null));
        Assert.AreEqual("{Name} IS NULL", P(x => null == x.Name));
        Assert.AreEqual("{Name} IS NULL", P(x => x.Name == none), "A captured variable that holds null read `= NULL` once.");
        Assert.AreEqual("{Score} IS NOT NULL", P(x => x.Score != null));
        Assert.AreEqual("{Name} IS NULL", P(x => x.Name!.ToLower() == null), "Over the column the operand is null through.");
        Assert.AreEqual("{Flag} IS NOT NULL", P(x => x.Flag.HasValue));
        Assert.AreEqual("{Flag} IS NULL", P(x => !x.Flag.HasValue));
    }

    // ---- predicates: boolean structure -------------------------------------------------------------------

    [TestMethod]
    public void Boolean_structure_is_kept_with_its_grouping()
    {
        var on = true;

        Assert.AreEqual("({Rank} > 5 AND {Active})", P(x => x.Rank > 5 && x.Active));
        Assert.AreEqual("({Rank} > 5 OR ({Active} AND {Code} = 'a'))", P(x => x.Rank > 5 || x.Active && x.Code == "a"));
        Assert.AreEqual("{Active}", P(x => x.Active));
        Assert.AreEqual("{Flag}", P(x => x.Flag!.Value));
        Assert.AreEqual("NOT ({Active})", P(x => !x.Active));
        Assert.AreEqual("TRUE", P(x => on), "A captured flag is baked in.");
        Assert.AreEqual("({Active} AND NOT (TRUE))", P(x => x.Active && !on), "A negated flag is not folded: 5.x wrote it so, and a stored template must not change.");
    }

    /// <summary>
    /// A negation over a comparison that can be NULL is pushed down to it: wrapped around it,
    /// <c>NOT (NULL)</c> is NULL where C# has true. Over what cannot be null it stays a NOT.
    /// </summary>
    [TestMethod]
    public void A_negation_is_pushed_down_to_a_comparison_that_can_be_null()
    {
        Assert.AreEqual("NOT ({Rank} > 5)", P(x => !(x.Rank > 5)));
        Assert.AreEqual("({Name} <> 'a' OR {Name} IS NULL)", P(x => !(x.Name == "a")));
        Assert.AreEqual("{Name} = 'a'", P(x => !(x.Name != "a")));

        // De Morgan on the way down, and the complement for a comparison that cannot be null.
        Assert.AreEqual("(({Name} <> 'a' OR {Name} IS NULL) OR {Rank} <= 5)", P(x => !(x.Name == "a" && x.Rank > 5)));
        Assert.AreEqual("(({Name} <> 'a' OR {Name} IS NULL) AND {Rank} < 5)", P(x => !(x.Name == "a" || x.Rank >= 5)));

        // C#'s lifted comparison is false for a null, so its negation is true there. EF Core writes that as a CASE.
        Assert.AreEqual("CASE WHEN {Score} > 5 THEN FALSE ELSE TRUE END", P(x => !(x.Score > 5)));
    }

    /// <summary>
    /// C# compares chars as ints: <c>x.Grade == 'A'</c> arrives as <c>Convert(x.Grade) == 65</c>,
    /// and 65 met a character column, which PostgreSQL refuses at apply.
    /// </summary>
    [TestMethod]
    public void A_char_is_compared_as_the_char_it_was_written_as()
    {
        Assert.AreEqual("{Grade} = 'A'", P(x => x.Grade == 'A'));
        Assert.AreEqual("'A' <> {Grade}", P(x => 'A' != x.Grade));
    }

    /// <summary>
    /// How an enum is stored depends on the property's value conversion, which is not known here:
    /// <c>Status == Status.Final</c> would compare a text column against 1 and fail at apply.
    /// </summary>
    [TestMethod]
    public void What_a_predicate_cannot_say_is_refused()
    {
        StringAssert.Contains(Refused(() => P(x => x.Status == DocStatus.Final)), "compares an enum");
        StringAssert.Contains(Refused(() => P(x => x.Status != DocStatus.Draft && x.Active)), "compares an enum");
        StringAssert.Contains(Refused(() => P(x => x.Code.Contains("a"))), "has no translation in a declaration's predicate");
        StringAssert.Contains(Refused(() => P(x => x.Code.PadLeft(3) == "a")), "has no translation in a declaration");
    }

    // ---- conversions ---------------------------------------------------------------------------------------

    // The conversions of what the lambda reads from the entity, as the compiler wrote them: a row
    // below states them, so that it tests the tree it says it tests.
    private static string Conversions(LambdaExpression lambda)
    {
        var found = new ConversionFinder(lambda.Parameters[0]);
        found.Visit(lambda.Body);
        return string.Join(", ", found.Found);
    }

    private sealed class ConversionFinder(ParameterExpression root) : ExpressionVisitor
    {
        public List<string> Found { get; } = [];

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType == ExpressionType.Convert && new ParameterSeeker(root).Sees(node))
                Found.Add($"{Name(node.Operand.Type)}->{Name(node.Type)}");
            return base.VisitUnary(node);
        }

        private static string Name(Type type) => Nullable.GetUnderlyingType(type) is { } underlying ? underlying.Name + "?" : type.Name;
    }

    private sealed class ParameterSeeker(ParameterExpression root) : ExpressionVisitor
    {
        private bool _seen;

        public bool Sees(Expression node)
        {
            Visit(node);
            return _seen;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _seen |= node == root;
            return node;
        }
    }

    private static void Silent(string template, string conversions, Expression<Func<Doc, bool>> lambda)
    {
        Assert.AreEqual(conversions, Conversions(lambda), $"What the compiler wrote for {lambda}");
        Assert.AreEqual(template, LambdaTranslator.Predicate(lambda), lambda.ToString());
    }

    private static void RefusedConversion(string conversion, Expression<Func<Doc, bool>> lambda)
    {
        StringAssert.Contains(Conversions(lambda), conversion.Replace(" to ", "->"), $"What the compiler wrote for {lambda}");
        StringAssert.Contains(Refused(() => LambdaTranslator.Predicate(lambda)), $"converts {conversion}.", lambda.ToString());
    }

    /// <summary>
    /// Issue 103: every conversion was left out of the template, so <c>(int)x.Price == 2</c> was
    /// stored as <c>{Price} = 2</c>. For a price of 2.5 the lambda holds and the predicate does not:
    /// the row is outside the constraint, and nothing said so.
    /// </summary>
    [TestMethod]
    public void A_cast_that_can_change_the_value_is_refused_where_it_is_written()
    {
        var message = Refused(() => P(x => (int)x.Price == 2));

        StringAssert.Contains(message, "Convert(x.Price, Int32)");
        StringAssert.Contains(message, "converts Decimal to Int32.");
        StringAssert.Contains(message, "x.Amount == 2m");
        StringAssert.Contains(message, "as SQL");

        // In an expression, and where the dispatch takes a conversion off without writing its
        // operand through the same path: a null test, a concatenation of a value that can be null.
        StringAssert.Contains(Refused(() => E(x => (int)x.Price)), "converts Decimal to Int32.");
        StringAssert.Contains(Refused(() => P(x => (int?)x.Discount == null)), "converts Decimal? to Int32?.");
        StringAssert.Contains(Refused(() => P(x => (int?)x.Discount != x.Score)), "converts Decimal? to Int32?.");
        StringAssert.Contains(Refused(() => E(x => x.Code + (int?)x.Discount)), "converts Decimal? to Int32?.");
        StringAssert.Contains(Refused(() => E(x => (x.Score ?? (int)x.Price))), "converts Decimal to Int32.");
        StringAssert.Contains(Refused(() => E(x => x.Code.Substring((int)x.Price))), "converts Decimal to Int32.");

        // A ?? converts its left side in a lambda of its own, which is no Convert under the entity.
        Expression<Func<Doc, decimal>> coalesced = x => x.Score ?? x.Price;
        Assert.IsNotNull(((BinaryExpression)coalesced.Body).Conversion, coalesced.ToString());
        StringAssert.Contains(Refused(() => LambdaTranslator.Expression(coalesced)), "converts Int32 to Decimal.");
        Assert.AreEqual("coalesce({Score}, {Big})", E(x => x.Score ?? x.Big));
        StringAssert.Contains(Refused(() => P(x => !(x.Active && (int)x.Price > 2))), "converts Decimal to Int32.");
    }

    /// <summary>
    /// What the compiler writes without being asked stays out of the template, as before: each row
    /// states the conversions in its tree and the template it had before the table existed.
    /// </summary>
    [TestMethod]
    public void A_conversion_that_keeps_every_value_is_left_out_as_before()
    {
        // To Nullable<T>, and back, which is .Value.
        Silent("{Score} = {Rank}", "Int32->Int32?", x => x.Score == x.Rank);
        Silent("{Flag} = {Active}", "Boolean->Boolean?", x => x.Flag == x.Active);
        Silent("{Score} > 5", "Int32?->Int32", x => (int)x.Score! > 5);
        Assert.AreEqual(P(x => x.Score!.Value > 5), P(x => (int)x.Score! > 5));

        // Between integer types: C# compares nothing narrower than an int, and widens to the wider side.
        Silent("{Small} = 5", "Int16->Int32", x => x.Small == 5);
        Silent("{Tiny} > 5", "Byte->Int32", x => x.Tiny > 5);
        Silent("{Signed} < 5", "SByte->Int32", x => x.Signed < 5);
        Silent("{Port} = 5", "UInt16->Int32", x => x.Port == 5);
        Silent("{Tiny} = {Small}", "Byte->Int32, Int16->Int32", x => x.Tiny == x.Small);
        Silent("{Rank} = 5", "Int32->Int64", x => x.Rank == 5L);
        Silent("{Rank} < {Big}", "Int32->Int64", x => x.Rank < x.Big);
        Silent("{Small} < {Big}", "Int16->Int64", x => x.Small < x.Big);
        Silent("{Count} = {Rank}", "UInt32->Int64, Int32->Int64", x => x.Count == x.Rank);
        Silent("{Tiny} = {Count}", "Byte->UInt32", x => x.Tiny == x.Count);
        Silent("{Count} <= {Huge}", "UInt32->UInt64", x => x.Count <= x.Huge);
        Silent("{Port} <= {Huge}", "UInt16->UInt64", x => x.Port <= x.Huge);
        Silent("{Score} = {Small}", "Int32->Int32?, Int16->Int32", x => x.Score == x.Small);

        // A char is compared as an int, and the constant goes back to the char it was written as.
        Silent("{Grade} = 'A'", "Char->Int32", x => x.Grade == 'A');

        // To object: C# concatenates through string.Concat(object, object).
        Expression<Func<Doc, string>> concat = x => x.Code + x.Rank;
        Assert.AreEqual("Int32->Object", Conversions(concat));
        Assert.AreEqual("({Code} || {Rank})", LambdaTranslator.Expression(concat));
    }

    /// <summary>
    /// A conversion of a value alone is C#'s to do, and what it returns is written: the value was
    /// written unconverted, <c>2.5</c> for <c>(int)limit</c>.
    /// </summary>
    [TestMethod]
    public void A_conversion_of_a_captured_value_is_done_before_the_value_is_written()
    {
        var limit = 2.5m;
        var grade = 'A';
        char? noGrade = null;
        var third = 1f / 3;

        Assert.AreEqual("{Rank} = 2", P(x => x.Rank == (int)limit));
        Assert.AreEqual("{Price} > 2.5", P(x => x.Price > limit));
        Assert.AreEqual("{Grade} = 'A'", P(x => x.Grade == grade));
        Assert.AreEqual("{Grade} IS NULL", P(x => x.Grade == noGrade));

        // A float met a double column as its own digits, which are another double.
        Assert.AreEqual("{Weight} > " + ((double)third).ToString(CultureInfo.InvariantCulture), P(x => x.Weight > third));
    }

    /// <summary>Each kind of conversion that is not in the table, as the compiler writes it.</summary>
    [TestMethod]
    public void Every_other_conversion_between_numbers_is_refused()
    {
        // To an integer: the fraction goes.
        RefusedConversion("Decimal to Int32", x => (int)x.Price == 2);
        RefusedConversion("Double to Int64", x => (long)x.Weight == 2);
        RefusedConversion("Single to Int32", x => (int)x.Ratio == 2);

        // To a narrower integer, or to one of the other sign: the high bits go.
        RefusedConversion("Int64 to Int32", x => (int)x.Big == 5);
        RefusedConversion("Int32 to Byte", x => (byte)x.Rank == 5);
        RefusedConversion("Int32 to UInt32", x => (uint)x.Rank == 5u);
        RefusedConversion("UInt64 to Int64", x => (long)x.Huge == 5L);

        // To a floating point: (float)16777217 is 16777216f. The compiler writes these unasked,
        // for a constant or a column of the wider type.
        RefusedConversion("Int32 to Single", x => (float)x.Rank == 16777216f);
        RefusedConversion("Int32 to Double", x => x.Rank > 2.5);
        RefusedConversion("Int64 to Double", x => x.Big > x.Weight);
        RefusedConversion("Single to Double", x => x.Ratio > 0.5);
        RefusedConversion("Decimal to Double", x => (double)x.Price > 0.5);

        // To a decimal: every integer is one, but how a database types the comparison is its own rule.
        RefusedConversion("Int32 to Decimal", x => x.Rank > 2.5m);
        RefusedConversion("Int32 to Decimal", x => x.Rank > x.Price);
        RefusedConversion("Double to Decimal", x => (decimal)x.Weight > 2.5m);

        // A char is an int to a comparison only.
        RefusedConversion("Char to Int64", x => (long)x.Grade == 65L);

        // An enum outside a comparison; in one it keeps its own reason.
        StringAssert.Contains(Refused(() => E(x => (int)x.Status)), "converts DocStatus to Int32.");
        StringAssert.Contains(Refused(() => P(x => (int)x.Status == 1)), "compares an enum");

        // In a checked context the compiler writes another node, ConvertChecked, held to the same table.
        StringAssert.Contains(Refused(() => P(x => checked((int)x.Big) == 2)), "converts Int64 to Int32.");
    }

    /// <summary>
    /// The rule over every pair of the types a comparison promotes, built by hand since the
    /// compiler writes only some of them: a pair is silent exactly when the table has it, and a
    /// pair with a <c>Nullable&lt;T&gt;</c> on either side is its underlying pair.
    /// </summary>
    [TestMethod]
    public void A_pair_of_numeric_types_is_silent_exactly_when_the_table_has_it()
    {
        Type[] numeric =
        [
            typeof(sbyte), typeof(byte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(decimal), typeof(char),
        ];

        var x = Expression.Parameter(typeof(Doc), "x");
        var properties = typeof(Doc).GetProperties().Where(p => numeric.Contains(p.PropertyType)).ToLookup(p => p.PropertyType);
        var silent = 0;

        foreach (var from in numeric)
        foreach (var to in numeric.Where(t => t != from))
        foreach (var target in new[] { to, typeof(Nullable<>).MakeGenericType(to) })
        {
            var column = Expression.Property(x, properties[from].First());
            var lambda = Expression.Lambda<Func<Doc, bool>>(
                Expression.Equal(Expression.Convert(column, target), Expression.Convert(column, target)), x);

            if (LambdaTranslator.SilentConversions.Contains((from, to)))
            {
                Assert.AreEqual($"{{{column.Member.Name}}} = {{{column.Member.Name}}}", LambdaTranslator.Predicate(lambda), lambda.ToString());
                silent++;
            }
            else
                StringAssert.Contains(Refused(() => LambdaTranslator.Predicate(lambda)), $"converts {from.Name} to ", lambda.ToString());
        }

        Assert.AreEqual(2 * LambdaTranslator.SilentConversions.Count, silent, "A pair of the table is between types this test does not have.");

        // What needs no entry: the type itself, its Nullable<T> both ways, and a type the source already is.
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(int), typeof(int)));
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(int), typeof(int?)));
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(int?), typeof(int)));
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(int), typeof(object)));
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(int?), typeof(IComparable)));
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(string), typeof(object)));
        Assert.IsTrue(LambdaTranslator.IsSilentConversion(typeof(DocStatus), typeof(Enum)));
        Assert.IsFalse(LambdaTranslator.IsSilentConversion(typeof(object), typeof(string)));
        Assert.IsFalse(LambdaTranslator.IsSilentConversion(typeof(DocStatus), typeof(int)));
        Assert.IsFalse(LambdaTranslator.IsSilentConversion(typeof(int), typeof(DocStatus)));
    }

    // ---- the tables ----------------------------------------------------------------------------------------

    /// <summary>
    /// The accepted constructs are data, read by the dispatch, the parity sweep and the analyzer. A
    /// construct in the dispatch and not in a table would be translated and never swept.
    /// </summary>
    [TestMethod]
    public void The_accepted_constructs_are_in_the_tables()
    {
        CollectionAssert.AreEquivalent(
            new[] { "ToLower()", "ToUpper()", "Trim()", "TrimStart()", "TrimEnd()", "Replace(String, String)", "Substring(Int32)", "Substring(Int32, Int32)" },
            LambdaTranslator.StringMethods.Keys.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})").ToList());
        CollectionAssert.AreEquivalent(
            new[] { "String.Length", "Nullable`1.Value", "Nullable`1.HasValue" },
            LambdaTranslator.ValueMembers.Keys.Select(k => $"{k.DeclaringType.Name}.{k.Name}").ToList());
        CollectionAssert.AreEquivalent(new[] { ExpressionType.Add, ExpressionType.Coalesce }, LambdaTranslator.ValueOperators.ToList());
        CollectionAssert.AreEquivalent(
            new[] { "=", "<>", "<", "<=", ">", ">=" },
            LambdaTranslator.ComparisonOperators.Values.ToList());
        CollectionAssert.AreEquivalent(
            new[] { ExpressionType.AndAlso, ExpressionType.OrElse, ExpressionType.Not },
            LambdaTranslator.LogicalOperators.ToList());
        Assert.IsFalse(LambdaTranslator.LiteralTypes.Any(t => t == typeof(DateTime) || t == typeof(Guid) || t.IsEnum));
        Assert.AreEqual(14, LambdaTranslator.LiteralTypes.Count);

        // Widenings between integer types, and the char a comparison promotes. Nothing with a
        // fraction on either side: adding a row is a decision about every database, not a convenience.
        CollectionAssert.AreEquivalent(
            new[]
            {
                "SByte->Int16", "SByte->Int32", "SByte->Int64", "Int16->Int32", "Int16->Int64", "Int32->Int64",
                "Byte->Int16", "Byte->UInt16", "Byte->Int32", "Byte->UInt32", "Byte->Int64", "Byte->UInt64",
                "UInt16->Int32", "UInt16->UInt32", "UInt16->Int64", "UInt16->UInt64", "UInt32->Int64", "UInt32->UInt64",
                "Char->Int32",
            },
            LambdaTranslator.SilentConversions.Select(c => $"{c.From.Name}->{c.To.Name}").ToList());
    }
}
