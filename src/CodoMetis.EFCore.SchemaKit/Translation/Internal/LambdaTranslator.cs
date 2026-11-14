using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit.Translation.Internal;

/// <summary>
/// Translates a deliberately small subset of C# expressions into a SQL template with
/// <c>{Property.Path}</c> placeholders, where the lambda is written. Property paths stay symbolic:
/// the resolver turns them into columns for each side of a diff.
/// </summary>
/// <remarks>
/// <para>
/// Ported from 5.x's <c>NpgsqlLinqIndexTranslator</c> with its rules unchanged; each comment below
/// that names a failure records one that happened there. The spellings are PostgreSQL's, written
/// as Npgsql's query translation writes them: an index serves only a query whose expression
/// matches it. SQL Server's come with its package.
/// </para>
/// <para>
/// Supported in an expression: property paths (through complex properties), <c>ToLower</c>,
/// <c>ToUpper</c>, <c>Trim</c>, <c>TrimStart</c>, <c>TrimEnd</c> (parameterless), <c>Substring</c>,
/// <c>Replace</c>, <c>string.Length</c>, concatenation (<c>+</c>), null coalescing (<c>??</c>),
/// and constants: a captured variable is evaluated and written as a literal. Anything else throws
/// <see cref="NotSupportedException" /> at the declaration.
/// </para>
/// <para>
/// The output is in the resolver's grammar: a placeholder is <c>{Path}</c> outside a single-quoted
/// literal. 5.x escaped a brace inside a literal as <c>{{</c> for a second grammar it had; here a
/// literal is written as it is, and the resolver leaves what is inside quotes alone.
/// </para>
/// </remarks>
internal static class LambdaTranslator
{
    // The characters Npgsql's query translation passes for a parameterless Trim/TrimStart/TrimEnd.
    private const string TrimCharacters = "E' \\t\\n\\r'";

    // ── The accepted constructs, as data ──
    // The dispatch below reads these tables, and so do the parity sweep, which compares every
    // construct with what Npgsql's query translation renders for the same lambda, and the analyzer.
    // A construct added here is swept with no edit there; one added to the dispatch without a table
    // entry is not, so keep new constructs in the tables.

    /// <summary>Renders a call from its rendered target, its arguments and the operand visitor.</summary>
    internal delegate string CallRenderer(string target, IReadOnlyList<System.Linq.Expressions.Expression> arguments, Func<System.Linq.Expressions.Expression, string> visit);

    /// <summary>The string methods a typed expression or filter operand may call, by exact overload.</summary>
    internal static readonly IReadOnlyDictionary<MethodInfo, CallRenderer> StringMethods = new Dictionary<MethodInfo, CallRenderer>
    {
        [StringMethod(nameof(string.ToLower))] = (target, _, _) => $"lower({target})",
        [StringMethod(nameof(string.ToUpper))] = (target, _, _) => $"upper({target})",

        // Spelled as Npgsql's query translation spells them, character for character: an index
        // serves only a query whose expression matches it, and btrim(x) alone trims spaces, so a
        // unique index on it let 'Y\t' and 'Y' coexist where Trim() says equal.
        [StringMethod(nameof(string.Trim))]      = (target, _, _) => $"btrim({target}, {TrimCharacters})",
        [StringMethod(nameof(string.TrimStart))] = (target, _, _) => $"ltrim({target}, {TrimCharacters})",
        [StringMethod(nameof(string.TrimEnd))]   = (target, _, _) => $"rtrim({target}, {TrimCharacters})",

        [StringMethod(nameof(string.Replace), typeof(string), typeof(string))]
            = (target, arguments, visit) => $"replace({target}, {visit(arguments[0])}, {visit(arguments[1])})",

        // .NET substrings are 0-based, SQL's are 1-based. substring, not substr: they are different
        // functions to the planner, and Npgsql's queries call substring.
        [StringMethod(nameof(string.Substring), typeof(int))]
            = (target, arguments, visit) => $"substring({target}, {SubstringStart(arguments[0], visit)})",
        [StringMethod(nameof(string.Substring), typeof(int), typeof(int))]
            = (target, arguments, visit) => $"substring({target}, {SubstringStart(arguments[0], visit)}, {visit(arguments[1])})",
    };

    /// <summary>
    /// The members rendered from their target, keyed by declaring type (a generic one by its
    /// definition) and name.
    /// </summary>
    internal static readonly IReadOnlyDictionary<(Type DeclaringType, string Name), Func<string, string>> ValueMembers
        = new Dictionary<(Type, string), Func<string, string>>
        {
            [(typeof(string), nameof(string.Length))] = target => $"length({target})",

            // Nullable<T>.Value is the column itself, as in EF Core's queries; HasValue is its null
            // check (a filter writes it bare, and pushes a negation into IS NULL).
            [(typeof(Nullable<>), nameof(Nullable<int>.Value))]    = target => target,
            [(typeof(Nullable<>), nameof(Nullable<int>.HasValue))] = target => $"({target} IS NOT NULL)",
        };

    /// <summary>The operators an operand may use besides calls and members: concatenation of strings and <c>??</c>.</summary>
    internal static readonly IReadOnlySet<ExpressionType> ValueOperators
        = new HashSet<ExpressionType> { ExpressionType.Add, ExpressionType.Coalesce };

    /// <summary>The comparisons a filter may make. Equality carries EF Core's null compensation.</summary>
    internal static readonly IReadOnlyDictionary<ExpressionType, string> ComparisonOperators = new Dictionary<ExpressionType, string>
    {
        [ExpressionType.Equal]              = "=",
        [ExpressionType.NotEqual]           = "<>",
        [ExpressionType.LessThan]           = "<",
        [ExpressionType.LessThanOrEqual]    = "<=",
        [ExpressionType.GreaterThan]        = ">",
        [ExpressionType.GreaterThanOrEqual] = ">=",
    };

    /// <summary>The boolean structure a filter may have.</summary>
    internal static readonly IReadOnlySet<ExpressionType> LogicalOperators
        = new HashSet<ExpressionType> { ExpressionType.AndAlso, ExpressionType.OrElse, ExpressionType.Not };

    /// <summary>
    /// The types a captured value or constant may have: the ones with a portable SQL spelling. A
    /// DateTime, Guid or enum is IFormattable too, and came out as bare text that is not SQL.
    /// </summary>
    internal static readonly IReadOnlySet<Type> LiteralTypes = new HashSet<Type>
    {
        typeof(string), typeof(bool), typeof(char),
        typeof(int), typeof(long), typeof(short), typeof(byte), typeof(sbyte),
        typeof(uint), typeof(ulong), typeof(ushort), typeof(decimal), typeof(double), typeof(float),
    };

    /// <summary>
    /// The conversions between two different types that are left out of a template, source to
    /// target: the ones that keep every value and that every database compares as C# does. The
    /// compiler writes them without being asked (<c>x.Short == 5</c> arrives as
    /// <c>Convert(x.Short, Int32) == 5</c>), so refusing one would refuse the comparison. A
    /// <c>Nullable&lt;T&gt;</c> on either side is looked through, and a conversion to a type the
    /// source already is (itself, <c>object</c>, an interface) needs no entry:
    /// <see cref="IsSilentConversion" /> is the whole rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not here, and so refused where the lambda is written: everything else. A conversion that can
    /// change the value was left out all the same, and <c>(int)x.Amount == 2</c> was stored as
    /// <c>{Amount} = 2</c>, which is false for 2.5 where the lambda is true. That holds for an
    /// integer to a floating point too (<c>(float)x.Count == 16777216f</c> is true for 16777217),
    /// and what a database does with the two sides of such a comparison is its own rule, not C#'s.
    /// An integer to a decimal keeps every value, but how the two then compare is the database's
    /// typing of a decimal; it is refused with the rest until a provider says how it writes one.
    /// </para>
    /// <para>
    /// <c>char</c> to <c>int</c> is no widening the database knows of. It is here because C#
    /// compares chars as ints, so every comparison of a char column carries it, and the comparison
    /// puts the constant back to the char it was written as (<see cref="VisitOperands" />).
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlySet<(Type From, Type To)> SilentConversions = new HashSet<(Type, Type)>
    {
        (typeof(sbyte), typeof(short)), (typeof(sbyte), typeof(int)), (typeof(sbyte), typeof(long)),
        (typeof(short), typeof(int)), (typeof(short), typeof(long)),
        (typeof(int), typeof(long)),

        (typeof(byte), typeof(short)), (typeof(byte), typeof(ushort)), (typeof(byte), typeof(int)),
        (typeof(byte), typeof(uint)), (typeof(byte), typeof(long)), (typeof(byte), typeof(ulong)),
        (typeof(ushort), typeof(int)), (typeof(ushort), typeof(uint)), (typeof(ushort), typeof(long)), (typeof(ushort), typeof(ulong)),
        (typeof(uint), typeof(long)), (typeof(uint), typeof(ulong)),

        (typeof(char), typeof(int)),
    };

    /// <summary>
    /// Whether a conversion of what a lambda reads from the entity is left out of the template: one
    /// to the type itself or its <c>Nullable&lt;T&gt;</c> (and back, which is <c>.Value</c>), to a
    /// type the source already is, or one of <see cref="SilentConversions" />. Decided from the two
    /// types alone.
    /// </summary>
    internal static bool IsSilentConversion(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;

        return source == target || to.IsAssignableFrom(from) || target.IsAssignableFrom(source) || SilentConversions.Contains((source, target));
    }

    private static MethodInfo StringMethod(string name, params Type[] parameters)
        => typeof(string).GetMethod(name, parameters)
        ?? throw new InvalidOperationException($"string.{name}({string.Join(", ", parameters.Select(p => p.Name))}) does not exist.");

    // A start known at the declaration, a literal or a captured variable, is folded as EF Core
    // folds it: substring("Code", 2), not substring("Code", (1 + 1)).
    private static string SubstringStart(System.Linq.Expressions.Expression start, Func<System.Linq.Expressions.Expression, string> visit)
        => !new ParameterFinder(parameter: null).Search(start) && Evaluate(start) is int startIndex
               ? (startIndex + 1).ToString(CultureInfo.InvariantCulture)
               : $"({visit(start)} + 1)";

    private static bool TryGetValueMember(MemberExpression member, out Func<string, string> render)
    {
        var declaringType = member.Member.DeclaringType!;
        if (declaringType.IsConstructedGenericType)
            declaringType = declaringType.GetGenericTypeDefinition();

        return ValueMembers.TryGetValue((declaringType, member.Member.Name), out render!);
    }

    /// <summary>Translates a lambda into an expression template: a key of an index, an element of a constraint.</summary>
    public static string Expression(LambdaExpression lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        var root = Root(lambda);
        new ConversionCheck(root).Visit(lambda.Body);
        return Visit(lambda.Body, root);
    }

    // The entity being configured is the one parameter. A lambda over two has no single table.
    private static ParameterExpression Root(LambdaExpression lambda)
        => lambda.Parameters.Count == 1 ? lambda.Parameters[0] : throw new NotSupportedException(SchemaKitStrings.LambdaTakesOneParameter(lambda));

    /// <summary>
    /// Translates a boolean lambda into a predicate template: comparisons (<c>==</c> and
    /// <c>!=</c> against null become <c>IS [NOT] NULL</c>), <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>,
    /// boolean members, and on the operands everything <see cref="Expression" /> supports.
    /// </summary>
    public static string Predicate(LambdaExpression lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        var root = Root(lambda);
        new ConversionCheck(root).Visit(lambda.Body);
        return VisitPredicate(lambda.Body, root, negated: false);
    }

    // The dispatch below takes a conversion off wherever it meets one, in the operand of a
    // comparison, in a null test, in a concatenation, and writes what is under it. That is right
    // only for a conversion that keeps the value, so every conversion of what the lambda reads from
    // the entity is held against the table first, in one pass over the whole lambda: a place that
    // strips one cannot be the place that forgot to ask. A conversion of a value alone (a captured
    // variable, a constant) is not asked about: C# does it, and the result is written.
    private sealed class ConversionCheck(ParameterExpression root) : ExpressionVisitor
    {
        public override System.Linq.Expressions.Expression? Visit(System.Linq.Expressions.Expression? node)
            => node is null || !DependsOnParameter(node, root) ? node : base.Visit(node);

        // A comparison of an enum keeps its own refusal: the compiler converts the enum to its
        // number first, and the reason that says why is the enum's, not the conversion's.
        protected override System.Linq.Expressions.Expression VisitBinary(BinaryExpression node)
        {
            if (ComparisonOperators.ContainsKey(node.NodeType))
            {
                RefuseEnum(node.Left);
                RefuseEnum(node.Right);
            }

            // `x.Score ?? x.Price` converts its left side in a lambda of its own, over a parameter
            // that is not the entity: it reads the entity all the same.
            if (node.Conversion is { } conversion)
                new ConversionCheck(conversion.Parameters[0]).Visit(conversion.Body);

            return base.VisitBinary(node);
        }

        protected override System.Linq.Expressions.Expression VisitUnary(UnaryExpression node)
            => node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked && !IsSilentConversion(node.Operand.Type, node.Type)
                   ? throw new NotSupportedException(SchemaKitStrings.ConversionNotTranslated(node, node.Operand.Type, node.Type))
                   : base.VisitUnary(node);
    }

    // C# compares with two-valued logic: `x.Kind != "a"` is true for a null Kind. SQL's `<>` is
    // NULL there, and a partial index or constraint filter that is NULL leaves the row out, so a
    // unique index "among rows that are not federated" silently stopped covering rows whose Kind
    // is null. Comparisons with an operand that can be null are therefore written the way EF Core
    // writes the same lambda in a query (`"Kind" <> 'a' OR "Kind" IS NULL`), which keeps the index
    // usable for that query too, and a negation over such a comparison is pushed down to it rather
    // than wrapped around a value that can be NULL. Operands that cannot be null keep the plain
    // forms, so their filters, and the indexes already built from them, do not change.
    private static string VisitPredicate(System.Linq.Expressions.Expression node, ParameterExpression root, bool negated)
    {
        switch (node)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                return VisitPredicate(convert.Operand, root, negated);

            case UnaryExpression { NodeType: ExpressionType.Not } not when LogicalOperators.Contains(not.NodeType):
                return ComparesNullable(not.Operand, root) || IsHasValue(not.Operand, root)
                           ? VisitPredicate(not.Operand, root, !negated)
                           : Negate($"NOT ({VisitPredicate(not.Operand, root, negated: false)})", negated);

            case BinaryExpression logical when LogicalOperators.Contains(logical.NodeType):
            {
                // De Morgan when a negation is being pushed through: NOT (a AND b) = NOT a OR NOT b.
                var and = logical.NodeType == ExpressionType.AndAlso ^ negated;
                return $"({VisitPredicate(logical.Left, root, negated)} {(and ? "AND" : "OR")} {VisitPredicate(logical.Right, root, negated)})";
            }

            case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality
                when ComparisonOperators.ContainsKey(equality.NodeType):
            {
                var notEqual = equality.NodeType == ExpressionType.NotEqual ^ negated;

                // A captured variable that holds null is a null comparison too; inlined, it read `= NULL`.
                if (IsNullValue(equality.Right, root))
                    return NullTest(equality.Left, root, isNull: !notEqual);

                if (IsNullValue(equality.Left, root))
                    return NullTest(equality.Right, root, isNull: !notEqual);

                var (left, right) = VisitOperands(equality, root);
                var leftNull      = NullCheck(equality.Left, root, isNull: true);
                var rightNull     = NullCheck(equality.Right, root, isNull: true);

                // EF Core's own null compensation for the same comparisons, testing the operands'
                // nullable columns as EF Core does: lower("Name") is null exactly when "Name" is.
                return (notEqual, leftNull, rightNull) switch
                {
                    (false, { } l, { } r) => $"({left} = {right} OR ({l} AND {r}))",
                    (false, _, _)         => $"{left} = {right}",
                    (true, { } l, { } r)  => $"(({left} <> {right} OR {l} OR {r}) AND ({NullCheck(equality.Left, root, isNull: false)} OR {NullCheck(equality.Right, root, isNull: false)}))",
                    (true, { } l, null)   => $"({left} <> {right} OR {l})",
                    (true, null, { } r)   => $"({left} <> {right} OR {r})",
                    (true, null, null)    => $"{left} <> {right}",
                };
            }

            case BinaryExpression comparison when ComparisonOperators.ContainsKey(comparison.NodeType):
            {
                var (left, right) = VisitOperands(comparison, root);
                var op            = ComparisonOperators[comparison.NodeType];

                if (!negated)
                    return $"{left} {op} {right}";

                // Negated: C#'s lifted comparison is false for a null, so its negation is true there,
                // which the complement alone would make NULL. EF Core writes that as a CASE.
                if (CanBeNull(comparison.Left, root) || CanBeNull(comparison.Right, root))
                    return $"CASE WHEN {left} {op} {right} THEN FALSE ELSE TRUE END";

                var complement = comparison.NodeType switch
                {
                    ExpressionType.LessThan        => ExpressionType.GreaterThanOrEqual,
                    ExpressionType.LessThanOrEqual => ExpressionType.GreaterThan,
                    ExpressionType.GreaterThan     => ExpressionType.LessThanOrEqual,
                    _                              => ExpressionType.LessThan,
                };

                return $"{left} {ComparisonOperators[complement]} {right}";
            }

            case MemberExpression hasValue when IsHasValue(hasValue, root):
                return $"{Visit(hasValue.Expression!, root)} IS {(negated ? "" : "NOT ")}NULL";

            // A boolean column, or a bool? column's Value, stands on its own.
            case MemberExpression member when member.Type == typeof(bool) && DependsOnParameter(member, root):
                return Negate(Visit(member, root), negated);
        }

        // A captured flag: bake it in.
        if (node.Type == typeof(bool) && !DependsOnParameter(node, root))
            return Negate(Literal(Evaluate(node)), negated);

        throw new NotSupportedException(SchemaKitStrings.PredicateNotTranslated(node));
    }

    // Enum values never reach SQL: how one is stored depends on the property's value conversion,
    // which the translator cannot see, so `Status == Status.Active` would compare against 0 on a
    // text column and fail at apply time. Refuse it here, at the declaration.
    private static string VisitOperand(System.Linq.Expressions.Expression node, ParameterExpression root)
    {
        RefuseEnum(node);
        return Visit(node, root);
    }

    private static void RefuseEnum(System.Linq.Expressions.Expression node)
    {
        var stripped = node;
        while (stripped is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            stripped = convert.Operand;

        if ((Nullable.GetUnderlyingType(stripped.Type) ?? stripped.Type).IsEnum)
            throw new NotSupportedException(SchemaKitStrings.EnumNotTranslated(node));
    }

    // C# compares chars as ints: x.Grade == 'A' arrives as Convert(x.Grade) == 65, and the constant
    // rendered as 65 met a character column, which PostgreSQL refuses at apply. The constant goes
    // back to the char it was written as, as EF Core renders it.
    private static (string Left, string Right) VisitOperands(BinaryExpression comparison, ParameterExpression root)
    {
        var left  = VisitOperand(comparison.Left, root);
        var right = VisitOperand(comparison.Right, root);

        if (IsConvertedChar(comparison.Left) && CharCode(comparison.Right, root) is { } rightChar)
            right = Literal(rightChar);
        else if (IsConvertedChar(comparison.Right) && CharCode(comparison.Left, root) is { } leftChar)
            left = Literal(leftChar);

        return (left, right);
    }

    private static bool IsConvertedChar(System.Linq.Expressions.Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert } convert
        && (Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type) == typeof(char);

    private static char? CharCode(System.Linq.Expressions.Expression node, ParameterExpression root)
        => !DependsOnParameter(node, root) && Evaluate(node) is int code ? (char)code : null;

    private static bool IsHasValue(System.Linq.Expressions.Expression node, ParameterExpression root)
        => node is MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } target }
        && Nullable.GetUnderlyingType(target.Type) is not null
        && DependsOnParameter(target, root);

    private static string Negate(string predicate, bool negated)
        => negated ? $"NOT ({predicate})" : predicate;

    // A literal null or a captured value that evaluates to null.
    private static bool IsNullValue(System.Linq.Expressions.Expression node, ParameterExpression root)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        return node is ConstantExpression { Value: null }
            || (!DependsOnParameter(node, root) && Evaluate(node) is null);
    }

    // Whether a comparison under `node` has an operand that can be null, i.e. whether wrapping it in
    // NOT (…) could leave NULL where C# has true. Null checks themselves never produce NULL.
    private static bool ComparesNullable(System.Linq.Expressions.Expression node, ParameterExpression root) => node switch
    {
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.Not } unary
            => ComparesNullable(unary.Operand, root),
        BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical
            => ComparesNullable(logical.Left, root) || ComparesNullable(logical.Right, root),
        BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } equality
            when IsNullValue(equality.Left, root) || IsNullValue(equality.Right, root)
            => false,
        BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan
                                    or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan
                                    or ExpressionType.GreaterThanOrEqual } comparison
            => CanBeNull(comparison.Left, root) || CanBeNull(comparison.Right, root),
        _ => false,
    };

    // `operand IS [NOT] NULL` as EF Core writes it: over the operand's nullable columns rather than
    // the operand itself where the two are null together, else over the operand.
    private static string NullTest(System.Linq.Expressions.Expression operand, ParameterExpression root, bool isNull)
        => NullCheck(operand, root, isNull) ?? $"{VisitOperand(operand, root)} IS {(isNull ? "" : "NOT ")}NULL";

    // The test for an operand being null (or not), written over the columns it is null through, or
    // null when it cannot be null. A function the translator accepts is strict: lower(x) is null
    // exactly when x is, so its test is its nullable inputs' tests ORed (ANDed for IS NOT NULL). A
    // coalesce is null when all its inputs are, a concatenation never (its operands are coalesced),
    // HasValue never.
    private static string? NullCheck(System.Linq.Expressions.Expression node, ParameterExpression root, bool isNull)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        if (!CanBeNull(node, root))
            return null;

        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce:
            {
                // Null only when every input is; an input that cannot be null makes it never null.
                var left  = NullCheck(coalesce.Left, root, isNull);
                var right = NullCheck(coalesce.Right, root, isNull);
                return left is null || right is null ? null : Combine([left, right], all: isNull);
            }

            case MemberExpression { Expression: { } target } member
                when DependsOnParameter(target, root) && TryGetValueMember(member, out _):
                return NullCheck(target, root, isNull);

            case MethodCallExpression { Object: { } target } call when StringMethods.ContainsKey(call.Method):
                return Combine([NullCheck(target, root, isNull), .. call.Arguments.Select(a => NullCheck(a, root, isNull))], all: !isNull);
        }

        return $"{Visit(node, root)} IS {(isNull ? "" : "NOT ")}NULL";
    }

    // ANDs (all) or ORs the tests of the inputs that can be null; with one, it stands alone.
    private static string? Combine(IReadOnlyList<string?> checks, bool all)
    {
        var present = checks.OfType<string>().ToList();
        return present.Count switch
        {
            0 => null,
            1 => present[0],
            _ => "(" + string.Join(all ? " AND " : " OR ", present) + ")",
        };
    }

    // Whether an operand can be NULL in SQL. A captured value is known; a property path is
    // nullable when any member on it is (a Nullable<T>, or a reference type not annotated as
    // non-nullable — oblivious code counts as nullable); anything computed from paths is nullable
    // when one of them is, since lower(NULL) and length(NULL) are NULL. A coalesce is nullable
    // only through its fallback, and a concatenation never is (its operands are coalesced).
    private static bool CanBeNull(System.Linq.Expressions.Expression node, ParameterExpression root)
    {
        while (node is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            node = convert.Operand;

        if (!DependsOnParameter(node, root))
            return Evaluate(node) is null;

        if (node is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
            return CanBeNull(coalesce.Right, root);

        // A concatenation coalesces its nullable operands, so it is never null itself.
        if (IsConcatenation(node))
            return false;

        var finder = new NullableMemberFinder(root);
        finder.Visit(node);
        return finder.Found;
    }

    private sealed class NullableMemberFinder(ParameterExpression root) : ExpressionVisitor
    {
        private readonly NullabilityInfoContext _nullability = new();

        public bool Found { get; private set; }

        public override System.Linq.Expressions.Expression? Visit(System.Linq.Expressions.Expression? node)
        {
            // A captured value inside the operand (`x.Name + suffix`) is known, not declared.
            if (node is not null && !DependsOnParameter(node, root))
            {
                if (Evaluate(node) is null)
                    Found = true;
                return node;
            }

            return base.Visit(node);
        }

        protected override System.Linq.Expressions.Expression VisitMember(MemberExpression node)
        {
            // HasValue is never null, whatever its target is.
            if (node.Member.Name == nameof(Nullable<int>.HasValue) && Nullable.GetUnderlyingType(node.Member.DeclaringType!) is not null)
                return node;

            // string.Length and Nullable<T>.Value are members too, but not of the entity: their
            // nullability is their target's, which the base visit reaches.
            var ofEntity = node.Member.DeclaringType != typeof(string)
                        && Nullable.GetUnderlyingType(node.Member.DeclaringType!) is null;

            if (ofEntity && node.Member switch
                {
                    PropertyInfo property => _nullability.Create(property).ReadState != NullabilityState.NotNull,
                    FieldInfo field       => _nullability.Create(field).ReadState != NullabilityState.NotNull,
                    _                     => !node.Type.IsValueType || Nullable.GetUnderlyingType(node.Type) is not null,
                })
                Found = true;

            return base.VisitMember(node);
        }

        protected override System.Linq.Expressions.Expression VisitBinary(BinaryExpression node)
        {
            // A concatenation is never null: its nullable operands are coalesced.
            if (IsConcatenation(node))
                return node;

            // `a ?? b` is null only when b is.
            if (node.NodeType != ExpressionType.Coalesce)
                return base.VisitBinary(node);

            Visit(node.Right);
            return node;
        }
    }

    private static string Visit(System.Linq.Expressions.Expression node, ParameterExpression root)
    {
        switch (node)
        {
            // Of what the lambda reads from the entity, and so one of SilentConversions: the check
            // in front of the dispatch refused every other. A conversion of a value alone is C#'s
            // to do, below: (int)limit with a limit of 2.5 is 2, and was written as 2.5.
            case UnaryExpression { NodeType: ExpressionType.Convert } convert when DependsOnParameter(convert, root):
                return Visit(convert.Operand, root);

            case MemberExpression { Expression: { } target } member
                when DependsOnParameter(target, root) && TryGetValueMember(member, out var render):
                return render(Visit(target, root));

            case MemberExpression member when TryGetPath(member, root, out var path):
                return "{" + path + "}";

            case MethodCallExpression call when DependsOnParameter(call, root):
                return VisitCall(call, root);

            case BinaryExpression { NodeType: ExpressionType.Add } concat
                when concat.Type == typeof(string) && ValueOperators.Contains(concat.NodeType):
                return $"({ConcatOperand(concat.Left, root)} || {ConcatOperand(concat.Right, root)})";

            case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce when ValueOperators.Contains(coalesce.NodeType):
                return $"coalesce({Visit(coalesce.Left, root)}, {Visit(coalesce.Right, root)})";

            case ConstantExpression constant:
                return Literal(constant.Value);
        }

        // A subtree that never touches the entity parameter is a value: bake it in as a literal.
        if (!DependsOnParameter(node, root))
            return Literal(Evaluate(node));

        throw NotSupported(node);
    }

    // C# concatenates null as an empty string; PostgreSQL's || returns NULL. An index on
    // "Code" || "Name" held NULL for every row without a Name, so it served no EF Core query (which
    // writes COALESCE("Name", '')) and, unique, checked none of those rows. An operand that can be
    // null is wrapped as EF Core wraps it; a non-string one is cast to text first, as COALESCE
    // needs one type.
    private static string ConcatOperand(System.Linq.Expressions.Expression node, ParameterExpression root)
    {
        if (!CanBeNull(node, root))
            return Visit(node, root);

        if (!DependsOnParameter(node, root))
            return "''";

        var stripped = node;
        while (stripped is UnaryExpression { NodeType: ExpressionType.Convert } convert)
            stripped = convert.Operand;

        var operand = Visit(stripped, root);
        return stripped.Type == typeof(string) ? $"coalesce({operand}, '')" : $"coalesce({operand}::text, '')";
    }

    private static bool IsConcatenation(System.Linq.Expressions.Expression node)
        => node is BinaryExpression { NodeType: ExpressionType.Add } add && add.Type == typeof(string);

    private static string VisitCall(MethodCallExpression call, ParameterExpression root)
    {
        if (call.Object is not null && StringMethods.TryGetValue(call.Method, out var render))
            return render(Visit(call.Object, root), call.Arguments, argument => Visit(argument, root));

        throw NotSupported(call);
    }

    private static bool TryGetPath(MemberExpression member, ParameterExpression root, out string path)
    {
        var segments = new Stack<string>();

        System.Linq.Expressions.Expression? current = member;
        while (current is MemberExpression m)
        {
            segments.Push(m.Member.Name);
            current = m.Expression;
        }

        path = string.Join(".", segments);
        if (!ReferenceEquals(current, root))
            return false;

        // A member of a scalar type (CreatedAt.Year, RevokedAt.Value outside the table above) is no
        // property the model maps: it passed as the path {CreatedAt.Year} and failed at `migrations
        // add` with "Could not resolve property path". Refuse it here, at the declaration.
        for (System.Linq.Expressions.Expression? node = member; node is MemberExpression m; node = m.Expression)
            if (m.Expression is { } owner && IsScalar(owner.Type))
                throw new NotSupportedException(SchemaKitStrings.MemberOfScalarNotTranslated(member, m.Member.Name, owner.Type));

        return true;
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type.IsArray || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly)
            || type == typeof(TimeSpan) || type == typeof(Guid);
    }

    private static string Literal(object? value) => value switch
    {
        null       => "NULL",
        // A brace needs no escape: inside quotes the resolver takes nothing for a placeholder.
        string s   => "'" + s.Replace("'", "''") + "'",
        bool b     => b ? "TRUE" : "FALSE",
        char c     => Literal(c.ToString()),
        // Invariant culture — a de-DE thread culture must never turn 1.5 into '1,5'.
        IFormattable number when LiteralTypes.Contains(value.GetType())
            => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(SchemaKitStrings.LiteralNotTranslated(value.GetType()))
    };

    private static object? Evaluate(System.Linq.Expressions.Expression node)
        => System.Linq.Expressions.Expression.Lambda(node).Compile().DynamicInvoke();

    private static bool DependsOnParameter(System.Linq.Expressions.Expression node, ParameterExpression root)
    {
        var finder = new ParameterFinder(root);
        finder.Visit(node);
        return finder.Found;
    }

    private static NotSupportedException NotSupported(System.Linq.Expressions.Expression node) => new(SchemaKitStrings.ExpressionNotTranslated(node));

    // Finds the given parameter, or with none given, any parameter.
    private sealed class ParameterFinder(ParameterExpression? parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        public bool Search(System.Linq.Expressions.Expression node)
        {
            Visit(node);
            return Found;
        }

        protected override System.Linq.Expressions.Expression VisitParameter(ParameterExpression node)
        {
            if (parameter is null || ReferenceEquals(node, parameter))
                Found = true;
            return base.VisitParameter(node);
        }
    }
}
