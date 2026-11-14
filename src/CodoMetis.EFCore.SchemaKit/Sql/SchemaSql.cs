using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// Builds the statements of one schema object. A statement is an interpolated string whose literal
/// parts are the contributor's own code and whose holes take only values that went through the
/// dialect: there is no hole for a <see cref="string" />, so a name or a path cannot reach the DDL
/// unquoted.
/// </summary>
/// <remarks>
/// EF Core's <c>FromSql($"…")</c> is the precedent. There a hole becomes a parameter; here it becomes
/// a delimited identifier or resolved SQL.
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed partial class SchemaSql
{
    private readonly List<SqlOperation> _operations = [];

    internal SchemaSql(ISchemaDialect dialect) => Dialect = dialect;

    /// <summary>The dialect the statements are written in.</summary>
    public ISchemaDialect Dialect { get; }

    /// <summary>The statements so far, as the operations a migration carries.</summary>
    internal IReadOnlyList<SqlOperation> Operations => _operations;

    /// <summary>Adds another builder's statements, in their order: a contributor's answer the planner keeps.</summary>
    internal void Append(SchemaSql other) => _operations.AddRange(other._operations);

    /// <summary>
    /// Adds one statement. The provider's statement terminator is appended; do not write one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF Core runs a statement of this kind exactly as it is. One without a terminator is accepted
    /// by <c>dotnet ef database update</c>, a bundle and <c>Migrate()</c>, and breaks every generated
    /// script, which is why the terminator is not the contributor's to remember.
    /// </para>
    /// <para>
    /// A <c>DROP … IF EXISTS</c> is refused. A drop that cannot fail hides an object that is already
    /// gone, which means the database and the snapshot disagree, and that is the thing to hear about.
    /// </para>
    /// </remarks>
    /// <param name="sql">The statement, as an interpolated string.</param>
    /// <param name="suppressTransaction">True for a statement that cannot run in a transaction, such as <c>CREATE INDEX CONCURRENTLY</c>.</param>
    public void Statement([InterpolatedStringHandlerArgument("")] ref SqlHandler sql, bool suppressTransaction = false)
    {
        var text = sql.ToStringAndClear().Trim();
        if (text.Length == 0)
            throw new InvalidOperationException(SchemaKitStrings.EmptyStatement);
        if (text.EndsWith(Dialect.StatementTerminator, StringComparison.Ordinal))
            throw new InvalidOperationException(SchemaKitStrings.StatementHasTerminator(text, Dialect.StatementTerminator));
        if (DropIfExists().IsMatch(text))
            throw new InvalidOperationException(SchemaKitStrings.DropIfExists(text));

        _operations.Add(new SqlOperation { Sql = text + Dialect.StatementTerminator, SuppressTransaction = suppressTransaction });
    }

    /// <summary>Composes a piece of SQL from the same kinds of value a statement takes.</summary>
    public SqlFragment Fragment([InterpolatedStringHandlerArgument("")] ref SqlHandler sql)
        => new(sql.ToStringAndClear(), sql.Dependencies(), nameToken: null);

    /// <summary>The fragments, separated by commas.</summary>
    public SqlFragment List(IEnumerable<SqlFragment> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        return new SqlFragment(
            string.Join(", ", list.Select(i => i.Text)),
            [.. list.SelectMany(i => i.Dependencies).Distinct()],
            nameToken: null);
    }

    /// <summary>The identifiers, delimited and separated by commas.</summary>
    public SqlFragment List(IEnumerable<Identifier> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new SqlFragment(string.Join(", ", items.Select(Dialect.Delimit)));
    }

    /// <summary>A string literal, escaped by the provider. For messages and values, never for names.</summary>
    public SqlFragment Literal(string value) => Dialect.Literal(value);

    [GeneratedRegex(@"\bDROP\b[^;]*\bIF\s+EXISTS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DropIfExists();
}

/// <summary>
/// The interpolated-string handler behind <see cref="SchemaSql" />: one <c>AppendFormatted</c> per
/// kind of value that is safe to write, and none for <see cref="string" />.
/// </summary>
/// <remarks>
/// A hole with a <see cref="string" />, an <see cref="object" />, a format or an alignment does not
/// compile. That absence is the feature; adding an overload "for convenience" removes it.
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
[InterpolatedStringHandler]
public ref struct SqlHandler
{
    private readonly StringBuilder _text;
    private readonly ISchemaDialect _dialect;
    private readonly List<Dependency> _dependencies;

    /// <summary>Called by the compiler.</summary>
    public SqlHandler(int literalLength, int formattedCount, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        _dialect = sql.Dialect;
        _text = new StringBuilder(literalLength + formattedCount * 16);
        _dependencies = [];
    }

    /// <summary>The contributor's own text.</summary>
    public readonly void AppendLiteral(string literal) => _text.Append(literal);

    /// <summary>A name, delimited.</summary>
    public readonly void AppendFormatted(Identifier identifier) => _text.Append(_dialect.Delimit(identifier));

    /// <summary>A table, with its schema when it has one.</summary>
    public readonly void AppendFormatted(TableRef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Qualified(table.Schema, table.Name);
    }

    /// <summary>A schema-scoped object's name, with its schema when it has one.</summary>
    public readonly void AppendFormatted(QualifiedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Qualified(name.Schema, name.Name);
    }

    /// <summary>A name the database defines: bare when it can be, delimited otherwise.</summary>
    public readonly void AppendFormatted(SqlName name) => _text.Append(_dialect.Delimit(name));

    /// <summary>An operator.</summary>
    public readonly void AppendFormatted(SqlOperator op)
    {
        ArgumentNullException.ThrowIfNull(op);
        _text.Append(op.Token);
    }

    /// <summary>SQL that SchemaKit produced.</summary>
    public readonly void AppendFormatted(SqlFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        _text.Append(fragment.Text);

        // What the parts read, the whole reads: a dependency lost in composition is an operation of
        // EF Core's that can break the object unnoticed.
        foreach (var dependency in fragment.Dependencies)
        {
            if (!_dependencies.Contains(dependency)) _dependencies.Add(dependency);
        }
    }

    /// <summary>A whole number, in the invariant culture.</summary>
    public readonly void AppendFormatted(int value) => _text.Append(value.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc cref="AppendFormatted(int)" />
    public readonly void AppendFormatted(long value) => _text.Append(value.ToString(CultureInfo.InvariantCulture));

    internal readonly ValueList<Dependency> Dependencies() => [.. _dependencies];

    internal readonly string ToStringAndClear()
    {
        var text = _text.ToString();
        _text.Clear();
        return text;
    }

    private readonly void Qualified(Identifier? schema, Identifier name)
    {
        if (schema is not null)
            _text.Append(_dialect.Delimit(schema)).Append('.');
        _text.Append(_dialect.Delimit(name));
    }
}
