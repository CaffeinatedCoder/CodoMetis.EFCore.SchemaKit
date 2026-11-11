using System.Diagnostics.CodeAnalysis;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// One schema object a model declares, as a value. Every declaration of every contributor implements
/// it; the kernel stores, reads and routes declarations through it and never switches over a
/// contributor's types.
/// </summary>
/// <remarks>
/// <para>
/// A declaration is stored as one annotation on its entity type, <c>CodoMetis:{Owner}:{hash}</c>.
/// The hash is over <see cref="Kind" /> and <see cref="Name" /> for a named declaration, and over
/// <see cref="Kind" /> and everything <see cref="Write" /> writes outside
/// <see cref="SchemaJsonWriter.Operational" /> for one without a name. Two declarations with the
/// same key cannot both be declared: the second is refused, never taken for a replacement.
/// </para>
/// <para>
/// Owner, kind, property names and the meaning of each written value end up in consumers' model
/// snapshots and stay there. Treat them as a file format.
/// </para>
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public interface ISchemaObject
{
    /// <summary>The contributor that renders the declaration, and the middle part of its annotation key.</summary>
    string Owner { get; }

    /// <summary>The case of the contributor's union this is, as it is written: <c>$kind</c>.</summary>
    string Kind { get; }

    /// <summary>The explicit name, or null when the name is derived from what the declaration resolves to.</summary>
    string? Name { get; }

    /// <summary>
    /// The resolution epoch the declaration was written under: <c>r</c>. It selects how the kernel
    /// turns the declaration's paths into columns and SQL, so that a snapshot keeps resolving as it
    /// did when it was scaffolded.
    /// </summary>
    int Epoch { get; }

    /// <summary>
    /// How the database compares names of this kind. It decides whether two explicit names are one.
    /// </summary>
    SchemaNameComparison NameComparison => SchemaNameComparison.Ordinal;

    /// <summary>
    /// Writes everything the declaration holds besides <see cref="Kind" />, <see cref="Name" /> and
    /// <see cref="Epoch" />, which the kernel writes. Always in the same order, and what does not
    /// change the object in the database through <see cref="SchemaJsonWriter.Operational" />.
    /// </summary>
    void Write(SchemaJsonWriter writer);
}

/// <summary>
/// A contributor's declaration union. The static members let the read model rebuild a declaration
/// from its annotation without a service provider, which <c>OnModelCreating</c> and a convention do
/// not have.
/// </summary>
/// <typeparam name="TSelf">The union itself.</typeparam>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public interface ISchemaDeclaration<TSelf> : ISchemaObject
    where TSelf : class, ISchemaDeclaration<TSelf>
{
    /// <summary>The owner of every declaration of this union.</summary>
    static abstract string OwnerId { get; }

    /// <summary>
    /// Rebuilds a declaration from what <see cref="ISchemaObject.Write" /> wrote. It has to read
    /// every property that was written: one left unread is refused, since it would be an option
    /// the declaration silently lost.
    /// </summary>
    /// <param name="header">What the kernel wrote: the kind, the name and the epoch.</param>
    /// <param name="reader">The declaration's own properties.</param>
    static abstract TSelf Read(SchemaDeclarationHeader header, SchemaJsonReader reader);
}

/// <summary>The part of a stored declaration the kernel writes itself.</summary>
/// <param name="Kind">The case of the union: <c>$kind</c>.</param>
/// <param name="Name">The explicit name, or null.</param>
/// <param name="Epoch">The resolution epoch: <c>r</c>.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record SchemaDeclarationHeader(string Kind, string? Name, int Epoch);

/// <summary>How a database compares the names of a kind of object.</summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public enum SchemaNameComparison
{
    /// <summary>Character by character: PostgreSQL, where SchemaKit delimits every name.</summary>
    Ordinal,

    /// <summary>Without regard to case: SQL Server under its default collations.</summary>
    IgnoreCase,
}
