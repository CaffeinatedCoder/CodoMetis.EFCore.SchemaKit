using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

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
/// property-level index beside its <c>IsIndexed</c>, and what 5.x stamped on operations.
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
}
