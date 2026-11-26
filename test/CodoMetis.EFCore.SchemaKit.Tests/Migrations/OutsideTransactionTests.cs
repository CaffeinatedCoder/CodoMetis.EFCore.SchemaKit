using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>
/// A statement that runs outside a transaction (#155, #206). Not every way of applying a
/// migration carries one: on PostgreSQL an idempotent script puts it into a block where the
/// server refuses it, psql goes on, and the migration is recorded without its object. Which way
/// a migration is applied is known only when its SQL is generated, where nothing but the
/// operation is at hand. So the statement says which object it is for, the dialect says which
/// ways carry it, and the generator refuses the rest.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OutsideTransactionTests
{
    /// <summary>
    /// The annotation's name, written down a second time. <c>migrations add</c> writes it into
    /// the migration, so it is in consumers' repositories: a file format.
    /// </summary>
    private const string Stamp = "CodoMetis:SchemaKit:Object";

    private const string Why = "This database runs no such statement from an idempotent script. Apply the migration another way.";

    /// <summary>A dialect as PostgreSQL's package brings it: an idempotent script does not carry the statement.</summary>
    private sealed class NotInAnIdempotentScript(SchemaDialectDependencies dependencies) : SchemaDialect(dependencies)
    {
        public override string? WhyNotOutsideTransaction(MigrationsSqlGenerationOptions options)
            => options.HasFlag(MigrationsSqlGenerationOptions.Idempotent) ? Why : null;

        public override void EnsureExtension(SqlName extension, IReadOnlyModel model, SchemaSql sql)
            => sql.Statement($"CREATE EXTENSION {extension}", suppressTransaction: true);
    }

    /// <summary>A dialect that must not be asked.</summary>
    private sealed class NeverAsked(SchemaDialectDependencies dependencies) : SchemaDialect(dependencies)
    {
        public override string? WhyNotOutsideTransaction(MigrationsSqlGenerationOptions options)
            => throw new InvalidOperationException("The dialect was asked about a statement that is not SchemaKit's, or runs in a transaction.");
    }

    private static SchemaDialectDependencies Dependencies(IServiceProvider provider) => new(
        provider.GetRequiredService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>(),
        provider.GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>());

    private static void Refusing(IServiceCollection services)
        => services.AddScoped<ISchemaDialect>(provider => new NotInAnIdempotentScript(Dependencies(provider)));

    private static void Unasked(IServiceCollection services)
        => services.AddScoped<ISchemaDialect>(provider => new NeverAsked(Dependencies(provider)));

    private static IReadOnlyList<MigrationOperation> Diff<TFrom, TTo>(
        Provider provider = Provider.Npgsql,
        bool outside = true,
        Action<IServiceCollection>? more = null)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = SchemaOperationPlannerTests.Context<TFrom>(provider, more);
        using var to = SchemaOperationPlannerTests.Context<TTo>(provider, more);
        ((TestDomainContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Domain")!).OutsideTransaction = outside;
        ((TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!).OutsideTransaction = outside;

        return to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to));
    }

    private static List<string> Stamps(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => $"{o.Sql} | {o.SuppressTransaction} | {o.FindAnnotation(Stamp)?.Value ?? "-"}")];

    /// <summary>
    /// Whatever the planner asks a contributor for, a create, a drop or a change in place, the
    /// statement names the object: by the name the database has for it and the entity type that
    /// declares it. A change in place names what the object becomes.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_statement_outside_a_transaction_says_which_object_it_is_for(Provider provider)
    {
        var create = Diff<PlanModels.Bare, PlanModels.Domain>(provider).OfType<SqlOperation>().Single();
        Assert.IsTrue(create.SuppressTransaction);
        Assert.AreEqual("Test.Domain 'dom' on Thing", create.FindAnnotation(Stamp)?.Value);

        var drop = Diff<PlanModels.Domain, PlanModels.Bare>(provider).OfType<SqlOperation>().Single();
        Assert.IsTrue(drop.SuppressTransaction);
        Assert.AreEqual("Test.Domain 'dom' on Thing", drop.FindAnnotation(Stamp)?.Value);

        var rename = Diff<PlanModels.Domain, PlanModels.DomainRenamed>(provider).OfType<SqlOperation>().Single();
        Assert.IsTrue(rename.SuppressTransaction);
        Assert.AreEqual("Test.Domain 'dom2' on Thing", rename.FindAnnotation(Stamp)?.Value);
    }

    /// <summary>Each statement names its own object, not the one written before it.</summary>
    [TestMethod]
    public void Each_statement_names_its_own_object()
        => CollectionAssert.AreEqual(
            new[]
            {
                "CREATE EXTENSION citext; | True | the extension 'citext'",
                "CREATE DOMAIN dom2 AS citext; | True | Test.Domain 'dom2' on Thing",
                "CREATE DOMAIN dom AS citext; | True | Test.Domain 'dom' on Thing",
                """CREATE UNIQUE INDEX ux_name ON "Thing" ("Name"); | True | Test.Guard 'ux_name' on Thing""",
            },
            Stamps(Diff<PlanModels.Bare, PlanModels.TwoDomainsOfAnExtension>(more: Refusing)));

    /// <summary>
    /// The same for what is attached to tables, which is created after EF Core's operations and
    /// dropped before them: three objects of two contributors, each statement with its own.
    /// </summary>
    [TestMethod]
    public void What_is_attached_to_tables_names_its_own_object_too()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "CREATE DOMAIN dom AS integer; | True | Test.Domain 'dom' on Thing",

                // A third contributor's index, written in a transaction: it says nothing.
                """CREATE INDEX lx_name ON "Thing" ("Name"); | False | -""",
                """CREATE UNIQUE INDEX ux_name ON "Thing" ("Name"); | True | Test.Guard 'ux_name' on Thing""",
            },
            Stamps(Diff<PlanModels.Bare, PlanModels.Mixed>()));

        CollectionAssert.AreEqual(
            new[]
            {
                "DROP INDEX ux_name; | True | Test.Guard 'ux_name' on Thing",
                "DROP INDEX lx_name; | False | -",
                "DROP DOMAIN dom; | True | Test.Domain 'dom' on Thing",
            },
            Stamps(Diff<PlanModels.Mixed, PlanModels.Bare>()));
    }

    /// <summary>
    /// A statement in a transaction says nothing of itself: every way of applying a migration
    /// carries it, and a migration is not written longer than it has to be.
    /// </summary>
    [TestMethod]
    public void A_statement_in_a_transaction_carries_nothing()
    {
        var operations = Diff<PlanModels.Bare, PlanModels.Domain>(outside: false);

        Assert.AreEqual(1, operations.Count);
        Assert.IsFalse(operations.Single().GetAnnotations().Any());
    }

    private static IReadOnlyList<MigrationCommand> Generate(
        Planned<PlanModels.Bare> context,
        IReadOnlyList<MigrationOperation> operations,
        MigrationsSqlGenerationOptions options)
        => context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.GetService<IDesignTimeModel>().Model, options);

    /// <summary>
    /// The way of applying a migration that the dialect says does not carry the statement is
    /// refused with the object, the statement and the dialect's sentence, and writes nothing.
    /// Every other way writes the statement as it is, with its transaction suppressed: the same
    /// migration is applied by a bundle and written into a script.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_way_that_does_not_carry_it_is_refused_and_no_other(Provider provider)
    {
        var operations = Diff<PlanModels.Bare, PlanModels.Domain>(provider);
        using var context = SchemaOperationPlannerTests.Context<PlanModels.Bare>(provider, Refusing);
        var statement = operations.OfType<SqlOperation>().Single().Sql;

        foreach (var carried in new[] { MigrationsSqlGenerationOptions.Default, MigrationsSqlGenerationOptions.Script })
        {
            var command = Generate(context, operations, carried).Single();
            Assert.IsTrue(command.TransactionSuppressed, carried.ToString());
            StringAssert.Contains(command.CommandText, statement);
        }

        foreach (var refused in new[] { MigrationsSqlGenerationOptions.Idempotent, MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent })
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => Generate(context, operations, refused), refused.ToString());
            StringAssert.Contains(error.Message, "Test.Domain 'dom' on Thing");
            StringAssert.Contains(error.Message, statement);
            StringAssert.Contains(error.Message, Why);
        }
    }

    /// <summary>
    /// The kernel's own dialect, which a provider without a package has, carries the statement
    /// every way. SQL Server does, in an idempotent script too (spike D).
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer)]
    public void A_provider_without_a_package_carries_it_every_way(Provider provider)
    {
        var operations = Diff<PlanModels.Bare, PlanModels.Domain>(provider);
        using var context = SchemaOperationPlannerTests.Context<PlanModels.Bare>(provider);

        var command = Generate(context, operations, MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent).Single();

        Assert.IsTrue(command.TransactionSuppressed);
    }

    /// <summary>
    /// Only SchemaKit's own statement is judged, and only one outside a transaction: a
    /// consumer's <c>migrationBuilder.Sql(…, suppressTransaction: true)</c> is theirs, as it was
    /// before SchemaKit, and a statement in a transaction is carried every way.
    /// </summary>
    [TestMethod]
    public void A_statement_that_is_not_SchemaKits_is_not_judged()
    {
        using var context = SchemaOperationPlannerTests.Context<PlanModels.Bare>(more: Unasked);
        const MigrationsSqlGenerationOptions idempotent = MigrationsSqlGenerationOptions.Script | MigrationsSqlGenerationOptions.Idempotent;

        var theirs = new SqlOperation { Sql = "CREATE INDEX CONCURRENTLY by_hand ON t (c);", SuppressTransaction = true };
        Assert.IsTrue(Generate(context, [theirs], idempotent).Single().TransactionSuppressed);

        var inATransaction = Diff<PlanModels.Bare, PlanModels.Domain>(outside: false);
        Assert.IsFalse(Generate(context, inATransaction, idempotent).Single().TransactionSuppressed);

        // One that says what it is for and runs in a transaction, as after a consumer's edit of
        // the migration: every way carries it.
        var edited = new SqlOperation { Sql = "CREATE INDEX edited ON t (c);" };
        edited.AddAnnotation(Stamp, "Test.Domain 'edited' on Thing");
        Assert.IsFalse(Generate(context, [edited], idempotent).Single().TransactionSuppressed);

        // And the dialect does throw when it is asked: the same statement, stamped.
        theirs.AddAnnotation(Stamp, "Test.Domain 'by_hand' on Thing");
        Assert.ThrowsExactly<InvalidOperationException>(() => Generate(context, [theirs], idempotent));
    }
}
