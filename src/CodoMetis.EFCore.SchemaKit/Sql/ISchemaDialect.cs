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

    /// <summary>
    /// The namespaces the name of one of EF Core's own objects occupies in this database. A declared
    /// object that claims the same name in one of them collides with it.
    /// </summary>
    /// <param name="kind">What the object is.</param>
    /// <param name="table">The table it belongs to; for a table or a sequence, the object itself.</param>
    /// <param name="name">Its name.</param>
    IEnumerable<NameClaim> Claims(SchemaModelObject kind, TableRef table, Identifier name);

    /// <summary>
    /// The schema the database puts an object in when its statement names none, or null when the
    /// dialect does not know it. A name claimed by a table without a schema is compared under it
    /// with one in that schema.
    /// </summary>
    /// <remarks>
    /// A model's own default schema is in the relational model already: a table without a schema
    /// is in the model's default schema when the model sets one, and only otherwise in this one.
    /// The answer is the database's as it is created, not as a session may be set up: where
    /// PostgreSQL's <c>search_path</c> points elsewhere, the database is one the model does not
    /// describe, and its tables are not where the model says they are.
    /// </remarks>
    Identifier? DefaultSchema { get; }
}

/// <summary>The objects of a model that EF Core creates itself and that take a name.</summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public enum SchemaModelObject
{
    /// <summary>A table.</summary>
    Table,

    /// <summary>An index declared with <c>HasIndex</c>.</summary>
    Index,

    /// <summary>A primary key.</summary>
    PrimaryKey,

    /// <summary>A unique constraint: an alternate key.</summary>
    UniqueConstraint,

    /// <summary>A foreign key.</summary>
    ForeignKey,

    /// <summary>A check constraint.</summary>
    CheckConstraint,

    /// <summary>A sequence.</summary>
    Sequence,
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

    /// <summary>
    /// The scopes most databases share: a table and a sequence are named within their schema, an
    /// index within its table, a constraint within its table. A provider whose rule differs
    /// overrides this; PostgreSQL's index is a relation of the schema.
    /// </summary>
    public virtual IEnumerable<NameClaim> Claims(SchemaModelObject kind, TableRef table, Identifier name)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(name);

        return kind switch
        {
            SchemaModelObject.Table or SchemaModelObject.Sequence => [new NameClaim.Relation(table.Schema, name)],
            SchemaModelObject.Index                               => [new NameClaim.TableIndex(table, name)],
            _                                                     => [new NameClaim.Constraint(table, name)],
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Null here: which schema a provider without a package puts an unqualified table in is not
    /// the kernel's to guess. A table without a schema then shares a namespace only with another
    /// table without one.
    /// </remarks>
    public virtual Identifier? DefaultSchema => null;

    [GeneratedRegex("^[a-z_][a-z0-9_$]*$")]
    private static partial Regex BareName();

    [GeneratedRegex(@"^[+\-*/<>=~!@#%^&|`?]{1,63}$")]
    private static partial Regex OperatorToken();
}
