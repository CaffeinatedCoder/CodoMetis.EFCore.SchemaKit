using System.Globalization;
using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;

/// <summary>
/// How SQLite's query translation spells what a lambda holds, for the constructs below and no
/// others: a spelling written against the public contract alone (issue 184).
/// <c>SpellingParityTests</c> compares each with EF Core's own query for the same lambda.
/// </summary>
internal sealed class ProbeSqliteSpelling : SqlSpelling
{
    private ProbeSqliteSpelling() : base("SQLite") { }

    public static ProbeSqliteSpelling Instance { get; } = new();

    public override IReadOnlyDictionary<MethodInfo, SpelledCall> Methods { get; } = new Dictionary<MethodInfo, SpelledCall>
    {
        [typeof(string).GetMethod(nameof(string.ToLower), [])!] = (target, _) => $"lower({target})",
        [typeof(string).GetMethod(nameof(string.ToUpper), [])!] = (target, _) => $"upper({target})",
        [typeof(string).GetMethod(nameof(string.Trim), [])!]    = (target, _) => $"trim({target})",
    };

    public override IReadOnlyDictionary<MemberInfo, Func<string, string>> Members { get; } = new Dictionary<MemberInfo, Func<string, string>>
    {
        [typeof(string).GetProperty(nameof(string.Length))!] = target => $"length({target})",
    };

    public override IReadOnlySet<Type> LiteralTypes { get; } = new HashSet<Type> { typeof(string), typeof(bool), typeof(int) };

    public override string Literal(object value) => value switch
    {
        string text => "'" + text.Replace("'", "''") + "'",
        bool flag   => flag ? "1" : "0",
        int number  => number.ToString(CultureInfo.InvariantCulture),
        _           => throw new ArgumentException(value.GetType().Name, nameof(value)),
    };

    public override string ConcatenationOperand(string operand, Type type, bool canBeNull)
        => canBeNull ? $"COALESCE({operand}, '')" : operand;

    public override string Concatenation(string left, string right) => $"{left} || {right}";

    public override string Coalesce(string left, string right) => $"COALESCE({left}, {right})";

    public override string Condition(string value, bool negated) => negated ? $"NOT ({value})" : value;

    public override string UnlessTrue(string comparison) => $"CASE WHEN {comparison} THEN 0 ELSE 1 END";
}
