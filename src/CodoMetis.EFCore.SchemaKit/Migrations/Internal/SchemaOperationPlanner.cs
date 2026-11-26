using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using Microsoft.EntityFrameworkCore;
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
    private IReadOnlyList<ILegacyDeclarationSource> Legacy => field ??= OneReaderForEachAnnotation([.. services.GetServices<ILegacyDeclarationSource>()]);

    // One annotation has one reader, as one owner has one contributor. Two would each read the
    // same objects out of a snapshot: one copy pairs with what the model declares, and the other
    // is left over where the diff starts, so it is dropped while the model still declares it,
    // and the next diff is empty (#108). A reader says its annotation only once it is made, so
    // this is asked here and not where the reader is added.
    private static IReadOnlyList<ILegacyDeclarationSource> OneReaderForEachAnnotation(IReadOnlyList<ILegacyDeclarationSource> readers)
    {
        // An annotation of an entity type and one of a property are two annotations, whatever their names.
        Dictionary<(bool, string), ILegacyDeclarationSource> byAnnotation = [];
        foreach (var reader in readers)
        {
            if (!byAnnotation.TryAdd((reader.OnProperties, reader.Annotation), reader))
                throw new InvalidOperationException(SchemaKitStrings.TwoReadersForOneAnnotation(
                    reader.Annotation, byAnnotation[(reader.OnProperties, reader.Annotation)].GetType(), reader.GetType()));
        }

        return readers;
    }

    private bool IsSqlite => services.GetRequiredService<IDatabaseProvider>().Name == "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// Whether the model is the one the consumer writes: the context's own design-time model, told
    /// by reference. Where a model stands in a diff does not say it. EF Core's scaffolder asks for
    /// Down as <c>GetDifferences(model, snapshot)</c> whenever Up is not empty, and
    /// <c>migrations remove</c> asks <c>HasDifferences</c> of two snapshots (#98). So what holds a
    /// consumer's model is asked of this one model wherever it stands, and every other model, a
    /// snapshot or a migration's target model, is only read.
    /// </summary>
    /// <remarks>
    /// Asked when a declaration or an earlier package's annotation is met, and not before: a
    /// context that declares nothing does not have its model resolved here.
    /// </remarks>
    private bool IsOwn(IRelationalModel model) => ReferenceEquals(model.Model, Own);

    private IModel Own => field ??= services.GetRequiredService<IDesignTimeModel>().Model;

    /// <summary>A declaration as what the database will hold, with the contributor that renders it.</summary>
    /// <param name="Contributor">The contributor of the declaration's owner.</param>
    /// <param name="Descriptor">What is compared: for the source side, the object as it is after EF Core's operations.</param>
    /// <param name="EntityType">The entity type the declaration is on.</param>
    /// <param name="Declaration">The declaration.</param>
    private sealed record Resolved(ISchemaContributor Contributor, ISchemaDescriptor Descriptor, IReadOnlyEntityType EntityType, ISchemaObject Declaration)
    {
        /// <summary>The table of those the declaration lands on that this object is in.</summary>
        public required StoreObjectIdentifier Table { get; init; }

        /// <summary>Whether a name the declaration states has the table's name behind it: a copy whose name is taken beyond its table.</summary>
        public bool Suffixed { get; init; }

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
        var from = Resolve(source);
        var to   = Resolve(target);
        if (from.Count == 0 && to.Count == 0) return SchemaOperationPlan.Empty;

        // What the consumer's model declares has to be something the database can hold: a name
        // taken twice fails at apply, in the middle of a migration. Only that model is held to it,
        // wherever it stands. A snapshot that holds a collision describes a database that exists,
        // or one that never did, and either way the way off it is a diff, Down included.
        if (to.Count > 0 && IsOwn(target!))
            NameClaims.Require([.. to.Select(t => (t.Contributor, t.Descriptor, t.EntityType, t.Declaration))], target!, Dialect);
        else if (from.Count > 0 && IsOwn(source!))
            NameClaims.Require([.. from.Select(s => (s.Contributor, s.Descriptor, s.EntityType, s.Declaration))], source!, Dialect);

        // What an operation of a type nobody has looked at does to these objects is not known.
        var changes = BaseChanges.Of(inner, Dialect, IsSqlite);
        if (changes.Unknown.Count > 0)
            throw new InvalidOperationException(SchemaKitStrings.UnknownOperation(changes.Unknown[0]));

        // An operation the provider writes nothing for leaves its table as it was, and an object
        // on that table would be compared, created or kept as if it had run (#105).
        foreach (var (operation, nothing) in changes.NotWritten)
        {
            foreach (var declared in from.Select(source => (Object: source, source.Descriptor)).Concat(to.Select(target => (Object: target, target.Descriptor))))
            {
                if (TablesOf(declared.Descriptor).FirstOrDefault(nothing.Tables.Contains) is { } table)
                    throw new InvalidOperationException(SchemaKitStrings.OperationNotWritten(Named(declared.Object), table, operation, nothing.WayOut));
            }
        }

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
        List<(Resolved Was, Resolved Becomes, SchemaSql Statements)> inPlaceChanges = [];
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

            // The answer's statements are kept only if the change stays in place: the planner can
            // still turn it into a rebuild below, and then they must not reach the migration.
            var statements = new SchemaSql(Dialect) { Subject = Subject(becomes, becomes.Descriptor) };
            switch (was.Contributor.Plan(was.Descriptor, becomes.Descriptor, statements))
            {
                case SchemaChange.None:
                    break;

                case SchemaChange.InPlace:
                    inPlaceChanges.Add((was, becomes, statements));
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

        // A change in place runs after EF Core's operations, where a name it gives up or takes may
        // be held by something else; such a change is rebuilt instead (#99).
        if (inPlaceChanges.Count > 0)
            KeepInPlace(inPlaceChanges, sourceOrder, dropped, created, target!, inPlace);

        List<Resolved> drops = [.. sourceOrder.Where(dropped.Contains)];
        List<Resolved> creates = [.. targetOrder.Where(created.Contains)];

        // SQLite's generator moves a table rebuild to the end of the migration, behind every
        // statement written after the operation it stands for. A create on such a table runs on
        // the old table, which the rebuild then drops, and the object with it: the migration
        // applies and the database lacks what the model declares, or the create meets the object
        // that is still there and fails at apply (#145). Neither is left to happen: until the
        // generator places such a create behind the rebuild (#115), it is refused here. Only
        // SQLite's operations are read as rebuilds, so nothing is asked of another provider.
        RefuseCreatesARebuildWouldLose(AttachedToTables(creates), changes);

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
        {
            before.Subject = SchemaKitStrings.TheExtension(extension.Name);
            Dialect.EnsureExtension(extension, target!.Model, before);
        }

        // What the consumer's model says an object needs has to be there to need: a dependency
        // on an object nothing declares would be a create that fails at apply, or one on a
        // leftover of the database nobody tracks.
        if (to.Count > 0 && IsOwn(target!))
            RequireDependencies(targetOrder);

        // Before EF Core's operations: what is attached to tables goes first, the latest placement
        // first, then what tables are made of is created. Within each, an object that needs
        // another is dropped before it and created after it, whatever the placement and the
        // model's order say (#115).
        foreach (var drop in DependentsFirst([.. AttachedToTables(drops).OrderByDescending(d => d.Contributor.Placement.Rank).ThenByDescending(drops.IndexOf)]))
            drop.Contributor.Drop(drop.Before, For(before, drop, drop.Before));
        foreach (var create in DependenciesFirst([.. MadeBeforeTables(creates)], created: targetOrder))
            create.Contributor.Create(create.Descriptor, For(before, create, create.Descriptor));

        // After them: changes in place, then creates by placement, then what tables were made of.
        foreach (var create in DependenciesFirst([.. AttachedToTables(creates).OrderBy(c => c.Contributor.Placement.Rank).ThenBy(creates.IndexOf)], created: targetOrder))
            create.Contributor.Create(create.Descriptor, For(after, create, create.Descriptor));
        foreach (var drop in DependentsFirst([.. MadeBeforeTables(drops).Reverse()]))
            drop.Contributor.Drop(drop.Descriptor, For(after, drop, drop.Descriptor));

        return new SchemaOperationPlan(before.Operations, [.. inPlace.Operations, .. after.Operations]);
    }

    private static bool Is(Resolved candidate, ISchemaDescriptor descriptor, Dependency.SchemaObject needed)
        => string.Equals(candidate.Declaration.Owner, needed.Owner, StringComparison.Ordinal)
           && descriptor.Scope.Equals(needed.Scope)
           && descriptor.Name.Equals(needed.Name);

    private static IEnumerable<Dependency.SchemaObject> Needs(ISchemaDescriptor descriptor) => descriptor.Dependencies.OfType<Dependency.SchemaObject>();

    // What a statement is for, as the migration says it of one that runs outside a transaction:
    // the object by the name the database has for it, which a declaration without a name lacks.
    private static string Subject(Resolved resolved, ISchemaDescriptor descriptor)
        => SchemaKitStrings.TheObject(resolved.Declaration.Owner, descriptor.Name, resolved.EntityType.ShortName());

    private static SchemaSql For(SchemaSql sql, Resolved resolved, ISchemaDescriptor descriptor)
    {
        sql.Subject = Subject(resolved, descriptor);
        return sql;
    }

    private static string Named(Resolved resolved) => $"{DeclarationCodec.Describe(resolved.Declaration)} on {resolved.EntityType.ShortName()}";

    /// <summary>Every object of the consumer's model that another one says it needs is declared in it.</summary>
    private static void RequireDependencies(List<Resolved> targets)
    {
        foreach (var target in targets)
        {
            foreach (var needed in Needs(target.Descriptor))
            {
                if (!targets.Any(candidate => Is(candidate, candidate.Descriptor, needed)))
                    throw new InvalidOperationException(SchemaKitStrings.DependsOnAnUndeclaredObject(Named(target), needed.Owner, needed.Name));
            }
        }
    }

    /// <summary>
    /// The creates of one phase, each after the creates of what it needs; otherwise in the order
    /// given. What is created before the tables cannot wait for what is created after them: a
    /// dependency that asks for it is refused.
    /// </summary>
    private static List<Resolved> DependenciesFirst(List<Resolved> creates, List<Resolved> created)
    {
        foreach (var create in creates.Where(create => create.Contributor.Placement.IsBeforeTables))
        {
            foreach (var needed in Needs(create.Descriptor))
            {
                if (created.FirstOrDefault(candidate => Is(candidate, candidate.Descriptor, needed)) is { } later && !later.Contributor.Placement.IsBeforeTables)
                    throw new InvalidOperationException(SchemaKitStrings.NeedsWhatIsCreatedLater(Named(create), Named(later)));
            }
        }

        return InOrder(creates, (item, other) => Needs(item.Descriptor).Any(needed => Is(other, other.Descriptor, needed)));
    }

    /// <summary>The drops of one phase, each before the drops of what it needs, as the database has them before the migration.</summary>
    private static List<Resolved> DependentsFirst(List<Resolved> drops)
        => InOrder(drops, (item, other) => Needs(other.Before).Any(needed => Is(item, item.Before, needed)));

    /// <summary>
    /// The items with each one behind those it <paramref name="waitsFor" />, and otherwise in the
    /// order given: of the items that wait for nothing left, the first goes next. When every item
    /// left waits for another, they need each other in a circle, and no order creates them.
    /// </summary>
    private static List<Resolved> InOrder(List<Resolved> items, Func<Resolved, Resolved, bool> waitsFor)
    {
        List<Resolved> left = [.. items];
        List<Resolved> ordered = [];

        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(item => !left.Any(other => !ReferenceEquals(other, item) && waitsFor(item, other)));
            if (next is null)
                throw new InvalidOperationException(SchemaKitStrings.DependencyCycle([.. left.Select(Named)]));

            ordered.Add(next);
            left.Remove(next);
        }

        return ordered;
    }

    // What an object is attached to is what it says it reads, here as wherever the planner asks
    // what an operation does to an object.
    private static IEnumerable<TableRef> TablesOf(ISchemaDescriptor descriptor) => descriptor.Dependencies
        .Select(dependency => dependency switch
        {
            Dependency.Column column    => column.Table,
            Dependency.WholeTable whole => whole.Ref,
            _                           => null,
        })
        .OfType<TableRef>();

    private static void RefuseCreatesARebuildWouldLose(IEnumerable<Resolved> creates, BaseChanges changes)
    {
        HashSet<TableRef> rebuilt = [.. changes.Changes.OfType<BaseChange.TableRebuilt>().Select(rebuild => rebuild.Table)];
        if (rebuilt.Count == 0) return;

        foreach (var create in creates)
        {
            if (TablesOf(create.Descriptor).FirstOrDefault(rebuilt.Contains) is { } table)
                throw new InvalidOperationException(SchemaKitStrings.CreateOnARebuiltTable(
                    create.EntityType.ShortName(), DeclarationCodec.Describe(create.Declaration), table));
        }
    }

    /// <summary>
    /// Writes the changes in place whose names are free when they run, in the order they run, and
    /// makes each of the others a rebuild (#99).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A change in place runs after EF Core's operations and before the creates. When it renames an
    /// object, the name it gives up and the name it takes are compared, through the claims and the
    /// namespace rules of <see cref="NameClaims" />, with what holds a name at that moment: EF Core's
    /// objects as the target has them, since all of EF Core's operations have run; every declared
    /// object of the source as EF Core's operations leave it, but for those dropped before them or
    /// lost with them (what tables are made of is dropped last, so it is still there); and the
    /// changes in place written before this one, under their new names. A name EF Core takes is the
    /// failure of #99: <c>CREATE INDEX</c> under the name a constraint gives up runs while the
    /// constraint still holds it. A name another rename still holds is the same failure among
    /// declarations, which is how two constraints on two tables of one schema swapping names
    /// failed.
    /// </para>
    /// <para>
    /// A change that collides is rebuilt: the drop frees the old name before EF Core's operations,
    /// the create takes the new one after them and after every change in place, and the create
    /// ensures its extensions like any other. No temporary name is used. A name EF Core gives up is
    /// no collision: its operation has run, which is why a rename into the name of an index EF Core
    /// drops or renames in the same migration stays a rename, as the server confirms.
    /// </para>
    /// <para>
    /// This is a point fix. It reasons about one moment, the changes in place, with everything else
    /// in its phase as a whole: a name an object's create takes against EF Core's operations, a
    /// <c>BeforeTables</c> create against an <c>EnsureSchema</c>, and an order among creates or among
    /// drops are not looked at here. The general answer is a ledger over the ordered actions (#115).
    /// </para>
    /// </remarks>
    private void KeepInPlace(
        List<(Resolved Was, Resolved Becomes, SchemaSql Statements)> changes,
        List<Resolved> sources,
        HashSet<Resolved> dropped,
        HashSet<Resolved> created,
        IRelationalModel target,
        SchemaSql inPlace)
    {
        List<NameClaims.Taken>? held = null;
        foreach (var (was, becomes, statements) in changes)
        {
            List<NameClaims.Taken> gives = [.. Claims(was)];
            List<NameClaims.Taken> takes = [.. NameClaims.Of(becomes.Contributor, becomes.Descriptor, becomes.EntityType, becomes.Declaration, party: was)];

            // A change that names nothing anew holds what it held: nothing to compare.
            if (gives.Select(taken => taken.Claim).ToHashSet().SetEquals(takes.Select(taken => taken.Claim)))
            {
                inPlace.Append(statements);
                continue;
            }

            held ??= Held(sources, dropped, target);
            if (gives.Concat(takes).Any(claim => held.Any(other => NameClaims.Collide(claim, other, Dialect))))
            {
                if (was.Contributor.Placement.IsBeforeTables)
                    throw new InvalidOperationException(SchemaKitStrings.RenameBeforeTablesCollides(was.Contributor.GetType(), was.Descriptor.Name, becomes.Descriptor.Name));

                dropped.Add(was);
                created.Add(becomes);
                held.RemoveAll(taken => ReferenceEquals(taken.Party, was));
                continue;
            }

            inPlace.Append(statements);
            held.RemoveAll(taken => ReferenceEquals(taken.Party, was));
            held.AddRange(takes);
        }
    }

    /// <summary>What holds a name when the changes in place begin: EF Core's objects as the target has them, and the declared objects still there.</summary>
    private List<NameClaims.Taken> Held(List<Resolved> sources, HashSet<Resolved> dropped, IRelationalModel target)
    {
        List<NameClaims.Taken> held = [.. NameClaims.OfTheModel(target, Dialect)];
        foreach (var source in sources)
        {
            // Gone with an operation of EF Core's, or dropped before them. What tables are made of
            // is dropped after everything else, so it holds its name throughout.
            if (source.Impact is Impact.Lost) continue;
            if (dropped.Contains(source) && !source.Contributor.Placement.IsBeforeTables) continue;

            held.AddRange(Claims(source));
        }

        return held;
    }

    /// <summary>What an object of the source takes as EF Core's operations leave it: under the new names of its table and columns, and its own name still.</summary>
    private static IEnumerable<NameClaims.Taken> Claims(Resolved source)
        => NameClaims.Of(source.Contributor, source.Descriptor, source.EntityType, source.Declaration, party: source);

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

        var resolver = Resolvers.Create(source.EntityType, source.Table, source.Declaration, changes, source.Suffixed);
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

    /// <summary>
    /// The declaration as an object in each table it lands on (#118): one, unless its type is
    /// mapped with TPC and types derived from it have tables. The same on both sides of a diff, so
    /// a derived type that is added gets its copy by the next migration, and one that goes loses it.
    /// </summary>
    private IEnumerable<Resolved> Land(ISchemaContributor contributor, IReadOnlyEntityType entityType, ISchemaObject declaration)
    {
        var landings = Landings.Of(entityType);

        // A declaration nothing lands would produce nothing, without an error.
        if (landings.Count == 0)
            throw new InvalidOperationException(SchemaKitStrings.DeclaredOnUnmappedType(entityType.ShortName(), DeclarationCodec.Describe(declaration)));

        // An entity split over tables: the declaration is in the one that has its columns.
        if (landings is [{ IsCopy: false }] && Landings.Fragments(entityType) is { Count: > 1 } fragments)
        {
            var (table, split) = InItsFragment(contributor, entityType, declaration, fragments);
            yield return new Resolved(contributor, split, entityType, declaration) { Table = table };
            yield break;
        }

        foreach (var landing in landings)
        {
            var descriptor = ResolveIn(contributor, entityType, landing.Table, declaration);

            // A copy's name is the declaration's, and where a name is taken beyond its table two
            // objects cannot have it. Whether it is, the contributor's claims say, of the object as
            // resolved: then the copy is resolved again with its table's name behind the name. A
            // name the kernel builds has the table in it already.
            var suffixed = landing.IsCopy && declaration.Name is not null && contributor.Claims(descriptor).Any(claim => claim is NameClaim.Relation);
            if (suffixed)
                descriptor = contributor.Resolve(declaration, Resolvers.Create(entityType, landing.Table, declaration, suffixed: true));

            yield return new Resolved(contributor, descriptor, entityType, declaration) { Table = landing.Table, Suffixed = suffixed };
        }
    }

    /// <summary>
    /// The declaration resolved in one table. A path without a column there is refused by the
    /// resolver; where the property is one the type inherits and the base type's table has it, the
    /// refusal names that type. An object there would hold among the rows of every type of the
    /// hierarchy, so it is the base type's to declare, as in a shared table (#82).
    /// </summary>
    private ISchemaDescriptor ResolveIn(ISchemaContributor contributor, IReadOnlyEntityType entityType, StoreObjectIdentifier table, ISchemaObject declaration)
    {
        try
        {
            return contributor.Resolve(declaration, Resolvers.Create(entityType, table, declaration));
        }
        catch (InvalidOperationException missing) when (MissingColumn.PathOf(missing) is { } path && InheritedFrom(entityType, path) is { } owner)
        {
            throw new InvalidOperationException(
                SchemaKitStrings.ReadsAnInheritedColumn(entityType.ShortName(), DeclarationCodec.Describe(declaration), path, owner.ShortName()), missing);
        }
    }

    /// <summary>The base type that declares the path's property and has a column for it in a table of its own, or null.</summary>
    private static IReadOnlyEntityType? InheritedFrom(IReadOnlyEntityType entityType, string path)
    {
        var root = path.Split('.')[0];
        var declaring = (IReadOnlyEntityType?)(entityType.FindProperty(root)?.DeclaringType ?? entityType.FindComplexProperty(root)?.DeclaringType);

        return declaring is not null && declaring != entityType && StoreObjectIdentifier.Create(declaring, StoreObjectType.Table) is not null ? declaring : null;
    }

    /// <summary>
    /// The one table of a split entity's that has a column for every path the declaration reads
    /// (#118). None: the declaration reads columns of more than one table, and an object is in
    /// one. More than one: it reads the key's columns alone, which every table repeats, and is in
    /// the first; unless it holds SQL written by hand, where nothing says which table is meant.
    /// </summary>
    private (StoreObjectIdentifier Table, ISchemaDescriptor Descriptor) InItsFragment(
        ISchemaContributor contributor, IReadOnlyEntityType entityType, ISchemaObject declaration, IReadOnlyList<StoreObjectIdentifier> fragments)
    {
        List<(StoreObjectIdentifier Table, ISchemaDescriptor Descriptor)> fits = [];
        InvalidOperationException? first = null;

        foreach (var table in fragments)
        {
            try
            {
                fits.Add((table, contributor.Resolve(declaration, Resolvers.Create(entityType, table, declaration))));
            }
            catch (InvalidOperationException missing) when (MissingColumn.PathOf(missing) is not null)
            {
                first ??= missing;
            }
        }

        var tables = string.Join(", ", fragments.Select(table => table.Name));
        if (fits.Count == 0)
            throw new InvalidOperationException(SchemaKitStrings.ReadsColumnsOfSeveralFragments(entityType.ShortName(), DeclarationCodec.Describe(declaration), tables), first);

        if (fits.Count > 1 && fits[0].Descriptor.Dependencies.Any(dependency => dependency is Dependency.WholeTable))
            throw new InvalidOperationException(SchemaKitStrings.HandWrittenSqlOnASplitEntity(entityType.ShortName(), DeclarationCodec.Describe(declaration), tables));

        return fits[0];
    }

    /// <summary>Every declaration of a model as a descriptor, in the order of its entity types and of their annotation keys.</summary>
    private List<Resolved> Resolve(IRelationalModel? model)
    {
        List<Resolved> resolved = [];
        if (model is null) return resolved;

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            // What 5.x declared. In the consumer's own model it is nobody's: nothing renders the
            // old format, with a reader or without one. In any other model it describes objects
            // the database has, and what nothing here reads is not passed over.
            if (LegacyFormats.DeclaredIn(entityType) is { } legacy)
            {
                if (IsOwn(model))
                    throw new InvalidOperationException(SchemaKitStrings.LegacyAnnotationInTheModel(entityType.ShortName(), legacy));

                LegacyFormats.RequireRead(entityType, Legacy);
            }

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
                    // Only in the consumer's own model: a snapshot that holds such a declaration, from
                    // an earlier version or from 5.x, describes an object that exists, and refusing
                    // it would leave no way to migrate off it, nor back to it.
                    if (TableOwner(entityType) is { } tableOwner && IsOwn(model))
                        throw new InvalidOperationException(SchemaKitStrings.DeclaredOnDerivedTypeOfSharedTable(
                            entityType.ShortName(), DeclarationCodec.Describe(declaration), tableOwner.ShortName()));

                    resolved.AddRange(Land(contributor, entityType, declaration));
                }
            }

            // What an earlier package wrote, in a model that is not the consumer's own: the
            // snapshot of a project that moved here still describes its objects in that package's
            // format, and they are in the database. Read into declarations, they are resolved like
            // any other, so the model that declares the same objects differs from the snapshot in
            // nothing, whichever of the two the diff starts from.
            foreach (var reader in Legacy)
            {
                // On the entity type, or on each of its properties that carries it.
                List<(string? Path, IReadOnlyProperty? Property, object? Value)> found = reader.OnProperties
                    ? [.. ILegacyDeclarationSource.Marked(entityType, reader.Annotation).Select(marked => ((string?)marked.Path, (IReadOnlyProperty?)marked.Property, marked.Value))]
                    : entityType.FindAnnotation(reader.Annotation) is { } annotation ? [(null, null, annotation.Value)] : [];
                if (found.Count == 0) continue;

                // Nothing renders the old format from a model, and nothing writes it any more.
                if (IsOwn(model))
                    throw new InvalidOperationException(SchemaKitStrings.LegacyAnnotationInTheModel(entityType.ShortName(), reader.Annotation));

                var contributor = Registry.Find(reader.Owner)
                                  ?? throw new InvalidOperationException(SchemaKitStrings.NoContributorForOwner(entityType.ShortName(), reader.Owner));

                foreach (var (path, property, value) in found)
                foreach (var declaration in reader.Read(entityType, path, property, value))
                    resolved.AddRange(Land(contributor, entityType, declaration));
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
