using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Contributors;

/// <summary>
/// The contributor contract, driven as the kernel will drive it: through
/// <see cref="ISchemaContributor" />, against a contributor written outside the kernel.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ContributorContractTests
{
    private sealed class Plain(DbContextOptions<Plain> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private sealed class Registered(DbContextOptions<Registered> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private sealed class Rivals(DbContextOptions<Rivals> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private sealed class Unplaced(DbContextOptions<Unplaced> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    /// <summary>Takes the dialect, as a contributor may: the dialect reads the design-time model.</summary>
    private sealed class DialectContributor(SchemaContributorDependencies dependencies, ISchemaDialect dialect)
        : SchemaContributor<LooseGuard, TestGuardDescriptor>(dependencies)
    {
        public ISchemaDialect Dialect => dialect;
        public override SchemaPlacement Placement => SchemaPlacement.BeforeTables;
        protected override TestGuardDescriptor Resolve(LooseGuard declaration, SchemaResolver resolver) => throw new NotSupportedException();
        protected override void Create(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
        protected override void Drop(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
        protected override SchemaChange Plan(TestGuardDescriptor from, TestGuardDescriptor to, SchemaSql sql) => throw new NotSupportedException();
        protected override Impact ImpactOf(TestGuardDescriptor descriptor, BaseChange change) => throw new NotSupportedException();
        protected override IEnumerable<NameClaim> Claims(TestGuardDescriptor descriptor) => throw new NotSupportedException();
    }

    private sealed class Direct(DbContextOptions<Direct> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private sealed class Layered(DbContextOptions<Layered> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private static Plain Context(Provider provider = Provider.Npgsql)
        => new(new DbContextOptionsBuilder<Plain>().UseProvider(provider).UseSchemaKit().Options);

    private static (ISchemaContributor Contributor, TestGuardContributor Typed, TestResolver Resolver, ISchemaDialect Dialect) Kit(DbContext context)
    {
        var dialect = context.GetService<ISchemaDialect>();
        var typed = new TestGuardContributor(new SchemaContributorDependencies());
        var table = new TableRef(dialect.Identifier("app"), dialect.Identifier("people"));
        return (typed, typed, new TestResolver(table, dialect), dialect);
    }

    private static readonly TestGuard.Unique Email = new([new GuardKey("Email"), new GuardKey("Name")]) { Name = "ux_people_email" };

    // ---- the pipeline: declaration, descriptor, statements --------------------------------------------

    [TestMethod]
    public void A_contributor_resolves_creates_and_drops_through_the_interface()
    {
        using var context = Context();
        var (contributor, _, resolver, dialect) = Kit(context);

        var descriptor = contributor.Resolve(Email, resolver);
        var create = new SchemaSql(dialect);
        var drop = new SchemaSql(dialect);
        contributor.Create(descriptor, create);
        contributor.Drop(descriptor, drop);

        Assert.AreEqual("Test.Guard", contributor.Owner);
        Assert.AreEqual(new SchemaScope.Table(resolver.Table), descriptor.Scope);
        Assert.AreEqual("app.ux_people_email", descriptor.Name.ToString());
        Assert.AreEqual("""CREATE UNIQUE INDEX ux_people_email ON app.people ("Email", "Name");""", create.Operations.Single().Sql);
        Assert.AreEqual("DROP INDEX app.ux_people_email;", drop.Operations.Single().Sql);
        CollectionAssert.AreEqual(
            new Dependency[] { new Dependency.Column(resolver.Table, dialect.Identifier("Email")), new Dependency.Column(resolver.Table, dialect.Identifier("Name")) },
            descriptor.Dependencies.ToList());
        CollectionAssert.AreEqual(
            new NameClaim[] { new NameClaim.Relation(dialect.Identifier("app"), dialect.Identifier("ux_people_email")) },
            contributor.Claims(descriptor).ToList());
    }

    /// <summary>Equality of descriptors is the diff: two resolutions of one declaration are equal, lists included.</summary>
    [TestMethod]
    public void Two_resolutions_of_one_declaration_are_equal_and_an_operational_option_does_not_make_them_differ()
    {
        using var context = Context();
        var (contributor, _, resolver, dialect) = Kit(context);

        var first = contributor.Resolve(Email, resolver);
        var second = contributor.Resolve(Email with { Concurrently = true }, resolver);
        var other = contributor.Resolve(Email with { Keys = [new GuardKey("Email")] }, resolver);
        var renamed = contributor.Resolve(Email with { Name = "ux_other" }, resolver);

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreNotEqual(first, other);
        Assert.AreNotEqual(first, renamed);
        Assert.AreEqual(renamed, first.WithName(resolver.Table.Qualify(dialect.Identifier("ux_other"))));
    }

    // ---- Plan and ImpactOf, the two mandatory answers ------------------------------------------------

    [TestMethod]
    public void A_rename_is_planned_in_place_and_a_change_of_columns_as_a_rebuild()
    {
        using var context = Context();
        var (contributor, _, resolver, dialect) = Kit(context);
        var from = contributor.Resolve(Email, resolver);

        var rename = new SchemaSql(dialect);
        Assert.AreEqual(new SchemaChange.InPlace(), contributor.Plan(from, contributor.Resolve(Email with { Name = "ux_new" }, resolver), rename));
        Assert.AreEqual("ALTER INDEX app.ux_people_email RENAME TO ux_new;", rename.Operations.Single().Sql);

        var rebuild = new SchemaSql(dialect);
        Assert.AreEqual(new SchemaChange.Rebuild(), contributor.Plan(from, contributor.Resolve(Email with { Keys = [new GuardKey("Email")] }, resolver), rebuild));
        Assert.AreEqual(0, rebuild.Operations.Count);
    }

    /// <summary>
    /// The answer and the statements have to agree. An InPlace with nothing written would report a
    /// change as handled and change nothing; statements beside a Rebuild or a None would be dropped.
    /// </summary>
    [TestMethod]
    public void A_plan_whose_answer_and_statements_disagree_is_refused()
    {
        using var context = Context();
        var (contributor, typed, resolver, dialect) = Kit(context);
        var from = contributor.Resolve(Email, resolver);
        var to = contributor.Resolve(Email with { Name = "ux_new" }, resolver);

        typed.PlanOverride = _ => new SchemaChange.InPlace();
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Plan(from, to, new SchemaSql(dialect))).Message,
            "answered InPlace for 'app.ux_new' and wrote no statement");

        foreach (SchemaChange answer in new SchemaChange[] { new SchemaChange.Rebuild(), new SchemaChange.None() })
        {
            typed.PlanOverride = sql =>
            {
                sql.Statement($"ALTER INDEX {from.Name} RENAME TO {to.Name.Name}");
                return answer;
            };
            StringAssert.Contains(
                Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Plan(from, to, new SchemaSql(dialect))).Message,
                $"answered {answer.GetType().Name} for 'app.ux_new' and wrote statements");
        }

        typed.PlanOverride = _ => null!;
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Plan(from, to, new SchemaSql(dialect))).Message,
            "Plan returned null");
    }

    /// <summary>A descriptor and a list of claims are answers too: the kernel compares the one and checks the other.</summary>
    [TestMethod]
    public void A_resolution_or_a_list_of_claims_that_is_null_is_refused()
    {
        using var context = Context();
        var (guard, _, resolver, _) = Kit(context);
        ISchemaContributor contributor = new DerivedTwiceContributor(new SchemaContributorDependencies());

        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Resolve(new LooseGuard("A"), resolver)).Message,
            "DerivedTwiceContributor'.Resolve returned null");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Claims(guard.Resolve(Email, resolver))).Message,
            "DerivedTwiceContributor'.Claims returned null");
    }

    /// <summary>Statements an earlier plan left in the builder are not this plan's.</summary>
    [TestMethod]
    public void A_plan_is_judged_by_what_it_wrote_itself()
    {
        using var context = Context();
        var (contributor, typed, resolver, dialect) = Kit(context);
        var from = contributor.Resolve(Email, resolver);
        var to = contributor.Resolve(Email with { Name = "ux_new" }, resolver);
        var sql = new SchemaSql(dialect);
        contributor.Plan(from, to, sql);

        typed.PlanOverride = _ => new SchemaChange.Rebuild();
        Assert.AreEqual(new SchemaChange.Rebuild(), contributor.Plan(from, to, sql));

        typed.PlanOverride = _ => new SchemaChange.InPlace();
        Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Plan(from, to, sql));
    }

    [TestMethod]
    public void A_contributor_answers_every_base_change()
    {
        using var context = Context();
        var (contributor, _, resolver, dialect) = Kit(context);
        var descriptor = contributor.Resolve(Email, resolver);
        var column = dialect.Identifier("Email");

        Assert.AreEqual(new Impact.Follows(), contributor.ImpactOf(descriptor, new BaseChange.ColumnRenamed(resolver.Table, column, dialect.Identifier("Mail"))));
        Assert.AreEqual(new Impact.Unaffected(), contributor.ImpactOf(descriptor, new BaseChange.ColumnAltered(resolver.Table, column)));
        Assert.AreEqual(new Impact.Lost(), contributor.ImpactOf(descriptor, new BaseChange.ColumnDropped(resolver.Table, column)));
        Assert.AreEqual(new Impact.Lost(), contributor.ImpactOf(descriptor, new BaseChange.TableRebuilt(resolver.Table)));
    }

    /// <summary>A null would reach the planner as "no answer", and the planner must act on every answer.</summary>
    [TestMethod]
    public void An_impact_that_is_null_is_refused()
    {
        using var context = Context();
        var (contributor, typed, resolver, _) = Kit(context);
        var descriptor = contributor.Resolve(Email, resolver);

        typed.ImpactOverride = _ => null!;
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => contributor.ImpactOf(descriptor, new BaseChange.TableDropped(resolver.Table)));

        StringAssert.Contains(error.Message, "ImpactOf returned null. Answer Unaffected, Follows, Lost or Blocks.");
        StringAssert.Contains(error.Message, nameof(TestGuardContributor));
    }

    // ---- routing ----------------------------------------------------------------------------------------

    [TestMethod]
    public void A_declaration_or_a_descriptor_of_another_contributor_is_refused()
    {
        using var context = Context();
        var (contributor, _, resolver, dialect) = Kit(context);

        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Resolve(new LooseGuard("A"), resolver)).Message,
            "LooseGuard' was handed to the Test.Guard contributor");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Create(new ReferenceDescriptor(), new SchemaSql(dialect))).Message,
            "ReferenceDescriptor' was handed to the Test.Guard contributor");
        Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Drop(new ReferenceDescriptor(), new SchemaSql(dialect)));
        Assert.ThrowsExactly<InvalidOperationException>(() => contributor.Claims(new ReferenceDescriptor()));
        Assert.ThrowsExactly<InvalidOperationException>(() => contributor.ImpactOf(new ReferenceDescriptor(), new BaseChange.TableDropped(resolver.Table)));
    }

    /// <summary>
    /// C# cannot constrain a type argument to a record, and a descriptor that compares by reference
    /// never equals its counterpart: every migration would drop and create the object.
    /// </summary>
    [TestMethod]
    public void A_descriptor_that_is_not_a_record_is_refused_when_the_contributor_is_built()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new ReferenceDescriptorContributor(new SchemaContributorDependencies()));

        StringAssert.Contains(error.Message, "ReferenceDescriptor', which is not a record");
    }

    // ---- registration ------------------------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_registered_contributor_reaches_the_registry_and_leaves_the_host_as_it_was(Provider provider)
    {
        var builder = new DbContextOptionsBuilder<Registered>().UseProvider(provider);
        ServicesExtension.Add(builder, "contributors", services => services
            .AddSchemaContributor<TestGuardContributor>()
            .AddSchemaContributor<TestGuardContributor>()
            .AddSchemaContributor<DialectContributor>());
        using var context = new Registered(builder.UseSchemaKit().Options);

        // Building the model runs the ownership check, which resolves the differ and the generator.
        // A contributor that takes the dialect needs the model, so neither may be built from one.
        _ = context.Model;
        Assert.AreEqual("SchemaKitModelDiffer", context.GetService<IMigrationsModelDiffer>().GetType().Name);
        Assert.AreEqual("SchemaKitSqlGenerator", context.GetService<IMigrationsSqlGenerator>().GetType().Name);

        var registry = context.GetService<SchemaContributorRegistry>();
        Assert.AreEqual(2, registry.All.Count, "Adding a contributor twice adds it once.");
        Assert.IsInstanceOfType<TestGuardContributor>(registry.Find("Test.Guard"));
        Assert.AreSame(context.GetService<ISchemaDialect>(), ((DialectContributor)registry.Find("Test.Loose")!).Dialect);
        Assert.IsNull(registry.Find("Pg.Index"));
    }

    [TestMethod]
    public void Two_contributors_for_one_owner_are_refused()
    {
        var builder = new DbContextOptionsBuilder<Rivals>().UseProvider(Provider.Sqlite);
        ServicesExtension.Add(builder, "rivals", services => services
            .AddSchemaContributor<TestGuardContributor>()
            .AddSchemaContributor<RivalGuardContributor>());
        using var context = new Rivals(builder.UseSchemaKit().Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<SchemaContributorRegistry>());

        StringAssert.Contains(error.Message, "Two contributors have the owner 'Test.Guard'");
        StringAssert.Contains(error.Message, nameof(TestGuardContributor));
        StringAssert.Contains(error.Message, nameof(RivalGuardContributor));
    }

    /// <summary>Without a placement the planner would not know which side of EF Core's operations to put the contributor's on.</summary>
    [TestMethod]
    public void A_contributor_without_a_placement_is_refused()
    {
        var builder = new DbContextOptionsBuilder<Unplaced>().UseProvider(Provider.Sqlite);
        ServicesExtension.Add(builder, "unplaced", services => services.AddSchemaContributor<UnplacedContributor>());
        using var context = new Unplaced(builder.UseSchemaKit().Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<SchemaContributorRegistry>());

        StringAssert.Contains(error.Message, "has no placement. Answer SchemaPlacement.BeforeTables or SchemaPlacement.AfterTables(order).");
        StringAssert.Contains(error.Message, nameof(UnplacedContributor));
    }

    /// <summary>
    /// The refusals of an answer are in the base class. A class that implements the interface itself
    /// has none of them, so it is refused where it is added.
    /// </summary>
    [TestMethod]
    public void A_contributor_that_implements_the_interface_itself_is_refused_where_it_is_added()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new ServiceCollection().AddSchemaContributor<DirectContributor>());

        StringAssert.Contains(error.Message, "DirectContributor' implements ISchemaContributor without deriving from SchemaContributor<TDeclaration, TDescriptor>");
    }

    /// <summary>Nothing makes a package call AddSchemaContributor: the collection takes the service as it takes any other.</summary>
    [TestMethod]
    public void A_contributor_that_implements_the_interface_itself_is_refused_by_the_registry_however_it_was_added()
    {
        var builder = new DbContextOptionsBuilder<Direct>().UseProvider(Provider.Sqlite);
        ServicesExtension.Add(builder, "direct", services => services.AddScoped<ISchemaContributor, DirectContributor>());
        using var context = new Direct(builder.UseSchemaKit().Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<SchemaContributorRegistry>());

        StringAssert.Contains(error.Message, "DirectContributor' implements ISchemaContributor without deriving from SchemaContributor<TDeclaration, TDescriptor>");
    }

    /// <summary>The allowed twin: a package may put a base class of its own in between.</summary>
    [TestMethod]
    public void A_contributor_that_derives_through_a_base_class_of_its_package_is_taken()
    {
        var builder = new DbContextOptionsBuilder<Layered>().UseProvider(Provider.Sqlite);
        ServicesExtension.Add(builder, "layered", services => services.AddSchemaContributor<DerivedTwiceContributor>());
        using var context = new Layered(builder.UseSchemaKit().Options);

        Assert.IsInstanceOfType<DerivedTwiceContributor>(context.GetService<SchemaContributorRegistry>().Find("Test.Loose"));
    }

    [TestMethod]
    public void A_context_nothing_contributed_to_has_an_empty_registry()
    {
        using var context = Context(Provider.Sqlite);

        Assert.AreEqual(0, context.GetService<SchemaContributorRegistry>().All.Count);
    }

    [TestMethod]
    public void A_caller_owned_provider_takes_contributors_on_its_collection()
    {
        using var internalProvider = new ServiceCollection()
            .AddProvider(Provider.Sqlite).AddEntityFrameworkSchemaKit().AddSchemaContributor<TestGuardContributor>().BuildServiceProvider();
        var builder = new DbContextOptionsBuilder<Plain>().UseProvider(Provider.Sqlite);
        builder.UseInternalServiceProvider(internalProvider);
        using var context = new Plain(builder.UseSchemaKit().Options);

        Assert.IsInstanceOfType<TestGuardContributor>(context.GetService<SchemaContributorRegistry>().Find("Test.Guard"));
    }
}
