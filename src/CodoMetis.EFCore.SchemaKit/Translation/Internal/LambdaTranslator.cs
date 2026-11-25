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
/// that names a failure records one that happened there. What is here is the walk over the lambda
/// and the rules that are C#'s. A function, a literal and each form that differs between
/// databases is the provider's <see cref="SqlSpelling" /> to write, as that provider's query
/// translation writes it: an index serves only a query whose expression matches it.
/// </para>
/// <para>
/// Supported in an expression: property paths (through complex properties), the methods and
/// members of the spelling, concatenation (<c>+</c>), null coalescing (<c>??</c>), and constants:
/// a captured variable is evaluated and written as a literal. Anything else throws
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
    // ── The accepted constructs, as data ──
    // The dispatch below reads these tables and the spelling's, and so do the parity sweep, which
    // compares every construct with what the provider's query translation renders for the same
    // lambda, and the analyzer. A construct added to a table is swept with no edit there; one
    // added to the dispatch without a table entry is not, so keep new constructs in the tables.

    /// <summary>
    /// The members of <c>Nullable&lt;T&gt;</c>, which are C#'s and the same on every database, keyed
    /// by name. Every other member rendered from its target is the spelling's.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Func<string, string>> NullableMembers = new Dictionary<string, Func<string, string>>
    {
        // Nullable<T>.Value is the column itself, as in EF Core's queries; HasValue is its null
        // check (a filter writes it bare, and pushes a negation into IS NULL).
        [nameof(Nullable<int>.Value)]    = target => target,
        [nameof(Nullable<int>.HasValue)] = target => $"({target} IS NOT NULL)",
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

    private static bool TryGetValueMember(MemberExpression member, Scope root, out Func<string, string> render)
    {
        if (Nullable.GetUnderlyingType(member.Member.DeclaringType!) is not null)
            return NullableMembers.TryGetValue(member.Member.Name, out render!);

        return root.Spelling.Members.TryGetValue(member.Member, out render!);
    }

    /// <summary>Translates a lambda into an expression template: a key of an index, an element of a constraint.</summary>
    /// <param name="lambda">The lambda.</param>
    /// <param name="spelling">The spelling of the provider the SQL is for.</param>
    /// <param name="facts">What the model says of the properties the lambda reads; null for what their C# types say.</param>
    public static string Expression(LambdaExpression lambda, SqlSpelling spelling, TranslationFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        ArgumentNullException.ThrowIfNull(spelling);
        var root = Root(lambda, spelling, facts);
        new ConversionCheck(root).Visit(lambda.Body);
        return Visit(lambda.Body, root);
    }

    // The entity being configured is the one parameter. A lambda over two has no single table.
    private static Scope Root(LambdaExpression lambda, SqlSpelling spelling, TranslationFacts? facts)
        => lambda.Parameters.Count == 1
            ? new Scope(lambda.Parameters[0], facts ?? TranslationFacts.OfTheTypes, spelling)
            : throw new NotSupportedException(SchemaKitStrings.LambdaTakesOneParameter(lambda));

    /// <summary>The entity parameter, where what the lambda cannot say comes from, and who writes what differs between databases.</summary>
    internal sealed record Scope(ParameterExpression Parameter, TranslationFacts Facts, SqlSpelling Spelling);

    /// <summary>
    /// Translates a boolean lambda into a predicate template: comparisons (<c>==</c> and
    /// <c>!=</c> against null become <c>IS [NOT] NULL</c>), <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>,
    /// boolean members, and on the operands everything <see cref="Expression" /> supports.
    /// </summary>
    /// <param name="lambda">The lambda.</param>
    /// <param name="spelling">The spelling of the provider the SQL is for.</param>
    /// <param name="facts">What the model says of the properties the lambda reads; null for what their C# types say.</param>
    public static string Predicate(LambdaExpression lambda, SqlSpelling spelling, TranslationFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        ArgumentNullException.ThrowIfNull(spelling);
        var root = Root(lambda, spelling, facts);
        new ConversionCheck(root).Visit(lambda.Body);
        return VisitPredicate(lambda.Body, root, negated: false);
    }

    // The dispatch below takes a conversion off wherever it meets one, in the operand of a
    // comparison, in a null test, in a concatenation, and writes what is under it. That is right
    // only for a conversion that keeps the value, so every conversion of what the lambda reads from
    // the entity is held against the table first, in one pass over the whole lambda: a place that
    // strips one cannot be the place that forgot to ask. A conversion of a value alone (a captured
    // variable, a constant) is not asked about: C# does it, and the result is written.
    private sealed class ConversionCheck(Scope root) : ExpressionVisitor
    {
        public override System.Linq.Expressions.Expression? Visit(System.Linq.Expressions.Expression? node)
            => node is null || !DependsOnParameter(node, root) ? node : base.Visit(node);

        // A comparison of an enum keeps its own refusal: the compiler converts the enum to its
        // number first, and the reason that says why is the enum's, not the conversion's.
        protected override System.Linq.Expressions.Expression VisitBinary(BinaryExpression node)
        {
            if (ComparisonOperators.ContainsKey(node.NodeType))
            {
                // An enum compared with a value is written by the property's mapping, which knows
                // how the enum is stored. The compiler's conversion of the enum to its number is
                // then no conversion of a column, and is not asked about.
                if (ValueComparison(node, root) is { Path: var side } && IsEnum(Unconverted(side).Type))
                {
                    Visit(ReferenceEquals(side, node.Left) ? node.Right : node.Left);
                    return node;
                }

                RefuseEnum(node.Left);
                RefuseEnum(node.Right);
            }

            // `x.Score ?? x.Price` converts its left side in a lambda of its own, over a parameter
            // that is not the entity: it reads the entity all the same.
            if (node.Conversion is { } conversion)
                new ConversionCheck(root with { Parameter = conversion.Parameters[0] }).Visit(conversion.Body);

            return base.VisitBinary(node);
        }

        protected override System.Linq.Expressions.Expression VisitUnary(UnaryExpression node)
            => Converted(node) is not null && !IsSilentConversion(node.Operand.Type, node.Type)
                   ? throw new NotSupportedException(SchemaKitStrings.ConversionNotTranslated(node, node.Operand.Type, node.Type))
                   : base.VisitUnary(node);
    }

    // A conversion, and what is under it. In a checked context, a project's
    // <CheckForOverflowUnderflow> or a checked(…), the compiler writes every numeric conversion
    // of an expression tree as ConvertChecked, the ones it adds unasked included: x.Small == 5 is
    // ConvertChecked(x.Small, Int32) == 5 there. Matched as Convert alone, every promoted
    // comparison of such a project was refused (#126). The two nodes mean the same wherever one
    // is taken off: of what the lambda reads from the entity only a conversion that keeps every
    // value passed the check above, and that one cannot overflow. Every place that takes a
    // conversion off asks here, so that none knows one node and not the other.
    private static System.Linq.Expressions.Expression? Converted(System.Linq.Expressions.Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion ? conversion.Operand : null;

    /// <summary>What is under every conversion around the node.</summary>
    private static System.Linq.Expressions.Expression Unconverted(System.Linq.Expressions.Expression node)
    {
        while (Converted(node) is { } operand)
            node = operand;

        return node;
    }

    // C# compares with two-valued logic: `x.Kind != "a"` is true for a null Kind. SQL's `<>` is
    // NULL there, and a partial index or constraint filter that is NULL leaves the row out, so a
    // unique index "among rows that are not federated" silently stopped covering rows whose Kind
    // is null. Comparisons with an operand that can be null are therefore written the way EF Core
    // writes the same lambda in a query (`"Kind" <> 'a' OR "Kind" IS NULL`), which keeps the index
    // usable for that query too, and a negation over such a comparison is pushed down to it rather
    // than wrapped around a value that can be NULL. Operands that cannot be null keep the plain
    // forms, so their filters, and the indexes already built from them, do not change.
    private static string VisitPredicate(System.Linq.Expressions.Expression node, Scope root, bool negated)
    {
        switch (node)
        {
            case var _ when Converted(node) is { } operand:
                return VisitPredicate(operand, root, negated);

            case UnaryExpression { NodeType: ExpressionType.Not } not when LogicalOperators.Contains(not.NodeType):
                // A boolean stored as something else is compared with what false is stored as, as
                // EF Core writes it, and not wrapped: "Active" = 'N', not NOT ("Active" = 'Y').
                // A boolean value that is negated once is the spelling's to negate: a database whose
                // boolean is a number compares it with what false is, as its queries do.
                return ComparesNullable(not.Operand, root) || IsHasValue(not.Operand, root) || IsStoredFlag(not.Operand, root)
                       || (!negated && IsBooleanValue(not.Operand, root))
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
                // which the complement alone would make NULL. EF Core writes that as a CASE, in
                // each provider's own words.
                if (CanBeNull(comparison.Left, root) || CanBeNull(comparison.Right, root))
                    return root.Spelling.UnlessTrue($"{left} {op} {right}");

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
            // A boolean stored as something else (a 'Y', a 1) is no condition by itself: it is
            // compared with what true is stored as, or false for a negation.
            case MemberExpression member when IsStoredFlag(member, root) && PathOf(member, root) is { } flag:
                return $"{{{flag}}} = {root.Facts.Literal(flag, !negated)}";

            case MemberExpression member when member.Type == typeof(bool) && DependsOnParameter(member, root):
                return root.Spelling.Condition(Visit(member, root), negated);
        }

        // A captured flag: bake it in.
        if (node.Type == typeof(bool) && !DependsOnParameter(node, root))
            return root.Spelling.Condition(Literal(Evaluate(node), root), negated);

        throw new NotSupportedException(SchemaKitStrings.PredicateNotTranslated(node));
    }

    // Enum values never reach SQL: how one is stored depends on the property's value conversion,
    // which the translator cannot see, so `Status == Status.Active` would compare against 0 on a
    // text column and fail at apply time. Refuse it here, at the declaration.
    private static string VisitOperand(System.Linq.Expressions.Expression node, Scope root)
    {
        RefuseEnum(node);
        return Visit(node, root);
    }

    private static void RefuseEnum(System.Linq.Expressions.Expression node)
    {
        var stripped = Unconverted(node);

        if ((Nullable.GetUnderlyingType(stripped.Type) ?? stripped.Type).IsEnum)
            throw new NotSupportedException(SchemaKitStrings.EnumNotTranslated(node));
    }

    private static bool IsEnum(Type type) => (Nullable.GetUnderlyingType(type) ?? type).IsEnum;

    /// <summary>
    /// A comparison of a property with a value: one side is a property path and nothing else, the
    /// other reads nothing from the entity. The value is then written as the property's column
    /// stores it, which only the model knows: through a converter, as an enum's name or number, as
    /// the provider writes a date.
    /// </summary>
    private static (System.Linq.Expressions.Expression Path, string Name, object Value)? ValueComparison(BinaryExpression comparison, Scope root)
    {
        if (PathOf(comparison.Left, root) is { } left && !DependsOnParameter(comparison.Right, root) && Evaluate(comparison.Right) is { } right)
            return (comparison.Left, left, right);
        if (PathOf(comparison.Right, root) is { } rightPath && !DependsOnParameter(comparison.Left, root) && Evaluate(comparison.Left) is { } leftValue)
            return (comparison.Right, rightPath, leftValue);

        return null;
    }

    /// <summary>The path of a node that is a property of the entity and nothing else, under conversions and <c>Nullable&lt;T&gt;.Value</c>; else null.</summary>
    private static string? PathOf(System.Linq.Expressions.Expression node, Scope root)
    {
        while (true)
        {
            if (Converted(node) is { } operand) node = operand;
            else if (node is MemberExpression { Member.Name: nameof(Nullable<int>.Value), Expression: { } target } && Nullable.GetUnderlyingType(target.Type) is not null) node = target;
            else break;
        }

        return node is MemberExpression member && !TryGetValueMember(member, root, out _) && IsOfEntity(member, root) && TryGetPath(member, root, out var path) ? path : null;
    }

    private static bool IsOfEntity(MemberExpression member, Scope root)
    {
        System.Linq.Expressions.Expression? current = member;
        while (current is MemberExpression m) current = m.Expression;
        return ReferenceEquals(current, root.Parameter);
    }

    // C# compares chars as ints: x.Grade == 'A' arrives as Convert(x.Grade) == 65, and the constant
    // rendered as 65 met a character column, which PostgreSQL refuses at apply. The constant goes
    // back to the char it was written as, as EF Core renders it.
    private static (string Left, string Right) VisitOperands(BinaryExpression comparison, Scope root)
    {
        if (ValueComparison(comparison, root) is var (side, path, value))
        {
            var ordered = comparison.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual);
            if (ordered && !root.Facts.KeepsOrder(path))
                throw new NotSupportedException(SchemaKitStrings.OrderOfConverted(comparison, path));

            var literal = root.Facts.Literal(path, value) ?? ValueWithoutTheModel(side, value, root);
            if (literal is not null)
            {
                var column = "{" + path + "}";
                return ReferenceEquals(side, comparison.Left) ? (column, literal) : (literal, column);
            }
        }

        var left  = VisitOperand(comparison.Left, root);
        var right = VisitOperand(comparison.Right, root);

        if (IsConvertedChar(comparison.Left) && CharCode(comparison.Right, root) is { } rightChar)
            right = Literal(rightChar, root);
        else if (IsConvertedChar(comparison.Right) && CharCode(comparison.Left, root) is { } leftChar)
            left = Literal(leftChar, root);

        return (left, right);
    }

    // Where the lambda is written nothing knows how the property is stored. A value the translator
    // has no literal of its own for, or an enum, is written as a mark: the translation at the call
    // is thrown away, and the one that is stored is made from the model. With the model's facts a
    // value they do not write falls through to the translator's own rule.
    /// <summary>What stands for a value in the translation at the call, where only the model can write it. It is no SQL and is never stored.</summary>
    internal const string BoundLater = "<bound when the model is finalized>";

    private static string? ValueWithoutTheModel(System.Linq.Expressions.Expression side, object value, Scope root)
        => !root.Facts.OfTheModel && (IsEnum(Unconverted(side).Type) || !root.Spelling.LiteralTypes.Contains(value.GetType())) ? BoundLater : null;

    private static bool IsConvertedChar(System.Linq.Expressions.Expression node)
        => Converted(node) is { } operand
        && (Nullable.GetUnderlyingType(operand.Type) ?? operand.Type) == typeof(char);

    // A number no char has would wrap round to another char (65601 to 'A'), and the comparison C#
    // answers alike for every row would hold for some rows only (#168).
    private static char? CharCode(System.Linq.Expressions.Expression node, Scope root)
    {
        if (DependsOnParameter(node, root) || Evaluate(node) is not int code)
            return null;

        if (code < char.MinValue || code > char.MaxValue)
            throw new NotSupportedException(SchemaKitStrings.NotACharacter(node, code));

        return (char)code;
    }

    private static bool IsHasValue(System.Linq.Expressions.Expression node, Scope root)
        => node is MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } target }
        && Nullable.GetUnderlyingType(target.Type) is not null
        && DependsOnParameter(target, root);

    private static bool IsStoredFlag(System.Linq.Expressions.Expression node, Scope root)
        => Unconverted(node) is MemberExpression { } member && member.Type == typeof(bool)
        && PathOf(member, root) is { } flag && !root.Facts.IsBooleanColumn(flag);

    // What VisitPredicate hands to the spelling's Condition: a boolean member that is no null
    // check, or a value that reads nothing from the entity.
    private static bool IsBooleanValue(System.Linq.Expressions.Expression node, Scope root)
        => node.Type == typeof(bool)
        && (!DependsOnParameter(node, root) || (node is MemberExpression && !IsHasValue(node, root)));

    private static string Negate(string predicate, bool negated)
        => negated ? $"NOT ({predicate})" : predicate;

    // A literal null or a captured value that evaluates to null.
    private static bool IsNullValue(System.Linq.Expressions.Expression node, Scope root)
    {
        node = Unconverted(node);

        return node is ConstantExpression { Value: null }
            || (!DependsOnParameter(node, root) && Evaluate(node) is null);
    }

    // Whether a comparison under `node` has an operand that can be null, i.e. whether wrapping it in
    // NOT (…) could leave NULL where C# has true. Null checks themselves never produce NULL.
    private static bool ComparesNullable(System.Linq.Expressions.Expression node, Scope root) => node switch
    {
        _ when Converted(node) is { } operand
            => ComparesNullable(operand, root),
        UnaryExpression { NodeType: ExpressionType.Not } not
            => ComparesNullable(not.Operand, root),
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
    private static string NullTest(System.Linq.Expressions.Expression operand, Scope root, bool isNull)
        => NullCheck(operand, root, isNull) ?? $"{VisitOperand(operand, root)} IS {(isNull ? "" : "NOT ")}NULL";

    // The test for an operand being null (or not), written over the columns it is null through, or
    // null when it cannot be null. A function of a spelling is strict: lower(x) is null
    // exactly when x is, so its test is its nullable inputs' tests ORed (ANDed for IS NOT NULL). A
    // coalesce is null when all its inputs are, a concatenation never (its operands are coalesced),
    // HasValue never.
    private static string? NullCheck(System.Linq.Expressions.Expression node, Scope root, bool isNull)
    {
        node = Unconverted(node);

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
                when DependsOnParameter(target, root) && TryGetValueMember(member, root, out _):
                return NullCheck(target, root, isNull);

            case MethodCallExpression { Object: { } target } call when root.Spelling.Methods.ContainsKey(call.Method):
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
    private static bool CanBeNull(System.Linq.Expressions.Expression node, Scope root)
    {
        node = Unconverted(node);

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

    private sealed class NullableMemberFinder(Scope root) : ExpressionVisitor
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
            // A property path the model knows: its column's nullability is the model's fact, not
            // the annotation's. IsRequired() and IsRequired(false) change one and not the other.
            if (!TryGetValueMember(node, root, out _) && IsOfEntity(node, root) && PathIfMapped(node) is { } path && root.Facts.CanBeNull(path) is { } nullable)
            {
                if (nullable) Found = true;
                return node;
            }

            // HasValue is never null, whatever its target is.
            if (node.Member.Name == nameof(Nullable<int>.HasValue) && Nullable.GetUnderlyingType(node.Member.DeclaringType!) is not null)
                return node;

            // A string's length and Nullable<T>.Value are members too, but not of the entity: their
            // nullability is their target's, which the base visit reaches.
            var ofEntity = !TryGetValueMember(node, root, out _);

            if (ofEntity && node.Member switch
                {
                    PropertyInfo property => _nullability.Create(property).ReadState != NullabilityState.NotNull,
                    FieldInfo field       => _nullability.Create(field).ReadState != NullabilityState.NotNull,
                    _                     => !node.Type.IsValueType || Nullable.GetUnderlyingType(node.Type) is not null,
                })
                Found = true;

            return base.VisitMember(node);
        }

        // The path of a member chain, without the refusal of a member of a scalar: that is the
        // dispatch's to give, with its own message.
        private static string PathIfMapped(MemberExpression member)
        {
            var segments = new Stack<string>();
            for (System.Linq.Expressions.Expression? current = member; current is MemberExpression m; current = m.Expression)
                segments.Push(m.Member.Name);
            return string.Join(".", segments);
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

    private static string Visit(System.Linq.Expressions.Expression node, Scope root)
    {
        switch (node)
        {
            // Of what the lambda reads from the entity, and so one of SilentConversions: the check
            // in front of the dispatch refused every other. A conversion of a value alone is C#'s
            // to do, below: (int)limit with a limit of 2.5 is 2, and was written as 2.5.
            case var _ when Converted(node) is { } operand && DependsOnParameter(node, root):
                return Visit(operand, root);

            case MemberExpression { Expression: { } target } member
                when DependsOnParameter(target, root) && TryGetValueMember(member, root, out var render):
                return render(Visit(target, root));

            case MemberExpression member when TryGetPath(member, root, out var path):
                return "{" + path + "}";

            case MethodCallExpression call when DependsOnParameter(call, root):
                return VisitCall(call, root);

            case BinaryExpression { NodeType: ExpressionType.Add } concat
                when concat.Type == typeof(string) && ValueOperators.Contains(concat.NodeType):
                return root.Spelling.Concatenation(ConcatOperand(concat.Left, root), ConcatOperand(concat.Right, root));

            case BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce when ValueOperators.Contains(coalesce.NodeType):
                return root.Spelling.Coalesce(Visit(coalesce.Left, root), Visit(coalesce.Right, root));

            case ConstantExpression constant:
                return Literal(constant.Value, root);
        }

        // A subtree that never touches the entity parameter is a value: bake it in as a literal.
        if (!DependsOnParameter(node, root))
            return Literal(Evaluate(node), root);

        throw NotSupported(node);
    }

    // C# concatenates null as an empty string; SQL's concatenation returns NULL. An index on
    // "Code" || "Name" held NULL for every row without a Name, so it served no EF Core query (which
    // writes COALESCE("Name", '')) and, unique, checked none of those rows. An operand that can be
    // null is the spelling's to wrap as its provider's queries wrap it. A captured null is the
    // empty string C# makes of it.
    private static string ConcatOperand(System.Linq.Expressions.Expression node, Scope root)
    {
        var canBeNull = CanBeNull(node, root);

        if (canBeNull && !DependsOnParameter(node, root))
            return root.Spelling.Literal("");

        var stripped = canBeNull ? Unconverted(node) : node;
        return root.Spelling.ConcatenationOperand(Visit(stripped, root), stripped.Type, canBeNull);
    }

    private static bool IsConcatenation(System.Linq.Expressions.Expression node)
        => node is BinaryExpression { NodeType: ExpressionType.Add } add && add.Type == typeof(string);

    private static string VisitCall(MethodCallExpression call, Scope root)
    {
        if (call.Object is not null && root.Spelling.Methods.TryGetValue(call.Method, out var render))
            return render(Visit(call.Object, root), [.. call.Arguments.Select(argument => Argument(argument, root))]);

        throw NotSupported(call);
    }

    // An argument that reads no parameter at all is a value the spelling may fold, as EF Core
    // folds a substring's start: substring("Code", 2), not substring("Code", (1 + 1)).
    private static SpelledArgument Argument(System.Linq.Expressions.Expression argument, Scope root)
    {
        var sql = Visit(argument, root);
        return new ParameterFinder(parameter: null).Search(argument)
            ? new SpelledArgument(sql, IsValue: false, Value: null)
            : new SpelledArgument(sql, IsValue: true, Evaluate(argument));
    }

    private static bool TryGetPath(MemberExpression member, Scope root, out string path)
    {
        var segments = new Stack<string>();

        System.Linq.Expressions.Expression? current = member;
        while (current is MemberExpression m)
        {
            segments.Push(m.Member.Name);
            current = m.Expression;
        }

        path = string.Join(".", segments);
        if (!ReferenceEquals(current, root.Parameter))
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

    // NULL is the same everywhere; every other value is the spelling's to write, of the types
    // it says it writes. A DateTime, Guid or enum is IFormattable too, and came out as bare text
    // that is not SQL.
    private static string Literal(object? value, Scope root) => value switch
    {
        null => "NULL",
        _ when root.Spelling.LiteralTypes.Contains(value.GetType()) => root.Spelling.Literal(value),
        _ => throw new NotSupportedException(SchemaKitStrings.LiteralNotTranslated(value.GetType()))
    };

    private static object? Evaluate(System.Linq.Expressions.Expression node)
        => System.Linq.Expressions.Expression.Lambda(node).Compile().DynamicInvoke();

    private static bool DependsOnParameter(System.Linq.Expressions.Expression node, Scope root)
    {
        var finder = new ParameterFinder(root.Parameter);
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
