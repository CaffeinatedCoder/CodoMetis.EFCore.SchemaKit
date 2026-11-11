using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// The differ every path uses: the inner differ's operations, with SchemaKit's spliced around them.
/// </summary>
/// <remarks>
/// A decorator, not a subclass of EF Core's differ: it is handed whatever differ the context would
/// have had (the provider's, or another package's) and never touches its operations or their order.
/// The same instance serves <c>dotnet ef</c>, because EF Core seeds its design-time services from
/// the context's.
/// </remarks>
internal sealed class SchemaKitModelDiffer(
    IServiceProvider services,
    IDbContextOptions options,
    InnerServices inner,
    ISchemaOperationPlanner planner) : IMigrationsModelDiffer
{
    /// <summary>The differ this one wraps.</summary>
    public IMigrationsModelDiffer Inner { get; } =
        inner.Create<IMigrationsModelDiffer>(services, options, options.FindExtension<SchemaKitOptionsExtension>()?.InnerDiffer);

    /// <summary>
    /// Whether anything differs, SchemaKit's objects included. It runs the plan
    /// <see cref="GetDifferences" /> runs, so the two cannot disagree: EF Core's own
    /// <c>HasDifferences</c> takes a shortcut past <c>GetDifferences</c>, which is how 5.x reported
    /// "no changes" for a change only it could see.
    /// </summary>
    public bool HasDifferences(IRelationalModel? source, IRelationalModel? target)
        => GetDifferences(source, target).Count > 0;

    public IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = Inner.GetDifferences(source, target);
        var plan       = planner.Plan(source, target, operations);

        return plan.IsEmpty ? operations : [.. plan.Before, .. operations, .. plan.After];
    }
}
