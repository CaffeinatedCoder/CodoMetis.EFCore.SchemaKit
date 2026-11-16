namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;

/// <summary>A nonclustered index as SQL Server holds it.</summary>
/// <param name="Table">The table.</param>
/// <param name="Name">The index's name. Without a schema: SQL Server names an index within its table.</param>
/// <param name="IsUnique">Whether no two rows the index covers may share a key.</param>
/// <param name="Keys">The key columns, delimited.</param>
/// <param name="Include">The included columns, delimited.</param>
/// <param name="Where">The filter, or the empty fragment.</param>
internal sealed record ProbeIndexDescriptor(
    TableRef Table,
    QualifiedName Name,
    bool IsUnique,
    ValueList<SqlFragment> Keys,
    ValueList<SqlFragment> Include,
    SqlFragment Where) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies =>
        [.. Keys.Concat(Include).Append(Where).SelectMany(part => part.Dependencies).Distinct()];

    /// <summary>
    /// Whether the filter reads the column. The role of a column is kept here, by the contributor:
    /// <see cref="Dependency.Column" /> does not say it. SQL without a placeholder is taken to read
    /// every column.
    /// </summary>
    /// <remarks>
    /// By the column's name alone. A <see cref="BaseChange" /> names its table as the target model
    /// has it and this descriptor names it as the source has it, so after a rename of the table in
    /// the same migration <c>new Dependency.Column(change.Table, change.From)</c> is in no list of
    /// this descriptor. The kernel carries the names forward before it asks; a contributor has
    /// nothing to do that with (<c>SqlServerIndexLiveTests.Renaming_the_table_and_a_column_the_filter_reads</c>).
    /// </remarks>
    public bool FilterReads(Identifier column) => Where.Dependencies.Any(read => read switch
    {
        Dependency.Column named => named.Name.Equals(column),
        Dependency.WholeTable   => true,
        _                       => false,
    });

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

/// <summary>
/// Renders <see cref="ProbeIndex" />: <c>CREATE NONCLUSTERED INDEX</c>, <c>DROP INDEX … ON</c>, a
/// rename through <c>sp_rename</c>.
/// </summary>
internal sealed class ProbeIndexContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<ProbeIndex, ProbeIndexDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    /// <summary>
    /// For the tests that break a rule to show its test failing: an answer in place of this
    /// contributor's own, or null to leave it.
    /// </summary>
    public Func<ProbeIndexDescriptor, BaseChange, Impact?>? ImpactOverride { get; set; }

    /// <summary>For the same: the create written as the statement it is, not through <c>EXEC</c>.</summary>
    public bool CreateAsAPlainStatement { get; set; }

    /// <summary>
    /// For the tests of the dialect's answers alone: the namespaces this index's name is claimed
    /// in, in place of its own. No index is an object of the schema, and this contributor has no
    /// other object, so one stands in for an object that is when the dialect's claims for EF
    /// Core's tables, constraints and sequences are compared with something.
    /// </summary>
    public Func<ProbeIndexDescriptor, IEnumerable<NameClaim>>? ClaimedAs { get; set; }

    /// <summary>Every descriptor this contributor resolved, in order: what the kernel compares.</summary>
    public List<ProbeIndexDescriptor> Resolved { get; } = [];

    protected override ProbeIndexDescriptor Resolve(ProbeIndex declaration, SchemaResolver resolver)
    {
        var keys = declaration.Keys.Select(resolver.Column).ToList();

        var descriptor = new ProbeIndexDescriptor(
            resolver.Table,
            new QualifiedName(null, resolver.Name(declaration.Name, "IX", keys)),
            declaration.IsUnique,
            [.. keys],
            [.. declaration.Include.Select(resolver.Column)],
            declaration.Where is { } where ? resolver.Template(where.Template) : SqlFragment.Empty);

        Resolved.Add(descriptor);
        return descriptor;
    }

    protected override void Create(ProbeIndexDescriptor descriptor, SchemaSql sql)
    {
        var unique = descriptor.IsUnique ? sql.Fragment($"UNIQUE ") : SqlFragment.Empty;
        var include = descriptor.Include.Count > 0 ? sql.Fragment($" INCLUDE ({sql.List(descriptor.Include)})") : SqlFragment.Empty;
        var where = descriptor.Where.IsEmpty ? SqlFragment.Empty : sql.Fragment($" WHERE {descriptor.Where}");
        var create = sql.Fragment(
            $"CREATE {unique}NONCLUSTERED INDEX {descriptor.Name.Name} ON {descriptor.Table} ({sql.List(descriptor.Keys)}){include}{where}");

        if (descriptor.Where.IsEmpty || CreateAsAPlainStatement)
        {
            sql.Statement($"{create}");
            return;
        }

        // EF Core writes a migration into a script as one batch, and SQL Server binds the columns
        // of a filter when it compiles the batch, before any statement of it has run. After a
        // rename of the filter's column in the same migration the batch does not compile
        // (Msg 207), and in an idempotent script neither does the batch of a migration that is
        // skipped, on a database a later migration took the column from. Inside EXEC the
        // statement is compiled when it runs. EF Core writes its own filtered index that way in
        // an idempotent script, and in no other (SqlServerScriptLiveTests).
        //
        // Audit: a statement is rendered where the migration is scaffolded, which is before
        // anyone has said whether it is applied by commands or as a script, so this has to be
        // written always. And the contract has no way from a composed statement to a literal
        // but the text "for messages and tests".
        sql.Statement($"EXEC({sql.Literal(create.ToString())})");
    }

    // No IF EXISTS: an index that is gone already is a database that differs from the snapshot.
    protected override void Drop(ProbeIndexDescriptor descriptor, SchemaSql sql)
        => sql.Statement($"DROP INDEX {descriptor.Name.Name} ON {descriptor.Table}");

    protected override SchemaChange Plan(ProbeIndexDescriptor from, ProbeIndexDescriptor to, SchemaSql sql)
    {
        if (!from.WithName(to.Name).Equals(to)) return new SchemaChange.Rebuild();

        // SQL Server renames an index through a procedure and has no statement for it. The first
        // argument is a string that holds the index's qualified, delimited name; the second is the
        // new name as it is, since N'[IX_new]' would name the index with its brackets. Both
        // descriptors name the table as it is by then: the kernel hands this method the source
        // as EF Core's operations leave it.
        //
        // Audit: the same way round as in Create, from SQL to a literal through ToString().
        var index = sql.Fragment($"{to.Table}.{from.Name.Name}");
        sql.Statement($"EXEC sp_rename {sql.Literal(index.ToString())}, {sql.Literal(to.Name.Name.Name)}, N'INDEX'");
        return new SchemaChange.InPlace();
    }

    // As measured on SQL Server 2022: design/schemakit-core-plan.md, section 3.7, and
    // SqlServerIndexLiveTests, which holds the server's refusal for each row that is not "follows".
    protected override Impact ImpactOf(ProbeIndexDescriptor descriptor, BaseChange change)
    {
        if (ImpactOverride?.Invoke(descriptor, change) is { } forced) return forced;

        return change switch
        {
            // sp_rename of a column is refused while a filter reads it (Msg 5074, Msg 4922); an
            // index follows the rename of a key and of an included column.
            BaseChange.ColumnRenamed renamed => descriptor.FilterReads(renamed.From) ? new Impact.RebuildAround() : new Impact.Follows(),

            // A change of type is refused under any index that reads the column (Msg 5074). A
            // change of nullability alone is not, unless the filter reads the column.
            //
            // Audit: the change does not say what was altered, so every alteration takes the
            // index away and builds it again, also one EF Core writes no ALTER COLUMN for.
            BaseChange.ColumnAltered => new Impact.RebuildAround(),

            // Refused while the index is there (Msg 5074, Msg 4922), in whatever way it reads the
            // column. PostgreSQL drops an index with its column in silence; that answer is Lost.
            BaseChange.ColumnDropped => new Impact.RebuildAround(),

            BaseChange.TableRenamed => new Impact.Follows(),
            BaseChange.TableMoved   => new Impact.Follows(),
            BaseChange.TableDropped => new Impact.Lost(),

            // Not something SQL Server's provider does.
            BaseChange.TableRebuilt => new Impact.Lost(),
        };
    }

    // An index is named within its table. Audit: the one case of NameClaim that is SQL Server's.
    protected override IEnumerable<NameClaim> Claims(ProbeIndexDescriptor descriptor)
        => ClaimedAs?.Invoke(descriptor) ?? [new NameClaim.TableIndex(descriptor.Table, descriptor.Name.Name)];
}
