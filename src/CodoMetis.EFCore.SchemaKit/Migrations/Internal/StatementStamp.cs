using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// What a statement of SchemaKit's that runs outside a transaction says about itself: the object
/// it is for, as an annotation on its <see cref="SqlOperation" />.
/// </summary>
/// <remarks>
/// <para>
/// <c>migrations add</c> writes the annotation into the migration, as <c>.Annotation(…)</c> on the
/// <c>migrationBuilder.Sql(…)</c> call, and the migration's code is its only carrier (spike D). So
/// <see cref="Annotation" /> is in consumers' repositories and is permanent from 11.0.0. Its value
/// is text for a person: nothing parses it.
/// </para>
/// <para>
/// Only a statement with the transaction suppressed carries it, since only of such a statement
/// is anything asked when the SQL is generated. What every statement of SchemaKit's says about
/// itself is issue 115's to decide.
/// </para>
/// </remarks>
internal static class StatementStamp
{
    public const string Annotation = "CodoMetis:SchemaKit:Object";

    public static void Stamp(SqlOperation operation, string subject) => operation.AddAnnotation(Annotation, subject);

    /// <summary>
    /// Refuses a statement of SchemaKit's that runs outside a transaction where the way the
    /// migration is applied does not carry one. The dialect is asked only when such a statement
    /// is met.
    /// </summary>
    public static void RequireCarried(
        IReadOnlyList<MigrationOperation> operations,
        MigrationsSqlGenerationOptions options,
        Func<ISchemaDialect> dialect)
    {
        foreach (var operation in operations)
        {
            if (operation is SqlOperation { SuppressTransaction: true } statement
                && statement.FindAnnotation(Annotation)?.Value is string subject
                && dialect().WhyNotOutsideTransaction(options) is { } why)
            {
                throw new InvalidOperationException(SchemaKitStrings.NotCarriedOutsideATransaction(subject, statement.Sql, why));
            }
        }
    }
}
