using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Update.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>An operation of a package SchemaKit has never heard of.</summary>
public sealed class StrangeOperation : MigrationOperation;

/// <summary>Another package's differ, which adds its own operation to every diff.</summary>
public sealed class StrangeDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies)
    : MigrationsModelDiffer(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider, rowIdentityMapFactory, commandBatchPreparerDependencies)
{
    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => [.. base.GetDifferences(source, target), new StrangeOperation()];
}

/// <summary>
/// What the planner does about EF Core's own operations: which contributor it asks about which
/// change, and what each answer turns into. The failure these exist for is an object both models
/// declare, which a migration removes or cannot get past, while the diff says nothing changed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaImpactTests
{
    private const string CreateIndex = """CREATE UNIQUE INDEX ux_name ON "Thing" ("Name");""";
    private const string DropIndex = "DROP INDEX ux_name;";

    private static (List<string> Operations, List<BaseChange> Asked) Diff<TFrom, TTo>(Func<BaseChange, Impact>? impact = null, Provider provider = Provider.Npgsql, bool planIsNotAsked = false)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = SchemaOperationPlannerTests.Context<TFrom>(provider);
        using var to = SchemaOperationPlannerTests.Context<TTo>(provider);
        var guard = (TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!;
        guard.ImpactOverride = impact;
        if (planIsNotAsked) guard.PlanOverride = _ => throw new InvalidOperationException("Plan was asked.");

        var operations = to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to));
        return (SchemaOperationPlannerTests.Describe(operations), guard.Asked);
    }

    private static TableRef Table(ISchemaDialect dialect, string name) => new(null, dialect.Identifier(name));

    private static ISchemaDialect Dialect()
    {
        using var context = SchemaOperationPlannerTests.Context<PlanModels.Bare>();
        return context.GetService<ISchemaDialect>();
    }

    // ---- who is asked about what ------------------------------------------------------------------------

    [TestMethod]
    public void A_contributor_is_asked_about_a_change_to_a_column_its_object_reads()
    {
        var dialect = Dialect();

        var (operations, asked) = Diff<PlanModels.Guarded, PlanModels.GuardedLonger>();

        CollectionAssert.AreEqual(new BaseChange[] { new BaseChange.ColumnAltered(Table(dialect, "Thing"), dialect.Identifier("Name")) }, asked);
        CollectionAssert.AreEqual(new[] { nameof(AlterColumnOperation) }, operations, "The sample answers Unaffected: nothing is added.");
    }

    [TestMethod]
    public void A_contributor_is_not_asked_about_a_column_its_object_does_not_read()
    {
        var (operations, asked) = Diff<PlanModels.OnId, PlanModels.OnIdLonger>(impact: _ => new Impact.RebuildAround());

        Assert.AreEqual(0, asked.Count);
        CollectionAssert.AreEqual(new[] { nameof(AlterColumnOperation) }, operations);
    }

    [TestMethod]
    public void A_renamed_column_is_asked_about_as_ef_core_names_it()
    {
        var dialect = Dialect();

        var (_, asked) = Diff<PlanModels.Guarded, PlanModels.GuardedTitle>();

        CollectionAssert.AreEqual(
            new BaseChange[] { new BaseChange.ColumnRenamed(Table(dialect, "Thing"), dialect.Identifier("Name"), dialect.Identifier("Title")) },
            asked);
    }

    /// <summary>
    /// EF Core renames a column first and names it as the target has it in what follows. The
    /// descriptor of the source side still says "Name"; it is the same column.
    /// </summary>
    [TestMethod]
    public void A_change_to_a_renamed_column_reaches_the_object_that_names_the_old_one()
    {
        var dialect = Dialect();

        var (_, asked) = Diff<PlanModels.Guarded, PlanModels.GuardedTitleLonger>();

        CollectionAssert.AreEqual(
            new BaseChange[]
            {
                new BaseChange.ColumnRenamed(Table(dialect, "Thing"), dialect.Identifier("Name"), dialect.Identifier("Title")),
                new BaseChange.ColumnAltered(Table(dialect, "Thing"), dialect.Identifier("Title")),
            },
            asked);
    }

    /// <summary>
    /// The same for a table: EF Core renames it first and names it as the target has it in every
    /// operation after. An object that depends on "Thing" depends on that table.
    /// </summary>
    [TestMethod]
    public void A_change_on_a_renamed_table_reaches_the_object_that_names_the_old_one()
    {
        var dialect = Dialect();
        List<BaseChange> asked = [];
        using var from = SchemaOperationPlannerTests.Context<PlanModels.Domain>();
        using var to = SchemaOperationPlannerTests.Context<PlanModels.DomainThingsTitle>();
        ((TestDomainContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Domain")!).ImpactOverride = change =>
        {
            asked.Add(change);
            return new Impact.Lost();
        };

        to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to));

        CollectionAssert.AreEqual(
            new BaseChange[]
            {
                new BaseChange.TableRenamed(Table(dialect, "Thing"), Table(dialect, "Things")),
                new BaseChange.ColumnRenamed(Table(dialect, "Things"), dialect.Identifier("Name"), dialect.Identifier("Title")),
            },
            asked);
    }

    /// <summary>
    /// Until the source side is resolved under a renamed table (issue 24), an object attached to
    /// one is another object on the target side: it is dropped under the old table and created
    /// under the new one, and no contributor is asked.
    /// </summary>
    [TestMethod]
    public void An_object_on_a_renamed_table_is_dropped_and_created()
    {
        var (operations, asked) = Diff<PlanModels.Guarded, PlanModels.GuardedThings>(impact: _ => throw new InvalidOperationException("ImpactOf was asked."), planIsNotAsked: true);

        Assert.AreEqual(0, asked.Count);
        Assert.AreEqual(DropIndex, operations[0]);
        Assert.AreEqual("""CREATE UNIQUE INDEX ux_name ON "Things" ("Name");""", operations[^1]);
        CollectionAssert.Contains(operations, nameof(RenameTableOperation));
    }

    // ---- what an answer turns into ------------------------------------------------------------------------

    /// <summary>SQL Server's ALTER COLUMN under an index: the diff of the declarations is empty, and the migration cannot run.</summary>
    [TestMethod]
    public void An_unchanged_object_is_rebuilt_around_an_operation_that_cannot_run_while_it_exists()
    {
        var (operations, _) = Diff<PlanModels.Guarded, PlanModels.GuardedLonger>(impact: _ => new Impact.RebuildAround());

        CollectionAssert.AreEqual(new[] { DropIndex, nameof(AlterColumnOperation), CreateIndex }, operations);
    }

    /// <summary>The database drops it with the operation: nothing to drop, and it is declared, so it is created again.</summary>
    [TestMethod]
    public void An_unchanged_object_the_operation_takes_away_is_created_again()
    {
        var (operations, _) = Diff<PlanModels.Guarded, PlanModels.GuardedLonger>(impact: _ => new Impact.Lost());

        CollectionAssert.AreEqual(new[] { nameof(AlterColumnOperation), CreateIndex }, operations);
    }

    /// <summary>
    /// For a pair that changed, the operation decides before the contributor plans: statements in
    /// place would be written for an object that is gone when they run.
    /// </summary>
    [TestMethod]
    public void A_changed_object_is_not_planned_when_the_operation_decides()
    {
        const string CreateOnTitle = """CREATE UNIQUE INDEX ux_name ON "Thing" ("Title");""";

        var (rebuilt, _) = Diff<PlanModels.Guarded, PlanModels.GuardedTitle>(impact: _ => new Impact.RebuildAround(), planIsNotAsked: true);
        var (lost, _) = Diff<PlanModels.Guarded, PlanModels.GuardedTitle>(impact: _ => new Impact.Lost(), planIsNotAsked: true);
        var (follows, _) = Diff<PlanModels.Guarded, PlanModels.GuardedTitle>();

        CollectionAssert.AreEqual(new[] { DropIndex, nameof(RenameColumnOperation), CreateOnTitle }, rebuilt);
        CollectionAssert.AreEqual(new[] { nameof(RenameColumnOperation), CreateOnTitle }, lost);
        CollectionAssert.AreEqual(new[] { DropIndex, nameof(RenameColumnOperation), CreateOnTitle }, follows, "Follows is taken as it stands: the pair differs, and the sample plans a rebuild.");
    }

    /// <summary>Of several answers the one that asks most decides.</summary>
    [TestMethod]
    public void The_answer_that_asks_most_decides()
    {
        var (operations, asked) = Diff<PlanModels.Guarded, PlanModels.GuardedTitleLonger>(
            impact: change => change is BaseChange.ColumnRenamed ? new Impact.Follows() : new Impact.Lost(), planIsNotAsked: true);

        Assert.AreEqual(2, asked.Count);
        CollectionAssert.AreEqual(
            new[] { nameof(RenameColumnOperation), nameof(AlterColumnOperation), """CREATE UNIQUE INDEX ux_name ON "Thing" ("Title");""" },
            operations);
    }

    [TestMethod]
    public void What_tables_are_made_of_cannot_be_rebuilt_around_an_operation()
    {
        using var from = SchemaOperationPlannerTests.Context<PlanModels.Domain>();
        using var to = SchemaOperationPlannerTests.Context<PlanModels.DomainLonger>();
        ((TestDomainContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Domain")!).ImpactOverride = _ => new Impact.RebuildAround();

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to)));

        StringAssert.Contains(error.Message, "TestDomainContributor'.ImpactOf answered RebuildAround for 'dom', and its placement is BeforeTables");
    }

    // ---- an operation nobody has looked at -----------------------------------------------------------------

    [TestMethod]
    public void An_operation_of_an_unknown_type_is_refused_when_the_model_declares_an_object()
    {
        static void Strange(IServiceCollection services) => services.AddScoped<IMigrationsModelDiffer, StrangeDiffer>();

        using var guarded = SchemaOperationPlannerTests.Context<PlanModels.Guarded>(more: Strange);
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => guarded.GetService<IMigrationsModelDiffer>().GetDifferences(null, SchemaOperationPlannerTests.Model(guarded)));
        StringAssert.Contains(error.Message, $"an operation of type '{typeof(StrangeOperation).FullName}', which SchemaKit does not know");

        // With nothing declared there is nothing it could harm, and the other package's migration is its own.
        using var bare = SchemaOperationPlannerTests.Context<PlanModels.Bare>(more: Strange);
        CollectionAssert.AreEqual(
            new[] { nameof(CreateTableOperation), nameof(StrangeOperation) },
            SchemaOperationPlannerTests.Describe(bare.GetService<IMigrationsModelDiffer>().GetDifferences(null, SchemaOperationPlannerTests.Model(bare))));
    }

    /// <summary>
    /// The table of operation types is complete, and holds nothing that is not there: a provider
    /// that gains an operation type fails this before a consumer's migration meets it.
    /// </summary>
    [TestMethod]
    public void Every_operation_type_of_ef_core_and_the_providers_is_known()
    {
        var found = new[]
            {
                typeof(MigrationOperation).Assembly,
                typeof(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder).Assembly,
                typeof(SqlServerDbContextOptionsBuilder).Assembly,
                typeof(SqliteDbContextOptionsBuilder).Assembly,
            }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(MigrationOperation).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        var known = BaseChanges.Known.Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(
            found, known,
            $"Not in the table: {string.Join(", ", found.Except(known))}. In the table and in no assembly: {string.Join(", ", known.Except(found))}.");
    }

    // ---- SQLite, where a table is rebuilt -------------------------------------------------------------------

    public static IEnumerable<object[]> SqliteOperations =>
    [
        [new AlterColumnOperation { Table = "Thing", Name = "Name", ClrType = typeof(string), MaxLength = 50, OldColumn = new AddColumnOperation { ClrType = typeof(string) } }],
        [new DropColumnOperation { Table = "Thing", Name = "Name" }],
        [new AddColumnOperation { Table = "Thing", Name = "Extra", ClrType = typeof(int) }],
        [new AddColumnOperation { Table = "Thing", Name = "Extra", ClrType = typeof(int), ComputedColumnSql = "Id + 1", IsStored = true }],
        [new AddColumnOperation { Table = "Thing", Name = "Extra", ClrType = typeof(int), ComputedColumnSql = "Id + 1", IsStored = false }],
        [new CreateTableOperation { Name = "Other", Columns = { new AddColumnOperation { Table = "Other", Name = "Id", ClrType = typeof(int) } } }],
        [new RenameColumnOperation { Table = "Thing", Name = "Name", NewName = "Title" }],
        [new RenameTableOperation { Name = "Thing", NewName = "Things" }],
        [new AddForeignKeyOperation { Table = "Thing", Name = "FK", Columns = ["Id"], PrincipalTable = "Thing", PrincipalColumns = ["Id"] }],
        [new DropForeignKeyOperation { Table = "Thing", Name = "FK" }],
        [new AddPrimaryKeyOperation { Table = "Thing", Name = "PK", Columns = ["Id"] }],
        [new DropPrimaryKeyOperation { Table = "Thing", Name = "PK" }],
        [new AddUniqueConstraintOperation { Table = "Thing", Name = "AK", Columns = ["Name"] }],
        [new DropUniqueConstraintOperation { Table = "Thing", Name = "AK" }],
        [new AddCheckConstraintOperation { Table = "Thing", Name = "CK", Sql = "Id > 0" }],
        [new DropCheckConstraintOperation { Table = "Thing", Name = "CK" }],
        [new CreateIndexOperation { Table = "Thing", Name = "IX", Columns = ["Name"] }],
        [new DropIndexOperation { Table = "Thing", Name = "IX" }],
        [new AlterTableOperation { Name = "Thing", Comment = "c" }],
    ];

    /// <summary>
    /// Which operations SQLite rebuilds a table for is the provider's rule, and the kernel keeps a
    /// copy of it. Here the provider's own generator is asked: it writes the rebuild through a
    /// table named <c>ef_temp_…</c>.
    /// </summary>
    [TestMethod, DynamicData(nameof(SqliteOperations))]
    public void The_kernel_takes_for_a_rebuild_what_sqlites_generator_rebuilds_for(MigrationOperation operation)
    {
        using var context = SchemaOperationPlannerTests.Context<PlanModels.Bare>(Provider.Sqlite);
        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.GetService<IDesignTimeModel>().Model)
            .Select(command => command.CommandText));

        Assert.AreEqual(sql.Contains("ef_temp_", StringComparison.Ordinal), BaseChanges.RebuiltOnSqlite(operation) is not null, $"{operation.GetType().Name}:\n{sql}");
    }

    /// <summary>
    /// The failure itself, on a live database, and how far this version gets with it. Both models
    /// declare the index and neither changes it. EF Core alters a column, and SQLite's provider
    /// creates the table again without the index.
    /// </summary>
    /// <remarks>
    /// The planner sees it: the contributor is asked about the rebuild, answers that the index is
    /// lost, and the index is created again after EF Core's operations. But SQLite's generator
    /// moves a rebuild to the end of the migration, behind every statement that follows the
    /// operation, so the create runs while the index is still there and fails. That is a migration
    /// that does not apply, where without the planner's answer it applied and left the database
    /// without the index. Placing the create after the rebuild is the generator decorator's work
    /// (plan, section 3.12), and this test changes with it: the index is then there afterwards.
    /// </remarks>
    [TestMethod]
    public void On_sqlite_an_index_lost_to_a_rebuild_fails_the_migration_and_is_not_lost_in_silence()
    {
        var file = Path.Combine(Path.GetTempPath(), $"schemakit-{Guid.NewGuid():N}.db");
        var connection = $"Data Source={file};Pooling=False";
        try
        {
            using (var before = SchemaOperationPlannerTests.Context<PlanModels.Guarded>(Provider.Sqlite, connection: connection))
            {
                before.Database.EnsureCreated();
                CollectionAssert.Contains(Indexes(before), "ux_name", "EnsureCreated() creates the declared index.");
            }

            using var from = SchemaOperationPlannerTests.Context<PlanModels.Guarded>(Provider.Sqlite, connection: connection);
            using var to = SchemaOperationPlannerTests.Context<PlanModels.GuardedOptional>(Provider.Sqlite, connection: connection);
            var guard = (TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!;

            var operations = to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to));
            var commands = to.GetService<IMigrationsSqlGenerator>().Generate(operations, to.GetService<IDesignTimeModel>().Model)
                .Select(command => command.CommandText).ToList();

            CollectionAssert.AreEqual(
                new[] { nameof(AlterColumnOperation), """CREATE UNIQUE INDEX "ux_name" ON "Thing" ("Name");""" },
                SchemaOperationPlannerTests.Describe(operations));
            Assert.IsTrue(guard.Asked.OfType<BaseChange.TableRebuilt>().Any(), "The contributor is asked about the rebuild.");

            // What SQLite's generator does with that list: the create first, the rebuild behind it.
            var create = commands.FindIndex(c => c.Contains("CREATE UNIQUE INDEX \"ux_name\"", StringComparison.Ordinal));
            var rebuild = commands.FindIndex(c => c.Contains("ef_temp_", StringComparison.Ordinal));
            Assert.IsTrue(create >= 0 && rebuild > create, string.Join("\n---\n", commands));

            to.Database.OpenConnection();
            var error = Assert.Throws<System.Data.Common.DbException>(() =>
            {
                foreach (var text in commands)
                {
                    using var run = to.Database.GetDbConnection().CreateCommand();
                    run.CommandText = text;
                    run.ExecuteNonQuery();
                }
            });

            StringAssert.Contains(error.Message, "index ux_name already exists");
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static List<string> Indexes(DbContext context)
        => [.. context.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Thing'")];
}
