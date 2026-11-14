using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// What <c>EFCore.ComplexIndexes</c> 5.x stored in a model, and so in a model snapshot: the names of
/// its annotations that declare an object. A snapshot that holds one nothing here reads describes
/// objects that are in the database and in no diff: declared again they are created a second time,
/// removed from the model they are never dropped, and neither is an error (#93).
/// </summary>
/// <remarks>
/// <para>
/// The list is 5.x's and is finished. It is taken from its source and held against a snapshot the
/// published 5.6.0 scaffolded (<c>test/Fixtures/ComplexIndexes5x/ScaffoldedOtherKinds</c>), which
/// says where each annotation sits. A temporal foreign key is in 5.x's source and in no published
/// version; it is listed all the same.
/// </para>
/// <para>
/// Not on it, since they declare nothing: <c>CustomIndex:RenderingVersion</c> and
/// <c>CustomTemporal:SuppressAutoExtension</c> on the model, the properties that describe a
/// property-level index beside its <c>IsIndexed</c>, and what 5.x stamped on operations, which
/// has a list of its own (<see cref="RequireRewritten" />).
/// </para>
/// <para>
/// An annotation on an entity type leaves the guard when a <see cref="LegacyDeclarationReader{TDeclaration}" />
/// for it is registered. One on a property has no reader yet.
/// </para>
/// </remarks>
internal static class LegacyFormats
{
    private const string TakeOver = "https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/";

    private const string NotYet = "This version of SchemaKit cannot take over a project that has them: keep it on EFCore.ComplexIndexes 5.x until one can. ";

    /// <summary>An annotation of an entity type, what it holds, and what to do about it.</summary>
    private static readonly (string Annotation, string What, string Advice)[] OnEntityTypes =
    [
        ("CustomIndex:CompositeIndexes", "indexes", NotYet + TakeOver + "27"),
        ("CustomTemporal:Constraints", "temporal constraints", NotYet + TakeOver + "18"),
        ("CustomTemporal:ForeignKeys", "temporal foreign keys", NotYet + TakeOver + "18"),
        ("CustomExclusion:Constraints", "exclusion constraints",
            "CodoMetis.EFCore.SchemaKit.PostgreSQL reads them: call UseSchemaKit() on the options builder of UseNpgsql."),
    ];

    /// <summary>5.x's marker of an index declared on a property of a complex type; its value is <c>true</c>.</summary>
    private const string IndexedProperty = "CustomIndex:IsIndexed";

    /// <summary>
    /// The first annotation of 5.x on the entity type, or on a property of it, that declares an
    /// object, or null. Whether that is an error depends on whose model it is, which is the
    /// planner's to say.
    /// </summary>
    public static string? DeclaredIn(IReadOnlyEntityType entityType)
    {
        foreach (var (annotation, _, _) in OnEntityTypes)
        {
            if (entityType.FindAnnotation(annotation) is { } found && HoldsSomething(found.Value)) return annotation;
        }

        return IndexedPropertyOf(entityType, path: "") is null ? null : IndexedProperty;
    }

    /// <summary>Throws when the entity type carries a declaration of 5.x that none of the readers reads.</summary>
    public static void RequireRead(IReadOnlyEntityType entityType, IReadOnlyList<ILegacyDeclarationSource> readers)
    {
        foreach (var (annotation, what, advice) in OnEntityTypes)
        {
            if (entityType.FindAnnotation(annotation) is not { } found || !HoldsSomething(found.Value)) continue;
            if (readers.Any(reader => reader.Annotation == annotation)) continue;

            throw new InvalidOperationException(SchemaKitStrings.UnreadLegacyAnnotation(entityType.ShortName(), what, annotation, advice));
        }

        if (IndexedPropertyOf(entityType, path: "") is { } property)
            throw new InvalidOperationException(SchemaKitStrings.UnreadLegacyAnnotation(
                entityType.ShortName(), $"an index on {property}", IndexedProperty, NotYet + TakeOver + "27"));
    }

    // 5.x read an empty annotation, and an empty list, as no declaration: it left the annotation
    // behind when the last declaration was replaced.
    private static bool HoldsSomething(object? value) => value switch
    {
        null        => false,
        string text => text.Trim() is not ("" or "[]"),
        _           => true,
    };

    /// <summary>The path of the first property marked as indexed, through complex properties, or null.</summary>
    private static string? IndexedPropertyOf(IReadOnlyTypeBase type, string path)
    {
        foreach (var property in type.GetProperties())
        {
            if (property.FindAnnotation(IndexedProperty)?.Value is true) return path + property.Name;
        }

        foreach (var complex in type.GetComplexProperties())
        {
            if (IndexedPropertyOf(complex.ComplexType, $"{path}{complex.Name}.") is { } found) return found;
        }

        return null;
    }

    private const string Replay = TakeOver + "28";

    /// <summary>
    /// What 5.x stamped on a <see cref="MigrationOperation" /> for its own SQL generators to render,
    /// and so what sits in a migration it scaffolded: the annotation, and what its generator made of
    /// the operation that carries it. The provider's generator does not read them and writes the
    /// operation as if they were not there (#110).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is 5.x's and is finished. It is taken from the two generators 5.x had and from the
    /// differs that stamped for them, over every published version:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>CustomTemporal:WithoutOverlaps</c> on an <see cref="AddUniqueConstraintOperation" />, its value
    /// the period column, up to 5.0.1; from 5.0.2 the constraint is scaffolded as SQL.
    /// <c>NpgsqlComplexIndexSqlGenerator</c> wrote <c>UNIQUE (…, period WITHOUT OVERLAPS)</c>.
    /// Npgsql's generator writes a plain <c>UNIQUE</c> under the same name, which applies.
    /// </item>
    /// <item>
    /// <c>CustomTemporal:ForeignKeyDependentPeriod</c> and <c>CustomTemporal:ForeignKeyPrincipalPeriod</c>
    /// on an <see cref="AddForeignKeyOperation" />, in 5.0.1 only. The same generator wrote
    /// <c>FOREIGN KEY (…, PERIOD a) REFERENCES t (…, PERIOD b)</c>; Npgsql's writes a plain foreign
    /// key. 5.x rendered the temporal one only with both annotations; either alone is refused here.
    /// </item>
    /// <item>
    /// <c>CustomIndex:IndexParts</c> on a <see cref="CreateIndexOperation" />, in every version to
    /// 5.6.0, for an index with an expression or <c>NULLS FIRST</c>/<c>LAST</c>. The same generator
    /// wrote the index from the annotation's parts. The operation's columns are the parts' text
    /// and a last column <c>__requires_UseNpgsqlComplexIndexes__</c>, so that any other generator
    /// fails at apply on a column that does not exist. SQLite, which reads a double-quoted name
    /// that is no column as a string unless that is switched off, creates an index over a constant.
    /// </item>
    /// <item>
    /// <c>CustomIndex:AfterTableRebuild</c> on a <see cref="CreateIndexOperation" />, from 5.5.0, on
    /// SQLite. <c>SqliteRebuildMigrationsSqlGenerator</c> moved the create behind the table rebuild
    /// and took <c>__requires_UseComplexIndexes__()</c> off its filter. SQLite's generator runs it
    /// where it stands and fails on that function.
    /// </item>
    /// </list>
    /// <para>
    /// 5.x had no generator for SQL Server: what it scaffolded there is EF Core's own. The provider
    /// annotations it forwarded to an index (<c>Npgsql:…</c>, <c>SqlServer:…</c>) are the provider's
    /// and its generator reads them.
    /// </para>
    /// <para>
    /// The guard is the same on every provider, and holds an annotation whatever its value and
    /// whatever operation carries it: one in a shape 5.x never wrote is not passed through either.
    /// An annotation leaves the guard when an <see cref="IMigrationOperationRewriter" /> takes it off
    /// the operation, or replaces the operation (#28).
    /// </para>
    /// </remarks>
    private static readonly (string Annotation, string Rendered)[] OnOperations =
    [
        ("CustomTemporal:WithoutOverlaps", "a unique constraint WITHOUT OVERLAPS, where the provider writes a plain unique constraint"),
        ("CustomTemporal:ForeignKeyDependentPeriod", "a temporal foreign key (PERIOD), where the provider writes a plain foreign key"),
        ("CustomTemporal:ForeignKeyPrincipalPeriod", "a temporal foreign key (PERIOD), where the provider writes a plain foreign key"),
        ("CustomIndex:IndexParts", "an index over the expressions and the null ordering the annotation holds, where the provider writes the operation's placeholder columns"),
        ("CustomIndex:AfterTableRebuild", "the index after SQLite's table rebuild and without the marker in its filter, where the provider runs it in place with the marker"),
    ];

    /// <summary>
    /// Throws when an operation still carries an annotation only 5.x's generators rendered. Called
    /// with the list the rewriters return, so what a rewriter handled is not met here.
    /// </summary>
    public static void RequireRewritten(IReadOnlyList<MigrationOperation> operations)
    {
        foreach (var operation in operations)
        {
            foreach (var (annotation, rendered) in OnOperations)
            {
                if (operation.FindAnnotation(annotation) is null) continue;

                throw new InvalidOperationException(SchemaKitStrings.UnreplayedLegacyOperation(Describe(operation), annotation, rendered, Replay));
            }
        }
    }

    private static string Describe(MigrationOperation operation)
    {
        var name = operation switch
        {
            CreateIndexOperation index              => index.Name,
            AddUniqueConstraintOperation constraint => constraint.Name,
            AddForeignKeyOperation foreignKey       => foreignKey.Name,
            _                                       => null,
        };
        var described = name is null ? operation.GetType().Name : $"{operation.GetType().Name} '{name}'";

        return operation is ITableMigrationOperation { Table: { } table } on
            ? $"{described} on table '{(on.Schema is null ? table : $"{on.Schema}.{table}")}'"
            : described;
    }
}
