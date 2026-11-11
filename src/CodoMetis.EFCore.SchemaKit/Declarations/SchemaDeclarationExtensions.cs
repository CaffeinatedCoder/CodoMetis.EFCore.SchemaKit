using System.Diagnostics.CodeAnalysis;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// Declaring, reading and amending schema objects on EF Core's own metadata. A provider's typed
/// entry point, a convention and a contributor's package all go through these.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public static class SchemaDeclarationExtensions
{
    /// <summary>
    /// Stores each declaration as its own annotation on the entity type. All of them, or none: a
    /// declaration whose key is taken, by one already on the entity type or by another in the same
    /// call, is refused before anything is stored.
    /// </summary>
    /// <remarks>
    /// A declaration never replaces another. Two declarations with the same explicit name, or two
    /// without a name that say the same, are one mistake and not an update:
    /// <see cref="Redeclare{TDeclaration}" /> is how a declaration is changed.
    /// </remarks>
    /// <param name="entityType">The entity type the objects belong to.</param>
    /// <param name="declarations">The declarations.</param>
    /// <exception cref="InvalidOperationException">
    /// <c>UseSchemaKit()</c> is not on the context's options, or a declaration's key is taken.
    /// </exception>
    public static void Declare(this IMutableEntityType entityType, IEnumerable<ISchemaObject> declarations)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(declarations);
        SchemaKitModelStamp.Require(entityType.Model, entityType.DisplayName());

        Dictionary<string, (ISchemaObject Declaration, string Value)> added = new(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            ArgumentNullException.ThrowIfNull(declaration, nameof(declarations));
            var key = DeclarationCodec.Key(declaration);

            if (added.TryGetValue(key, out var earlier))
                throw new InvalidOperationException(SchemaKitStrings.DeclaredTwice(
                    entityType.DisplayName(), DeclarationCodec.Describe(earlier.Declaration), DeclarationCodec.Describe(declaration)));
            if (entityType.FindAnnotation(key) is not null)
                throw new InvalidOperationException(SchemaKitStrings.AlreadyDeclared(
                    entityType.DisplayName(), DeclarationCodec.Describe(declaration)));

            added.Add(key, (declaration, DeclarationCodec.Value(declaration)));
        }

        foreach (var (key, (_, value)) in added)
            entityType.SetAnnotation(key, value);
    }

    /// <summary>
    /// Replaces each declaration of one union on the entity type with <paramref name="amend" />'s
    /// answer for it: the one way a stored declaration changes. Every resulting key is computed
    /// first, and two declarations that would end up under one are refused before anything changes.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="amend">Returns the declaration to keep in place of the one it is given; the same value to leave it.</param>
    /// <typeparam name="TDeclaration">The contributor's declaration union.</typeparam>
    /// <returns>How many declarations changed.</returns>
    public static int Redeclare<TDeclaration>(this IMutableEntityType entityType, Func<TDeclaration, TDeclaration> amend)
        where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(amend);
        SchemaKitModelStamp.Require(entityType.Model, entityType.DisplayName());

        var current = Stored<TDeclaration>(entityType).ToList();
        Dictionary<string, (TDeclaration Declaration, string Value)> result = new(StringComparer.Ordinal);

        foreach (var (_, _, declaration) in current)
        {
            var amended = amend(declaration) ?? throw new InvalidOperationException(SchemaKitStrings.AmendReturnedNull(entityType.DisplayName()));
            var key = DeclarationCodec.Key(amended);

            if (result.TryGetValue(key, out var earlier))
                throw new InvalidOperationException(SchemaKitStrings.RedeclaredOntoOneKey(
                    entityType.DisplayName(), DeclarationCodec.Describe(earlier.Declaration), DeclarationCodec.Describe(amended)));

            result.Add(key, (amended, DeclarationCodec.Value(amended)));
        }

        var changed = current.Count(c => !result.TryGetValue(c.Key, out var kept) || kept.Value != c.Value);

        foreach (var (key, _, _) in current.Where(c => !result.ContainsKey(c.Key)))
            entityType.RemoveAnnotation(key);
        foreach (var (key, (_, value)) in result)
            entityType.SetAnnotation(key, value);

        return changed;
    }

    /// <summary>
    /// The declarations of one union on the entity type, as the values they were declared as, in
    /// the order of their keys. The differ reads the same.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <typeparam name="TDeclaration">The contributor's declaration union.</typeparam>
    public static IReadOnlyList<TDeclaration> GetDeclarations<TDeclaration>(this IReadOnlyEntityType entityType)
        where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return [.. Stored<TDeclaration>(entityType).Select(s => s.Declaration)];
    }

    /// <summary>The declaration of one union with this explicit name, or null.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="name">The explicit name, compared as the database compares names of the kind.</param>
    /// <typeparam name="TDeclaration">The contributor's declaration union.</typeparam>
    /// <exception cref="InvalidOperationException">More than one declaration has the name, which two cases of a union may.</exception>
    public static TDeclaration? FindDeclaration<TDeclaration>(this IReadOnlyEntityType entityType, string name)
        where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var found = entityType.GetDeclarations<TDeclaration>()
            .Where(d => d.Name is { } declared && DeclarationCodec.SameName(declared, name, d.NameComparison))
            .ToList();

        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => throw new InvalidOperationException(SchemaKitStrings.NameIsAmbiguous(entityType.DisplayName(), TDeclaration.OwnerId, name)),
        };
    }

    private static IEnumerable<(string Key, string Value, TDeclaration Declaration)> Stored<TDeclaration>(IReadOnlyEntityType entityType)
        where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    {
        var prefix = DeclarationCodec.KeyPrefix(TDeclaration.OwnerId);
        return entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => a.Value is string value
                ? (a.Name, value, DeclarationCodec.Read<TDeclaration>(a.Name, value))
                : throw new InvalidOperationException(SchemaKitStrings.DeclarationNotJson(a.Name)));
    }
}
