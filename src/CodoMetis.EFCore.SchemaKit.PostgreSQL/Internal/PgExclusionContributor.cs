namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>An exclusion constraint as the database holds it.</summary>
/// <param name="Table">The table.</param>
/// <param name="Name">The constraint's name. Without a schema: a constraint is named within its table.</param>
/// <param name="Method">The access method of the backing index.</param>
/// <param name="Elements">Each element as it is written: <c>"column" WITH op</c>, <c>(expression) WITH op</c>.</param>
/// <param name="Where">The condition, or the empty fragment.</param>
/// <param name="Deferral">When it is checked.</param>
internal sealed record PgExclusionDescriptor(
    TableRef Table,
    QualifiedName Name,
    SqlName Method,
    ValueList<SqlFragment> Elements,
    SqlFragment Where,
    Deferral Deferral) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies => [.. ElementReads.Concat(Where.Dependencies).Distinct()];

    /// <summary>What the elements read, apart from the condition: PostgreSQL treats the two differently when a column goes.</summary>
    public IEnumerable<Dependency> ElementReads => Elements.SelectMany(element => element.Dependencies);

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

/// <summary>
/// Renders <see cref="PgExclusion" />: a plain <c>ADD CONSTRAINT … EXCLUDE</c>, a drop that names
/// the constraint, a rename in place.
/// </summary>
internal sealed class PgExclusionContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<PgExclusion, PgExclusionDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 20);

    protected override PgExclusionDescriptor Resolve(PgExclusion declaration, SchemaResolver resolver)
    {
        var sql = resolver.Sql;
        var elements = declaration.ElementList;

        // The keys as the resolver returns them, before anything is written around them: a default
        // name is built from what each one carries for it, which is not its SQL.
        var keys = elements
            .Select(element => element.Target switch
            {
                KeyTarget.Column column     => resolver.Column(column.Path),
                KeyTarget.Template template => resolver.Template(template.Sql),
            })
            .ToList();

        return new PgExclusionDescriptor(
            resolver.Table,
            new QualifiedName(null, resolver.Name(declaration.Name, "EX", keys)),
            resolver.Dialect.Name(declaration.Kind),
            [.. elements.Select((element, index) =>
            {
                var op = resolver.Dialect.Operator(element.Operator.Token);
                return element.Target switch
                {
                    KeyTarget.Column   => sql.Fragment($"{keys[index]} WITH {op}"),
                    KeyTarget.Template => sql.Fragment($"({keys[index]}) WITH {op}"),
                };
            })],
            declaration.Where is { } where ? resolver.Template(where.Template) : SqlFragment.Empty,
            declaration.Deferral);
    }

    // No DROP CONSTRAINT IF EXISTS in front, as 5.x wrote it to adopt a constraint that was there
    // already: that is how a constraint of the same name was replaced without a word. A name that
    // is taken fails here (42P07: the constraint's index is a relation of that name).
    protected override void Create(PgExclusionDescriptor descriptor, SchemaSql sql)
    {
        var where = descriptor.Where.IsEmpty ? SqlFragment.Empty : sql.Fragment($" WHERE ({descriptor.Where})");
        var deferral = descriptor.Deferral switch
        {
            { IsInitiallyDeferred: true } => sql.Fragment($" DEFERRABLE INITIALLY DEFERRED"),
            { IsDeferrable: true }        => sql.Fragment($" DEFERRABLE"),
            _                             => SqlFragment.Empty,
        };

        sql.Statement(
            $"ALTER TABLE {descriptor.Table} ADD CONSTRAINT {descriptor.Name.Name} EXCLUDE USING {descriptor.Method} ({sql.List(descriptor.Elements)}){where}{deferral}");
    }

    // A constraint is named within its table, so it is the table that carries the schema. The
    // backing index goes with the constraint.
    protected override void Drop(PgExclusionDescriptor descriptor, SchemaSql sql)
        => sql.Statement($"ALTER TABLE {descriptor.Table} DROP CONSTRAINT {descriptor.Name.Name}");

    // PostgreSQL can rename an exclusion constraint, which renames its index with it, and can change
    // nothing else about one: ALTER CONSTRAINT is for foreign keys.
    protected override SchemaChange Plan(PgExclusionDescriptor from, PgExclusionDescriptor to, SchemaSql sql)
    {
        if (!from.WithName(to.Name).Equals(to)) return new SchemaChange.Rebuild();

        sql.Statement($"ALTER TABLE {to.Table} RENAME CONSTRAINT {from.Name.Name} TO {to.Name.Name}");
        return new SchemaChange.InPlace();
    }

    // As measured on PostgreSQL 16 and 18 (design/schemakit-core-plan.md, section 3.7; spikes/E.ImpactTable).
    protected override Impact ImpactOf(PgExclusionDescriptor descriptor, BaseChange change) => change switch
    {
        BaseChange.ColumnRenamed => new Impact.Follows(),

        // A type change rebuilds the index under a new OID; by name and definition it is the same.
        BaseChange.ColumnAltered => new Impact.Unaffected(),

        // With the column as an element the constraint goes with it, in silence. With the column in
        // the condition only, the drop is refused (2BP01). SQL that names no column through a
        // placeholder may read this one or not, and nothing here can tell: dropping the constraint
        // before and creating it after is right in both cases.
        BaseChange.ColumnDropped dropped => descriptor.ElementReads.Contains(new Dependency.Column(dropped.Table, dropped.Column))
            ? new Impact.Lost()
            : new Impact.RebuildAround(),

        BaseChange.TableRenamed => new Impact.Follows(),
        BaseChange.TableMoved   => new Impact.Follows(),
        BaseChange.TableDropped => new Impact.Lost(),

        // Not something PostgreSQL does; were a table created again, nothing attached to it would be on the new one.
        BaseChange.TableRebuilt => new Impact.Lost(),
    };

    // A constraint's name is unique within its table, and its backing index is a relation, whose
    // name is unique within the schema.
    protected override IEnumerable<NameClaim> Claims(PgExclusionDescriptor descriptor) =>
    [
        new NameClaim.Constraint(descriptor.Table, descriptor.Name.Name),
        new NameClaim.Relation(descriptor.Table.Schema, descriptor.Name.Name),
    ];
}
