using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Legacy.Internal;

/// <summary>
/// Replays the index <c>EFCore.ComplexIndexes</c> 5.x scaffolded as a
/// <see cref="CreateIndexOperation" /> with the annotation <c>CustomIndex:IndexParts</c>: one with
/// an expression or with <c>NULLS FIRST</c>/<c>LAST</c>, which only 5.x's own SQL generator wrote
/// (#28). The operation becomes the statement that generator wrote for it.
/// </summary>
/// <remarks>
/// <para>
/// The statement is 5.x's, not this package's: it is built as
/// <c>NpgsqlComplexIndexSqlGenerator</c> of 5.6.0 built it, clause for clause, and held against
/// the scripts the published 5.6.0 wrote (<c>test/Fixtures/ComplexIndexes5x/ScaffoldedReplay</c>),
/// character for character. That is why a name is delimited by the provider's helper, an
/// expression stands in parentheses of its own whatever it holds, and a storage parameter is
/// written <c>false</c> where <c>PgIndex</c> writes <c>off</c>. 5.x rendered when the migration
/// was applied, with the version installed then, whichever version had scaffolded it. From 5.1.0
/// to 5.6.0 the generator wrote one text. Before 5.0.0 it wrote neither <c>DESC</c> nor a null
/// order (read from its tags), so a database applied back then holds such an index without the
/// order, and one built from the same migration today has it, as it would with 5.6.0.
/// </para>
/// <para>
/// The operation's columns are not read. 5.x filled them with the parts' text and a last column
/// <c>__requires_UseNpgsqlComplexIndexes__</c>, so that another generator failed on it, and its
/// own generator read the parts alone. Nor is anything written for an annotation 5.x's generator
/// passed over: the index a migration built with 5.x did not have it.
/// </para>
/// <para>
/// What the annotation holds is 5.x's format, finished since 5.0.0: <c>e</c> and <c>v</c> from
/// 3.0.0, <c>d</c> from 3.1.0, <c>n</c> as <c>First</c> or <c>Last</c> from 5.0.0. Anything else
/// in it, and a value of an option in a type no migration of 5.x holds, is refused by name: read
/// loosely, the index would be built without it.
/// </para>
/// </remarks>
internal sealed class ComplexIndexes5xIndexPartsRewriter(ISqlGenerationHelper sql) : LegacyOperationRewriter
{
    public const string IndexParts = "CustomIndex:IndexParts";

    // Npgsql's names, as 5.x read them from the operation. A collation is under the relational
    // name there: 5.x's differ moved Npgsql:IndexCollation to it for the provider's generator.
    private const string Method = "Npgsql:IndexMethod";
    private const string Operators = "Npgsql:IndexOperators";
    private const string Collation = "Relational:Collation";
    private const string Include = "Npgsql:IndexInclude";
    private const string NullsDistinct = "Npgsql:NullsDistinct";
    private const string Concurrently = "Npgsql:CreatedConcurrently";
    private const string StorageParameter = "Npgsql:StorageParameter:";

    public override string Annotation => IndexParts;

    protected override IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation)
    {
        if (operation is not CreateIndexOperation index)
            throw Refused(operation.GetType().Name, "5.x put the annotation on a CreateIndexOperation alone");

        var parts = Parts(index);
        var concurrently = Flag(index, Concurrently) is true;
        var method = Text(index, Method);
        var operators = List(index, Operators);
        var collations = List(index, Collation);
        var include = List(index, Include);

        var statement = new StringBuilder("CREATE ");
        if (index.IsUnique) statement.Append("UNIQUE ");
        statement.Append("INDEX ");
        if (concurrently) statement.Append("CONCURRENTLY ");
        statement.Append(sql.DelimitIdentifier(index.Name)).Append(" ON ").Append(sql.DelimitIdentifier(index.Table, index.Schema));
        if (!string.IsNullOrEmpty(method)) statement.Append(" USING ").Append(method);

        statement.Append(" (");
        for (var i = 0; i < parts.Count; i++)
        {
            var (expression, value, descending, nulls) = parts[i];
            if (i > 0) statement.Append(", ");
            statement.Append(expression ? $"({value})" : sql.DelimitIdentifier(value));

            // PostgreSQL's order: collation, operator class, direction, null order.
            if (collations is not null && i < collations.Count && collations[i].Length > 0)
                statement.Append(" COLLATE ").Append(sql.DelimitIdentifier(collations[i]));
            if (operators is not null && i < operators.Count && operators[i].Length > 0)
                statement.Append(' ').Append(operators[i]);
            if (descending) statement.Append(" DESC");
            if (nulls is not null) statement.Append(" NULLS ").Append(nulls);
        }

        statement.Append(')');

        if (include is { Count: > 0 })
            statement.Append(" INCLUDE (").AppendJoin(", ", include.Select(sql.DelimitIdentifier)).Append(')');

        // PostgreSQL's default is NULLS DISTINCT, which 5.x did not write.
        if (Flag(index, NullsDistinct) is false) statement.Append(" NULLS NOT DISTINCT");

        // In the order the operation holds its annotations, which is by name.
        var parameters = index.GetAnnotations()
            .Where(a => a.Name.StartsWith(StorageParameter, StringComparison.Ordinal))
            .Select(a => $"{a.Name[StorageParameter.Length..]}={Parameter(index, a)}")
            .ToList();
        if (parameters.Count > 0) statement.Append(" WITH (").AppendJoin(", ", parameters).Append(')');

        if (!string.IsNullOrEmpty(index.Filter)) statement.Append(" WHERE ").Append(index.Filter);
        statement.Append(sql.StatementTerminator);

        return [new SqlOperation { Sql = statement.ToString(), SuppressTransaction = concurrently }];
    }

    private static List<(bool Expression, string Value, bool Descending, string? Nulls)> Parts(CreateIndexOperation index)
    {
        if (index[IndexParts] is not string json)
            throw Refused(Name(index), $"its value is of type {index[IndexParts]?.GetType().Name ?? "null"}, and 5.x wrote text");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw Refused(Name(index), $"its value is no JSON ({e.Message})");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                throw Refused(Name(index), "its value is no list of parts, or an empty one");

            List<(bool, string, bool, string?)> parts = [];
            foreach (var part in document.RootElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) throw Refused(Name(index), "a part of it is no object");

                bool? expression = null;
                string? value = null;
                var descending = false;
                string? nulls = null;
                foreach (var property in part.EnumerateObject())
                {
                    switch (property.Name, property.Value.ValueKind)
                    {
                        case ("e", JsonValueKind.True or JsonValueKind.False):
                            expression = property.Value.GetBoolean();
                            break;
                        case ("v", JsonValueKind.String):
                            value = property.Value.GetString();
                            break;
                        case ("d", JsonValueKind.True or JsonValueKind.False):
                            descending = property.Value.GetBoolean();
                            break;
                        case ("n", JsonValueKind.String) when property.Value.GetString() is "First":
                            nulls = "FIRST";
                            break;
                        case ("n", JsonValueKind.String) when property.Value.GetString() is "Last":
                            nulls = "LAST";
                            break;
                        default:
                            throw Refused(Name(index), $"a part of it holds '{property.Name}': {property.Value.GetRawText()}, which 5.x never wrote");
                    }
                }

                if (expression is null || string.IsNullOrEmpty(value))
                    throw Refused(Name(index), "a part of it lacks 'e' or 'v', which 5.x always wrote");

                parts.Add((expression.Value, value, descending, nulls));
            }

            return parts;
        }
    }

    private static bool? Flag(CreateIndexOperation index, string annotation) => index[annotation] switch
    {
        null       => null,
        bool value => value,
        var other  => throw Refused(Name(index), $"'{annotation}' beside it is of type {other.GetType().Name}, and 5.x wrote true or false"),
    };

    private static string? Text(CreateIndexOperation index, string annotation) => index[annotation] switch
    {
        null         => null,
        string value => value,
        var other    => throw Refused(Name(index), $"'{annotation}' beside it is of type {other.GetType().Name}, and 5.x wrote text"),
    };

    // One entry per part, an empty one for a part without. A string is a list of characters to
    // the runtime, and is no list here.
    private static List<string>? List(CreateIndexOperation index, string annotation) => index[annotation] switch
    {
        null                                => null,
        string                              => throw Refused(Name(index), $"'{annotation}' beside it is text, and 5.x wrote a list"),
        IEnumerable values when values.Cast<object?>().All(v => v is null or string)
                                            => [.. values.Cast<string?>().Select(v => v ?? "")],
        var other                           => throw Refused(Name(index), $"'{annotation}' beside it is of type {other.GetType().Name}, and 5.x wrote a list of text"),
    };

    // A flag bare, text quoted, a number as the invariant culture writes it.
    private static string Parameter(CreateIndexOperation index, IAnnotation parameter) => parameter.Value switch
    {
        bool flag          => flag ? "true" : "false",
        string text        => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        var other          => throw Refused(Name(index), $"'{parameter.Name}' beside it is {(other is null ? "null" : $"of type {other.GetType().Name}")}, and 5.x wrote a flag, text or a number"),
    };

    private static string Name(CreateIndexOperation index)
        => $"CreateIndexOperation '{index.Name}' on table '{(index.Schema is null ? index.Table : $"{index.Schema}.{index.Table}")}'";

    private static InvalidOperationException Refused(string operation, string why)
        => new($"The migration holds {operation} with the annotation '{IndexParts}' of EFCore.ComplexIndexes 5.x, and {why}. " +
               "The index is written from that annotation as 5.x's SQL generator wrote it, and from one it would have read " +
               "otherwise, or not at all, another index would be built. Replace the operation in the migration with " +
               "migrationBuilder.Sql(…) of the statement the index is to be created by.");
}
