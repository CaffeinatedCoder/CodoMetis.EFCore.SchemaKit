using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// Rewrites the operations only SchemaKit understands before the provider's generator sees them.
/// </summary>
/// <remarks>
/// SchemaKit's own objects need none of this: they are rendered at design time into
/// <see cref="SqlOperation" />s, which every generator runs as they are. What does need it is a
/// migration scaffolded by EFCore.ComplexIndexes 5.x, whose operations carry annotations the stock
/// generator cannot render.
/// </remarks>
internal interface IMigrationOperationRewriter
{
    /// <returns>
    /// The list to generate from. Operations it does not own are returned as they came, in place.
    /// </returns>
    IReadOnlyList<MigrationOperation> Rewrite(IReadOnlyList<MigrationOperation> operations, IModel? model);
}

/// <summary>
/// The generator every path uses: the rewriters, then the inner generator over the whole list.
/// </summary>
/// <remarks>
/// It hands the inner generator one list in one call, so the order of operations, transaction
/// suppression and the script options stay the provider's to honour.
/// </remarks>
internal sealed class SchemaKitSqlGenerator(
    IServiceProvider services,
    IDbContextOptions options,
    InnerServices inner) : IMigrationsSqlGenerator
{
    // Resolved on first use, for the reason SchemaKitModelDiffer gives for its planner.
    private IEnumerable<IMigrationOperationRewriter> Rewriters => field ??= services.GetServices<IMigrationOperationRewriter>();

    /// <summary>The generator this one wraps.</summary>
    public IMigrationsSqlGenerator Inner { get; } =
        inner.Create<IMigrationsSqlGenerator>(services, options, options.FindExtension<SchemaKitOptionsExtension>()?.InnerGenerator);

    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        foreach (var rewriter in Rewriters)
            operations = rewriter.Rewrite(operations, model);

        return Inner.Generate(operations, model, options);
    }
}
