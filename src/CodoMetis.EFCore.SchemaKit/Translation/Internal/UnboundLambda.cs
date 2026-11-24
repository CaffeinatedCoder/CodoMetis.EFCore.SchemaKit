using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace CodoMetis.EFCore.SchemaKit.Translation.Internal;

/// <summary>
/// A lambda as a declaration holds it between the call that wrote it and the moment its model is
/// finalized: checked, with every value it captured read, and not yet SQL.
/// </summary>
/// <remarks>
/// <para>
/// It is made where the lambda is written, and making it translates the lambda once: what the
/// translator has no SQL for is refused on the consumer's own line. The translation is thrown away;
/// the one that is stored is made when the model is finalized (issue 114).
/// </para>
/// <para>
/// Two of them are one when their trees are: that is what lets a declaration said twice be refused
/// at the call, before either is SQL.
/// </para>
/// </remarks>
internal sealed class UnboundLambda : IEquatable<UnboundLambda>
{
    private UnboundLambda(LambdaExpression lambda, SqlSpelling spelling, bool isPredicate)
    {
        Lambda = lambda;
        Spelling = spelling;
        IsPredicate = isPredicate;
    }

    /// <summary>The lambda, with each value it read from outside as a constant.</summary>
    public LambdaExpression Lambda { get; }

    /// <summary>The spelling of the provider the declaration is for.</summary>
    public SqlSpelling Spelling { get; }

    /// <summary>Whether the lambda is a condition; otherwise it is an expression.</summary>
    public bool IsPredicate { get; }

    public static UnboundLambda Of(LambdaExpression lambda, SqlSpelling spelling, bool isPredicate)
    {
        var unbound = new UnboundLambda((LambdaExpression)new CapturedValues().Visit(lambda)!, spelling, isPredicate);
        unbound.Translate();
        return unbound;
    }

    /// <summary>The lambda as SQL with placeholders, as the translator writes it today.</summary>
    public string Translate() => IsPredicate ? LambdaTranslator.Predicate(Lambda) : LambdaTranslator.Expression(Lambda);

    public bool Equals(UnboundLambda? other)
        => other is not null
           && IsPredicate == other.IsPredicate
           && ReferenceEquals(Spelling, other.Spelling)
           && ExpressionEqualityComparer.Instance.Equals(Lambda, other.Lambda);

    public override bool Equals(object? obj) => Equals(obj as UnboundLambda);

    public override int GetHashCode() => HashCode.Combine(IsPredicate, ExpressionEqualityComparer.Instance.GetHashCode(Lambda));

    /// <summary>The lambda as C# prints it, for a message.</summary>
    public override string ToString() => Lambda.ToString();

    /// <summary>
    /// Replaces every part of the lambda that reads no parameter by the value it has now. A local
    /// that changes after the declaration was written, or a method whose answer does, would
    /// otherwise be read when the model is finalized: the declaration would say what nobody wrote.
    /// </summary>
    private sealed class CapturedValues : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is null or ConstantExpression or DefaultExpression or LambdaExpression or ParameterExpression
                || node.NodeType == ExpressionType.Quote
                || new Parameters().Reads(node))
                return base.Visit(node);

            return Expression.Constant(Expression.Lambda(node).Compile().DynamicInvoke(), node.Type);
        }

        private sealed class Parameters : ExpressionVisitor
        {
            private bool _found;

            public bool Reads(Expression node)
            {
                Visit(node);
                return _found;
            }

            public override Expression? Visit(Expression? node) => _found ? node : base.Visit(node);

            protected override Expression VisitParameter(ParameterExpression node)
            {
                _found = true;
                return node;
            }
        }
    }
}
