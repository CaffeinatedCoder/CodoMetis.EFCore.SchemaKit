using System.Globalization;
using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;

/// <summary>
/// How SQL Server's query translation spells what a lambda holds, for the constructs below and no
/// others: a spelling written against the public contract alone (issue 184).
/// <c>SpellingParityTests</c> compares each with EF Core's own query for the same lambda.
/// </summary>
internal sealed class ProbeSqlServerSpelling : SqlSpelling
{
    private ProbeSqlServerSpelling() : base("SQL Server") { }

    public static ProbeSqlServerSpelling Instance { get; } = new();

    public override IReadOnlyDictionary<MethodInfo, SpelledCall> Methods { get; } = new Dictionary<MethodInfo, SpelledCall>
    {
        [typeof(string).GetMethod(nameof(string.ToLower), [])!] = (target, _) => $"LOWER({target})",
        [typeof(string).GetMethod(nameof(string.ToUpper), [])!] = (target, _) => $"UPPER({target})",
        [typeof(string).GetMethod(nameof(string.Trim), [])!]    = (target, _) => $"LTRIM(RTRIM({target}))",
        [typeof(string).GetMethod(nameof(string.Replace), [typeof(string), typeof(string)])!]
            = (target, arguments) => $"REPLACE({target}, {arguments[0].Sql}, {arguments[1].Sql})",
        [typeof(string).GetMethod(nameof(string.Substring), [typeof(int), typeof(int)])!]
            = (target, arguments) => $"SUBSTRING({target}, {Start(arguments[0])}, {arguments[1].Sql})",
    };

    public override IReadOnlyDictionary<MemberInfo, Func<string, string>> Members { get; } = new Dictionary<MemberInfo, Func<string, string>>
    {
        [typeof(string).GetProperty(nameof(string.Length))!] = target => $"CAST(LEN({target}) AS int)",
    };

    public override IReadOnlySet<Type> LiteralTypes { get; } = new HashSet<Type> { typeof(string), typeof(bool), typeof(int) };

    public override string Literal(object value) => value switch
    {
        string text => "N'" + text.Replace("'", "''") + "'",
        bool flag   => flag ? "CAST(1 AS bit)" : "CAST(0 AS bit)",
        int number  => number.ToString(CultureInfo.InvariantCulture),
        _           => throw new ArgumentException(value.GetType().Name, nameof(value)),
    };

    public override string ConcatenationOperand(string operand, Type type, bool canBeNull)
        => canBeNull ? $"ISNULL({operand}, N'')" : operand;

    public override string Concatenation(string left, string right) => $"{left} + {right}";

    public override string Coalesce(string left, string right) => $"ISNULL({left}, {right})";

    // SQL Server has no boolean that is a condition: a bit is compared with what true or false is.
    public override string Condition(string value, bool negated) => $"{value} = {Literal(!negated)}";

    public override string UnlessTrue(string comparison) => $"CASE WHEN {comparison} THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END = CAST(1 AS bit)";

    private static string Start(SpelledArgument start)
        => $"{start.Sql} + 1";
}
