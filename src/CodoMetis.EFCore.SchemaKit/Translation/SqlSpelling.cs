using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// An argument of a call in a lambda, as a <see cref="SqlSpelling" /> is handed it.
/// </summary>
/// <param name="Sql">The argument as SQL, with each column as <c>{Path}</c>.</param>
/// <param name="IsValue">Whether the argument reads nothing from the entity, so that its value is known where the lambda is written.</param>
/// <param name="Value">The value, when <paramref name="IsValue" /> is true. It can be null.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public readonly record struct SpelledArgument(string Sql, bool IsValue, object? Value);

/// <summary>Writes a call from its target and its arguments, each already SQL.</summary>
/// <param name="target">What the method is called on, as SQL, with each column as <c>{Path}</c>.</param>
/// <param name="arguments">The arguments, in the method's order.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public delegate string SpelledCall(string target, IReadOnlyList<SpelledArgument> arguments);

/// <summary>
/// How one provider's queries spell what a lambda holds: its functions, its literals, and the few
/// forms that are not the same SQL on every database. A lambda is translated for one provider,
/// never for all of them: an index serves a query only when its expression is written as that
/// provider's query writes it.
/// </summary>
/// <remarks>
/// <para>
/// A provider's package derives its spelling from this class and passes it to
/// <see cref="KeyTarget.Of" /> and <see cref="Predicate.Of{TEntity}" />. A consumer never does: it
/// writes a lambda into the provider's own factory, which knows whose it is. That is why the two
/// methods carry the contract's mark and the values they return do not. The kernel holds no
/// spelling: a provider without one has no value to pass, so a lambda cannot be stored for it as
/// another provider's SQL.
/// </para>
/// <para>
/// The kernel walks the lambda and keeps what is C#'s: which conversions keep a value, where a
/// comparison can be null and how that is compensated, the comparison and boolean operators,
/// which are the same on every database. Everything it writes besides those it asks of the
/// spelling. SQL goes in and out as text in which a column is <c>{Path}</c>, the grammar of
/// <see cref="SqlTemplate.FromPlaceholders" />.
/// </para>
/// <para>
/// Three things the kernel takes for granted of every spelling. What <see cref="Methods" /> and
/// <see cref="Members" /> write is null exactly when one of its inputs is: the null compensation
/// tests the inputs' columns, as EF Core does. What <see cref="Concatenation" /> writes from the
/// operands <see cref="ConcatenationOperand" /> wrote is never null. And one spelling is one
/// object: two lambdas are the same declaration only when their spellings are the same instance.
/// </para>
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public abstract class SqlSpelling
{
    /// <summary>A spelling of this provider's queries.</summary>
    /// <param name="provider">The provider, for a message.</param>
    protected SqlSpelling(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Provider = provider;
    }

    /// <summary>The provider whose queries are spelled this way, for a message.</summary>
    public string Provider { get; }

    /// <summary>
    /// The methods a lambda may call on what it reads from the entity, by exact overload. A method
    /// that is not here is refused where the lambda is written.
    /// </summary>
    public abstract IReadOnlyDictionary<MethodInfo, SpelledCall> Methods { get; }

    /// <summary>
    /// The members a lambda may read of a value, such as a string's length, each written from its
    /// target. <c>Nullable&lt;T&gt;.Value</c> and <c>HasValue</c> are the kernel's and need no entry.
    /// </summary>
    public abstract IReadOnlyDictionary<MemberInfo, Func<string, string>> Members { get; }

    /// <summary>
    /// The types of the values <see cref="Literal" /> writes without knowing the column they are
    /// compared with. A value of another type is written only where it is compared with a property,
    /// by that property's mapping, when the model is finalized.
    /// </summary>
    public abstract IReadOnlySet<Type> LiteralTypes { get; }

    /// <summary>A value of one of <see cref="LiteralTypes" /> as a literal. Null is the kernel's to write.</summary>
    /// <param name="value">The value, never null.</param>
    public abstract string Literal(object value);

    /// <summary>
    /// An operand of a string concatenation. C# concatenates null as an empty string, so an operand
    /// that can be null is written as one that cannot, the way the provider's queries write it.
    /// </summary>
    /// <param name="operand">The operand as SQL.</param>
    /// <param name="type">The operand's C# type.</param>
    /// <param name="canBeNull">Whether the operand can be null.</param>
    public abstract string ConcatenationOperand(string operand, Type type, bool canBeNull);

    /// <summary>The concatenation of two strings, each written by <see cref="ConcatenationOperand" />.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public abstract string Concatenation(string left, string right);

    /// <summary>C#'s <c>left ?? right</c>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public abstract string Coalesce(string left, string right);

    /// <summary>
    /// A boolean value where a condition stands, or its negation: a boolean column, or a literal.
    /// On a database whose boolean is a condition by itself, the value as it is; on one where it
    /// is a number, its comparison with what true or false is.
    /// </summary>
    /// <param name="value">The boolean column or literal as SQL.</param>
    /// <param name="negated">Whether the condition is that the value is false.</param>
    public abstract string Condition(string value, bool negated);

    /// <summary>
    /// The condition that holds where <paramref name="comparison" /> does not, a row for which it
    /// is null included: C#'s negation of a comparison with an operand that can be null.
    /// </summary>
    /// <param name="comparison">The comparison as SQL, such as <c>{Rank} &lt; 5</c>.</param>
    public abstract string UnlessTrue(string comparison);

    /// <inheritdoc />
    public override string ToString() => Provider;
}
