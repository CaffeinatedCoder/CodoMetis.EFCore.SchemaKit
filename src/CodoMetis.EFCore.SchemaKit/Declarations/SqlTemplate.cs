using System.Diagnostics.CodeAnalysis;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// One part of the SQL a declaration holds. The kinds are kept apart because they are read
/// differently: a column is resolved against a table, and of the two kinds of text only one is
/// known to name no column.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record SqlSegment
{
    /// <summary>
    /// Text a provider's package wrote, such as the SQL of a translated lambda between its columns.
    /// It names no column: every column the package reads is a <see cref="Column" />.
    /// </summary>
    /// <param name="Sql">The text.</param>
    public sealed record Text(string Sql) : SqlSegment
    {
        /// <summary>The text.</summary>
        public string Sql
        {
            get;
            init => field = NotEmpty(value, nameof(Sql));
        } = NotEmpty(Sql, nameof(Sql));
    }

    /// <summary>A property of the entity type, by its path: <c>Name</c>, <c>Address.City</c>.</summary>
    /// <param name="Path">The property names from the entity type down, separated by dots.</param>
    public sealed record Column(string Path) : SqlSegment
    {
        /// <summary>The property names from the entity type down, separated by dots.</summary>
        public string Path
        {
            get;
            init => field = PropertyPath(value, nameof(Path));
        } = PropertyPath(Path, nameof(Path));
    }

    /// <summary>
    /// SQL somebody wrote by hand. A column it names directly cannot be seen, so the object it is
    /// part of is taken to read every column of its table, whatever else it reads.
    /// </summary>
    /// <param name="Sql">The text.</param>
    public sealed record Raw(string Sql) : SqlSegment
    {
        /// <summary>The text.</summary>
        public string Sql
        {
            get;
            init => field = NotEmpty(value, nameof(Sql));
        } = NotEmpty(Sql, nameof(Sql));
    }

    // A record's `with` goes through init and not through the constructor, so each rule is in both.
    private static string NotEmpty(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        return value;
    }

    private static string PropertyPath(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        return ModelSchemaResolver.IsPropertyPath(value)
            ? value
            : throw new ArgumentException(SchemaKitStrings.SegmentNotAPropertyPath(value), name);
    }
}

/// <summary>
/// The SQL of an expression or a condition as a declaration holds it: text, and the columns it
/// reads by their property paths. It is stored as it is and resolved against a table when a
/// migration is planned; nothing looks for a column inside its text.
/// </summary>
/// <remarks>
/// <para>
/// A consumer gets one from a provider's factory and hands it on. What it is made of is the
/// contract's, for the package that writes it and the contributor that resolves it.
/// </para>
/// <para>
/// A template made from a lambda is not bound until its model is finalized: it holds the lambda,
/// and becomes segments then (<see cref="IsBound" />). It compares, and is added to another, in
/// either state; read from a finalized model or a snapshot it is always bound.
/// </para>
/// </remarks>
public sealed class SqlTemplate : IEquatable<SqlTemplate>
{
#pragma warning disable SK9001 // The segments are the contract's; the template that holds them is a consumer's value.
    // A segment, or a lambda that is not bound yet. Neighbouring text of one kind is one part.
    private readonly ValueList<object> _parts;

    private SqlTemplate(ValueList<object> parts) => _parts = parts;

    /// <summary>
    /// Whether the template is segments throughout. One made from a lambda is not, from the call
    /// that wrote it until its model is finalized.
    /// </summary>
    public bool IsBound => _parts.All(part => part is SqlSegment);

    /// <summary>The segments, in their order. No two neighbours are text of the same kind, and none is empty.</summary>
    /// <exception cref="InvalidOperationException">The template holds a lambda that is not bound yet.</exception>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public ValueList<SqlSegment> Segments
        => IsBound ? [.. _parts.Cast<SqlSegment>()] : throw new InvalidOperationException(SchemaKitStrings.TemplateNotBound(ToString()));

    /// <summary>A template of these segments. Neighbouring text of one kind becomes one segment.</summary>
    /// <param name="segments">The segments, at least one.</param>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public static SqlTemplate Of(IEnumerable<SqlSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return OfParts(segments, nameof(segments));
    }

    /// <summary>A template that is a lambda, to be bound when its model is finalized.</summary>
    internal static SqlTemplate Unbound(UnboundLambda lambda) => new([lambda]);

    /// <summary>The parts of several templates in a row, bound or not: how a condition is added to another.</summary>
    internal static SqlTemplate Join(params ReadOnlySpan<SqlTemplate> templates)
    {
        List<object> parts = [];
        foreach (var template in templates) parts.AddRange(template._parts);
        return OfParts(parts, nameof(templates));
    }

    /// <summary>The template with each lambda replaced by what <paramref name="bind" /> makes of it.</summary>
    internal SqlTemplate Bind(Func<UnboundLambda, SqlTemplate> bind)
        => IsBound ? this : OfParts(_parts.SelectMany(part => part is UnboundLambda lambda ? bind(lambda)._parts : [part]), nameof(bind));

    /// <summary>The parts, for the kernel's own comparison of two templates in either state.</summary>
    internal ValueList<object> Parts => _parts;

    private static SqlTemplate OfParts(IEnumerable<object> parts, string name)
    {
        List<object> merged = [];
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part, name);
            merged.Add((merged.Count > 0 ? merged[^1] : null, part) switch
            {
                (SqlSegment.Text last, SqlSegment.Text next) => Replace(new SqlSegment.Text(last.Sql + next.Sql)),
                (SqlSegment.Raw last, SqlSegment.Raw next)   => Replace(new SqlSegment.Raw(last.Sql + next.Sql)),
                _                                            => part,
            });
        }

        return merged.Count > 0
            ? new SqlTemplate([.. merged])
            : throw new ArgumentException(SchemaKitStrings.TemplateWithoutSegments, name);

        object Replace(SqlSegment joined)
        {
            merged.RemoveAt(merged.Count - 1);
            return joined;
        }
    }

    /// <summary>
    /// A template from SQL a provider's package wrote, in which each <c>{Path}</c> stands for that
    /// path's column. Everything else is text that names no column.
    /// </summary>
    /// <remarks>
    /// A placeholder is recognised outside single-quoted literals only, and only when its content
    /// is a property path; any other text, braces included, stays as written. For SQL a consumer
    /// wrote, <see cref="Raw" /> is the method: nothing can tell that it names no column.
    /// </remarks>
    /// <param name="template">The SQL, with its placeholders.</param>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public static SqlTemplate FromPlaceholders(string template) => Parse(template, static text => new SqlSegment.Text(text));

    /// <summary>
    /// A template from SQL somebody wrote by hand, in which each <c>{Path}</c> stands for that
    /// path's column. The object it becomes part of is taken to read every column of its table.
    /// </summary>
    /// <param name="template">The SQL, with its placeholders.</param>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public static SqlTemplate Raw(string template) => Parse(template, static text => new SqlSegment.Raw(text));

    /// <summary>Whether the template is one column and nothing else, and then its path.</summary>
    internal string? SingleColumn => _parts is [SqlSegment.Column column] ? column.Path : null;

    /// <summary>
    /// The template in one line, for a message: a column as <c>{Path}</c>, and a lambda that is
    /// not bound yet as C# prints it. It is not the stored form and not SQL, and nothing reads it back.
    /// </summary>
    public override string ToString()
    {
        StringBuilder text = new();
        foreach (var part in _parts)
        {
            _ = part switch
            {
                SqlSegment.Text written  => text.Append(written.Sql),
                SqlSegment.Column column => text.Append('{').Append(column.Path).Append('}'),
                SqlSegment.Raw raw       => text.Append(raw.Sql),
                _                        => text.Append(part),
            };
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public bool Equals(SqlTemplate? other) => other is not null && _parts.Equals(other._parts);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SqlTemplate);

    /// <inheritdoc />
    public override int GetHashCode() => _parts.GetHashCode();

    private static SqlTemplate Parse(string template, Func<string, SqlSegment> written)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        List<SqlSegment> segments = [];
        StringBuilder text = new(template.Length);
        var inLiteral = false;

        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];

            // A doubled quote inside a literal toggles twice, which leaves the state where it was.
            if (ch == '\'') inLiteral = !inLiteral;

            if (ch == '{' && !inLiteral)
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i + 1 && ModelSchemaResolver.IsPropertyPath(template.AsSpan(i + 1, end - i - 1)))
                {
                    if (text.Length > 0) segments.Add(written(text.ToString()));
                    text.Clear();
                    segments.Add(new SqlSegment.Column(template[(i + 1)..end]));
                    i = end;
                    continue;
                }
            }

            text.Append(ch);
        }

        if (text.Length > 0) segments.Add(written(text.ToString()));
        return Of(segments);
    }
#pragma warning restore SK9001
}
