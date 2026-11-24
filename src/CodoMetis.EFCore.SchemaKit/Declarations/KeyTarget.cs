using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit;

// What a declaration is made of before anything is resolved: the two values every provider's
// declarations share. They are what a consumer writes and what the read model hands back, so
// unlike the contributor contract they are not marked experimental. The two methods that make
// one from a lambda are the contract's: the SQL they write is one provider's, so they are handed
// that provider's spelling, by its package (#132).

/// <summary>
/// What a key of an index or an element of a constraint is built from: a property path, which is
/// resolved to a column, or SQL with <c>{Path}</c> placeholders, which is resolved to an expression.
/// </summary>
/// <remarks>
/// The two are kept apart because the database keeps them apart: a column is written as it is, an
/// expression in parentheses, and a default name takes a column's name but an expression's letters
/// and digits.
/// </remarks>
public closed record KeyTarget
{
    /// <summary>A property of the entity type, by its path: <c>Name</c>, <c>Address.City</c>.</summary>
    /// <param name="Path">The property names from the entity type down, separated by dots.</param>
    public sealed record Column(string Path) : KeyTarget
    {
        /// <summary>The property names from the entity type down, separated by dots.</summary>
        public string Path
        {
            get;
            init => field = Required(value, nameof(Path));
        } = Required(Path, nameof(Path));
    }

    /// <summary>An expression: SQL, and the columns it reads by their property paths.</summary>
    /// <remarks>
    /// The SQL is one provider's, as its queries spell it. It comes from that provider's factory;
    /// it is not made from text.
    /// </remarks>
    /// <param name="Sql">The expression.</param>
    public sealed record Template(SqlTemplate Sql) : KeyTarget
    {
        /// <summary>The expression.</summary>
        public SqlTemplate Sql
        {
            get;
            init => field = value ?? throw new ArgumentNullException(nameof(Sql));
        } = Sql ?? throw new ArgumentNullException(nameof(Sql));
    }

    /// <summary>
    /// Translates a lambda over the entity type, where it is written. A lambda that names a property
    /// and nothing else is a <see cref="Column" />; anything else the translator accepts is a
    /// <see cref="Template" />.
    /// </summary>
    /// <remarks>
    /// For a provider's package, which passes its own spelling. A consumer writes the lambda into
    /// the provider's factory.
    /// </remarks>
    /// <param name="key">The lambda, with the entity as its one parameter.</param>
    /// <param name="spelling">The spelling of the provider the declaration is for.</param>
    /// <exception cref="NotSupportedException">The lambda holds something the translator has no SQL for.</exception>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public static KeyTarget Of(LambdaExpression key, SqlSpelling spelling)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(spelling);

        // Checked here, where the lambda is written, and SQL when the model is finalized. Which of
        // the two cases it is does not wait: a lambda that names a property and nothing else is a
        // path, and a path needs no binding.
        var unbound = UnboundLambda.Of(key, spelling, isPredicate: false);
#pragma warning disable SK9001 // The kernel's own translator writes placeholders; this is where they become segments.
        return SqlTemplate.FromPlaceholders(unbound.Translate()).SingleColumn is { } path
            ? new Column(path)
            : new Template(SqlTemplate.Unbound(unbound));
#pragma warning restore SK9001
    }

    // A record's `with` goes through init and not through the constructor, so the rule is in both.
    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

/// <summary>
/// The condition of a partial index or a constraint: SQL, and the columns it reads by their
/// property paths.
/// </summary>
/// <remarks>
/// The SQL is one provider's, as its queries spell it. It comes from that provider's factory; it is
/// not made from text.
/// </remarks>
/// <param name="Template">The condition.</param>
public sealed record Predicate(SqlTemplate Template)
{
    /// <summary>The condition.</summary>
    public SqlTemplate Template
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(Template));
    } = Template ?? throw new ArgumentNullException(nameof(Template));

    /// <summary>
    /// Translates a typed condition, where it is written. A comparison that can be NULL is written
    /// as EF Core writes it in a query, so the condition keeps the rows C# keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a provider's package, which passes its own spelling. A consumer writes the condition
    /// into the provider's factory.
    /// </para>
    /// <para>
    /// <typeparamref name="TEntity" /> may be an interface, so that one predicate serves a
    /// convention over every entity type that implements it. Its paths are resolved against each
    /// entity type it is declared on.
    /// </para>
    /// </remarks>
    /// <param name="predicate">The condition.</param>
    /// <param name="spelling">The spelling of the provider the declaration is for.</param>
    /// <typeparam name="TEntity">The entity type, or an interface of it.</typeparam>
    /// <exception cref="NotSupportedException">The lambda holds something the translator has no SQL for.</exception>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public static Predicate Of<TEntity>(Expression<Func<TEntity, bool>> predicate, SqlSpelling spelling)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(spelling);
        return new Predicate(SqlTemplate.Unbound(UnboundLambda.Of(predicate, spelling, isPredicate: true)));
    }

    /// <summary>
    /// Both conditions. Adding one that is already the whole condition, or the last one this
    /// method added, changes nothing, so a convention may run twice.
    /// </summary>
    /// <param name="other">The condition to add.</param>
    public Predicate And(Predicate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Template.Equals(other.Template) || EndsWithConjunct(other.Template)) return this;

#pragma warning disable SK9001 // The parentheses and the AND are the kernel's own text.
        return new Predicate(SqlTemplate.Join(Text("("), Template, Text(") AND ("), other.Template, Text(")")));

        static SqlTemplate Text(string sql) => SqlTemplate.Of([new SqlSegment.Text(sql)]);
#pragma warning restore SK9001
    }

    // Only what this method writes counts: "(…) AND (other)", where the first parenthesis closes
    // right before the AND. 5.x took any text that ends in " AND (other)" for it, and so does
    // "{A} OR {B} AND ({C})", which is A OR (B AND C): adding C to it changed nothing, and the
    // condition stayed wider than the one asked for (#83). The comparison is of the segments,
    // written out so that a column and hand-written SQL are told from the text between them:
    // hand-written SQL that reads ") AND (" is not this method's text.
    private bool EndsWithConjunct(SqlTemplate other)
    {
        // A lambda that is not bound yet is told from another by a number of its own, the same
        // number for the same lambda on both sides.
        List<object> lambdas = [];
        var whole = Flat(Template, lambdas);
        var ending = $" AND ({Flat(other, lambdas)})";
        return whole.EndsWith(ending, StringComparison.Ordinal) && IsOneGroup(whole.AsSpan(0, whole.Length - ending.Length));
    }

    private const char Escape = '\u0001';
    private const char ColumnMark = '\u0002';
    private const char RawMark = '\u0003';
    private const char LambdaMark = '\u0004';

    /// <summary>
    /// The parts in one string: a package's text as it is, and a column, hand-written SQL and a
    /// lambda each between two marks of their kind. A mark that is part of a segment's text is
    /// escaped, so no text can pass for a part of another kind.
    /// </summary>
    private static string Flat(SqlTemplate template, List<object> lambdas)
    {
#pragma warning disable SK9001 // The kernel reads the parts of its own value.
        StringBuilder text = new();
        foreach (var part in template.Parts)
        {
            _ = part switch
            {
                SqlSegment.Text written  => Append(written.Sql),
                SqlSegment.Column column => Append(column.Path, ColumnMark),
                SqlSegment.Raw raw       => Append(raw.Sql, RawMark),
                _                        => Append(Number(part).ToString(System.Globalization.CultureInfo.InvariantCulture), LambdaMark),
            };
        }

        return text.ToString();
#pragma warning restore SK9001

        int Number(object lambda)
        {
            var index = lambdas.IndexOf(lambda);
            if (index >= 0) return index;
            lambdas.Add(lambda);
            return lambdas.Count - 1;
        }

        StringBuilder Append(string content, char? mark = null)
        {
            if (mark is { } opening) text.Append(opening);
            foreach (var ch in content)
            {
                if (ch is Escape or ColumnMark or RawMark or LambdaMark) text.Append(Escape);
                text.Append(ch);
            }

            return mark is { } closing ? text.Append(closing) : text;
        }
    }

    /// <summary>Whether the text is one parenthesised group: its first parenthesis closes at its last character, quoted text aside.</summary>
    private static bool IsOneGroup(ReadOnlySpan<char> text)
    {
        if (text.Length < 2 || text[0] != '(' || text[^1] != ')') return false;

        var depth = 0;
        var inLiteral = false;
        for (var i = 0; i < text.Length; i++)
        {
            // A doubled quote inside a literal toggles twice, which leaves the state where it was.
            if (text[i] == '\'') inLiteral = !inLiteral;
            if (inLiteral) continue;

            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i == text.Length - 1;
        }

        return false;
    }
}
