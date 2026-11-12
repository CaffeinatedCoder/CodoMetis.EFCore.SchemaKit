using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// The provider's spellings: how a name is checked and delimited, how a statement ends, how a string
/// becomes a literal. It is the only place that turns text into the values a statement is built from.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public interface ISchemaDialect
{
    /// <summary>
    /// Checks a name and wraps it. Throws, naming it and the limit, when the database would refuse it
    /// or, worse, shorten it without a word.
    /// </summary>
    Identifier Identifier(string name);

    /// <summary>Wraps a name the database defines (an operator class, a collation, a function).</summary>
    SqlName Name(string name);

    /// <summary>Checks an operator against the dialect's operator grammar and wraps it.</summary>
    SqlOperator Operator(string token);

    /// <summary>A string literal, escaped as the provider escapes it. For messages and values, never for names.</summary>
    SqlFragment Literal(string value);

    /// <summary>The identifier as it is written into a statement.</summary>
    string Delimit(Identifier identifier);

    /// <summary>The name as it is written into a statement: bare when it can be, delimited otherwise.</summary>
    string Delimit(SqlName name);

    /// <summary>What ends a statement. <see cref="SchemaSql" /> appends it to every one.</summary>
    string StatementTerminator { get; }

    /// <summary>
    /// Writes what makes a database extension present, for an object about to be created that
    /// lists it as a <see cref="Dependency.Extension" />. The kernel calls it once per extension
    /// and migration, ahead of every other statement.
    /// </summary>
    /// <remarks>
    /// Whether anything has to be written is the provider's to say: a model may install the
    /// extension through the provider's own API, or state that it is installed some other way.
    /// </remarks>
    /// <param name="extension">The extension.</param>
    /// <param name="model">The model the migration leads to.</param>
    /// <param name="sql">Where the statement goes, if there is one.</param>
    void EnsureExtension(SqlName extension, IReadOnlyModel model, SchemaSql sql);
}

/// <summary>What <see cref="SchemaDialect" /> is built from. A record, so adding a member breaks no derived dialect.</summary>
/// <param name="SqlGenerationHelper">The provider's own helper: SchemaKit delimits and terminates as the provider does.</param>
/// <param name="TypeMappingSource">The provider's type mappings, for literals.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record SchemaDialectDependencies(
    ISqlGenerationHelper SqlGenerationHelper,
    IRelationalTypeMappingSource TypeMappingSource);

/// <summary>
/// The dialect of a provider SchemaKit has no package for, and the base of the ones it has: it asks
/// the provider's own services wherever they answer.
/// </summary>
/// <param name="dependencies">The provider's services.</param>
/// <param name="maxIdentifierLength">
/// The longest identifier the database keeps, as the model states it
/// (<c>Relational:MaxIdentifierLength</c>); zero for no limit.
/// </param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public partial class SchemaDialect(SchemaDialectDependencies dependencies, int maxIdentifierLength = 0) : ISchemaDialect
{
    /// <summary>The provider's services.</summary>
    protected SchemaDialectDependencies Dependencies { get; } = dependencies;

    /// <summary>The longest identifier the database keeps, in the unit <see cref="Measure" /> counts; zero for no limit.</summary>
    protected virtual int MaxIdentifierLength { get; } = maxIdentifierLength;

    /// <summary>
    /// The length the database counts. Characters here; PostgreSQL counts bytes, so its dialect
    /// overrides this.
    /// </summary>
    protected virtual int Measure(string name) => name.Length;

    /// <inheritdoc />
    public virtual Identifier Identifier(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (MaxIdentifierLength > 0 && Measure(name) is var length && length > MaxIdentifierLength)
            throw new InvalidOperationException(SchemaKitStrings.IdentifierTooLong(name, length, MaxIdentifierLength));

        return new Identifier(name);
    }

    /// <inheritdoc />
    public virtual SqlName Name(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new SqlName(name);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The grammar is PostgreSQL's, the widest of the providers': one to 63 of
    /// <c>+ - * / &lt; &gt; = ~ ! @ # % ^ &amp; | ` ?</c>, with no comment start in it.
    /// </remarks>
    public virtual SqlOperator Operator(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (!OperatorToken().IsMatch(token) || token.Contains("--", StringComparison.Ordinal) || token.Contains("/*", StringComparison.Ordinal))
            throw new ArgumentException(SchemaKitStrings.NotAnOperator(token), nameof(token));

        return new SqlOperator(token);
    }

    /// <inheritdoc />
    public virtual SqlFragment Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var mapping = Dependencies.TypeMappingSource.FindMapping(typeof(string))
                      ?? throw new InvalidOperationException(SchemaKitStrings.NoStringMapping);
        return new SqlFragment(mapping.GenerateSqlLiteral(value));
    }

    /// <inheritdoc />
    public virtual string Delimit(Identifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return Dependencies.SqlGenerationHelper.DelimitIdentifier(identifier.Name);
    }

    /// <inheritdoc />
    public virtual string Delimit(SqlName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return BareName().IsMatch(name.Name) ? name.Name : Dependencies.SqlGenerationHelper.DelimitIdentifier(name.Name);
    }

    /// <inheritdoc />
    public virtual string StatementTerminator => Dependencies.SqlGenerationHelper.StatementTerminator;

    /// <inheritdoc />
    /// <remarks>
    /// Refused here: what installs an extension is a provider's own statement, and an object whose
    /// dependency nothing satisfies must not be created as if something had.
    /// </remarks>
    public virtual void EnsureExtension(SqlName extension, IReadOnlyModel model, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(extension);
        throw new InvalidOperationException(SchemaKitStrings.NoExtensions(extension.Name));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex BareName();

    [GeneratedRegex(@"^[+\-*/<>=~!@#%^&|`?]{1,63}$")]
    private static partial Regex OperatorToken();
}
