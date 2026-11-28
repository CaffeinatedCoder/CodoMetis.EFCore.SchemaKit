using CodoMetis.EFCore.SchemaKit.Migrations.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>A rewriter of another package's operation, written against the public contract: two statements for one index.</summary>
public sealed class ReplaysMarked : LegacyOperationRewriter
{
    public const string Mark = "Earlier:Marked";

    public override string Annotation => Mark;

    protected override IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation)
    {
        Assert.IsNotNull(operation.FindAnnotation(Mark), "A rewriter is asked for an operation that carries its annotation, and for no other.");
        var name = ((CreateIndexOperation)operation).Name;
        return [new SqlOperation { Sql = $"SELECT 'first of {name}'" }, new SqlOperation { Sql = $"SELECT 'second of {name}'" }];
    }
}

/// <summary>A second class for the same annotation.</summary>
public sealed class ReplaysMarkedToo : LegacyOperationRewriter
{
    public override string Annotation => ReplaysMarked.Mark;

    protected override IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation) => [new SqlOperation { Sql = "SELECT 'too'" }];
}

/// <summary>A rewriter of another annotation, which says which of the two it was.</summary>
public sealed class ReplaysOther : LegacyOperationRewriter
{
    public const string Mark = "Earlier:Other";

    public override string Annotation => Mark;

    protected override IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation) => [new SqlOperation { Sql = "SELECT 'other'" }];
}

/// <summary>What a rewriter must not answer, each in turn, by the value of the operation's annotation.</summary>
public sealed class AnswersWrongly : LegacyOperationRewriter
{
    public const string Mark = "Earlier:Wrong";

    public override string Annotation => Mark;

    protected override IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation) => operation[Mark] switch
    {
        "null"       => null!,
        "nothing"    => [],
        "a null"     => [new SqlOperation { Sql = "SELECT 1" }, null!],
        "itself"     => [operation],
        "another"    => [Marked(new DropIndexOperation { Name = "IX" })],
        "stripped"   => [new SqlOperation { Sql = "SELECT 'extra'" }, Stripped(operation)],
        _            => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static MigrationOperation Stripped(MigrationOperation operation)
    {
        operation.RemoveAnnotation(Mark);
        return operation;
    }

    private static MigrationOperation Marked(MigrationOperation operation)
    {
        operation.AddAnnotation(Mark, "itself");
        return operation;
    }
}

/// <summary>A rewriter whose statement runs outside the migration's transaction.</summary>
public sealed class ReplaysOutside : LegacyOperationRewriter
{
    public const string Mark = "Earlier:Outside";

    public override string Annotation => Mark;

    protected override IReadOnlyList<MigrationOperation> Rewrite(MigrationOperation operation) =>
    [
        new SqlOperation { Sql = "SELECT 'inside'" },
        new SqlOperation { Sql = "SELECT 'outside'", SuppressTransaction = true },
    ];
}

/// <summary>
/// The contract a package replays an earlier package's operations through (#28): an operation that
/// carries a rewriter's annotation is replaced by the rewriter's answer, in its place, and nothing
/// else of the migration is touched. The same on every provider: which statement an operation
/// becomes is the package's, how the list is rewritten is the kernel's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LegacyOperationRewriterTests
{
    private sealed class Plain(DbContextOptions<Plain> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private static Plain Context(Provider provider, Action<IServiceCollection> rewriters)
    {
        var builder = new DbContextOptionsBuilder<Plain>().UseProvider(provider);
        ServicesExtension.Add(builder, rewriters.Method.Name, rewriters);
        return new Plain(builder.UseSchemaKit().Options);
    }

    private static void One(IServiceCollection services) => services.AddLegacyOperationRewriter<ReplaysMarked>();

    private static void OneTwice(IServiceCollection services) => services.AddLegacyOperationRewriter<ReplaysMarked>().AddLegacyOperationRewriter<ReplaysMarked>();

    private static void TwoForOneAnnotation(IServiceCollection services) => services.AddLegacyOperationRewriter<ReplaysMarked>().AddLegacyOperationRewriter<ReplaysMarkedToo>();

    private static void Wrong(IServiceCollection services) => services.AddLegacyOperationRewriter<AnswersWrongly>();

    private static void Outside(IServiceCollection services) => services.AddLegacyOperationRewriter<ReplaysOutside>();

    private static CreateIndexOperation Index(string annotation, object? value = null, string name = "IX_Thing_Name")
    {
        var index = new CreateIndexOperation { Table = "Thing", Name = name, Columns = ["Name"] };
        index.AddAnnotation(annotation, value);
        return index;
    }

    private static List<string> Sql(DbContext context, params MigrationOperation[] operations)
        => [.. context.GetService<IMigrationsSqlGenerator>().Generate(operations).Select(c => c.CommandText.Trim())];

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_marked_operation_is_replaced_in_its_place(Provider provider)
    {
        using var context = Context(provider, One);

        var sql = Sql(context,
            new SqlOperation { Sql = "SELECT 'before'" },
            Index(ReplaysMarked.Mark),
            new SqlOperation { Sql = "SELECT 'between'" },
            Index(ReplaysMarked.Mark, name: "IX_Thing_Other"),
            new SqlOperation { Sql = "SELECT 'after'" });

        CollectionAssert.AreEqual(
            new[]
            {
                "SELECT 'before'", "SELECT 'first of IX_Thing_Name'", "SELECT 'second of IX_Thing_Name'", "SELECT 'between'",
                "SELECT 'first of IX_Thing_Other'", "SELECT 'second of IX_Thing_Other'", "SELECT 'after'",
            },
            sql);
    }

    /// <summary>The premise of the test above: without the rewriter the provider writes the index, annotation and all.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Without_the_rewriter_the_provider_writes_the_operation(Provider provider)
    {
        using var context = Context(provider, _ => { });

        StringAssert.StartsWith(Sql(context, Index(ReplaysMarked.Mark)).Single(), "CREATE INDEX");
    }

    [TestMethod]
    public void A_list_without_a_marked_operation_is_handed_on_as_it_came()
    {
        IReadOnlyList<MigrationOperation> operations = [new SqlOperation { Sql = "SELECT 1" }, Index("Another:Annotation")];

        Assert.AreSame(operations, new LegacyOperationRewriters([new ReplaysMarked()]).Rewrite(operations, model: null));
        Assert.AreSame(operations, new LegacyOperationRewriters([]).Rewrite(operations, model: null));
    }

    /// <summary>The migration's operations are compiled code that is generated from again and again.</summary>
    [TestMethod]
    public void The_operations_that_came_are_not_changed()
    {
        var marked = Index(ReplaysMarked.Mark);
        MigrationOperation[] operations = [new SqlOperation { Sql = "SELECT 1" }, marked];

        var rewritten = new LegacyOperationRewriters([new ReplaysMarked()]).Rewrite(operations, model: null);

        Assert.HasCount(3, rewritten);
        Assert.AreSame(operations[0], rewritten[0]);
        CollectionAssert.AreEqual(new MigrationOperation[] { operations[0], marked }, operations);
        Assert.IsNotNull(marked.FindAnnotation(ReplaysMarked.Mark));
    }

    [TestMethod]
    public void The_same_rewriter_added_twice_is_one()
    {
        using var context = Context(Provider.Npgsql, OneTwice);

        Assert.HasCount(2, Sql(context, Index(ReplaysMarked.Mark)));
    }

    /// <summary>
    /// Each of two would have a statement for the operation. Refused when a migration is generated,
    /// with or without such an operation in it: a rewriter says its annotation only once it is made.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Two_rewriters_for_one_annotation_are_refused(Provider provider)
    {
        using var context = Context(provider, TwoForOneAnnotation);

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Sql(context, new SqlOperation { Sql = "SELECT 1" }));

        StringAssert.Contains(refused.Message, "Two rewriters replay the annotation 'Earlier:Marked'");
        StringAssert.Contains(refused.Message, typeof(ReplaysMarked).FullName);
        StringAssert.Contains(refused.Message, typeof(ReplaysMarkedToo).FullName);
    }

    /// <summary>An operation stands for an object. Answered with nothing, the database does not get it, and nothing says so.</summary>
    [TestMethod, DataRow("null"), DataRow("nothing"), DataRow("a null")]
    public void An_answer_that_is_nothing_is_refused(string answer)
    {
        using var context = Context(Provider.Npgsql, Wrong);

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Sql(context, Index(AnswersWrongly.Mark, answer)));

        StringAssert.Contains(refused.Message, $"'{typeof(AnswersWrongly).FullName}'.Rewrite returned null, an empty list, or a list that holds a null");
    }

    /// <summary>Handed back with the annotation, by itself or on another operation, the provider would write it as if it were not there.</summary>
    [TestMethod, DataRow("itself"), DataRow("another")]
    public void An_answer_that_still_carries_the_annotation_is_refused(string answer)
    {
        using var context = Context(Provider.Npgsql, Wrong);

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Sql(context, Index(AnswersWrongly.Mark, answer)));

        StringAssert.Contains(refused.Message, "returned an operation that still carries the annotation 'Earlier:Wrong'");
    }

    /// <summary>
    /// The migration's operation is generated from again. A rewriter that took the annotation off
    /// it and handed it back beside a statement of its own was right once: generated a second time,
    /// the operation went to the provider alone and the statement was gone, without an error.
    /// </summary>
    [TestMethod]
    public void A_rewriter_that_takes_the_annotation_off_the_operation_is_refused()
    {
        using var context = Context(Provider.Npgsql, Wrong);

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Sql(context, Index(AnswersWrongly.Mark, "stripped")));

        StringAssert.Contains(refused.Message, $"'{typeof(AnswersWrongly).FullName}'.Rewrite took the annotation 'Earlier:Wrong' off the operation it was asked for");
    }

    /// <summary>Which rewriter answers an operation with two annotations is the operation's to say, not the order of two registrations.</summary>
    [TestMethod]
    public void The_first_annotation_the_operation_holds_decides()
    {
        var both = Index(ReplaysOther.Mark);
        both.AddAnnotation(ReplaysMarked.Mark, null);
        LegacyOperationRewriter[] oneWay = [new ReplaysMarked(), new ReplaysOther()];
        LegacyOperationRewriter[] theOther = [new ReplaysOther(), new ReplaysMarked()];

        var first = new LegacyOperationRewriters(oneWay).Rewrite([both], model: null);
        var second = new LegacyOperationRewriters(theOther).Rewrite([both], model: null);

        // An operation holds its annotations by name, and 'Earlier:Marked' is before 'Earlier:Other'.
        Assert.HasCount(2, first);
        CollectionAssert.AreEqual(first.Cast<SqlOperation>().Select(o => o.Sql).ToList(), second.Cast<SqlOperation>().Select(o => o.Sql).ToList());
    }

    /// <summary>
    /// A replayed statement that runs outside a transaction says what it is for, as SchemaKit's own
    /// does (#155), and one inside says nothing.
    /// </summary>
    [TestMethod]
    public void A_statement_outside_a_transaction_is_stamped_with_the_operation_it_replays()
    {
        var rewritten = new LegacyOperationRewriters([new ReplaysOutside()]).Rewrite([Index(ReplaysOutside.Mark)], model: null);

        Assert.IsNull(rewritten[0].FindAnnotation(StatementStamp.Annotation));
        Assert.AreEqual(
            "CreateIndexOperation 'IX_Thing_Name' on table 'Thing', which the migration holds with the annotation 'Earlier:Outside' of an earlier package",
            rewritten[1].FindAnnotation(StatementStamp.Annotation)?.Value);
    }

    /// <summary>
    /// So that it is refused where the way the migration is applied does not carry it. The kernel's
    /// own dialect, which is every provider's here, says that of an idempotent script (#214).
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_replayed_statement_outside_a_transaction_is_refused_where_nothing_carries_it(Provider provider)
    {
        using var context = Context(provider, Outside);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        Assert.HasCount(2, generator.Generate([Index(ReplaysOutside.Mark)]));
        var refused = Assert.ThrowsExactly<InvalidOperationException>(()
            => generator.Generate([Index(ReplaysOutside.Mark)], model: null, MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.Script));

        StringAssert.Contains(refused.Message, "The statement for CreateIndexOperation 'IX_Thing_Name' on table 'Thing', which the migration holds with the annotation 'Earlier:Outside'");
        StringAssert.Contains(refused.Message, "SELECT 'outside'");
    }
}
