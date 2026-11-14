using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit.Contributors.Internal;

/// <summary>
/// The contributors of a context, by owner. A declaration is routed to the one contributor that has
/// its owner, so two for one owner are refused: which of them rendered would depend on the order
/// they were registered in.
/// </summary>
/// <remarks>
/// A diff-time service. Contributors may need the dialect, which reads the design-time model, so
/// nothing that is resolved while a model is built may take this in its constructor.
/// </remarks>
internal sealed class SchemaContributorRegistry
{
    private readonly Dictionary<string, ISchemaContributor> _byOwner = new(StringComparer.Ordinal);

    public SchemaContributorRegistry(IEnumerable<ISchemaContributor> contributors)
    {
        foreach (var contributor in contributors)
        {
            // AddSchemaContributor refuses it too, but a contributor can be added to the collection
            // without that method.
            if (!DerivesFromTheBaseClass(contributor.GetType()))
                throw new InvalidOperationException(SchemaKitStrings.ContributorNotDerived(contributor.GetType()));

            var owner = contributor.Owner;
            if (string.IsNullOrEmpty(owner))
                throw new InvalidOperationException(SchemaKitStrings.InvalidOwner(contributor.GetType(), owner));
            if (contributor.Placement is null)
                throw new InvalidOperationException(SchemaKitStrings.ContributorWithoutPlacement(contributor.GetType()));
            if (!_byOwner.TryAdd(owner, contributor))
                throw new InvalidOperationException(SchemaKitStrings.TwoContributorsForOneOwner(owner, _byOwner[owner].GetType(), contributor.GetType()));
        }
    }

    /// <summary>
    /// Whether a contributor is a <see cref="SchemaContributor{TDeclaration, TDescriptor}" />. What
    /// makes <c>Plan</c> and <c>ImpactOf</c> trustworthy is in that class: the refusal of an answer
    /// that disagrees with the statements, of a null, of a descriptor that is not a record. A class
    /// that implements the interface itself would be called without any of it.
    /// </summary>
    public static bool DerivesFromTheBaseClass(Type contributor)
    {
        for (var type = contributor.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SchemaContributor<,>)) return true;
        }

        return false;
    }

    /// <summary>
    /// The owner of a contributor, read from its type: <c>TDeclaration.OwnerId</c> of the base
    /// class, which is what <see cref="SchemaContributor{TDeclaration, TDescriptor}.Owner" /> answers.
    /// Null for a class that does not derive from it.
    /// </summary>
    public static string? OwnerOf(Type contributor)
    {
        for (var type = contributor.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SchemaContributor<,>))
                return (string?)typeof(SchemaContributorRegistry)
                    .GetMethod(nameof(OwnerId), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(type.GetGenericArguments()[0])
                    .Invoke(null, null);
        }

        return null;
    }

    private static string OwnerId<TDeclaration>() where TDeclaration : class, ISchemaDeclaration<TDeclaration>
        => TDeclaration.OwnerId;

    public IReadOnlyCollection<ISchemaContributor> All => _byOwner.Values;

    public ISchemaContributor? Find(string owner) => _byOwner.GetValueOrDefault(owner);
}

/// <summary>
/// That a contributor is registered, as a value: what the model's stamp is built from while the
/// model is created, when no contributor may be resolved yet. <c>AddSchemaContributor</c> adds it.
/// </summary>
/// <param name="Owner">The contributor's owner.</param>
/// <param name="Contributor">The contributor's type.</param>
internal sealed record SchemaContributorRegistration(string Owner, Type Contributor);
