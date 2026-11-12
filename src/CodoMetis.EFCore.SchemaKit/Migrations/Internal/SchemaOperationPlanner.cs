using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
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
/// The inner differ's operations are read, never changed: each is a <see cref="BaseChange" /> or
/// harmless (<see cref="BaseChanges" />), and the contributor of every object that depends on what
/// one changes is asked <c>ImpactOf</c>. That is how an object both models declare unchanged is
/// created again when the database loses it, and taken out of the way of an operation that cannot
/// run while it exists.
/// </para>
/// <para>
/// An object that follows EF Core's renames is compared as it will be after them: its declaration
/// is resolved again under the names the target gives the table and its columns. That is what
/// keeps a rename of a table or a column from rebuilding what reads it. A drop is written for the
/// object as it is before EF Core's operations, since drops run before them.
/// </para>
/// </remarks>
internal sealed class SchemaOperationPlanner(IServiceProvider services) : ISchemaOperationPlanner
{
    private SchemaContributorRegistry Registry => field ??= services.GetRequiredService<SchemaContributorRegistry>();

    private SchemaResolverFactory Resolvers => field ??= services.GetRequiredService<SchemaResolverFactory>();

    private ISchemaDialect Dialect => field ??= services.GetRequiredService<ISchemaDialect>();

    // Readers of an earlier package's annotations. They are made without the dialect, so asking
    // for them builds nothing a context without declarations must not build.
    private IReadOnlyList<ILegacyDeclarationSource> Legacy => field ??= [.. services.GetServices<ILegacyDeclarationSource>()];

    private bool IsSqlite => services.GetRequiredService<IDatabaseProvider>().Name == "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>A declaration as what the database will hold, with the contributor that renders it.</summary>
    /// <param name="Contributor">The contributor of the declaration's owner.</param>
    /// <param name="Descriptor">What is compared: for the source side, the object as it is after EF Core's operations.</param>
    /// <param name="EntityType">The entity type the declaration is on.</param>
    /// <param name="Declaration">The declaration.</param>
    private sealed record Resolved(ISchemaContributor Contributor, ISchemaDescriptor Descriptor, IReadOnlyEntityType EntityType, ISchemaObject Declaration)
    {
        /// <summary>The object as the database has it before EF Core's operations, which is what a drop names.</summary>
        public ISchemaDescriptor Before { get; init; } = Descriptor;

        /// <summary>What EF Core's operations do to the object: of the contributor's answers, the one that asks most.</summary>
        public Impact Impact { get; init; } = new Impact.Unaffected();

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
        var from = Resolve(source, isTarget: false);
        var to   = Resolve(target, isTarget: true);
        if (from.Count == 0 && to.Count == 0) return SchemaOperationPlan.Empty;

        // What an operation of a type nobody has looked at does to these objects is not known.
        var changes = BaseChanges.Of(inner, Dialect, IsSqlite);
        if (changes.Unknown.Count > 0)
            throw new InvalidOperationException(SchemaKitStrings.UnknownOperation(changes.Unknown[0]));

        // The source side as EF Core's operations leave it, which is what the target is compared with.
        from = [.. from.Select(source => AfterOperations(source, changes))];

        // The order statements are written in is the order of the models, whatever the matching
        // below takes out of the two lists.
        List<Resolved> sourceOrder = [.. from];
        List<Resolved> targetOrder = [.. to];

        // Equal descriptors are the same object: nothing to do, unless an operation of EF Core's does something to it.
        var same = Pair(from, to, (s, t) => s.EqualTo(t));

        // What is left on both sides and is the same object by name, or the same but for its name,
        // changed. How it gets from the one to the other is the contributor's answer.
        var changed = Pair(from, to, (s, t) => s.SameObjectAs(t));
        changed.AddRange(Pair(from, to, (s, t) => s.SameButForTheNameAs(t)));

        var inPlace = new SchemaSql(Dialect);
        HashSet<Resolved> dropped = new(from, ReferenceEqualityComparer.Instance);
        HashSet<Resolved> created = new(to, ReferenceEqualityComparer.Instance);

        foreach (var (was, becomes) in same)
        {
            switch (Decided(was))
            {
                case Impact.RebuildAround:
                    dropped.Add(was);
                    created.Add(becomes);
                    break;

                // The database drops it with the operation, so there is nothing to drop, and the
                // target declares it, so it is created again.
                case Impact.Lost:
                    created.Add(becomes);
                    break;
            }
        }

        foreach (var (was, becomes) in changed)
        {
            // An operation that takes the object away, or cannot run while it is there, decides
            // before the contributor is asked how to get from the one to the other: statements
            // in place would be written for an object that is gone by the time they run.
            switch (Decided(was))
            {
                case Impact.RebuildAround:
                    dropped.Add(was);
                    created.Add(becomes);
                    continue;

                case Impact.Lost:
                    created.Add(becomes);
                    continue;
            }

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

        // Ahead of everything: the extensions what is created depends on. One statement for each,
        // however many objects need it, and whether one is needed at all is the dialect's answer
        // for the model the migration leads to. A dependency left unanswered here is an object
        // whose creation fails at apply, or on the one database that lacks the extension.
        //
        // An object that stays can come to need one too: a column's type changes, the database
        // rebuilds the object's index under the new type, and the operator class it then needs is
        // an extension's (#81). So what a target object depends on and its source did not counts
        // as well, though nothing is created.
        var needed = creates.SelectMany(create => Extensions(create.Descriptor))
            .Concat(same.Concat(changed).SelectMany(pair => Extensions(pair.Target.Descriptor).Except(Extensions(pair.Source.Before))))
            .Distinct();
        foreach (var extension in needed)
            Dialect.EnsureExtension(extension, target!.Model, before);

        // Before EF Core's operations: what is attached to tables goes first, the latest placement
        // first, then what tables are made of is created.
        foreach (var drop in AttachedToTables(drops).OrderByDescending(d => d.Contributor.Placement.Rank).ThenByDescending(drops.IndexOf))
            drop.Contributor.Drop(drop.Before, before);
        foreach (var create in MadeBeforeTables(creates))
            create.Contributor.Create(create.Descriptor, before);

        // After them: changes in place, then creates by placement, then what tables were made of.
        foreach (var create in AttachedToTables(creates).OrderBy(c => c.Contributor.Placement.Rank).ThenBy(creates.IndexOf))
            create.Contributor.Create(create.Descriptor, after);
        foreach (var drop in MadeBeforeTables(drops).Reverse())
            drop.Contributor.Drop(drop.Descriptor, after);

        return new SchemaOperationPlan(before.Operations, [.. inPlace.Operations, .. after.Operations]);
    }

    /// <summary>
    /// An object of the source side as EF Core's operations leave it. The contributor is asked
    /// about each change that touches what the object depends on, with the object as it is now;
    /// the answer that asks most is kept. When the object follows every rename that touches it,
    /// its declaration is resolved again under the new names.
    /// </summary>
    private Resolved AfterOperations(Resolved source, BaseChanges changes)
    {
        Impact worst = new Impact.Unaffected();
        var renamed = false;
        var follows = true;

        foreach (var change in changes.Touching(source.Descriptor))
        {
            var impact = source.Contributor.ImpactOf(source.Descriptor, change);
            if (Weight(impact) > Weight(worst)) worst = impact;

            if (change is BaseChange.ColumnRenamed or BaseChange.TableRenamed or BaseChange.TableMoved)
            {
                renamed = true;
                follows &= impact is Impact.Follows;
            }
        }

        // An answer other than Follows to a rename says the object stays as it was, under the old
        // names: then it is compared as it was, and the difference is the contributor's to plan.
        if (!renamed || !follows)
            return source with { Impact = worst };

        var resolver = Resolvers.Create(source.EntityType, source.Declaration, changes);
        return source with { Descriptor = source.Contributor.Resolve(source.Declaration, resolver), Before = source.Descriptor, Impact = worst };

        static int Weight(Impact impact) => impact switch
        {
            Impact.Unaffected    => 0,
            Impact.Follows       => 1,
            Impact.Lost          => 2,
            Impact.RebuildAround => 3,
        };
    }

    /// <summary>The impact that decides the fate of an object that is on both sides.</summary>
    private static Impact Decided(Resolved source)
    {
        // What tables are made of is created before them and dropped after them, so it cannot be
        // taken out of the way of an operation on one.
        if (source.Impact is Impact.RebuildAround && source.Contributor.Placement.IsBeforeTables)
            throw new InvalidOperationException(SchemaKitStrings.RebuildAroundBeforeTables(source.Contributor.GetType(), source.Descriptor.Name));

        return source.Impact;
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

    private static IEnumerable<SqlName> Extensions(ISchemaDescriptor descriptor)
        => descriptor.Dependencies.OfType<Dependency.Extension>().Select(extension => extension.Name);

    /// <summary>
    /// The base type whose table the entity type shares, the topmost one, or null when its table is
    /// its own. An object is attached to a table, not to the rows of one type in it: declared on a
    /// derived type, an exclusion constraint or a unique index would hold among its siblings' rows
    /// too, and the database would refuse writes the model allows.
    /// </summary>
    private static IReadOnlyEntityType? TableOwner(IReadOnlyEntityType entityType)
    {
        if (StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) is not { } table) return null;

        IReadOnlyEntityType? owner = null;
        for (var type = entityType.BaseType; type is not null; type = type.BaseType)
        {
            if (StoreObjectIdentifier.Create(type, StoreObjectType.Table) is { } shared && shared.Equals(table)) owner = type;
        }

        return owner;
    }

    /// <summary>Every declaration of a model as a descriptor, in the order of its entity types and of their annotation keys.</summary>
    private List<Resolved> Resolve(IRelationalModel? model, bool isTarget)
    {
        List<Resolved> resolved = [];
        if (model is null) return resolved;

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            // Where a migration starts, what 5.x declared and nothing here reads is not passed over.
            if (!isTarget) LegacyFormats.RequireRead(entityType, Legacy);

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
                {
                    // Only where the migration leads: a snapshot that holds such a declaration, from
                    // an earlier version or from 5.x, describes an object that exists, and refusing
                    // it would leave no way to migrate off it.
                    if (isTarget && TableOwner(entityType) is { } tableOwner)
                        throw new InvalidOperationException(SchemaKitStrings.DeclaredOnDerivedTypeOfSharedTable(
                            entityType.ShortName(), DeclarationCodec.Describe(declaration), tableOwner.ShortName()));

                    resolved.Add(new Resolved(contributor, contributor.Resolve(declaration, Resolvers.Create(entityType, declaration)), entityType, declaration));
                }
            }

            // What an earlier package wrote, where a migration starts: the snapshot of a project
            // that moved here still describes its objects in that package's format, and they are
            // in the database. Read into declarations, they are resolved like any other, so the
            // model that declares the same objects differs from the snapshot in nothing.
            foreach (var reader in Legacy)
            {
                if (entityType.FindAnnotation(reader.Annotation) is not { } annotation) continue;

                // Nothing renders the old format from a model, and nothing writes it any more.
                if (isTarget)
                    throw new InvalidOperationException(SchemaKitStrings.LegacyAnnotationInTarget(entityType.ShortName(), reader.Annotation));

                var contributor = Registry.Find(reader.Owner)
                                  ?? throw new InvalidOperationException(SchemaKitStrings.NoContributorForOwner(entityType.ShortName(), reader.Owner));

                foreach (var declaration in reader.Read(entityType, annotation.Value))
                    resolved.Add(new Resolved(contributor, contributor.Resolve(declaration, Resolvers.Create(entityType, declaration)), entityType, declaration));
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
