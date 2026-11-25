namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>An index as the database holds it.</summary>
/// <param name="Table">The table.</param>
/// <param name="Name">The index's name, with the table's schema: an index is a relation of the schema its table is in.</param>
/// <param name="IsUnique">Whether no two rows the index covers may share a key.</param>
/// <param name="NullsNotDistinct">Whether two rows with a null in the key share it. Of a unique index only.</param>
/// <param name="Keys">Each key as it is written: <c>"column"</c>, <c>(expression)</c>, and the order behind it where it is not the default.</param>
/// <param name="Include">The columns carried without being keys.</param>
/// <param name="Where">The condition, or the empty fragment.</param>
internal sealed record PgIndexDescriptor(
    TableRef Table,
    QualifiedName Name,
    bool IsUnique,
    bool NullsNotDistinct,
    ValueList<SqlFragment> Keys,
    ValueList<SqlFragment> Include,
    SqlFragment Where) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies =>
        [.. Keys.Concat(Include).Append(Where).SelectMany(part => part.Dependencies).Distinct()];

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

/// <summary>
/// Renders <see cref="PgIndex" />: a plain <c>CREATE INDEX</c>, a drop by the qualified name, a
/// rename in place.
/// </summary>
internal sealed class PgIndexContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<PgIndex, PgIndexDescriptor>(dependencies)
{
    // Ahead of the exclusion constraints, as EF Core writes its indexes ahead of nothing that
    // needs them: neither kind reads the other.
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    protected override PgIndexDescriptor Resolve(PgIndex declaration, SchemaResolver resolver)
    {
        var sql = resolver.Sql;
        var btree = declaration switch
        {
            PgIndex.BTree index => index,
        };

        // The keys as the resolver returns them, before anything is written around them: a default
        // name is built from what each one carries for it, which is not its SQL.
        var targets = btree.Keys
            .Select(key => key.Target switch
            {
                KeyTarget.Column column     => resolver.Column(column.Path),
                KeyTarget.Template template => resolver.Template(template.Sql),
            })
            .ToList();

        return new PgIndexDescriptor(
            resolver.Table,
            resolver.Table.Qualify(resolver.Name(declaration.Name, "IX", targets)),
            btree.Unique is not null,
            btree.Unique is { NullsDistinct: false },
            [.. btree.Keys.Select((key, index) =>
            {
                var target = key.Target switch
                {
                    KeyTarget.Column   => targets[index],
                    KeyTarget.Template => sql.Fragment($"({targets[index]})"),
                };

                // What PostgreSQL does unasked is not written: an ascending key has its nulls
                // last and a descending one first, and the catalog says neither. Written out,
                // a key that says so would be another index than one that does not, and the
                // database holds one index for both.
                return (key.Descending, key.Nulls) switch
                {
                    (false, NullOrder.First) => sql.Fragment($"{target} NULLS FIRST"),
                    (false, _)               => target,
                    (true, NullOrder.Last)   => sql.Fragment($"{target} DESC NULLS LAST"),
                    (true, _)                => sql.Fragment($"{target} DESC"),
                };
            })],
            [.. btree.Include.Select(path => resolver.Column(path).As(ColumnRole.Included))],
            declaration.Where is { } where ? resolver.Template(where.Template).As(ColumnRole.Condition) : SqlFragment.Empty);
    }

    // No IF NOT EXISTS: a name that is taken fails (42P07) and nothing is passed over. The index
    // is created in its table's schema, so the statement names it without one; PostgreSQL has no
    // syntax for another.
    protected override void Create(PgIndexDescriptor descriptor, SchemaSql sql)
    {
        var unique = descriptor.IsUnique ? sql.Fragment($"UNIQUE ") : SqlFragment.Empty;
        var include = descriptor.Include.Count > 0 ? sql.Fragment($" INCLUDE ({sql.List(descriptor.Include)})") : SqlFragment.Empty;
        var nulls = descriptor.NullsNotDistinct ? sql.Fragment($" NULLS NOT DISTINCT") : SqlFragment.Empty;
        var where = descriptor.Where.IsEmpty ? SqlFragment.Empty : sql.Fragment($" WHERE {descriptor.Where}");

        sql.Statement($"CREATE {unique}INDEX {descriptor.Name.Name} ON {descriptor.Table} ({sql.List(descriptor.Keys)}){include}{nulls}{where}");
    }

    // By its qualified name: without the schema PostgreSQL takes the first index of that name on
    // the search path, which may be another schema's. No IF EXISTS: an index that is gone already
    // is a database that differs from the snapshot.
    protected override void Drop(PgIndexDescriptor descriptor, SchemaSql sql)
        => sql.Statement($"DROP INDEX {descriptor.Name}");

    // PostgreSQL renames an index and changes nothing else that is declared here: there is no
    // statement for another key, another condition, uniqueness or an included column.
    protected override SchemaChange Plan(PgIndexDescriptor from, PgIndexDescriptor to, SchemaSql sql)
    {
        if (!from.WithName(to.Name).Equals(to)) return new SchemaChange.Rebuild();

        // The new name without a schema: a rename leaves the index in the schema it is in.
        sql.Statement($"ALTER INDEX {from.Name} RENAME TO {to.Name.Name}");
        return new SchemaChange.InPlace();
    }

    // What PostgreSQL does is the package's one table's to say, not this contributor's.
    protected override Impact ImpactOf(PgIndexDescriptor descriptor, BaseChange change)
        => PgImpact.Of(PgObjectKind.Index, descriptor.Dependencies, change);

    // An index is a relation: its name is unique within its schema, among tables, views,
    // sequences and every other index.
    protected override IEnumerable<NameClaim> Claims(PgIndexDescriptor descriptor)
        => [new NameClaim.Relation(descriptor.Name.Schema, descriptor.Name.Name)];
}
