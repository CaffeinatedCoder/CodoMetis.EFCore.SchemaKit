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
/// <remarks>
/// Reading and amending are a consumer's: a convention reads what is declared and changes it, and
/// needs no opt-in for that. <see cref="Declare" /> alone is marked experimental: a consumer
/// declares through a provider's typed entry point, and only a contributor's package calls it.
/// </remarks>
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
    /// <c>UseSchemaKit()</c> is not on the context's options, they register no contributor for a
    /// declaration's owner, or a declaration's key is taken.
    /// </exception>
    [Experimental(SchemaKitDiagnosticIds.ContributorContract)]
    public static void Declare(this IMutableEntityType entityType, IEnumerable<ISchemaObject> declarations)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(declarations);
        SchemaKitModelStamp.Require(entityType.Model, entityType.DisplayName());

        // A declaration that holds a lambda is not SQL until the model is finalized, so it is held
        // as it is until then (issue 114). With a name its key is known now and is checked as any
        // other; without one its key is its content, and here it is compared as it was written.
        Dictionary<string, (ISchemaObject Declaration, object Value)> added = new(StringComparer.Ordinal);
        List<ISchemaObject> unbound = [];
        foreach (var declaration in declarations)
        {
            ArgumentNullException.ThrowIfNull(declaration, nameof(declarations));
            SchemaKitModelStamp.Require(entityType.Model, entityType.DisplayName(), declaration.Owner, declaration.GetType());

            var isUnbound = DeclarationCodec.IsUnbound(declaration);
            if (isUnbound && declaration.Name is null)
            {
                if (unbound.Find(declaration.Equals) is { } earlierUnbound)
                    throw new InvalidOperationException(SchemaKitStrings.DeclaredTwice(
                        entityType.DisplayName(), DeclarationCodec.Describe(earlierUnbound), DeclarationCodec.Describe(declaration)));
                if (UnboundDeclarations.Of(entityType, declaration.Owner).Any(held => held.Declaration.Equals(declaration)))
                    throw new InvalidOperationException(SchemaKitStrings.AlreadyDeclared(
                        entityType.DisplayName(), DeclarationCodec.Describe(declaration)));

                unbound.Add(declaration);
                continue;
            }

            var key = DeclarationCodec.Key(declaration);

            if (added.TryGetValue(key, out var earlier))
                throw new InvalidOperationException(SchemaKitStrings.DeclaredTwice(
                    entityType.DisplayName(), DeclarationCodec.Describe(earlier.Declaration), DeclarationCodec.Describe(declaration)));
            if (entityType.FindAnnotation(key) is not null)
                throw new InvalidOperationException(SchemaKitStrings.AlreadyDeclared(
                    entityType.DisplayName(), DeclarationCodec.Describe(declaration)));

            added.Add(key, (declaration, isUnbound ? declaration : DeclarationCodec.Value(declaration)));
        }

        foreach (var (key, (_, value)) in added)
            entityType.SetAnnotation(key, value);
        foreach (var declaration in unbound)
            entityType.SetAnnotation(UnboundDeclarations.NextKey(entityType, declaration.Owner), declaration);
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
        SchemaKitModelStamp.Require(entityType.Model, entityType.DisplayName(), TDeclaration.OwnerId, typeof(TDeclaration));

        var current = Stored<TDeclaration>(entityType).ToList();

        // What each declaration becomes, and under which key: its own for one that is SQL or has a
        // name, and for one that is neither the place it was written at, so that it keeps its turn.
        List<(string? Key, object Value, TDeclaration Declaration, string? Was)> result = [];
        foreach (var (was, _, declaration) in current)
        {
            var amended = amend(declaration) ?? throw new InvalidOperationException(SchemaKitStrings.AmendReturnedNull(entityType.DisplayName()));
            var isUnbound = DeclarationCodec.IsUnbound(amended);
            var key = isUnbound && amended.Name is null ? null : DeclarationCodec.Key(amended);

            var earlier = result.Find(r => key is null ? r.Key is null && r.Declaration.Equals(amended) : r.Key == key);
            if (earlier.Declaration is not null)
                throw new InvalidOperationException(SchemaKitStrings.RedeclaredOntoOneKey(
                    entityType.DisplayName(), DeclarationCodec.Describe(earlier.Declaration), DeclarationCodec.Describe(amended)));

            result.Add((key, isUnbound ? amended : DeclarationCodec.Value(amended), amended, was));
        }

        var kept = result
            .Select(r => r.Key ?? (UnboundDeclarations.IsHeldKey(r.Was!) ? r.Was! : null))
            .ToList();
        var changed = current.Count(c =>
        {
            var index = kept.IndexOf(c.Key);
            return index < 0 || !result[index].Value.Equals(c.Value);
        });

        foreach (var (key, _, _) in current)
            entityType.RemoveAnnotation(key);
        for (var i = 0; i < result.Count; i++)
        {
            if (kept[i] is { } key) entityType.SetAnnotation(key, result[i].Value);
        }
        for (var i = 0; i < result.Count; i++)
        {
            if (kept[i] is null) entityType.SetAnnotation(UnboundDeclarations.NextKey(entityType, TDeclaration.OwnerId), result[i].Value);
        }

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

    // In the order of their keys, and a declaration that is held for binding after those, in the
    // order written.
    private static IEnumerable<(string Key, object Value, TDeclaration Declaration)> Stored<TDeclaration>(IReadOnlyEntityType entityType)
        where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    {
        var prefix = DeclarationCodec.KeyPrefix(TDeclaration.OwnerId);
        return entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => a.Value switch
            {
                string value             => (a.Name, (object)value, DeclarationCodec.Read<TDeclaration>(a.Name, value)),
                TDeclaration declaration => (a.Name, declaration, declaration),
                _                        => throw new InvalidOperationException(SchemaKitStrings.DeclarationNotJson(a.Name)),
            });
    }
}

/// <summary>
/// The declarations an entity type holds as they were written, until its model is finalized: each
/// has a lambda that is not SQL yet. One with a name is under the key it will keep. One without is
/// under a key no stored declaration can have, numbered in the order it was written.
/// </summary>
internal static class UnboundDeclarations
{
    // A stored key ends in sixteen hexadecimal digits.
    private const char Held = '~';

    public static bool IsHeldKey(string key) => key.LastIndexOf(':') is var colon and >= 0 && colon + 1 < key.Length && key[colon + 1] == Held;

    public static string NextKey(IReadOnlyEntityType entityType, string owner)
    {
        var prefix = DeclarationCodec.KeyPrefix(owner) + Held;
        var next = entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(a => int.Parse(a.Name.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture) + 1)
            .DefaultIfEmpty(0)
            .Max();
        return prefix + next.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Every declaration held for binding, of one owner or of all: those with a name first, then the others in the order written.</summary>
    public static IEnumerable<(string Key, ISchemaObject Declaration)> Of(IReadOnlyEntityType entityType, string? owner = null)
    {
        var prefix = owner is null ? DeclarationCodec.Prefix : DeclarationCodec.KeyPrefix(owner);
        return entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith(prefix, StringComparison.Ordinal) && a.Value is ISchemaObject)
            .OrderBy(a => IsHeldKey(a.Name))
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => (a.Name, (ISchemaObject)a.Value!));
    }
}
