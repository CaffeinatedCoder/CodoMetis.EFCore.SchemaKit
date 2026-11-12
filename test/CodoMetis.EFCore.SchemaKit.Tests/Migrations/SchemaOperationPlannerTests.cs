using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>One model of a planner test. A context type per model, so no test reads a model another cached.</summary>
public interface IPlanModel
{
    static abstract void Build(ModelBuilder modelBuilder);
}

public sealed class Planned<TModel>(DbContextOptions<Planned<TModel>> options) : DbContext(options)
    where TModel : IPlanModel
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => TModel.Build(modelBuilder);
}

public static class PlanModels
{
    private static readonly TestGuard.Unique Name = new([new GuardKey("Name")]) { Name = "ux_name" };

    private static void Declare(ModelBuilder modelBuilder, params ISchemaObject[] declarations)
        => modelBuilder.Entity<Thing>().Metadata.Declare(declarations);

    public sealed class Bare : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    public sealed class Guarded : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name);
    }

    public sealed class Renamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Name = "ux_name2" });
    }

    /// <summary>The same name on other columns, and a column EF Core adds.</summary>
    public sealed class OnIdWithAColumn : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property<int>("Extra");
            Declare(modelBuilder, Name with { Keys = [new GuardKey("Id")] });
        }
    }

    /// <summary>Another name and other columns: nothing says it is the object there was.</summary>
    public sealed class RenamedAndChanged : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Name = "ux_other", Keys = [new GuardKey("Id")] });
    }

    /// <summary>Differs from <see cref="Guarded" /> in how the index is built, not in what it is.</summary>
    public sealed class Concurrently : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Concurrently = true });
    }

    /// <summary>Another contributor's object that resolves to the very descriptor <see cref="Guarded" />'s does.</summary>
    public sealed class LooseOfTheSameName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new LooseGuard("Name") { Name = "ux_name" });
    }

    public sealed class RenamedWithANewOne : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Declare(modelBuilder, Name with { Name = "ux_name2" }, new LooseGuard("Name") { Name = "lx_name" });
    }

    /// <summary>One object of each placement: before the tables, and after them at 5 and at 10.</summary>
    public sealed class Mixed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Declare(modelBuilder, Name, new LooseGuard("Name") { Name = "lx_name" }, new TestDomain("integer") { Name = "dom" });
    }

    public sealed class Domain : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("integer") { Name = "dom" });
    }

    public sealed class DomainOfAnotherType : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("bigint") { Name = "dom" });
    }

    public sealed class DomainRenamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("integer") { Name = "dom2" });
    }
}

/// <summary>
/// The planner, through the host: what two models' declarations amount to, in the list of
/// operations a migration gets. The statements are the sample contributors'; which of them are
/// written, and where they stand among EF Core's operations, is the planner's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaOperationPlannerTests
{
    private const string CreateIndex = """CREATE UNIQUE INDEX ux_name ON "Thing" ("Name");""";
    private const string DropIndex = "DROP INDEX ux_name;";

    private static Planned<TModel> Context<TModel>(Provider provider = Provider.Npgsql, Action<IServiceCollection>? more = null)
        where TModel : IPlanModel
    {
        var builder = new DbContextOptionsBuilder<Planned<TModel>>().UseProvider(provider);
        ServicesExtension.Add(builder, more is null ? "guards" : $"guards+{more.Method.Name}", services =>
        {
            services.AddSchemaContributor<TestGuardContributor>().AddSchemaContributor<LooseGuardContributor>().AddSchemaContributor<TestDomainContributor>();
            more?.Invoke(services);
        });
        return new Planned<TModel>(builder.UseSchemaKit().Options);
    }

    private static IRelationalModel Model(DbContext context) => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    /// <summary>The model a later diff starts from: the generated snapshot, compiled, as <c>dotnet ef</c> prepares it.</summary>
    private static IRelationalModel Snapshot(DbContext context)
        => context.GetService<IModelRuntimeInitializer>()
            .Initialize(Snapshots.Of(context).Model, designTime: true, validationLogger: null)
            .GetRelationalModel();

    /// <summary>A statement as its SQL, an operation of EF Core's as its type.</summary>
    private static List<string> Describe(IEnumerable<MigrationOperation> operations)
        => [.. operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name)];

    private static List<string> Diff<TFrom, TTo>(Action<TestGuardContributor>? arrange = null)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = Context<TFrom>();
        using var to = Context<TTo>();
        arrange?.Invoke((TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!);

        return Describe(to.GetService<IMigrationsModelDiffer>().GetDifferences(Model(from), Model(to)));
    }

    // ---- the same on both sides ----------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_model_against_its_own_snapshot_plans_nothing(Provider provider)
    {
        using var context = Context<PlanModels.Mixed>(provider);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var snapshot = Snapshot(context);

        Assert.AreEqual(0, differ.GetDifferences(snapshot, Model(context)).Count);
        Assert.IsFalse(differ.HasDifferences(snapshot, Model(context)));

        // And it is not that the planner sees nothing: from no model at all, each declaration is a statement.
        Assert.AreEqual(3, differ.GetDifferences(null, Model(context)).OfType<SqlOperation>().Count());
        Assert.AreEqual(3, differ.GetDifferences(snapshot, null).OfType<SqlOperation>().Count());
    }

    [TestMethod]
    public void A_difference_in_how_an_object_is_built_is_no_difference()
        => Assert.AreEqual(0, Diff<PlanModels.Guarded, PlanModels.Concurrently>().Count);

    // ---- creates and drops -----------------------------------------------------------------------------

    [TestMethod]
    public void A_new_declaration_is_created_after_the_table_it_is_on()
    {
        CollectionAssert.AreEqual(new[] { CreateIndex }, Diff<PlanModels.Bare, PlanModels.Guarded>());

        using var context = Context<PlanModels.Guarded>();
        CollectionAssert.AreEqual(
            new[] { nameof(CreateTableOperation), CreateIndex },
            Describe(context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context))));
    }

    [TestMethod]
    public void A_declaration_that_is_gone_is_dropped_before_the_table_it_was_on()
    {
        CollectionAssert.AreEqual(new[] { DropIndex }, Diff<PlanModels.Guarded, PlanModels.Bare>());

        using var context = Context<PlanModels.Guarded>();
        CollectionAssert.AreEqual(
            new[] { DropIndex, nameof(DropTableOperation) },
            Describe(context.GetService<IMigrationsModelDiffer>().GetDifferences(Model(context), null)));
    }

    // ---- a changed pair: the contributor's answer -------------------------------------------------------

    [TestMethod]
    public void A_name_that_changed_is_the_contributors_statement_in_place()
        => CollectionAssert.AreEqual(new[] { "ALTER INDEX ux_name RENAME TO ux_name2;" }, Diff<PlanModels.Guarded, PlanModels.Renamed>());

    [TestMethod]
    public void A_rebuild_drops_before_ef_cores_operations_and_creates_after_them()
        => CollectionAssert.AreEqual(
            new[] { DropIndex, nameof(AddColumnOperation), """CREATE UNIQUE INDEX ux_name ON "Thing" ("Id");""" },
            Diff<PlanModels.Guarded, PlanModels.OnIdWithAColumn>());

    /// <summary>
    /// The same name with other content is the same object, changed: what it takes is the
    /// contributor's to say, and it may be less than a rebuild.
    /// </summary>
    [TestMethod]
    public void A_changed_object_of_the_same_name_is_the_contributors_to_plan()
        => CollectionAssert.AreEqual(
            new[] { nameof(AddColumnOperation), "ALTER INDEX ux_name SET (fillfactor = 70);" },
            Diff<PlanModels.Guarded, PlanModels.OnIdWithAColumn>(guard => guard.PlanOverride = sql =>
            {
                var index = new QualifiedName(null, sql.Dialect.Identifier("ux_name"));
                sql.Statement($"ALTER INDEX {index} SET (fillfactor = {70})");
                return new SchemaChange.InPlace();
            }));

    /// <summary>
    /// Two contributors may share a descriptor type. An equal descriptor of another contributor is
    /// another object: here a unique index gives way to a plain one of the same name and column.
    /// </summary>
    [TestMethod]
    public void Equal_descriptors_of_two_contributors_are_two_objects()
        => CollectionAssert.AreEqual(
            new[] { DropIndex, """CREATE INDEX ux_name ON "Thing" ("Name");""" },
            Diff<PlanModels.Guarded, PlanModels.LooseOfTheSameName>());

    /// <summary>The descriptors differ, and the contributor says the database need not hear of it.</summary>
    [TestMethod]
    public void An_answer_of_none_adds_nothing()
    {
        using var from = Context<PlanModels.Guarded>();
        using var to = Context<PlanModels.Renamed>();
        ((TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!).PlanOverride = _ => new SchemaChange.None();
        var differ = to.GetService<IMigrationsModelDiffer>();

        Assert.AreEqual(0, differ.GetDifferences(Model(from), Model(to)).Count);
        Assert.IsFalse(differ.HasDifferences(Model(from), Model(to)));
    }

    /// <summary>With another name and other columns nothing says the two are one object, so the contributor is not asked.</summary>
    [TestMethod]
    public void Two_objects_that_share_neither_name_nor_content_are_a_drop_and_a_create()
        => CollectionAssert.AreEqual(
            new[] { DropIndex, """CREATE UNIQUE INDEX ux_other ON "Thing" ("Id");""" },
            Diff<PlanModels.Guarded, PlanModels.RenamedAndChanged>(guard => guard.PlanOverride = _ => throw new InvalidOperationException("Plan was asked.")));

    // ---- order -------------------------------------------------------------------------------------------

    [TestMethod]
    public void Statements_in_place_come_before_creates()
        => CollectionAssert.AreEqual(
            new[] { "ALTER INDEX ux_name RENAME TO ux_name2;", """CREATE INDEX lx_name ON "Thing" ("Name");""" },
            Diff<PlanModels.Guarded, PlanModels.RenamedWithANewOne>());

    /// <summary>
    /// What tables are made of is created before them and dropped after them; what is attached to
    /// them is created after them by placement, and dropped before them in the reverse.
    /// </summary>
    [TestMethod]
    public void Contributors_are_ordered_by_their_placement_around_ef_cores_operations()
    {
        using var context = Context<PlanModels.Mixed>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        CollectionAssert.AreEqual(
            new[] { "CREATE DOMAIN dom AS integer;", nameof(CreateTableOperation), """CREATE INDEX lx_name ON "Thing" ("Name");""", CreateIndex },
            Describe(differ.GetDifferences(null, Model(context))));
        CollectionAssert.AreEqual(
            new[] { DropIndex, "DROP INDEX lx_name;", nameof(DropTableOperation), "DROP DOMAIN dom;" },
            Describe(differ.GetDifferences(Model(context), null)));
    }

    [TestMethod]
    public void What_tables_are_made_of_changes_in_place_or_not_at_all()
    {
        CollectionAssert.AreEqual(new[] { "ALTER DOMAIN dom RENAME TO dom2;" }, Diff<PlanModels.Domain, PlanModels.DomainRenamed>());

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<PlanModels.Domain, PlanModels.DomainOfAnotherType>());
        StringAssert.Contains(error.Message, "TestDomainContributor'.Plan answered Rebuild for 'dom', and its placement is BeforeTables");
    }

    // ---- what the planner refuses, and what it leaves alone ---------------------------------------------

    /// <summary>A declaration nothing renders would be left out of every migration, in silence.</summary>
    [TestMethod]
    public void A_declaration_with_no_contributor_is_refused_on_either_side()
    {
        var builder = new DbContextOptionsBuilder<Planned<PlanModels.Guarded>>().UseProvider(Provider.Npgsql);
        using var context = new Planned<PlanModels.Guarded>(builder.UseSchemaKit().Options);
        var differ = context.GetService<IMigrationsModelDiffer>();

        foreach (var (source, target) in new[] { ((IRelationalModel?)null, (IRelationalModel?)Model(context)), (Model(context), null) })
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => differ.GetDifferences(source, target));
            StringAssert.Contains(error.Message, "Thing carries a declaration of 'Test.Guard', and no contributor with that owner is registered");
        }
    }

    /// <summary>
    /// A context that declares nothing pays nothing: the dialect, which reads the design-time model,
    /// is not built. The second half shows the first is not vacuous.
    /// </summary>
    [TestMethod]
    public void A_context_that_declares_nothing_never_builds_the_dialect()
    {
        static void Poisoned(IServiceCollection services)
            => services.AddScoped<ISchemaDialect>(_ => throw new InvalidOperationException("The dialect was built."));

        using var bare = Context<PlanModels.Bare>(more: Poisoned);
        Assert.AreEqual(1, bare.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(bare)).Count);

        using var guarded = Context<PlanModels.Guarded>(more: Poisoned);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => guarded.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(guarded)));
        Assert.AreEqual("The dialect was built.", error.Message);
    }
}
