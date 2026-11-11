using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// What the host asks of the planning layer: the operations SchemaKit adds around the inner
/// differ's. The host knows nothing of declarations, descriptors or contributors; they sit behind
/// this.
/// </summary>
/// <remarks>
/// Internal on purpose. The public seam is the contributor contract, which is not frozen before
/// rc.1; the planner that reads contributors replaces <see cref="NoSchemaOperations" />.
/// </remarks>
internal interface ISchemaOperationPlanner
{
    /// <param name="source">The model migrated from; null when there is none.</param>
    /// <param name="target">The model migrated to; null when everything is dropped.</param>
    /// <param name="inner">The inner differ's operations, sorted. Read them (a renamed table, a dropped column); never change them.</param>
    SchemaOperationPlan Plan(IRelationalModel? source, IRelationalModel? target, IReadOnlyList<MigrationOperation> inner);
}

/// <summary>
/// SchemaKit's operations, by where they go: <see cref="Before" /> the inner differ's (drops, and
/// the drop half of a rebuild), and <see cref="After" /> them (in-place changes, then creates).
/// </summary>
internal sealed record SchemaOperationPlan(IReadOnlyList<MigrationOperation> Before, IReadOnlyList<MigrationOperation> After)
{
    public static SchemaOperationPlan Empty { get; } = new([], []);

    public bool IsEmpty => Before.Count == 0 && After.Count == 0;
}

/// <summary>The planner of a context nothing has contributed to.</summary>
internal sealed class NoSchemaOperations : ISchemaOperationPlanner
{
    public SchemaOperationPlan Plan(IRelationalModel? source, IRelationalModel? target, IReadOnlyList<MigrationOperation> inner)
        => SchemaOperationPlan.Empty;
}
