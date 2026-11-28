using System.Diagnostics.CodeAnalysis;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// Replays an operation of a migration an earlier package scaffolded: one that carries an
/// annotation only that package's SQL generator rendered, such as the index with an expression
/// <c>EFCore.ComplexIndexes</c> 5.x scaffolded as a <see cref="CreateIndexOperation" />. What it
/// returns is generated in the operation's place, by the provider's generator.
/// </summary>
/// <remarks>
/// <para>
/// Such a migration is in a consumer's repository and is applied to a fresh database for as long
/// as the project lives. The provider's generator does not read the annotation and writes the
/// operation as if it were not there: another object, or a statement that fails. A rewriter
/// writes what the earlier package's generator wrote.
/// </para>
/// <para>
/// A rewriter translates and decides nothing. An annotation in a shape the earlier package never
/// wrote, and an option its generator rendered that the rewriter does not, are thrown: an
/// operation passed on, or answered with nothing, is an object the database does not get.
/// </para>
/// <para>
/// It is asked for an operation that carries <see cref="Annotation" />, in the place the operation
/// has in its migration, and for no other. It is created without the model and without the
/// dialect: the SQL is generated for a migration, not for the model of the day.
/// </para>
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public abstract class LegacyOperationRewriter
{
    /// <summary>The name of the annotation on a migration operation that this rewriter replays.</summary>
    public abstract string Annotation { get; }

    /// <summary>
    /// The operations to generate in place of <paramref name="operation" />, in their order. None
    /// of them carries <see cref="Annotation" />.
    /// </summary>
    /// <param name="operation">The operation that carries the annotation.</param>
    /// <exception cref="InvalidOperationException">The operation holds something the rewriter has no statement for.</exception>
    protected abstract IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation);

    /// <summary>The answer, held to what the class says of it.</summary>
    internal IReadOnlyList<MigrationOperation> Replay(MigrationOperation operation)
    {
        var replaced = Rewrite(operation);
        if (replaced is null || replaced.Count == 0 || replaced.Any(r => r is null))
            throw new InvalidOperationException(SchemaKitStrings.LegacyRewriterReturnedNothing(GetType(), Annotation));

        if (replaced.Any(r => ((IAnnotatable)r).FindAnnotation(Annotation) is not null))
            throw new InvalidOperationException(SchemaKitStrings.LegacyRewriterPassedOn(GetType(), Annotation));

        return replaced;
    }
}
