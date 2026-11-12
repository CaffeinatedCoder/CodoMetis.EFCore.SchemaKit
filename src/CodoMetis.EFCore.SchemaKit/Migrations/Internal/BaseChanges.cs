using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// The inner differ's operations, reduced to what a schema object can depend on.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="MigrationOperation" /> type of EF Core Relational and of the providers is in
/// <see cref="Known" />: mapped to a <see cref="BaseChange" /> here, or harmless to an object
/// attached to a table. A type that is not in it is reported in <see cref="Unknown" />, and the
/// planner refuses it: what it does to a declared object is not known. A convention test fails
/// when an assembly gains a type the table lacks.
/// </para>
/// <para>
/// EF Core names a column operation's table as the target model has it, and renames a table before
/// it touches its columns and a column before it alters it. A descriptor of the source side names
/// both as they were, so its tables and columns are carried forward through the renames
/// (<see cref="Forward(TableRef)" />) before they are compared.
/// </para>
/// </remarks>
internal sealed class BaseChanges
{
    /// <summary>The operation types that change nothing a schema object attached to a table depends on.</summary>
    private static readonly HashSet<string> Harmless = new(StringComparer.Ordinal)
    {
        typeof(AddCheckConstraintOperation).FullName!,
        typeof(AddColumnOperation).FullName!,
        typeof(AddForeignKeyOperation).FullName!,
        typeof(AddPrimaryKeyOperation).FullName!,
        typeof(AddUniqueConstraintOperation).FullName!,
        typeof(AlterDatabaseOperation).FullName!,
        "Microsoft.EntityFrameworkCore.Migrations.Operations.AlterDatabaseOperation+CreateDatabaseOperation",
        typeof(AlterSequenceOperation).FullName!,
        typeof(AlterTableOperation).FullName!,
        typeof(CreateIndexOperation).FullName!,
        typeof(CreateSequenceOperation).FullName!,
        typeof(CreateTableOperation).FullName!,
        typeof(DeleteDataOperation).FullName!,
        typeof(DropCheckConstraintOperation).FullName!,
        typeof(DropForeignKeyOperation).FullName!,
        typeof(DropIndexOperation).FullName!,
        typeof(DropPrimaryKeyOperation).FullName!,
        typeof(DropSchemaOperation).FullName!,
        typeof(DropSequenceOperation).FullName!,
        typeof(DropUniqueConstraintOperation).FullName!,
        typeof(EnsureSchemaOperation).FullName!,
        typeof(InsertDataOperation).FullName!,
        typeof(RenameIndexOperation).FullName!,
        typeof(RenameSequenceOperation).FullName!,
        typeof(RestartSequenceOperation).FullName!,
        typeof(UpdateDataOperation).FullName!,

        // Raw SQL of the consumer's or of another package: what it does cannot be known, and
        // refusing it would refuse every migration that carries one.
        typeof(SqlOperation).FullName!,

        // The providers' own, by name: the kernel references no provider.
        "Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Operations.NpgsqlCreateDatabaseOperation",
        "Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Operations.NpgsqlDropDatabaseOperation",
        "Microsoft.EntityFrameworkCore.Migrations.Operations.SqlServerCreateDatabaseOperation",
        "Microsoft.EntityFrameworkCore.Migrations.Operations.SqlServerDropDatabaseOperation",
    };

    /// <summary>The operation types that become a <see cref="BaseChange" />.</summary>
    private static readonly HashSet<string> Mapped = new(StringComparer.Ordinal)
    {
        typeof(RenameColumnOperation).FullName!,
        typeof(AlterColumnOperation).FullName!,
        typeof(DropColumnOperation).FullName!,
        typeof(RenameTableOperation).FullName!,
        typeof(DropTableOperation).FullName!,
    };

    /// <summary>Every operation type the kernel has an answer for.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(Harmless.Concat(Mapped), StringComparer.Ordinal);

    private readonly Dictionary<TableRef, TableRef> _renamed = [];
    private readonly Dictionary<(TableRef Table, Identifier Column), Identifier> _renamedColumns = [];

    private BaseChanges(List<BaseChange> changes, List<Type> unknown)
    {
        Changes = changes;
        Unknown = unknown;
    }

    public IReadOnlyList<BaseChange> Changes { get; }

    /// <summary>The types of the operations <see cref="Known" /> does not hold, each once.</summary>
    public IReadOnlyList<Type> Unknown { get; }

    /// <summary>A table of the source model as the target model names it.</summary>
    public TableRef Forward(TableRef table) => _renamed.GetValueOrDefault(table, table);

    /// <summary>A column of a table, the table as the target names it, as the target model names the column.</summary>
    private Identifier Forward(TableRef table, Identifier column) => _renamedColumns.GetValueOrDefault((table, column), column);

    public static BaseChanges Of(IReadOnlyList<MigrationOperation> operations, ISchemaDialect dialect, bool sqlite)
    {
        List<BaseChange> changes = [];
        List<Type> unknown = [];
        var result = new BaseChanges(changes, unknown);

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case RenameColumnOperation rename:
                    var renamed = new BaseChange.ColumnRenamed(Table(rename.Schema, rename.Table), dialect.Identifier(rename.Name), dialect.Identifier(rename.NewName));
                    result._renamedColumns[(renamed.Table, renamed.From)] = renamed.To;
                    changes.Add(renamed);
                    break;

                case AlterColumnOperation alter:
                    changes.Add(new BaseChange.ColumnAltered(Table(alter.Schema, alter.Table), dialect.Identifier(alter.Name)));
                    break;

                case DropColumnOperation drop:
                    changes.Add(new BaseChange.ColumnDropped(Table(drop.Schema, drop.Table), dialect.Identifier(drop.Name)));
                    break;

                case RenameTableOperation rename:
                    // The differ sets both: the table as the target has it, a null schema being the default one.
                    var from = Table(rename.Schema, rename.Name);
                    var to   = Table(rename.NewSchema, rename.NewName ?? rename.Name);
                    result._renamed[from] = to;
                    if (!Equals(from.Name, to.Name)) changes.Add(new BaseChange.TableRenamed(from, to));
                    if (!Equals(from.Schema, to.Schema)) changes.Add(new BaseChange.TableMoved(from, to));
                    break;

                case DropTableOperation drop:
                    changes.Add(new BaseChange.TableDropped(Table(drop.Schema, drop.Name)));
                    break;

                default:
                    if (!Harmless.Contains(operation.GetType().FullName!) && !unknown.Contains(operation.GetType()))
                        unknown.Add(operation.GetType());
                    break;
            }

            // SQLite has no ALTER for most changes of a table: its provider creates the table again
            // and copies the rows. Whatever was attached to the old table is not on the new one.
            if (sqlite && RebuiltOnSqlite(operation) is var (schema, table))
            {
                BaseChange rebuilt = new BaseChange.TableRebuilt(Table(schema, table));
                if (!changes.Contains(rebuilt)) changes.Add(rebuilt);
            }
        }

        return result;

        TableRef Table(string? schema, string name) => new(schema is null ? null : dialect.Identifier(schema), dialect.Identifier(name));
    }

    /// <summary>
    /// The table SQLite's provider rebuilds for the operation, or null when it does not. Held
    /// against the provider's own generator by a test: this is the kernel's copy of a rule that is
    /// not its own.
    /// </summary>
    internal static (string? Schema, string Table)? RebuiltOnSqlite(MigrationOperation operation) => operation switch
    {
        AlterTableOperation alter => (alter.Schema, alter.Name),

        AlterColumnOperation
            or DropColumnOperation
            or AddForeignKeyOperation
            or DropForeignKeyOperation
            or AddPrimaryKeyOperation
            or DropPrimaryKeyOperation
            or AddUniqueConstraintOperation
            or DropUniqueConstraintOperation
            or AddCheckConstraintOperation
            or DropCheckConstraintOperation
            when operation is ITableMigrationOperation on => (on.Schema, on.Table),

        _ => null,
    };

    /// <summary>The changes that touch what a descriptor of the source side depends on.</summary>
    public IEnumerable<BaseChange> Touching(ISchemaDescriptor descriptor) => Changes.Where(change => change switch
    {
        BaseChange.ColumnRenamed renamed => Reads(descriptor, renamed.Table, renamed.From),
        BaseChange.ColumnAltered altered => ReadsAsRenamed(descriptor, altered.Table, altered.Column),
        BaseChange.ColumnDropped dropped => ReadsAsRenamed(descriptor, dropped.Table, dropped.Column),
        BaseChange.TableRenamed renamed  => IsOn(descriptor, renamed.To),
        BaseChange.TableMoved moved      => IsOn(descriptor, moved.To),
        BaseChange.TableDropped dropped  => IsOn(descriptor, dropped.Table),
        BaseChange.TableRebuilt rebuilt  => IsOn(descriptor, rebuilt.Table),
    });

    private bool Reads(ISchemaDescriptor descriptor, TableRef table, Identifier column) => descriptor.Dependencies.Any(dependency => dependency switch
    {
        Dependency.Column read      => Forward(read.Table).Equals(table) && read.Name.Equals(column),
        Dependency.WholeTable whole => Forward(whole.Ref).Equals(table),
        Dependency.SchemaObject     => false,
        Dependency.Extension        => false,
    });

    /// <summary>For an operation that comes after a rename of the column and names it as the target has it.</summary>
    private bool ReadsAsRenamed(ISchemaDescriptor descriptor, TableRef table, Identifier column) => descriptor.Dependencies.Any(dependency => dependency switch
    {
        Dependency.Column read      => Forward(read.Table).Equals(table) && Forward(table, read.Name).Equals(column),
        Dependency.WholeTable whole => Forward(whole.Ref).Equals(table),
        Dependency.SchemaObject     => false,
        Dependency.Extension        => false,
    });

    private bool IsOn(ISchemaDescriptor descriptor, TableRef table)
        => descriptor.Scope is SchemaScope.Table scope && Forward(scope.Ref).Equals(table)
           || descriptor.Dependencies.Any(dependency => dependency switch
           {
               Dependency.Column read      => Forward(read.Table).Equals(table),
               Dependency.WholeTable whole => Forward(whole.Ref).Equals(table),
               Dependency.SchemaObject     => false,
               Dependency.Extension        => false,
           });
}
