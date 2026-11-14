using System.Diagnostics.CodeAnalysis;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// How one provider's queries spell what a lambda holds: its functions, its operators, its
/// literals. A lambda is translated for one provider, never for all of them: an index serves a
/// query only when its expression is written as that provider's query writes it.
/// </summary>
/// <remarks>
/// <para>
/// A provider's package passes its spelling to <see cref="KeyTarget.Of" /> and
/// <see cref="Predicate.Of{TEntity}" />. A consumer never does: it writes a lambda into the
/// provider's own factory, which knows whose it is. That is why the two methods carry the
/// contract's mark and the values they return do not.
/// </para>
/// <para>
/// A provider for which no spelling exists has no value to pass, so a lambda cannot be translated
/// for it by accident and stored as another provider's SQL.
/// </para>
/// <para>
/// The kernel holds one spelling for now, PostgreSQL's, with the tables it is made of. They move
/// to the provider's package with the binder (issue 114), and SQL Server's come with its package
/// (issue 29). Until then this type names a provider, which nothing else in the kernel's surface
/// does.
/// </para>
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed class SqlSpelling
{
    private SqlSpelling(string provider) => Provider = provider;

    /// <summary>The provider whose queries are spelled this way, for a message.</summary>
    public string Provider { get; }

    /// <summary>As Npgsql's query translation spells it, character for character.</summary>
    public static SqlSpelling PostgreSql { get; } = new("PostgreSQL");

    /// <inheritdoc />
    public override string ToString() => Provider;
}
