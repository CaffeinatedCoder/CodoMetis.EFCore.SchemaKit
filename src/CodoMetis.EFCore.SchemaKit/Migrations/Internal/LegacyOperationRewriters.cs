using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// The packages' <see cref="LegacyOperationRewriter" />s, as the one rewriter the generator runs:
/// each operation that carries a rewriter's annotation is replaced by that rewriter's answer, in
/// its place, and every other operation is handed on as it came.
/// </summary>
/// <remarks>
/// <para>
/// One annotation has one rewriter, as one annotation of a snapshot has one reader. Two would
/// each have a statement for the operation, and which of them wrote the database would be the
/// order of two registrations. The pair is refused at the first migration that is generated, not
/// where it is registered: a rewriter says its annotation only once it is made.
/// </para>
/// <para>
/// A statement a rewriter writes outside a transaction is stamped as SchemaKit's own are
/// (<see cref="StatementStamp" />), so that it is refused where the way the migration is applied
/// does not carry one. The stamp is on the operation in memory only: the migration's file holds
/// the earlier package's operation, and is not written again.
/// </para>
/// </remarks>
internal sealed class LegacyOperationRewriters(IEnumerable<LegacyOperationRewriter> rewriters) : IMigrationOperationRewriter
{
    private Dictionary<string, LegacyOperationRewriter> ByAnnotation => field ??= Index(rewriters);

    public IReadOnlyList<MigrationOperation> Rewrite(IReadOnlyList<MigrationOperation> operations, IModel? model)
    {
        if (ByAnnotation.Count == 0) return operations;

        List<MigrationOperation>? rewritten = null;
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (For(operation) is not { } rewriter)
            {
                rewritten?.Add(operation);
                continue;
            }

            rewritten ??= [.. operations.Take(i)];
            foreach (var replaced in rewriter.Replay(operation))
            {
                if (replaced is SqlOperation { SuppressTransaction: true } statement && statement.FindAnnotation(StatementStamp.Annotation) is null)
                    StatementStamp.Stamp(statement, SchemaKitStrings.ReplayedOperation(LegacyFormats.Describe(operation), rewriter.Annotation));
                rewritten.Add(replaced);
            }
        }

        return rewritten ?? operations;
    }

    // In the order the operation holds its annotations, so that the answer does not depend on
    // the order two packages were registered in.
    private LegacyOperationRewriter? For(MigrationOperation operation)
    {
        foreach (var annotation in operation.GetAnnotations())
        {
            if (ByAnnotation.TryGetValue(annotation.Name, out var rewriter)) return rewriter;
        }

        return null;
    }

    private static Dictionary<string, LegacyOperationRewriter> Index(IEnumerable<LegacyOperationRewriter> rewriters)
    {
        Dictionary<string, LegacyOperationRewriter> byAnnotation = new(StringComparer.Ordinal);
        foreach (var rewriter in rewriters)
        {
            if (byAnnotation.TryGetValue(rewriter.Annotation, out var first))
            {
                // One class registered twice is one rewriter; TryAddEnumerable sees to that.
                throw new InvalidOperationException(SchemaKitStrings.TwoRewritersForOneAnnotation(rewriter.Annotation, first.GetType(), rewriter.GetType()));
            }

            byAnnotation.Add(rewriter.Annotation, rewriter);
        }

        return byAnnotation;
    }
}
