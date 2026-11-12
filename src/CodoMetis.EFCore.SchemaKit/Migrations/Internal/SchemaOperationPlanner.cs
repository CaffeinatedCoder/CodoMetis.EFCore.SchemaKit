using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// The planner: what the declarations of two models amount to, as statements around the inner
/// differ's operations.
/// </summary>
/// <remarks>
/// <para>
/// Both sides are read the same way: each declaration through its contributor, each descriptor
/// through the kernel's resolver. Equal descriptors are the same object and produce nothing, which
/// is what makes a snapshot diff empty; nothing here compares SQL text.
/// </para>
/// <para>
/// What it needs is resolved when the first declaration is met. A context that declares nothing gets
/// an empty plan and never builds a dialect, which reads the design-time model.
/// </para>
/// <para>
/// It does not read the inner differ's operations yet (issue 24): a descriptor that reads a renamed
/// column or table differs from its source and is rebuilt, and an object the database loses with an
/// operation of EF Core's while both models declare it unchanged is not created again.
/// </para>
/// </remarks>
internal sealed class SchemaOperationPlanner(IServiceProvider services) : ISchemaOperationPlanner
{
    private SchemaContributorRegistry Registry => field ??= services.GetRequiredService<SchemaContributorRegistry>();

    private SchemaResolverFactory Resolvers => field ??= services.GetRequiredService<SchemaResolverFactory>();

    private ISchemaDialect Dialect => field ??= services.GetRequiredService<ISchemaDialect>();

    /// <summary>A declaration as what the database will hold, with the contributor that renders it.</summary>
    private sealed record Resolved(ISchemaContributor Contributor, ISchemaDescriptor Descriptor)
    {
        /// <summary>
        /// Equal descriptors of one contributor. Two contributors may share a descriptor type, and
        /// an equal descriptor of another's is another object: it is rendered by other statements.
        /// </summary>
        public bool EqualTo(Resolved other) => SameKindAs(other) && Descriptor.Equals(other.Descriptor);

        public bool SameObjectAs(Resolved other)
            => SameKindAs(other) && Descriptor.Scope.Equals(other.Descriptor.Scope) && Descriptor.Name.Equals(other.Descriptor.Name);

        public bool SameButForTheNameAs(Resolved other)
            => SameKindAs(other) && Descriptor.WithName(other.Descriptor.Name).Equals(other.Descriptor);

        // A contributor never sees another's descriptors, so two of different owners are never one.
        private bool SameKindAs(Resolved other)
            => ReferenceEquals(Contributor, other.Contributor) && Descriptor.GetType() == other.Descriptor.GetType();
    }

    public SchemaOperationPlan Plan(IRelationalModel? source, IRelationalModel? target, IReadOnlyList<MigrationOperation> inner)
    {
        var from = Resolve(source);
        var to   = Resolve(target);
        if (from.Count == 0 && to.Count == 0) return SchemaOperationPlan.Empty;

        // The order statements are written in is the order of the models, whatever the matching
        // below takes out of the two lists.
        List<Resolved> sourceOrder = [.. from];
        List<Resolved> targetOrder = [.. to];

        // Equal descriptors are the same object: nothing to do, whatever their declarations say.
        Pair(from, to, (s, t) => s.EqualTo(t));

        // What is left on both sides and is the same object by name, or the same but for its name,
        // changed. How it gets from the one to the other is the contributor's answer.
        var changed = Pair(from, to, (s, t) => s.SameObjectAs(t));
        changed.AddRange(Pair(from, to, (s, t) => s.SameButForTheNameAs(t)));

        var inPlace = new SchemaSql(Dialect);
        HashSet<Resolved> dropped = new(from, ReferenceEqualityComparer.Instance);
        HashSet<Resolved> created = new(to, ReferenceEqualityComparer.Instance);

        foreach (var (was, becomes) in changed)
        {
            switch (was.Contributor.Plan(was.Descriptor, becomes.Descriptor, inPlace))
            {
                case SchemaChange.None:
                case SchemaChange.InPlace:
                    break;

                case SchemaChange.Rebuild:
                    // What tables are made of is created before them and dropped after them, so it
                    // cannot be taken away and put back around them.
                    if (was.Contributor.Placement.IsBeforeTables)
                        throw new InvalidOperationException(SchemaKitStrings.RebuildBeforeTables(was.Contributor.GetType(), becomes.Descriptor.Name));

                    dropped.Add(was);
                    created.Add(becomes);
                    break;
            }
        }

        List<Resolved> drops = [.. sourceOrder.Where(dropped.Contains)];
        List<Resolved> creates = [.. targetOrder.Where(created.Contains)];

        var before = new SchemaSql(Dialect);
        var after  = new SchemaSql(Dialect);

        // Before EF Core's operations: what is attached to tables goes first, the latest placement
        // first, then what tables are made of is created.
        foreach (var drop in AttachedToTables(drops).OrderByDescending(d => d.Contributor.Placement.Rank).ThenByDescending(drops.IndexOf))
            drop.Contributor.Drop(drop.Descriptor, before);
        foreach (var create in MadeBeforeTables(creates))
            create.Contributor.Create(create.Descriptor, before);

        // After them: changes in place, then creates by placement, then what tables were made of.
        foreach (var create in AttachedToTables(creates).OrderBy(c => c.Contributor.Placement.Rank).ThenBy(creates.IndexOf))
            create.Contributor.Create(create.Descriptor, after);
        foreach (var drop in MadeBeforeTables(drops).Reverse())
            drop.Contributor.Drop(drop.Descriptor, after);

        return new SchemaOperationPlan(before.Operations, [.. inPlace.Operations, .. after.Operations]);
    }

    private static IEnumerable<Resolved> AttachedToTables(IEnumerable<Resolved> objects) => objects.Where(o => !o.Contributor.Placement.IsBeforeTables);

    private static IEnumerable<Resolved> MadeBeforeTables(IEnumerable<Resolved> objects) => objects.Where(o => o.Contributor.Placement.IsBeforeTables);

    /// <summary>
    /// Takes the pairs for which <paramref name="match" /> holds out of both lists: each target with
    /// the first source that matches it, so that two equal declarations pair one to one.
    /// </summary>
    private static List<(Resolved Source, Resolved Target)> Pair(List<Resolved> sources, List<Resolved> targets, Func<Resolved, Resolved, bool> match)
    {
        List<(Resolved, Resolved)> pairs = [];
        foreach (var target in targets.ToList())
        {
            var index = sources.FindIndex(source => match(source, target));
            if (index < 0) continue;

            pairs.Add((sources[index], target));
            sources.RemoveAt(index);
            targets.Remove(target);
        }

        return pairs;
    }

    /// <summary>Every declaration of a model as a descriptor, in the order of its entity types and of their annotation keys.</summary>
    private List<Resolved> Resolve(IRelationalModel? model)
    {
        List<Resolved> resolved = [];
        if (model is null) return resolved;

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            var owners = entityType.GetAnnotations()
                .Select(annotation => DeclarationCodec.OwnerOf(annotation.Name))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            foreach (var owner in owners)
            {
                // A declaration nothing renders would be left out of every migration, in silence.
                var contributor = Registry.Find(owner)
                                  ?? throw new InvalidOperationException(SchemaKitStrings.NoContributorForOwner(entityType.ShortName(), owner));

                foreach (var declaration in ((IDeclarationSource)contributor).Declarations(entityType))
                    resolved.Add(new Resolved(contributor, contributor.Resolve(declaration, Resolvers.Create(entityType, declaration))));
            }
        }

        return resolved;
    }
}

/// <summary>
/// How the kernel reads a contributor's declarations without knowing their type.
/// <see cref="SchemaContributor{TDeclaration, TDescriptor}" /> implements it, and the registry takes
/// no contributor that does not derive from that class.
/// </summary>
internal interface IDeclarationSource
{
    IReadOnlyList<ISchemaObject> Declarations(IReadOnlyEntityType entityType);
}
