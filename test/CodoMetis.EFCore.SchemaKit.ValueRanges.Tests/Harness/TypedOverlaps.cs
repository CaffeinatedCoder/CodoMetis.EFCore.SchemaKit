using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using CodoMetis.ValueRanges;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;

/// <summary>
/// What a package of ValueRanges' own can offer today (CaffeinatedCoder/CodoMetis.ValueRanges#540),
/// written against SchemaKit's public surface with nothing experimental: an <c>Overlaps</c> that
/// takes a range of ValueRanges and nothing else, after an element compared by <c>=</c> and after
/// any other element. It is the unchecked operator with the operand's type checked by the compiler.
/// </summary>
/// <remarks>
/// What it cannot offer is the form that starts the elements, <c>pg.Overlaps(x => x.Period)</c>:
/// nothing public makes an <see cref="ExclusionElements{TEntity}" /> but SchemaKit's own factory
/// methods, and the factory has no unchecked operator to start with.
/// <see cref="SatelliteSurfaceTests" /> holds both halves.
/// </remarks>
public static class TypedOverlaps
{
    public static ExclusionElements<TEntity> Overlaps<TEntity>(this ExclusionDraft<TEntity> draft, Expression<Func<TEntity, DateRange>> range)
        where TEntity : class
        => draft.WithUncheckedOperator(range, "&&");

    public static ExclusionElements<TEntity> Overlaps<TEntity>(this ExclusionElements<TEntity> elements, Expression<Func<TEntity, DateRange>> range)
        where TEntity : class
        => elements.WithUncheckedOperator(range, "&&");

    public static ExclusionElements<TEntity> Overlaps<TEntity>(this ExclusionDraft<TEntity> draft, Expression<Func<TEntity, RangeSet<DateRange, DateOnly>>> multirange)
        where TEntity : class
        => draft.WithUncheckedOperator(multirange, "&&");
}
