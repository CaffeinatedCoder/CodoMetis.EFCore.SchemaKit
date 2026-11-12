using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit;

// What a declaration is made of before anything is resolved: the two values every provider's
// declarations share. They are what a consumer writes and what the read model hands back, so
// unlike the contributor contract they are not marked experimental.

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

    /// <summary>An expression: SQL in which each <c>{Path}</c> stands for that path's column.</summary>
    /// <param name="Sql">The SQL, with its placeholders.</param>
    public sealed record Template(string Sql) : KeyTarget
    {
        /// <summary>The SQL, with its placeholders.</summary>
        public string Sql
        {
            get;
            init => field = Required(value, nameof(Sql));
        } = Required(Sql, nameof(Sql));
    }

    /// <summary>
    /// Translates a lambda over the entity type, where it is written. A lambda that names a property
    /// and nothing else is a <see cref="Column" />; anything else the translator accepts is a
    /// <see cref="Template" />.
    /// </summary>
    /// <param name="key">The lambda, with the entity as its one parameter.</param>
    /// <exception cref="NotSupportedException">The lambda holds something the translator has no SQL for.</exception>
    public static KeyTarget Of(LambdaExpression key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var template = LambdaTranslator.Expression(key);

        // The translator writes a property as one placeholder and nothing around it. Anything it
        // wrote besides is SQL, and SQL is an expression however short.
        return template.Length > 2 && template[0] == '{' && template[^1] == '}' && template.AsSpan(1, template.Length - 2).IndexOfAny('{', '}') < 0
            ? new Column(template[1..^1])
            : new Template(template);
    }

    // A record's `with` goes through init and not through the constructor, so the rule is in both.
    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

/// <summary>
/// The condition of a partial index or a constraint, as it is stored: SQL in which each
/// <c>{Path}</c> stands for that path's column.
/// </summary>
/// <param name="Template">The SQL, with its placeholders.</param>
public sealed record Predicate(string Template)
{
    /// <summary>The SQL, with its placeholders.</summary>
    public string Template
    {
        get;
        init => field = Required(value);
    } = Required(Template);

    // A record's `with` goes through init and not through the constructor, so the rule is in both.
    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Template));
        return value;
    }

    /// <summary>
    /// Translates a typed condition, where it is written. A comparison that can be NULL is written
    /// as EF Core writes it in a query, so the condition keeps the rows C# keeps.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TEntity" /> may be an interface, so that one predicate serves a
    /// convention over every entity type that implements it. Its paths are resolved against each
    /// entity type it is declared on.
    /// </remarks>
    /// <param name="predicate">The condition.</param>
    /// <typeparam name="TEntity">The entity type, or an interface of it.</typeparam>
    /// <exception cref="NotSupportedException">The lambda holds something the translator has no SQL for.</exception>
    public static Predicate Of<TEntity>(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new Predicate(LambdaTranslator.Predicate(predicate));
    }

    /// <summary>
    /// Both conditions. Adding one that is already the whole condition, or its last conjunct,
    /// changes nothing, so a convention may run twice.
    /// </summary>
    /// <param name="other">The condition to add.</param>
    public Predicate And(Predicate other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Template == other.Template || Template.EndsWith($" AND ({other.Template})", StringComparison.Ordinal)
            ? this
            : new Predicate($"({Template}) AND ({other.Template})");
    }
}
