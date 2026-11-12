using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>
/// What the decorators do with operations: splice the plan around the inner differ's list without
/// touching it, answer HasDifferences from the same plan, and hand the inner generator one list.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OperationOrderTests
{
    private sealed class Empty(DbContextOptions<Empty> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private sealed class Declared(DbContextOptions<Declared> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>().DeclareTestObject("thing_name");
    }

    private static IRelationalModel Model(DbContext context) => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    private static DbContextOptions<TContext> Wired<TContext>(Provider provider) where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>().Use(provider).UseSchemaKit().Options;

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Creates_go_after_the_inner_operations(Provider provider)
    {
        using var context = new Declared(Wired<Declared>(provider));
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context));

        Assert.IsInstanceOfType<SqlOperation>(operations[^1]);
        StringAssert.Contains(((SqlOperation)operations[^1]).Sql, "CREATE INDEX");
        Assert.IsTrue(operations.Take(operations.Count - 1).OfType<CreateTableOperation>().Any(), "The table must be created before its index.");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Drops_go_before_the_inner_operations(Provider provider)
    {
        using var context = new Declared(Wired<Declared>(provider));
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(Model(context), null);

        Assert.IsInstanceOfType<SqlOperation>(operations[0]);
        StringAssert.Contains(((SqlOperation)operations[0]).Sql, "DROP INDEX");
        Assert.IsTrue(operations.Skip(1).OfType<DropTableOperation>().Any(), "The index must be dropped before its table.");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_inner_operations_are_passed_through_untouched(Provider provider)
    {
        using var context = new Declared(Wired<Declared>(provider));
        var differ = (SchemaKitModelDiffer)context.GetService<IMigrationsModelDiffer>();

        var inner = differ.Inner.GetDifferences(null, Model(context));
        var all   = differ.GetDifferences(null, Model(context));

        CollectionAssert.AreEqual(
            inner.Select(o => o.GetType()).ToList(),
            all.Take(inner.Count).Select(o => o.GetType()).ToList());
        Assert.AreEqual(inner.Count + 1, all.Count);
    }

    /// <summary>
    /// The change only SchemaKit can see: the tables are the same, a declaration was added. EF Core's
    /// own differ says nothing differs; the pending-model-changes check must not.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Has_differences_sees_a_change_only_schemakit_can_see(Provider provider)
    {
        using var before = new Empty(Wired<Empty>(provider));
        using var after  = new Declared(Wired<Declared>(provider));
        var differ = (SchemaKitModelDiffer)after.GetService<IMigrationsModelDiffer>();

        Assert.IsFalse(differ.Inner.HasDifferences(Model(before), Model(after)), "The premise: the inner differ sees no change.");
        Assert.IsTrue(differ.HasDifferences(Model(before), Model(after)));
        Assert.IsTrue(differ.HasDifferences(Model(after), Model(before)), "Removing the declaration is a change too.");
        Assert.IsFalse(differ.HasDifferences(Model(after), Model(after)));
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Without_a_planner_the_inner_list_is_returned_as_it_is(Provider provider)
    {
        // No test kit here: the context declares nothing, as an application with no
        // contributor has.
        var options = new DbContextOptionsBuilder<Empty>();
        options.UseProvider(provider);
        using var context = new Empty(options.UseSchemaKit().Options);
        var differ = (SchemaKitModelDiffer)context.GetService<IMigrationsModelDiffer>();

        var inner = differ.Inner.GetDifferences(null, Model(context));
        var all   = differ.GetDifferences(null, Model(context));

        Assert.AreEqual(inner.Count, all.Count);
        Assert.IsFalse(all.OfType<SqlOperation>().Any());
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_generator_rewrites_what_a_rewriter_owns_and_keeps_the_order(Provider provider)
    {
        using var context = new Empty(Wired<Empty>(provider));
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var legacy = new CreateIndexOperation { Name = "ix_legacy", Table = "Thing", Columns = ["__not_a_column__"] };
        legacy.AddAnnotation(TestKit.Legacy, true);
        var commands = generator.Generate(
        [
            new SqlOperation { Sql = "SELECT 1 /* first */" },
            legacy,
            new SqlOperation { Sql = "SELECT 1 /* last */" },
        ]);

        var text = string.Join("\n", commands.Select(c => c.CommandText));
        var first = text.IndexOf("/* first */", StringComparison.Ordinal);
        var mid   = text.IndexOf("/* legacy:ix_legacy */", StringComparison.Ordinal);
        var last  = text.IndexOf("/* last */", StringComparison.Ordinal);

        Assert.IsTrue(first >= 0 && mid > first && last > mid, "Expected first, the rewritten operation, last, in that order:\n" + text);
        Assert.IsFalse(text.Contains("__not_a_column__"), "The legacy operation reached the provider's generator unrewritten.");
    }
}
