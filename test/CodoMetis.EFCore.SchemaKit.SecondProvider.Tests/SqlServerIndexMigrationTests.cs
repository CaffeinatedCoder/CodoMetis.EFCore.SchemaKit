using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests;

/// <summary>
/// What a migration holds for the SQL Server index, without a server: the statements and their
/// place among EF Core's operations. Whether they do what they say is
/// <see cref="Integration.SqlServerIndexLiveTests" />'s, which applies each of these pairs.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SqlServerIndexMigrationTests
{
    private const string Create =
        "EXEC(N'CREATE NONCLUSTERED INDEX [IX_orders_open] ON [Orders] ([CustomerId]) INCLUDE ([Total]) WHERE [ShippedAt] IS NULL');";

    private const string Drop = "DROP INDEX [IX_orders_open] ON [Orders];";

    private static List<string> Shape<TFrom, TTo>(Action<ProbeIndexContributor>? arrange = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Contexts.SqlServer<TFrom>();
        using var to = Contexts.SqlServer<TTo>();
        arrange?.Invoke(to.Contributor<ProbeIndexContributor>());
        return from.DiffTo(to).Shape();
    }

    [TestMethod]
    public void The_create_script_creates_the_index_after_its_table()
    {
        using var context = Contexts.SqlServer<Sales.Indexed>();
        var script = context.Database.GenerateCreateScript();

        Assert.IsTrue(script.IndexOf("CREATE TABLE [Orders]", StringComparison.Ordinal) is >= 0 and var table && table < script.IndexOf(Create, StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void A_new_index_is_one_statement()
        => CollectionAssert.AreEqual(new[] { Create }, Shape<Sales.Bare, Sales.Indexed>());

    /// <summary>An index without a filter is the statement itself: nothing in it is bound when a batch is compiled.</summary>
    [TestMethod]
    public void An_index_without_a_filter_is_written_as_it_is()
        => CollectionAssert.AreEqual(
            new[] { "CREATE NONCLUSTERED INDEX [IX_orders_number] ON [Orders] ([Number]) INCLUDE ([Total]);" },
            Shape<Sales.Bare, Sales.IndexedByNumber>());

    [TestMethod]
    public void An_index_no_longer_declared_is_dropped_by_name_on_its_table()
        => CollectionAssert.AreEqual(new[] { Drop }, Shape<Sales.Indexed, Sales.Bare>());

    /// <summary>The new name goes into the procedure as it is; delimited, the index would be named with its brackets.</summary>
    [TestMethod]
    public void Another_name_is_a_rename_through_the_procedure()
        => CollectionAssert.AreEqual(
            new[] { "EXEC sp_rename N'[Orders].[IX_orders_open]', N'IX_orders_unshipped', N'INDEX';" },
            Shape<Sales.Indexed, Sales.Renamed>());

    /// <summary>The index follows the rename of a key and of an included column, so SchemaKit writes nothing.</summary>
    [TestMethod]
    public void A_renamed_key_or_included_column_is_EF_Cores_rename_alone()
    {
        CollectionAssert.AreEqual(new[] { nameof(RenameColumnOperation) }, Shape<Sales.Indexed, Sales.KeyColumnRenamed>());
        CollectionAssert.AreEqual(new[] { nameof(RenameColumnOperation) }, Shape<Sales.Indexed, Sales.IncludedColumnRenamed>());
    }

    /// <summary>A default name is built from the key, so the index is renamed after its column, in place and on the names EF Core has left.</summary>
    [TestMethod]
    public void A_renamed_key_column_renames_a_default_named_index()
        => CollectionAssert.AreEqual(
            new[] { nameof(RenameColumnOperation), "EXEC sp_rename N'[Orders].[IX_Orders_CustomerId]', N'IX_Orders_BuyerId', N'INDEX';" },
            Shape<Sales.Unnamed, Sales.UnnamedKeyColumnRenamed>());

    /// <summary>The rename of a column the filter reads is refused while the index is there, so the index is out of its way.</summary>
    [TestMethod]
    public void A_renamed_filter_column_is_a_drop_before_and_a_create_after()
        => CollectionAssert.AreEqual(
            new[] { Drop, nameof(RenameColumnOperation), Create.Replace("[ShippedAt]", "[SentAt]") },
            Shape<Sales.Indexed, Sales.FilterColumnRenamed>());

    [TestMethod]
    public void A_renamed_table_takes_its_index_along()
    {
        CollectionAssert.AreEqual(
            new[] { nameof(DropPrimaryKeyOperation), nameof(RenameTableOperation), nameof(AddPrimaryKeyOperation) },
            Shape<Sales.Indexed, Sales.TableRenamed>());

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(DropPrimaryKeyOperation), nameof(RenameTableOperation), nameof(AddPrimaryKeyOperation),
                "EXEC sp_rename N'[Purchases].[IX_Orders_CustomerId]', N'IX_Purchases_CustomerId', N'INDEX';",
            },
            Shape<Sales.Unnamed, Sales.UnnamedTableRenamed>());
    }

    [TestMethod]
    public void A_moved_table_takes_its_index_along()
        => CollectionAssert.AreEqual(
            new[] { nameof(EnsureSchemaOperation), nameof(RenameTableOperation) },
            Shape<Sales.Indexed, Sales.TableMoved>());

    /// <summary>
    /// The drop names the table as it is before EF Core's operations and the create as it is after
    /// them. The contributor finds the filter's column by its name: the change names the table as
    /// the target has it, the descriptor as the source has it.
    /// </summary>
    [TestMethod]
    public void A_renamed_table_and_a_renamed_filter_column_in_one_migration()
        => CollectionAssert.AreEqual(
            new[]
            {
                Drop,
                nameof(DropPrimaryKeyOperation), nameof(RenameTableOperation), nameof(RenameColumnOperation), nameof(AddPrimaryKeyOperation),
                Create.Replace("[Orders]", "[Purchases]").Replace("[ShippedAt]", "[SentAt]"),
            },
            Shape<Sales.Indexed, Sales.TableAndFilterColumnRenamed>());

    /// <summary>A change of type is refused under an index, in whatever way the index reads the column (Msg 5074).</summary>
    [TestMethod]
    public void An_altered_column_is_a_drop_before_and_a_create_after()
    {
        string[] around = [Drop, nameof(AlterColumnOperation), Create];

        CollectionAssert.AreEqual(around, Shape<Sales.Indexed, Sales.KeyColumnWidened>());
        CollectionAssert.AreEqual(around, Shape<Sales.Indexed, Sales.IncludedColumnWidened>());
        CollectionAssert.AreEqual(around, Shape<Sales.Indexed, Sales.FilterColumnRetyped>());
        CollectionAssert.AreEqual(around, Shape<Sales.Indexed, Sales.KeyColumnComputed>());
    }

    /// <summary>The drop of a column is refused under an index (Msg 5074, Msg 4922); PostgreSQL would drop the index with it.</summary>
    [TestMethod]
    public void A_dropped_column_is_a_drop_before_and_a_create_after()
    {
        CollectionAssert.AreEqual(
            new[] { Drop, nameof(DropColumnOperation), Create.Replace(" INCLUDE ([Total])", "") },
            Shape<Sales.Indexed, Sales.WithoutTheTotal>());

        CollectionAssert.AreEqual(
            new[] { Drop, nameof(DropColumnOperation), "CREATE NONCLUSTERED INDEX [IX_orders_open] ON [Orders] ([CustomerId]) INCLUDE ([Total]);" },
            Shape<Sales.Indexed, Sales.WithoutTheShippingDate>());

        CollectionAssert.AreEqual(
            new[] { Drop, nameof(DropColumnOperation), Create.Replace("([CustomerId])", "([Number])") },
            Shape<Sales.Indexed, Sales.WithoutTheCustomer>());
    }

    [TestMethod]
    public void A_dropped_column_the_index_does_not_read_is_EF_Cores_drop_alone()
        => CollectionAssert.AreEqual(new[] { nameof(DropColumnOperation) }, Shape<Sales.Indexed, Sales.WithoutTheNote>());

    /// <summary>
    /// What the contract cannot say (#119, <c>BaseChange</c>): an <c>AlterColumnOperation</c> for
    /// which EF Core writes no <c>ALTER COLUMN</c>. A default is a constraint of its own on SQL
    /// Server, the index is not in its way, and the change still arrives as "a column was
    /// altered": the index is dropped and built again for nothing, which on a large table is the
    /// cost of the migration.
    /// </summary>
    [TestMethod]
    public void A_default_added_to_a_key_column_rebuilds_the_index_although_no_column_is_altered()
    {
        using var from = Contexts.SqlServer<Sales.IndexedByNumber>();
        using var to = Contexts.SqlServer<Sales.IndexedByANumberWithADefault>();
        var operations = from.DiffTo(to);

        CollectionAssert.AreEqual(
            new[]
            {
                "DROP INDEX [IX_orders_number] ON [Orders];",
                nameof(AlterColumnOperation),
                "CREATE NONCLUSTERED INDEX [IX_orders_number] ON [Orders] ([Number]) INCLUDE ([Total]);",
            },
            operations.Shape());

        var written = string.Join("\n", to.GetService<IMigrationsSqlGenerator>()
            .Generate([.. operations.OfType<AlterColumnOperation>()], to.GetService<IDesignTimeModel>().Model)
            .Select(command => command.CommandText));
        Assert.IsFalse(written.Contains("ALTER COLUMN", StringComparison.Ordinal), written);
        StringAssert.Contains(written, "ADD DEFAULT N'none' FOR [Number]");
    }

    /// <summary>
    /// EF Core's generator answers the same question for an index of its own, and by what was
    /// altered: it takes the index out of the way of a change of type, leaves it through a change
    /// of nullability, and writes no <c>ALTER COLUMN</c> for a default. That is the distinction a
    /// contributor is not handed.
    /// </summary>
    [TestMethod]
    public void EF_Core_takes_its_own_index_out_of_the_way_for_a_change_of_type_alone()
    {
        using var context = Contexts.SqlServer<Sales.RenamedAndItsOldNameReused>();

        string Written(Action<AlterColumnOperation> change)
        {
            var alter = new AlterColumnOperation
            {
                Table = "Orders", Name = "Number", ClrType = typeof(string), ColumnType = "nvarchar(40)", MaxLength = 40,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), ColumnType = "nvarchar(40)", MaxLength = 40 },
            };
            change(alter);
            return string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
                .Generate([alter], context.GetService<IDesignTimeModel>().Model)
                .Select(command => command.CommandText));
        }

        var type = Written(alter => (alter.ColumnType, alter.MaxLength) = ("nvarchar(80)", 80));
        StringAssert.Contains(type, "DROP INDEX [IX_orders_open] ON [Orders]");
        StringAssert.Contains(type, "CREATE INDEX [IX_orders_open] ON [Orders] ([Number])");

        var nullability = Written(alter => alter.IsNullable = true);
        StringAssert.Contains(nullability, "ALTER COLUMN [Number]");
        Assert.IsFalse(nullability.Contains("DROP INDEX", StringComparison.Ordinal), nullability);

        var withDefault = Written(alter => alter.DefaultValue = "none");
        Assert.IsFalse(withDefault.Contains("ALTER COLUMN", StringComparison.Ordinal) || withDefault.Contains("DROP INDEX", StringComparison.Ordinal), withDefault);
    }

    // ── Names ──

    private static SchemaValidationException? Collision<TModel>(bool dialect = true) where TModel : ITestModel
    {
        using var context = Contexts.SqlServer<TModel>(dialect: dialect);
        return context.Collisions();
    }

    /// <summary>
    /// SQL Server builds a primary key and a unique constraint on an index of their name, so the
    /// name is taken among the table's indexes. With the namespaces said by SQL Server's dialect
    /// the collision is found where the migration is scaffolded.
    /// </summary>
    [TestMethod]
    public void An_index_named_like_a_key_of_its_table_is_refused_at_the_diff()
    {
        var primary = Collision<Sales.NamedLikeThePrimaryKey>()!.Diagnostics.Single();
        Assert.AreEqual("SK1001", primary.Id);
        Assert.AreEqual(
            "SK1001: Probe.SqlServerIndex nonclustered 'PK_Orders' on Order and the primary key 'PK_Orders' of 'Orders' both claim the name "
            + "'PK_Orders' among the indexes of table 'Orders'. The database keeps a name once there: give one of them another name.",
            primary.Message);

        Assert.AreEqual("SK1001", Collision<Sales.NamedLikeAUniqueConstraint>()!.Diagnostics.Single().Id);
    }

    /// <summary>The declaration says its names compare without regard to case, which is so under SQL Server's default collation.</summary>
    [TestMethod]
    public void A_name_that_differs_from_the_keys_in_case_is_refused_too()
        => Assert.AreEqual("SK1001", Collision<Sales.NamedLikeThePrimaryKeyInAnotherCase>()!.Diagnostics.Single().Id);

    /// <summary>A foreign key and a check constraint are objects of the schema and have no index: their names are free among the table's indexes.</summary>
    [TestMethod]
    public void An_index_named_like_a_foreign_key_or_a_check_constraint_of_its_table_is_let_through()
    {
        Assert.IsNull(Collision<Sales.NamedLikeItsForeignKey>());
        Assert.IsNull(Collision<Sales.NamedLikeACheckConstraint>());
    }

    /// <summary>
    /// The kernel's own dialect, which a provider without a package gets, names a constraint
    /// within its table and knows of no index behind one. On SQL Server that is wrong: the
    /// collision passes the diff (and the server refuses the migration, Msg 1913:
    /// <c>SqlServerNameLiveTests</c>).
    /// </summary>
    [TestMethod]
    public void The_kernels_own_scopes_let_a_key_named_index_through()
    {
        Assert.IsNull(Collision<Sales.NamedLikeThePrimaryKey>(dialect: false));
        Assert.IsNull(Collision<Sales.NamedLikeAUniqueConstraint>(dialect: false));
    }

    /// <summary>One index name on two tables is two indexes, told apart by the table each is on.</summary>
    [TestMethod]
    public void One_name_on_two_tables_is_no_collision_and_one_of_the_two_is_dropped_by_its_table()
    {
        Assert.IsNull(Collision<Sales.TwoTables>());
        CollectionAssert.AreEqual(new[] { Drop }, Shape<Sales.TwoTables, Sales.TwoTablesOneIndex>());
    }

    /// <summary>The index of a table that goes is dropped by name before its table: the planner drops what is no longer declared.</summary>
    [TestMethod]
    public void A_dropped_tables_index_is_dropped_before_the_table()
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX [IX_orders_open] ON [Invoices];", nameof(DropTableOperation) },
            Shape<Sales.TwoTables, Sales.OrdersAlone>());

    /// <summary>
    /// What a table without a schema is compared under. On SQL Server that is the default schema
    /// of the user who runs the migration, <c>dbo</c> for a database's owner
    /// (<c>SqlServerNameLiveTests.A_table_created_without_a_schema_is_in_dbo</c>); the kernel's own
    /// dialect does not guess and says none.
    /// </summary>
    [TestMethod]
    public void The_dialect_names_the_schema_of_a_table_without_one()
    {
        using var probe = Contexts.SqlServer<Sales.Bare>();
        using var kernel = Contexts.SqlServer<Sales.Bare>(dialect: false);

        Assert.AreEqual("dbo", probe.GetService<ISchemaDialect>().DefaultSchema?.Name);
        Assert.IsNull(kernel.GetService<ISchemaDialect>().DefaultSchema);
    }

    // ── A name a rename frees or takes in the same migration (#99, on the second provider) ──

    /// <summary>
    /// EF Core creates an index under the name the declared index gives up, and a rename in place
    /// runs after EF Core's operations: the rename becomes a drop before and a create after.
    /// </summary>
    [TestMethod]
    public void An_index_of_EF_Cores_takes_the_name_a_renamed_index_gives_up()
        => CollectionAssert.AreEqual(
            new[] { Drop, nameof(CreateIndexOperation), Create.Replace("IX_orders_open", "IX_orders_unshipped") },
            Shape<Sales.Indexed, Sales.RenamedAndItsOldNameReused>());

    /// <summary>The other way the name is free by then: EF Core's drop has run, and the rename stays one.</summary>
    [TestMethod]
    public void A_renamed_index_takes_the_name_of_an_index_EF_Core_drops()
        => CollectionAssert.AreEqual(
            new[] { nameof(DropIndexOperation), "EXEC sp_rename N'[Orders].[IX_orders_unshipped]', N'IX_orders_open', N'INDEX';" },
            Shape<Sales.RenamedAndItsOldNameReused, Sales.Indexed>());

    // ── A snapshot ──

    /// <summary>
    /// The model against its own compiled snapshot is nothing, both ways: the snapshot is what the
    /// next <c>migrations add</c> reads, and a difference here is a migration nobody asked for.
    /// </summary>
    [TestMethod]
    public void A_snapshot_round_trip_changes_nothing()
    {
        using var context = Contexts.SqlServer<Sales.Indexed>();
        var snapshot = context.Snapshot();
        var differ = context.GetService<IMigrationsModelDiffer>();

        Assert.AreEqual(0, differ.GetDifferences(snapshot, context.Relational()).Count);
        Assert.AreEqual(0, differ.GetDifferences(context.Relational(), snapshot).Count);
        Assert.IsFalse(differ.HasDifferences(snapshot, context.Relational()));
        Assert.AreEqual(Sales.Open, snapshot.Model.GetEntityTypes().Single().GetDeclarations<ProbeIndex>().Single());
    }

    /// <summary>A snapshot is a property bag without CLR types, and the same change is the same migration from it.</summary>
    [TestMethod]
    public void A_change_from_the_snapshot_is_the_change_from_the_model()
    {
        using var from = Contexts.SqlServer<Sales.Indexed>();
        using var to = Contexts.SqlServer<Sales.FilterColumnRenamed>();

        CollectionAssert.AreEqual(
            from.DiffTo(to).Shape(),
            to.GetService<IMigrationsModelDiffer>().GetDifferences(from.Snapshot(), to.Relational()).Shape());
    }

    /// <summary>The stored form, as it stands in a consumer's snapshot.</summary>
    [TestMethod]
    public void The_declaration_is_one_annotation_in_the_snapshot()
    {
        using var context = Contexts.SqlServer<Sales.Indexed>();
        var code = CodoMetis.EFCore.SchemaKit.Tests.Harness.Snapshots.Of(context).Code;

        StringAssert.Contains(code, "b.HasAnnotation(\"CodoMetis:Probe.SqlServerIndex:");
        StringAssert.Contains(code, """{\"$kind\":\"nonclustered\",\"v\":2,\"r\":1,\"name\":\"IX_orders_open\",\"keys\":[\"CustomerId\"],\"include\":[\"Total\"],\"where\":\"{ShippedAt} IS NULL\",\"unique\":false}""");
    }
}
