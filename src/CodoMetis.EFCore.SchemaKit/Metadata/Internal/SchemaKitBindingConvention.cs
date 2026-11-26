using System.Runtime.CompilerServices;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

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
/// <para>
/// No property holds its type mapping yet at this moment (spike F, question 1), so a mapping is
/// asked of the provider's mapping source. That is a singleton that needs no model, which is why
/// it may be resolved while one is built.
/// </para>
/// </remarks>
internal sealed class SchemaKitBindingConvention(IServiceProvider services) : IModelFinalizingConvention
{
    /// <summary>What was bound, by model, so that the finalized model can be asked again.</summary>
    internal static readonly ConditionalWeakTable<object, List<(string EntityType, string Key, ISchemaObject Declaration)>> Bound = new();

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        IRelationalTypeMappingSource? mappings = null;
        var kept = Bound.GetOrCreateValue(modelBuilder.Metadata);

        foreach (var conventionEntityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var entityType = (IMutableEntityType)conventionEntityType;
            Dictionary<string, string> bound = new(StringComparer.Ordinal);

            foreach (var (held, declaration) in UnboundDeclarations.Of(entityType).ToList())
            {
                mappings ??= services.GetRequiredService<IRelationalTypeMappingSource>();
                SqlTemplate Bind(UnboundLambda lambda) => DeclarationBinder.Bind(lambda, entityType, mappings);

                var key = DeclarationCodec.Key(declaration, Bind);
                var value = DeclarationCodec.Value(declaration, Bind);

                if (bound.TryGetValue(key, out var earlier))
                    throw new InvalidOperationException(SchemaKitStrings.BoundToOneKey(entityType.DisplayName(), earlier, DeclarationCodec.Describe(declaration)));
                if (key != held && entityType.FindAnnotation(key) is { Value: var stored })
                    throw new InvalidOperationException(SchemaKitStrings.BoundToOneKey(
                        entityType.DisplayName(), $"{declaration.Owner} {stored}", DeclarationCodec.Describe(declaration)));

                bound.Add(key, DeclarationCodec.Describe(declaration));
                kept.Add((entityType.Name, key, declaration));
                entityType.RemoveAnnotation(held);
                entityType.SetAnnotation(key, value);
            }
        }
    }
}

/// <summary>
/// Runs first once a model is finalized: each declaration that was bound is bound a second time,
/// against the finalized model, whose properties hold their mappings. A value that comes out
/// differently was bound from a fact that changed afterwards.
/// </summary>
/// <remarks>
/// SchemaKit's binding is the last finalizing convention it can see, and a package registered
/// after it adds its own behind that. A convention there that makes a column nullable or gives a
/// property another conversion leaves a stored condition that no longer says what the lambda
/// says, and nothing would fail (spike F, question 1).
/// </remarks>
internal sealed class SchemaKitBindingCheckConvention : IModelFinalizedConvention
{
    public IModel ProcessModelFinalized(IModel model)
    {
        if (!SchemaKitBindingConvention.Bound.TryGetValue(model, out var kept)) return model;

        foreach (var (entityTypeName, key, declaration) in kept)
        {
            if (model.FindEntityType(entityTypeName) is not { } entityType) continue;

            var again = DeclarationCodec.Value(declaration, lambda => DeclarationBinder.Bind(lambda, entityType, mappings: null));
            var stored = entityType.FindAnnotation(key)?.Value as string;
            if (again != stored)
                throw new InvalidOperationException(SchemaKitStrings.ModelChangedAfterBinding(
                    entityType.DisplayName(), DeclarationCodec.Describe(declaration), stored, again));
        }

        SchemaKitBindingConvention.Bound.Remove(model);
        return model;
    }
}

/// <summary>Makes segments of a lambda for one entity type.</summary>
internal static class DeclarationBinder
{
    /// <param name="lambda">The lambda.</param>
    /// <param name="entityType">The entity type the declaration is on; null for what the C# types say alone, which is for a test of the translator.</param>
    /// <param name="mappings">Where a property's mapping comes from while the model is being finalized; null for a finalized model.</param>
    public static SqlTemplate Bind(UnboundLambda lambda, IReadOnlyEntityType? entityType, IRelationalTypeMappingSource? mappings = null)
    {
        var facts = entityType is null ? null : new ModelFacts(entityType, mappings);
        var translated = lambda.Translate(facts);

        // Without a model a value only the model can write is a mark, and a mark is not SQL.
        if (translated.Contains(LambdaTranslator.BoundLater, StringComparison.Ordinal))
            throw new NotSupportedException(SchemaKitStrings.NeedsTheModel(lambda.ToString()));

        // A path in SQL written by hand is the one path no compiler saw. It is asked of the model
        // here, where every other part of the lambda becomes SQL, and not at the first diff.
        return RawSql.Template(translated, facts is null ? null : facts.Require);
    }
}
