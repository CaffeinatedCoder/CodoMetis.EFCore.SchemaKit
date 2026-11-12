using System.Diagnostics.CodeAnalysis;

namespace CodoMetis.EFCore.SchemaKit;

// The closed unions of the contributor contract. A public closed union is closed for the major:
// adding a case breaks every exhaustive switch of a contributor that builds with warnings as
// errors, which is the point of closing it. A new case waits for the next major.

/// <summary>
/// A contributor's answer to a pair of descriptors that are not equal: what it takes to get from
/// the one to the other.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record SchemaChange
{
    /// <summary>
    /// Nothing: the two differ only in what the database does not hold. The contributor wrote no
    /// statement.
    /// </summary>
    public sealed record None : SchemaChange;

    /// <summary>
    /// The statements the contributor wrote change the object where it is: a rename, a storage
    /// parameter. They run after EF Core's own operations.
    /// </summary>
    public sealed record InPlace : SchemaChange;

    /// <summary>
    /// The object is dropped before EF Core's operations and created after them. The contributor
    /// wrote no statement; the kernel calls <c>Drop</c> and <c>Create</c>.
    /// </summary>
    public sealed record Rebuild : SchemaChange;
}

/// <summary>
/// What an operation of EF Core's on a column or a table does to a schema object that depends on
/// it, as the database behaves and not as one would wish.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record Impact
{
    /// <summary>The object survives as it is.</summary>
    public sealed record Unaffected : Impact;

    /// <summary>The database renames or moves the object along; SchemaKit emits nothing.</summary>
    public sealed record Follows : Impact;

    /// <summary>
    /// The database drops the object without saying so. SchemaKit creates it again afterwards when
    /// the target model still declares it.
    /// </summary>
    public sealed record Lost : Impact;

    /// <summary>
    /// The object cannot stay as it is through the operation: the operation fails while the object
    /// exists, or it succeeds and leaves the object broken, as the rename of a column does to a
    /// trigger whose body names it. SchemaKit drops the object before and creates it after, even
    /// when it is unchanged.
    /// </summary>
    public sealed record RebuildAround : Impact;
}

/// <summary>
/// An operation of EF Core's own, reduced to what a schema object can depend on. The kernel maps
/// every <c>MigrationOperation</c> type it knows onto one of these or marks it harmless; a type it
/// does not know, on a table with SchemaKit objects, is an error.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record BaseChange
{
    /// <summary>A column got another name.</summary>
    /// <param name="Table">The table, as the target model names it.</param>
    /// <param name="From">The column's name before.</param>
    /// <param name="To">The column's name after.</param>
    public sealed record ColumnRenamed(TableRef Table, Identifier From, Identifier To) : BaseChange;

    /// <summary>A column's type, nullability, default or computation changed.</summary>
    /// <param name="Table">The table.</param>
    /// <param name="Column">The column.</param>
    public sealed record ColumnAltered(TableRef Table, Identifier Column) : BaseChange;

    /// <summary>A column was dropped.</summary>
    /// <param name="Table">The table.</param>
    /// <param name="Column">The column.</param>
    public sealed record ColumnDropped(TableRef Table, Identifier Column) : BaseChange;

    /// <summary>A table got another name within its schema.</summary>
    /// <param name="From">The table before.</param>
    /// <param name="To">The table after.</param>
    public sealed record TableRenamed(TableRef From, TableRef To) : BaseChange;

    /// <summary>A table moved to another schema.</summary>
    /// <param name="From">The table before.</param>
    /// <param name="To">The table after.</param>
    public sealed record TableMoved(TableRef From, TableRef To) : BaseChange;

    /// <summary>A table was dropped.</summary>
    /// <param name="Table">The table.</param>
    public sealed record TableDropped(TableRef Table) : BaseChange;

    /// <summary>
    /// A table was created again under its name and its rows copied over, as SQLite does for a
    /// change it has no <c>ALTER</c> for. Nothing that was attached to the old table is on the new one.
    /// </summary>
    /// <param name="Table">The table.</param>
    public sealed record TableRebuilt(TableRef Table) : BaseChange;
}

/// <summary>
/// One thing a schema object depends on. EF Core's operations are checked against these: a
/// dependency that is not listed is one the kernel cannot protect.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record Dependency
{
    /// <summary>A column the object reads.</summary>
    /// <param name="Table">The column's table.</param>
    /// <param name="Name">The column.</param>
    public sealed record Column(TableRef Table, Identifier Name) : Dependency;

    /// <summary>A table as a whole, beyond the columns listed.</summary>
    /// <param name="Ref">The table.</param>
    public sealed record WholeTable(TableRef Ref) : Dependency;

    /// <summary>Another schema object, which is created before this one and dropped after it.</summary>
    /// <param name="Owner">The owner of the contributor that renders it.</param>
    /// <param name="Name">Its name.</param>
    public sealed record SchemaObject(string Owner, QualifiedName Name) : Dependency;

    /// <summary>A database extension that must be installed, such as <c>btree_gist</c>.</summary>
    /// <param name="Name">The extension.</param>
    public sealed record Extension(SqlName Name) : Dependency;
}

/// <summary>
/// A namespace a name occupies. Which names collide is the database's rule: on PostgreSQL an index
/// name is unique within its schema and a constraint name within its table, and an exclusion
/// constraint claims both, since its backing index is a relation.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record NameClaim
{
    /// <summary>A relation's name, unique within a schema: a PostgreSQL index.</summary>
    /// <param name="Schema">The schema, or null for the default one.</param>
    /// <param name="Name">The name.</param>
    public sealed record Relation(Identifier? Schema, Identifier Name) : NameClaim;

    /// <summary>An index name unique within its table: SQL Server's rule.</summary>
    /// <param name="Table">The table.</param>
    /// <param name="Name">The name.</param>
    public sealed record TableIndex(TableRef Table, Identifier Name) : NameClaim;

    /// <summary>A constraint name, unique within its table.</summary>
    /// <param name="Table">The table.</param>
    /// <param name="Name">The name.</param>
    public sealed record Constraint(TableRef Table, Identifier Name) : NameClaim;

    /// <summary>A trigger name, unique within its table.</summary>
    /// <param name="Table">The table.</param>
    /// <param name="Name">The name.</param>
    public sealed record Trigger(TableRef Table, Identifier Name) : NameClaim;
}

/// <summary>Where a contributor's operations go relative to EF Core's own.</summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public closed record SchemaPlacement
{
    private sealed record BeforeTablesCase : SchemaPlacement;

    private sealed record AfterTablesCase(int Order) : SchemaPlacement;

    /// <summary>
    /// Created before EF Core's <c>CREATE TABLE</c> and dropped after its <c>DROP TABLE</c>: what a
    /// table's columns are made of, such as a domain.
    /// </summary>
    public static SchemaPlacement BeforeTables { get; } = new BeforeTablesCase();

    /// <summary>
    /// Dropped before EF Core's operations and created after them: what is attached to a table.
    /// </summary>
    /// <param name="order">The place among the contributors that come after the tables; lower is created earlier and dropped later.</param>
    public static SchemaPlacement AfterTables(int order) => new AfterTablesCase(order);

    /// <summary>Whether the contributor's objects are what tables are made of.</summary>
    internal bool IsBeforeTables => this is BeforeTablesCase;

    /// <summary>The creation order across contributors; drops run in the reverse.</summary>
    internal long Rank => this switch
    {
        BeforeTablesCase  => long.MinValue,
        AfterTablesCase a => a.Order,
    };
}
