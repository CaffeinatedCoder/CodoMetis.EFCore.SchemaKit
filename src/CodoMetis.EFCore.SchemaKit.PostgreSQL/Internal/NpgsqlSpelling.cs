using System.Globalization;
using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>
/// How Npgsql's query translation spells what a lambda holds, character for character: an index
/// serves only a query whose expression matches it.
/// </summary>
/// <remarks>
/// <para>
/// The tables are 5.x's translator's, with its rules unchanged; each comment that names a failure
/// records one that happened there. A stored template must come out the same, and the parity
/// sweep compares every entry with what Npgsql renders for the same lambda.
/// </para>
/// <para>
/// A construct belongs in a table, not only in a method: the sweep and the analyzer read the tables.
/// </para>
/// </remarks>
internal sealed class NpgsqlSpelling : SqlSpelling
{
    // The characters Npgsql's query translation passes for a parameterless Trim/TrimStart/TrimEnd.
    private const string TrimCharacters = "E' \\t\\n\\r'";

    private NpgsqlSpelling() : base("PostgreSQL") { }

    /// <summary>The one spelling: two lambdas are one declaration only under the same instance.</summary>
    public static NpgsqlSpelling Instance { get; } = new();

    /// <inheritdoc />
    public override IReadOnlyDictionary<MethodInfo, SpelledCall> Methods { get; } = new Dictionary<MethodInfo, SpelledCall>
    {
        [StringMethod(nameof(string.ToLower))] = (target, _) => $"lower({target})",
        [StringMethod(nameof(string.ToUpper))] = (target, _) => $"upper({target})",

        // Spelled as Npgsql's query translation spells them, character for character: an index
        // serves only a query whose expression matches it, and btrim(x) alone trims spaces, so a
        // unique index on it let 'Y\t' and 'Y' coexist where Trim() says equal.
        [StringMethod(nameof(string.Trim))]      = (target, _) => $"btrim({target}, {TrimCharacters})",
        [StringMethod(nameof(string.TrimStart))] = (target, _) => $"ltrim({target}, {TrimCharacters})",
        [StringMethod(nameof(string.TrimEnd))]   = (target, _) => $"rtrim({target}, {TrimCharacters})",

        [StringMethod(nameof(string.Replace), typeof(string), typeof(string))]
            = (target, arguments) => $"replace({target}, {arguments[0].Sql}, {arguments[1].Sql})",

        // .NET substrings are 0-based, SQL's are 1-based. substring, not substr: they are different
        // functions to the planner, and Npgsql's queries call substring.
        [StringMethod(nameof(string.Substring), typeof(int))]
            = (target, arguments) => $"substring({target}, {SubstringStart(arguments[0])})",
        [StringMethod(nameof(string.Substring), typeof(int), typeof(int))]
            = (target, arguments) => $"substring({target}, {SubstringStart(arguments[0])}, {arguments[1].Sql})",
    };

    /// <inheritdoc />
    public override IReadOnlyDictionary<MemberInfo, Func<string, string>> Members { get; } = new Dictionary<MemberInfo, Func<string, string>>
    {
        [typeof(string).GetProperty(nameof(string.Length))!] = target => $"length({target})",
    };

    /// <summary>
    /// The types with a spelling that needs no column: a DateTime, Guid or enum is IFormattable
    /// too, and came out as bare text that is not SQL.
    /// </summary>
    public override IReadOnlySet<Type> LiteralTypes { get; } = new HashSet<Type>
    {
        typeof(string), typeof(bool), typeof(char),
        typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte),
        typeof(uint), typeof(ulong), typeof(ushort), typeof(decimal), typeof(double), typeof(float),
    };

    /// <inheritdoc />
    public override string Literal(object value) => value switch
    {
        // A brace needs no escape: inside quotes the resolver takes nothing for a placeholder.
        string s => "'" + s.Replace("'", "''") + "'",
        bool b   => b ? "TRUE" : "FALSE",
        char c   => Literal(c.ToString()),
        // Invariant culture: a de-DE thread culture must never turn 1.5 into '1,5'.
        IFormattable number when LiteralTypes.Contains(value.GetType())
            => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"A value of type '{value.GetType().Name}' is none of {nameof(LiteralTypes)}.", nameof(value)),
    };

    // PostgreSQL's || returns NULL for a NULL operand. An operand that can be null is wrapped as
    // Npgsql's queries wrap it; one that is no string is cast to text first, as COALESCE needs
    // one type.
    /// <inheritdoc />
    public override string ConcatenationOperand(string operand, Type type, bool canBeNull)
        => !canBeNull           ? operand
         : type == typeof(string) ? $"coalesce({operand}, '')"
         :                          $"coalesce({operand}::text, '')";

    /// <inheritdoc />
    public override string Concatenation(string left, string right) => $"({left} || {right})";

    /// <inheritdoc />
    public override string Coalesce(string left, string right) => $"coalesce({left}, {right})";

    // A boolean is a condition by itself.
    /// <inheritdoc />
    public override string Condition(string value, bool negated) => negated ? $"NOT ({value})" : value;

    /// <inheritdoc />
    public override string UnlessTrue(string comparison) => $"CASE WHEN {comparison} THEN FALSE ELSE TRUE END";

    private static MethodInfo StringMethod(string name, params Type[] parameters)
        => typeof(string).GetMethod(name, parameters)
        ?? throw new InvalidOperationException($"string.{name}({string.Join(", ", parameters.Select(p => p.Name))}) does not exist.");

    // A start known at the declaration, a literal or a captured variable, is folded as EF Core
    // folds it: substring("Code", 2), not substring("Code", (1 + 1)).
    private static string SubstringStart(SpelledArgument start)
        => start is { IsValue: true, Value: int index }
               ? (index + 1).ToString(CultureInfo.InvariantCulture)
               : $"({start.Sql} + 1)";
}
