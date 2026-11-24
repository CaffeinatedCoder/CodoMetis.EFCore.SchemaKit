using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace CodoMetis.EFCore.SchemaKit.Metadata.Internal;

/// <summary>
/// Runs when a model is finalized, after every other convention of that moment: each declaration
/// that was held as it was written becomes its stored form, under the key of what it now says.
/// </summary>
/// <remarks>
/// <para>
/// From here on the model holds text alone, and so does everything made from it: the runtime model,
/// a snapshot, a compiled model. None of them is bound again; a snapshot's model is finalized
/// without this convention (spike F).
/// </para>
/// <para>
/// A declaration is refused where it is written when it says what another says. Two that were
/// written differently can still be one object once they are SQL, and that shows only here.
/// </para>
/// </remarks>
internal sealed class SchemaKitBindingConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var conventionEntityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var entityType = (IMutableEntityType)conventionEntityType;
            Dictionary<string, string> bound = new(StringComparer.Ordinal);

            foreach (var (held, declaration) in UnboundDeclarations.Of(entityType).ToList())
            {
                SqlTemplate Bind(UnboundLambda lambda) => DeclarationBinder.Bind(lambda, entityType);

                var key = DeclarationCodec.Key(declaration, Bind);
                var value = DeclarationCodec.Value(declaration, Bind);

                if (bound.TryGetValue(key, out var earlier))
                    throw new InvalidOperationException(SchemaKitStrings.BoundToOneKey(entityType.DisplayName(), earlier, DeclarationCodec.Describe(declaration)));
                if (key != held && entityType.FindAnnotation(key) is { Value: var stored })
                    throw new InvalidOperationException(SchemaKitStrings.BoundToOneKey(
                        entityType.DisplayName(), $"{declaration.Owner} {stored}", DeclarationCodec.Describe(declaration)));

                bound.Add(key, DeclarationCodec.Describe(declaration));
                entityType.RemoveAnnotation(held);
                entityType.SetAnnotation(key, value);
            }
        }
    }
}

/// <summary>Makes segments of a lambda for one entity type.</summary>
internal static class DeclarationBinder
{
    /// <remarks>
    /// The translator asks nothing of the model yet, so the entity type is not read: what it knows
    /// of a column's nullability and of a property's converter comes with issue 180.
    /// </remarks>
    public static SqlTemplate Bind(UnboundLambda lambda, IReadOnlyEntityType entityType)
    {
        _ = entityType;
#pragma warning disable SK9001 // The kernel's own translator writes placeholders; this is where they become segments.
        return SqlTemplate.FromPlaceholders(lambda.Translate());
#pragma warning restore SK9001
    }
}
