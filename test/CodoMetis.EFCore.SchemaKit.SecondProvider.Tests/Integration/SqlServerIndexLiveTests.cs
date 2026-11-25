using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Integration;

/// <summary>
/// The oracles of the plan's section 4 for a contributor on a second provider, on a live SQL
/// Server 2022: what the catalog holds after each path that creates the index, whether a row it
/// must refuse is refused, and whether a migration leaves the catalog <c>EnsureCreated()</c> would
/// have built. Every test has a database of its own.
/// </summary>
/// <remarks>
/// Each rule of the contributor's <c>ImpactOf</c> has two tests: the migration applies with the
/// rule, and with the rule answered as PostgreSQL would answer it the server refuses the
/// migration, by the message number the rule is there for.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SqlServerIndexLiveTests
{
    private const string Key = "dbo.Orders | PK_Orders | CLUSTERED PRIMARY KEY (Id)";

    private const string Open = "dbo.Orders | IX_orders_open | NONCLUSTERED (CustomerId) INCLUDE (Total) WHERE ([ShippedAt] IS NULL)";

    private const string ByNumber = "dbo.Orders | IX_orders_number | NONCLUSTERED (Number) INCLUDE (Total)";

    /// <summary>
    /// What a migration left, said outright. The comparison with <c>EnsureCreated()</c> holds the
    /// two paths to each other, and both are this contributor's: a statement that is wrong in both
    /// would pass it.
    /// </summary>
    private static Action<SqlServerDatabase> Leaves(params string[] declared)
        => migrated => CollectionAssert.AreEqual(declared, migrated.Declared());

    // ── Every path that creates the index ──

    [TestMethod]
    public void EnsureCreated_creates_the_index()
    {
        using var database = new SqlServerDatabase();
        using var context = Contexts.SqlServer<Sales.Indexed>(database.ConnectionString);

        context.Database.EnsureCreated();

        CollectionAssert.AreEqual(new[] { Open, Key }, database.Indexes());
    }

    [TestMethod]
    public void A_first_migration_creates_the_index()
    {
        using var database = new SqlServerDatabase();

        database.Create<Sales.Indexed>();

        CollectionAssert.AreEqual(new[] { Open, Key }, database.Indexes());
    }

    /// <summary>EF Core's own name for an index on the column, which is what a consumer who names nothing expects.</summary>
    [TestMethod]
    public void A_default_name_reaches_the_catalog()
    {
        using var database = new SqlServerDatabase();

        database.Create<Sales.Unnamed>();

        CollectionAssert.AreEqual(new[] { Open.Replace("IX_orders_open", "IX_Orders_CustomerId") }, database.Declared());
    }

    // ── Behaviour: what must fail does, what must pass does ──

    [TestMethod]
    public void A_second_open_order_of_a_number_is_refused_and_a_shipped_one_is_not()
    {
        using var database = new SqlServerDatabase();
        database.Create<Sales.OpenNumbersAreUnique>();

        void Save(string number, bool shipped)
        {
            using var context = Contexts.SqlServer<Sales.OpenNumbersAreUnique>(database.ConnectionString);
            context.Set<Order>().Add(new Order { Number = number, ShippedAt = shipped ? new DateTime(2026, 1, 1) : null });
            context.SaveChanges();
        }

        Save("A-1", shipped: false);

        // The one thing the index is for: Msg 2601, a duplicate key in a unique index.
        var refused = Assert.ThrowsExactly<DbUpdateException>(() => Save("A-1", shipped: false));
        Assert.AreEqual(2601, ((SqlException)refused.InnerException!).Number);
        StringAssert.Contains(refused.InnerException!.Message, "UX_orders_open_number");

        // Each differs from the refused row in one thing.
        Save("A-2", shipped: false);   // another number
        Save("A-1", shipped: true);    // shipped: outside the filter
        Save("A-1", shipped: true);    // and a second one like it

        using var read = Contexts.SqlServer<Sales.OpenNumbersAreUnique>(database.ConnectionString);
        Assert.AreEqual(4, read.Set<Order>().Count());
    }

    // ── Catalog equivalence: a migration leaves what EnsureCreated would have built ──

    private static void AssertMigratesTo<TFrom, TTo>(Action<SqlServerDatabase>? also = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var migrated = new SqlServerDatabase();
        using var created = new SqlServerDatabase();
        using (var context = Contexts.SqlServer<TTo>(created.ConnectionString)) context.Database.EnsureCreated();

        migrated.Create<TFrom>();
        var before = migrated.Indexes();
        migrated.Migrate<TFrom, TTo>();

        CollectionAssert.AreEqual(created.Indexes(), migrated.Indexes(), "Before the migration: " + string.Join("\n", before));
        also?.Invoke(migrated);
    }

    /// <summary>
    /// The migration with one rule of the contributor answered otherwise: what the server says to
    /// it, as the numbers of its messages.
    /// </summary>
    private static List<int> Refusal<TFrom, TTo>(Func<BaseChange, Impact?> answer)
        where TFrom : ITestModel
        where TTo : ITestModel
        => Refusal<TFrom, TTo>((_, change) => answer(change));

    private static List<int> Refusal<TFrom, TTo>(Func<ProbeIndexDescriptor, BaseChange, Impact?> answer)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var database = new SqlServerDatabase();
        database.Create<TFrom>();

        var refused = Assert.ThrowsExactly<SqlException>(() =>
            database.Migrate<TFrom, TTo>(contributor => contributor.ImpactOverride = answer));

        return [.. refused.Errors.Cast<SqlError>().Select(error => error.Number)];
    }

    [TestMethod]
    public void Adding_the_index_to_a_table_that_exists()
        => AssertMigratesTo<Sales.Bare, Sales.Indexed>(migrated => CollectionAssert.AreEqual(new[] { Open }, migrated.Declared()));

    [TestMethod]
    public void Dropping_the_index()
        => AssertMigratesTo<Sales.Indexed, Sales.Bare>(migrated => Assert.AreEqual(0, migrated.Declared().Count));

    /// <summary>
    /// Through <c>sp_rename</c>, in place: the index that is there afterwards is the one that was
    /// there before, by the identity of its storage. An index that was dropped and created has
    /// another, which the last lines show for a migration that takes the index out of the way of
    /// an altered column.
    /// </summary>
    [TestMethod]
    public void Renaming_the_index_keeps_the_index()
    {
        using var database = new SqlServerDatabase();
        database.Create<Sales.Indexed>();
        var storage = database.StorageOf(Sales.Name);

        database.Migrate<Sales.Indexed, Sales.Renamed>();

        Assert.AreEqual(storage, database.StorageOf("IX_orders_unshipped"));
        CollectionAssert.AreEqual(new[] { Open.Replace(Sales.Name, "IX_orders_unshipped") }, database.Declared());

        using var rebuilt = new SqlServerDatabase();
        rebuilt.Create<Sales.Indexed>();
        var before = rebuilt.StorageOf(Sales.Name);
        rebuilt.Migrate<Sales.Indexed, Sales.KeyColumnWidened>();
        Assert.AreNotEqual(before, rebuilt.StorageOf(Sales.Name), "The oracle: an index that was dropped and created has another storage.");
    }

    // ── The index changes under its name ──
    //
    // Nothing of EF Core's is in these migrations, so nothing but the contributor's own answer
    // decides them. Written as a rename to the name the index has, each would be taken by the
    // server and leave the index as it was.

    [TestMethod]
    public void Changing_the_filter_under_the_same_name() => AssertMigratesTo<Sales.Indexed, Sales.IndexedOnTheShipped>(
        Leaves(Open.Replace("IS NULL", "IS NOT NULL")));

    [TestMethod]
    public void Adding_an_included_column_under_the_same_name() => AssertMigratesTo<Sales.Indexed, Sales.IndexedWithTheNoteIncluded>(
        Leaves(Open.Replace("INCLUDE (Total)", "INCLUDE (Total, Note)")));

    [TestMethod]
    public void Making_the_index_unique_under_the_same_name() => AssertMigratesTo<Sales.Indexed, Sales.IndexedUniquely>(
        Leaves(Open.Replace("NONCLUSTERED (", "NONCLUSTERED UNIQUE (")));

    // ── Keys and included columns in their order ──

    private const string ByCustomerAndNumber = "dbo.Orders | IX_Orders_CustomerId_Number | NONCLUSTERED (CustomerId, Number) INCLUDE (Total, Note)";

    [TestMethod]
    public void Two_keys_and_two_included_columns_reach_the_catalog_in_their_order()
        => AssertMigratesTo<Sales.Bare, Sales.IndexedByCustomerAndNumber>(Leaves(ByCustomerAndNumber));

    [TestMethod]
    public void Changing_the_order_of_the_keys() => AssertMigratesTo<Sales.IndexedByCustomerAndNumber, Sales.IndexedByNumberAndCustomer>(
        Leaves("dbo.Orders | IX_Orders_Number_CustomerId | NONCLUSTERED (Number, CustomerId) INCLUDE (Note, Total)"));

    // ── A table in a schema of its own ──

    private const string OpenInSales = "sales.Orders | IX_orders_open | NONCLUSTERED (CustomerId) INCLUDE (Total) WHERE ([ShippedAt] IS NULL)";

    [TestMethod]
    public void Dropping_the_index_of_a_table_in_a_schema() => AssertMigratesTo<Sales.TableMoved, Sales.MovedBare>(Leaves());

    [TestMethod]
    public void Rebuilding_the_index_of_a_table_in_a_schema() => AssertMigratesTo<Sales.TableMoved, Sales.MovedAndKeyColumnWidened>(Leaves(OpenInSales));

    [TestMethod]
    public void Renaming_the_index_of_a_table_in_a_schema_keeps_the_index()
    {
        using var database = new SqlServerDatabase();
        database.Create<Sales.TableMoved>();
        var storage = database.StorageOf(Sales.Name);

        database.Migrate<Sales.TableMoved, Sales.MovedAndRenamed>();

        Assert.AreEqual(storage, database.StorageOf("IX_orders_unshipped"));
        CollectionAssert.AreEqual(new[] { OpenInSales.Replace(Sales.Name, "IX_orders_unshipped") }, database.Declared());
    }

    private const string Archived = "archive.Orders | IX_orders_open | NONCLUSTERED (CustomerId) INCLUDE (Total) WHERE ([PaidAt] IS NULL)";

    /// <summary>
    /// Two tables of one name in two schemas, each with an index of one name, and the archive's
    /// dropped: the other is still there. Without the schema the statement would name the table
    /// in <c>dbo</c> and take that one's index.
    /// </summary>
    [TestMethod]
    public void Dropping_an_index_leaves_its_namesake_in_another_schema()
        => AssertMigratesTo<Sales.TwoSchemas, Sales.TwoSchemasOneIndex>(Leaves(Open));

    [TestMethod]
    public void Renaming_an_index_leaves_its_namesake_in_another_schema()
        => AssertMigratesTo<Sales.TwoSchemas, Sales.TwoSchemasAndTheArchivesIndexRenamed>(Leaves(Archived.Replace(Sales.Name, "IX_orders_unpaid"), Open));

    // ── EF Core renames what the index reads ──

    /// <summary>The index follows the rename by itself: the catalog names the new column, and SchemaKit wrote nothing.</summary>
    [TestMethod]
    public void Renaming_a_key_column() => AssertMigratesTo<Sales.Indexed, Sales.KeyColumnRenamed>(
        migrated => CollectionAssert.AreEqual(new[] { Open.Replace("(CustomerId)", "(BuyerId)") }, migrated.Declared()));

    [TestMethod]
    public void Renaming_an_included_column() => AssertMigratesTo<Sales.Indexed, Sales.IncludedColumnRenamed>(
        migrated => CollectionAssert.AreEqual(new[] { Open.Replace("(Total)", "(Amount)") }, migrated.Declared()));

    [TestMethod]
    public void Renaming_the_key_column_of_a_default_named_index() => AssertMigratesTo<Sales.Unnamed, Sales.UnnamedKeyColumnRenamed>(
        migrated => StringAssert.Contains(migrated.Declared().Single(), "| IX_Orders_BuyerId |"));

    [TestMethod]
    public void Renaming_a_column_the_filter_reads() => AssertMigratesTo<Sales.Indexed, Sales.FilterColumnRenamed>(
        migrated => CollectionAssert.AreEqual(new[] { Open.Replace("[ShippedAt]", "[SentAt]") }, migrated.Declared()));

    /// <summary>
    /// Why the test above is not the two before it: answered as a key's rename is, the rename of a
    /// filter's column is refused while the index is there (Msg 5074, the index is dependent on
    /// the column).
    /// </summary>
    [TestMethod]
    public void Answered_as_followed_the_rename_of_a_filters_column_is_refused()
        => CollectionAssert.Contains(
            Refusal<Sales.Indexed, Sales.FilterColumnRenamed>(change => change is BaseChange.ColumnRenamed ? new Impact.Follows() : null),
            5074);

    /// <summary>
    /// A filter written as SQL, with no placeholder, says nothing of what it reads, and the
    /// contributor takes it to read every column. Both renames apply and leave the index: of the
    /// column the filter reads, and of one it does not.
    /// </summary>
    /// <remarks>
    /// This does not hold that rule, and no test on the server can. When the column such a filter
    /// reads is renamed, the filter's text changes with it, and that is a rebuild whatever is
    /// answered; a column it does not read may be renamed under the index. The rule is caution
    /// and costs a rebuild nobody needed. What holds it is the diff
    /// (<c>SqlServerIndexMigrationTests.A_filter_without_a_placeholder_is_taken_to_read_every_column</c>).
    /// </remarks>
    [TestMethod]
    public void Renaming_a_column_under_a_filter_written_as_SQL()
    {
        AssertMigratesTo<Sales.FilteredByRawSql, Sales.FilteredByRawSqlAndItsColumnRenamed>(Leaves(Open.Replace("[ShippedAt]", "[SentAt]")));
        AssertMigratesTo<Sales.FilteredByRawSql, Sales.FilteredByRawSqlAndTheNoteRenamed>(Leaves(Open));
    }

    [TestMethod]
    public void Renaming_the_table() => AssertMigratesTo<Sales.Indexed, Sales.TableRenamed>(
        migrated => CollectionAssert.AreEqual(new[] { Open.Replace("dbo.Orders", "dbo.Purchases") }, migrated.Declared()));

    [TestMethod]
    public void Renaming_the_table_of_a_default_named_index() => AssertMigratesTo<Sales.Unnamed, Sales.UnnamedTableRenamed>(
        migrated => StringAssert.Contains(migrated.Declared().Single(), "| IX_Purchases_CustomerId |"));

    /// <summary>
    /// A change names its table as the target model has it and the contributor's descriptor names
    /// it as the source has it. The contributor finds the filter's column by its name alone; by
    /// table and name it would not find it, answer "follows", and the server would refuse the rename.
    /// </summary>
    [TestMethod]
    public void Renaming_the_table_and_a_column_the_filter_reads() => AssertMigratesTo<Sales.Indexed, Sales.TableAndFilterColumnRenamed>(
        Leaves(Open.Replace("dbo.Orders", "dbo.Purchases").Replace("[ShippedAt]", "[SentAt]")));

    /// <summary>
    /// The lookup a contributor would write first, and the PostgreSQL package's for a dropped
    /// column: the changed column as a dependency, by the change's table and name. After a rename
    /// of the table in the same migration no dependency of the descriptor equals it.
    /// </summary>
    [TestMethod]
    public void Looked_up_by_the_changes_table_the_filters_column_is_not_found_and_the_rename_is_refused()
        => CollectionAssert.Contains(
            Refusal<Sales.Indexed, Sales.TableAndFilterColumnRenamed>((descriptor, change) => change is BaseChange.ColumnRenamed renamed
                ? descriptor.Where.Dependencies.Contains(new Dependency.Column(renamed.Table, renamed.From)) ? new Impact.RebuildAround() : new Impact.Follows()
                : null),
            5074);

    [TestMethod]
    public void Moving_the_table_to_another_schema() => AssertMigratesTo<Sales.Indexed, Sales.TableMoved>(
        migrated => CollectionAssert.AreEqual(new[] { Open.Replace("dbo.Orders", "sales.Orders") }, migrated.Declared()));

    /// <summary>The move and a rename of the index in one migration: the procedure finds the index in the table's new schema, and it is the same index.</summary>
    [TestMethod]
    public void Moving_the_table_and_renaming_its_index_keeps_the_index()
    {
        using var database = new SqlServerDatabase();
        database.Create<Sales.Indexed>();
        var storage = database.StorageOf(Sales.Name);

        database.Migrate<Sales.Indexed, Sales.MovedAndRenamed>();

        Assert.AreEqual(storage, database.StorageOf("IX_orders_unshipped"));
        CollectionAssert.AreEqual(new[] { OpenInSales.Replace(Sales.Name, "IX_orders_unshipped") }, database.Declared());
    }

    /// <summary>And back, which SQL Server's provider writes (Npgsql's does not, #105).</summary>
    [TestMethod]
    public void Moving_the_table_back_to_the_default_schema() => AssertMigratesTo<Sales.TableMoved, Sales.Indexed>(
        migrated => CollectionAssert.AreEqual(new[] { Open }, migrated.Declared()));

    /// <summary>
    /// The model begins to say the schema its table is in already. To EF Core that is a move, and
    /// it writes one; the server takes it, and the index is where it was.
    /// </summary>
    [TestMethod]
    public void Saying_the_default_schema_moves_nothing() => AssertMigratesTo<Sales.Indexed, Sales.TableInDboSaid>(
        migrated => CollectionAssert.AreEqual(new[] { Open }, migrated.Declared()));

    // ── EF Core alters or drops what the index reads ──

    [TestMethod]
    public void Altering_the_type_of_a_key_column() => AssertMigratesTo<Sales.Indexed, Sales.KeyColumnWidened>(Leaves(Open));

    [TestMethod]
    public void Altering_the_type_of_an_included_column() => AssertMigratesTo<Sales.Indexed, Sales.IncludedColumnWidened>(Leaves(Open));

    [TestMethod]
    public void Altering_the_type_of_a_column_the_filter_reads() => AssertMigratesTo<Sales.Indexed, Sales.FilterColumnRetyped>(Leaves(Open));

    /// <summary>Msg 5074: the index is dependent on the column. In whatever way the index reads it.</summary>
    [TestMethod]
    public void Answered_as_unaffected_an_altered_type_is_refused()
    {
        static Impact? Unaffected(BaseChange change) => change is BaseChange.ColumnAltered ? new Impact.Unaffected() : null;

        CollectionAssert.Contains(Refusal<Sales.Indexed, Sales.KeyColumnWidened>(Unaffected), 5074);
        CollectionAssert.Contains(Refusal<Sales.Indexed, Sales.IncludedColumnWidened>(Unaffected), 5074);
        CollectionAssert.Contains(Refusal<Sales.Indexed, Sales.FilterColumnRetyped>(Unaffected), 5074);
    }

    [TestMethod]
    public void Dropping_an_included_column() => AssertMigratesTo<Sales.Indexed, Sales.WithoutTheTotal>(Leaves(Open.Replace(" INCLUDE (Total)", "")));

    [TestMethod]
    public void Dropping_a_column_the_filter_reads() => AssertMigratesTo<Sales.Indexed, Sales.WithoutTheShippingDate>(
        Leaves(Open.Replace(" WHERE ([ShippedAt] IS NULL)", "")));

    [TestMethod]
    public void Dropping_a_key_column() => AssertMigratesTo<Sales.Indexed, Sales.WithoutTheCustomer>(Leaves(Open.Replace("(CustomerId)", "(Number)")));

    [TestMethod]
    public void Dropping_a_column_the_index_does_not_read() => AssertMigratesTo<Sales.Indexed, Sales.WithoutTheNote>(
        migrated => CollectionAssert.AreEqual(new[] { Open }, migrated.Declared()));

    /// <summary>
    /// PostgreSQL's answer, on SQL Server. There an index goes with its column in silence and the
    /// contributor says <c>Lost</c>; here the drop is refused while the index is there, Msg 5074
    /// and Msg 4922, so the answer has to be the other one.
    /// </summary>
    [TestMethod]
    public void Answered_as_lost_a_dropped_column_is_refused()
    {
        static Impact? Lost(BaseChange change) => change is BaseChange.ColumnDropped ? new Impact.Lost() : null;

        CollectionAssert.IsSubsetOf(new[] { 5074, 4922 }, Refusal<Sales.Indexed, Sales.WithoutTheTotal>(Lost));
        CollectionAssert.IsSubsetOf(new[] { 5074, 4922 }, Refusal<Sales.Indexed, Sales.WithoutTheShippingDate>(Lost));
        CollectionAssert.IsSubsetOf(new[] { 5074, 4922 }, Refusal<Sales.Indexed, Sales.WithoutTheCustomer>(Lost));
    }

    /// <summary>
    /// The key becomes a computed column. EF Core writes that as <c>DROP COLUMN</c> and
    /// <c>ADD</c>, and the kernel says a column was dropped: the plan has that from the
    /// generator's text for SQL Server, and here it is run.
    /// </summary>
    [TestMethod]
    public void Making_a_key_column_a_computed_one() => AssertMigratesTo<Sales.Indexed, Sales.KeyColumnComputed>(Leaves(Open));

    [TestMethod]
    public void Answered_as_unaffected_a_column_made_computed_is_refused()
        => CollectionAssert.IsSubsetOf(
            new[] { 5074, 4922 },
            Refusal<Sales.Indexed, Sales.KeyColumnComputed>(change => change is BaseChange.ColumnDropped ? new Impact.Unaffected() : null));

    // ── What the change says was altered (#117; it could not, #119) ──

    private static Impact? LeftAlone(BaseChange change) => change is BaseChange.ColumnAltered ? new Impact.Unaffected() : null;

    private static Impact? TakenAway(BaseChange change) => change is BaseChange.ColumnAltered ? new Impact.RebuildAround() : null;

    [TestMethod]
    public void Making_a_key_column_optional() => AssertMigratesTo<Sales.IndexedByNumber, Sales.IndexedByAnOptionalNumber>(Leaves(ByNumber));

    [TestMethod]
    public void Giving_a_key_column_a_default() => AssertMigratesTo<Sales.IndexedByNumber, Sales.IndexedByANumberWithADefault>(Leaves(ByNumber));

    /// <summary>
    /// The server takes a change of nullability under an index that has no filter on the column:
    /// the contributor leaves the index where it is, the migration applies, and the catalog is
    /// the one <c>EnsureCreated()</c> builds. The column the filter reads is another matter: left
    /// alone there, the server refuses the alteration, so the index is taken out of its way.
    /// </summary>
    [TestMethod]
    public void A_change_of_nullability_leaves_the_index_unless_its_filter_reads_the_column()
    {
        using var database = new SqlServerDatabase();
        using var created = new SqlServerDatabase();
        using (var context = Contexts.SqlServer<Sales.IndexedByAnOptionalNumber>(created.ConnectionString)) context.Database.EnsureCreated();
        database.Create<Sales.IndexedByNumber>();

        database.Migrate<Sales.IndexedByNumber, Sales.IndexedByAnOptionalNumber>();

        CollectionAssert.AreEqual(created.Indexes(), database.Indexes());
        CollectionAssert.AreEqual(new[] { ByNumber }, database.Declared());

        var refused = Refusal<Sales.Indexed, Sales.FilterColumnRequired>(LeftAlone);
        Assert.IsTrue(refused.Count > 0, "Left alone, the alteration of the filter's column applies: the answer would be Unaffected.");
        Console.WriteLine("Msg " + string.Join(", ", refused));
        AssertMigratesTo<Sales.Indexed, Sales.FilterColumnRequired>(Leaves(Open));
    }

    /// <summary>
    /// A default is a constraint of its own, and EF Core writes no <c>ALTER COLUMN</c> for it.
    /// The contributor leaves the index alone, and it is the one it was, by its storage. Taken
    /// away and built again, as every alteration was before the change said what it altered, it
    /// is another.
    /// </summary>
    [TestMethod]
    public void A_default_leaves_the_index_the_one_it_was()
    {
        using var left = new SqlServerDatabase();
        left.Create<Sales.IndexedByNumber>();
        var kept = left.StorageOf("IX_orders_number");
        left.Migrate<Sales.IndexedByNumber, Sales.IndexedByANumberWithADefault>();
        Assert.AreEqual(kept, left.StorageOf("IX_orders_number"), "The migration applies and the index is the one it was.");

        using var rebuilt = new SqlServerDatabase();
        rebuilt.Create<Sales.IndexedByNumber>();
        var before = rebuilt.StorageOf("IX_orders_number");
        rebuilt.Migrate<Sales.IndexedByNumber, Sales.IndexedByANumberWithADefault>(contributor => contributor.ImpactOverride = (_, change) => TakenAway(change));
        Assert.AreNotEqual(before, rebuilt.StorageOf("IX_orders_number"), "Taken out of the way, the index is built again.");
    }

    // ── Two tables ──

    /// <summary>One index name on two tables, and one of the two dropped: the other is still there.</summary>
    [TestMethod]
    public void Dropping_an_index_leaves_its_namesake_on_another_table()
        => AssertMigratesTo<Sales.TwoTables, Sales.TwoTablesOneIndex>(migrated =>
            CollectionAssert.AreEqual(
                new[] { "dbo.Invoices | IX_orders_open | NONCLUSTERED (CustomerId) INCLUDE (Total) WHERE ([PaidAt] IS NULL)" },
                migrated.Declared()));

    [TestMethod]
    public void Dropping_a_table_and_its_index()
        => AssertMigratesTo<Sales.TwoTables, Sales.OrdersAlone>(migrated => CollectionAssert.AreEqual(new[] { Open }, migrated.Declared()));

    // ── A name a rename frees or takes in the same migration (#99, on the second provider) ──

    [TestMethod]
    public void An_index_of_EF_Cores_takes_the_name_a_renamed_index_gives_up()
        => AssertMigratesTo<Sales.Indexed, Sales.RenamedAndItsOldNameReused>(
            Leaves("dbo.Orders | IX_orders_open | NONCLUSTERED (Number)", Open.Replace(Sales.Name, "IX_orders_unshipped")));

    /// <summary>The other way: EF Core's drop runs first, so the rename finds the name free and the index stays the one it was.</summary>
    [TestMethod]
    public void A_renamed_index_takes_the_name_of_an_index_EF_Core_drops()
    {
        AssertMigratesTo<Sales.RenamedAndItsOldNameReused, Sales.Indexed>(Leaves(Open));

        using var database = new SqlServerDatabase();
        database.Create<Sales.RenamedAndItsOldNameReused>();
        var storage = database.StorageOf("IX_orders_unshipped");
        database.Migrate<Sales.RenamedAndItsOldNameReused, Sales.Indexed>();
        Assert.AreEqual(storage, database.StorageOf(Sales.Name));
    }

    // ── A snapshot ──

    /// <summary>
    /// The migration scaffolded from the compiled snapshot of the model the database was created
    /// from, which is what a consumer's second <c>migrations add</c> diffs.
    /// </summary>
    [TestMethod]
    public void A_migration_from_the_snapshot_leaves_what_EnsureCreated_builds()
    {
        using var migrated = new SqlServerDatabase();
        using var created = new SqlServerDatabase();
        using (var context = Contexts.SqlServer<Sales.FilterColumnRenamed>(created.ConnectionString)) context.Database.EnsureCreated();

        migrated.Create<Sales.Indexed>();
        migrated.MigrateFromSnapshot<Sales.Indexed, Sales.FilterColumnRenamed>();

        CollectionAssert.AreEqual(created.Indexes(), migrated.Indexes());
        CollectionAssert.AreEqual(new[] { Open.Replace("[ShippedAt]", "[SentAt]") }, migrated.Declared());
    }
}
