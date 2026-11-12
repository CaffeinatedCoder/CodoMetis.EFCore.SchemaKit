using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// <c>UseNpgsql(…, o => o.UseSchemaKit())</c> is the whole registration: the kernel's decorators
/// outermost, PostgreSQL's dialect in place of the kernel's, and the contributor.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NpgsqlWiringTests
{
    private static string Script(DbContext context) => context.Database.GenerateCreateScript();

    [TestMethod]
    public void The_provider_builders_call_wires_the_kernel_and_the_contributor()
    {
        using var context = Contexts.Of<Models.Scheduling>();

        Assert.AreEqual("SchemaKitModelDiffer", context.GetService<IMigrationsModelDiffer>().GetType().Name);
        Assert.AreEqual("SchemaKitSqlGenerator", context.GetService<IMigrationsSqlGenerator>().GetType().Name);
        StringAssert.Contains(Script(context), $"ADD CONSTRAINT {Models.Name} EXCLUDE");
    }

    // 40 characters and 80 bytes: PostgreSQL keeps 63 bytes and cuts the rest off without an error.
    private static readonly string FortyTwoByteCharacters = new('ä', 40);

    [TestMethod]
    public void The_dialect_counts_a_names_bytes()
    {
        using var context = Contexts.Of<Models.Bare>();
        var dialect = context.GetService<ISchemaDialect>();

        Assert.AreEqual("NpgsqlSchemaDialect", dialect.GetType().Name);
        Assert.AreEqual(new string('a', 63), dialect.Identifier(new string('a', 63)).Name);
        Assert.ThrowsExactly<InvalidOperationException>(() => dialect.Identifier(new string('a', 64)));

        // 31 two-byte characters and one of one byte are 63 bytes; one more two-byte character is 65.
        Assert.AreEqual(32, dialect.Identifier(new string('ä', 31) + "a").Name.Length);
        Assert.ThrowsExactly<InvalidOperationException>(() => dialect.Identifier(new string('ä', 32)));
        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => dialect.Identifier(FortyTwoByteCharacters));
        StringAssert.Contains(refusal.Message, "80");
    }

    private sealed class KernelOnly(DbContextOptions<KernelOnly> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>();
    }

    /// <summary>
    /// What the override is for, held as a fact about the kernel's dialect: wired without this
    /// package, the same name passes. If this stopped being true the override would guard nothing.
    /// </summary>
    [TestMethod]
    public void The_kernels_own_dialect_lets_the_same_name_through()
    {
        using var context = new KernelOnly(new DbContextOptionsBuilder<KernelOnly>().UseNpgsql(Contexts.Offline).UseSchemaKit().Options);

        Assert.AreEqual(40, context.GetService<ISchemaDialect>().Identifier(FortyTwoByteCharacters).Name.Length);
    }

    private sealed class LongName : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), name: FortyTwoByteCharacters)]);
    }

    [TestMethod]
    public void A_constraint_name_the_server_would_shorten_is_refused_when_the_model_is_diffed()
    {
        using var context = Contexts.Of<LongName>();

        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => Script(context));
        StringAssert.Contains(refusal.Message, FortyTwoByteCharacters);
    }

    /// <summary>Either order of the two calls leaves PostgreSQL's dialect in place: the kernel's is added with TryAdd, this one replaces.</summary>
    [TestMethod]
    public void The_kernels_call_before_or_after_changes_nothing()
    {
        var before = new DbContextOptionsBuilder<Db<Models.Scheduling>>().UseSchemaKit().UseNpgsql(Contexts.Offline, o => o.UseSchemaKit());
        var after = new DbContextOptionsBuilder<Db<Models.Scheduling>>().UseNpgsql(Contexts.Offline, o => o.UseSchemaKit()).UseSchemaKit();

        foreach (var builder in new[] { before, after })
        {
            using var context = new Db<Models.Scheduling>(builder.Options);

            Assert.AreEqual("NpgsqlSchemaDialect", context.GetService<ISchemaDialect>().GetType().Name);
            Assert.AreEqual(1, context.GetService<IEnumerable<ISchemaContributor>>().Count());
            StringAssert.Contains(Script(context), $"ADD CONSTRAINT {Models.Name} EXCLUDE");
        }
    }

    [TestMethod]
    public void A_caller_owned_provider_takes_one_call_for_both_packages()
    {
        var provider = new ServiceCollection().AddEntityFrameworkNpgsql().AddEntityFrameworkSchemaKitNpgsql().BuildServiceProvider();
        using var context = new Db<Models.Scheduling>(new DbContextOptionsBuilder<Db<Models.Scheduling>>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .UseInternalServiceProvider(provider)
            .UseNpgsql(Contexts.Offline, o => o.UseSchemaKit())
            .Options);

        Assert.AreEqual("NpgsqlSchemaDialect", context.GetService<ISchemaDialect>().GetType().Name);
        StringAssert.Contains(Script(context), $"ADD CONSTRAINT {Models.Name} EXCLUDE");
    }
}
