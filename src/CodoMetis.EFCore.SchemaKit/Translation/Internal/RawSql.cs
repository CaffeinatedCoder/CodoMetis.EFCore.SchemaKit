using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit.Translation.Internal;

/// <summary>
/// <see cref="Sql.Raw{T}" /> in a lambda: how the call is told, and how its SQL travels through the
/// translator's text to the place that makes segments of it.
/// </summary>
/// <remarks>
/// The translator writes one string, which the spelling's methods compose. SQL written by hand has
/// to come out of it as its own kind of segment, so it stands between two characters that are no
/// characters (U+FDD0 and U+FDD1, which Unicode keeps out of text for good). They are looked for
/// outside single-quoted literals only: a value that holds one is inside a literal.
/// </remarks>
internal static partial class RawSql
{
    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)?\$")]
    private static partial Regex DollarQuote();

    private const char Open = '﷐';
    private const char Close = '﷑';

    private static readonly MethodInfo Marker = typeof(Sql).GetMethod(nameof(Sql.Raw))!;

    /// <summary>Whether the node is a call of the marker.</summary>
    public static bool Is(Expression? node)
        => node is MethodCallExpression { Method: { IsGenericMethod: true } method } && method.GetGenericMethodDefinition() == Marker;

    /// <summary>Whether what the marker stands for can be NULL, which only its type says.</summary>
    public static bool CanBeNull(Expression marker) => !marker.Type.IsValueType || Nullable.GetUnderlyingType(marker.Type) is not null;

    /// <summary>The SQL of a marker call, between the marks.</summary>
    /// <param name="marker">The call.</param>
    /// <param name="readsAParameter">Whether a node reads a parameter of a lambda, or holds a marker.</param>
    public static string Mark(MethodCallExpression marker, Func<Expression, bool> readsAParameter)
    {
        var argument = marker.Arguments[0];

        // The SQL is a value: a constant, or what a variable held where the lambda was written.
        if (readsAParameter(argument))
            throw new NotSupportedException(SchemaKitStrings.RawFromTheEntity(marker));

        var sql = argument is ConstantExpression constant ? constant.Value as string : Expression.Lambda(argument).Compile().DynamicInvoke() as string;

        if (string.IsNullOrWhiteSpace(sql))
            throw new NotSupportedException(SchemaKitStrings.RawWithoutSql(marker));
        if (sql.AsSpan().ContainsAny(Open, Close))
            throw new NotSupportedException(SchemaKitStrings.RawHoldsAMark(marker));

        // A placeholder is looked for outside single-quoted literals, and a quote ends a literal.
        // Two spellings of a literal break that: a backslash before a quote, which in an escape
        // string does not end it, and a dollar quote, which is no quote at all. A property's
        // name in braces inside one was replaced by its column, and after the first the scan was
        // turned round for the rest of the text (issue 224). Which of them a database has is its
        // own grammar, so text that has braces and either is not read at all.
        if (sql.Contains('{', StringComparison.Ordinal) && (sql.Contains(@"\'", StringComparison.Ordinal) || DollarQuote().IsMatch(sql)))
            throw new NotSupportedException(SchemaKitStrings.RawWithALiteralNotRead(marker));

        return Open + sql + Close;
    }

    /// <summary>
    /// The translator's text as a template: what stands between the marks as SQL written by hand,
    /// everything else as the package's own text, and each <c>{Path}</c> of either as a column.
    /// </summary>
    /// <param name="translated">The text.</param>
    /// <param name="byHand">Told each path that SQL written by hand names: nothing else has checked it.</param>
    public static SqlTemplate Template(string translated, Action<string>? byHand = null)
    {
#pragma warning disable SK9001 // The kernel's own translator writes placeholders; this is where they become segments.
        List<SqlTemplate> parts = [];
        StringBuilder text = new(translated.Length);
        var inLiteral = false;

        for (var i = 0; i < translated.Length; i++)
        {
            var ch = translated[i];

            // A doubled quote inside a literal toggles twice, which leaves the state where it was.
            if (ch == '\'') inLiteral = !inLiteral;

            if (ch == Open && !inLiteral && translated.IndexOf(Close, i + 1) is var end and > 0)
            {
                Written();
                var raw = SqlTemplate.Raw(translated[(i + 1)..end]);
                if (byHand is not null)
                    foreach (var column in raw.Segments.OfType<SqlSegment.Column>())
                        byHand(column.Path);

                parts.Add(raw);
                i = end;
                continue;
            }

            text.Append(ch);
        }

        Written();
        return SqlTemplate.Join([.. parts]);

        void Written()
        {
            if (text.Length == 0) return;

            // Between two marks there may be a blank and nothing else, which is text all the same.
            var written = text.ToString();
            parts.Add(string.IsNullOrWhiteSpace(written) ? SqlTemplate.Of([new SqlSegment.Text(written)]) : SqlTemplate.FromPlaceholders(written));
            text.Clear();
        }
#pragma warning restore SK9001
    }
}
