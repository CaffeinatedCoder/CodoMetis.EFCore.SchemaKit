using System.Diagnostics.CodeAnalysis;

namespace CodoMetis.EFCore.SchemaKit;

// The values a statement is built from. None has a public constructor that takes text and none
// converts from string: an identifier comes from the dialect, which checks it, and a fragment from
// the statement builder or the resolver. That is what makes "nothing reaches the DDL unquoted" a
// property of the types and not of a review.

/// <summary>
/// A name the dialect accepted: its length was checked when it was made, and it is delimited when it
/// is written. Made by <see cref="ISchemaDialect.Identifier" /> only.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record Identifier
{
    internal Identifier(string name) => Name = name;

    /// <summary>The name as declared, not delimited.</summary>
    public string Name { get; }

    /// <summary>The bare name, for messages. Statements take the value itself, never this text.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A table, with its schema. <see cref="Schema" /> is null for the provider's default schema.
/// </summary>
/// <param name="Schema">The schema, or null for the default one.</param>
/// <param name="Name">The table's name.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record TableRef(Identifier? Schema, Identifier Name)
{
    /// <summary>
    /// The qualified name of an object that lives in this table's schema, such as a PostgreSQL index.
    /// </summary>
    /// <remarks>
    /// An unqualified <c>DROP INDEX "ix"</c> on PostgreSQL resolves through <c>search_path</c>: with an
    /// index of that name in two schemas it removes whichever comes first, not the one meant. Whether
    /// a kind of object takes a qualified name is the database's rule (a constraint and a trigger are
    /// named within their table and take none), so it is the contributor that calls this.
    /// </remarks>
    public QualifiedName Qualify(Identifier name) => new(Schema, name);

    /// <inheritdoc />
    public override string ToString() => Schema is null ? Name.Name : $"{Schema.Name}.{Name.Name}";
}

/// <summary>The name of an object that is named within a schema, with that schema.</summary>
/// <param name="Schema">The schema, or null for the default one.</param>
/// <param name="Name">The object's name.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record QualifiedName(Identifier? Schema, Identifier Name)
{
    /// <inheritdoc />
    public override string ToString() => Schema is null ? Name.Name : $"{Schema.Name}.{Name.Name}";
}

/// <summary>
/// A name the database defines, not the model: an operator class, a collation, a function. Written
/// bare when it can be (<c>jsonb_path_ops</c>), delimited otherwise (<c>"C"</c>, <c>"en-US-x-icu"</c>).
/// Made by <see cref="ISchemaDialect.Name" /> only.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record SqlName
{
    internal SqlName(string name) => Name = name;

    /// <summary>The name as given, not delimited.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// An operator, checked against the dialect's operator grammar when it was made: it cannot carry
/// anything but operator characters into a statement. Made by <see cref="ISchemaDialect.Operator" /> only.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record SqlOperator
{
    internal SqlOperator(string token) => Token = token;

    /// <summary>The operator's characters.</summary>
    public string Token { get; }

    /// <inheritdoc />
    public override string ToString() => Token;
}

/// <summary>
/// A piece of SQL SchemaKit produced: a resolved column, a resolved expression or predicate, a
/// literal, or what a contributor composed through <see cref="SchemaSql.Fragment" />. There is no
/// public constructor and no conversion from <see cref="string" />.
/// </summary>
/// <remarks>Fragments compare by their text, which is what makes a descriptor's equality its diff identity.</remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record SqlFragment
{
    internal SqlFragment(string text) => Text = text;

    internal string Text { get; }

    /// <summary>A fragment with no text, for an optional clause that is absent.</summary>
    public static SqlFragment Empty { get; } = new("");

    /// <summary>Whether this is the empty fragment.</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>The SQL text, for messages and tests.</summary>
    public override string ToString() => Text;
}

/// <summary>
/// Where a schema object lives, which decides what its name must be unique among and what it is
/// dropped with.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record SchemaScope
{
    /// <summary>An object of one table: an index, a constraint, a trigger, a policy.</summary>
    /// <param name="Ref">The table.</param>
    public sealed record Table(TableRef Ref) : SchemaScope;

    /// <summary>An object of a schema that belongs to no table: a domain, a function.</summary>
    /// <param name="Name">The schema, or null for the default one.</param>
    public sealed record Schema(Identifier? Name) : SchemaScope;

    /// <summary>An object of the database as a whole: an extension.</summary>
    public sealed record Database : SchemaScope;
}
