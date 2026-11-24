using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// A declaration with a lambda is held as it was written until its model is finalized, and is SQL
/// from then on (issue 114, slice 2). The rows of spike F, against the kernel, as a consumer
/// declares.
/// </summary>
[TestClass]
public sealed class UnboundDeclarationTests
{
    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    private static IModel Design<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static List<IAnnotation> Declared(IReadOnlyModel model)
        => [.. model.FindEntityType(typeof(RoleGrant))!.GetAnnotations().Where(a => a.Name.StartsWith("CodoMetis:Pg.Exclusion:", StringComparison.Ordinal))];

    private static string Refused<TModel>() where TModel : ITestModel
        => Assert.ThrowsExactly<InvalidOperationException>(() => Design<TModel>()).Message;

    // ---- what a model holds --------------------------------------------------------------------------

    private sealed class WithLambdas : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().DeclarePostgres(pg =>
        [
            pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null, name: "ex_named"),
            pg.Exclusion(pg.Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period), where: x => x.Note != "none"),
        ]);
    }

    /// <summary>
    /// The design-time model and the runtime model hold text alone, under keys of the stored form:
    /// nothing that was held for binding is left, with a name or without one.
    /// </summary>
    [TestMethod]
    public void A_finalized_model_holds_text_alone()
    {
        using var context = Contexts.Of<WithLambdas>();

        foreach (var model in new IReadOnlyModel[] { context.GetService<IDesignTimeModel>().Model, context.Model })
        {
            var declared = Declared(model);

            Assert.HasCount(2, declared);
            Assert.IsTrue(declared.All(a => a.Value is string), model.GetType().Name);
            Assert.IsTrue(declared.All(a => System.Text.RegularExpressions.Regex.IsMatch(a.Name, "^CodoMetis:Pg\\.Exclusion:[0-9a-f]{16}$")), string.Join(", ", declared.Select(a => a.Name)));
        }

        var unnamed = Declared(context.GetService<IDesignTimeModel>().Model).Select(a => (string)a.Value!).Single(v => !v.Contains("ex_named", StringComparison.Ordinal));
        Assert.AreEqual(
            """{"$kind":"gist","v":3,"r":1,"elements":[{"sql":["lower(",{"col":"Note"},")"],"op":"="},{"column":"Period","op":"&&"}],"where":["(",{"col":"Note"}," <> 'none' OR ",{"col":"Note"}," IS NULL)"],"deferral":"notDeferrable"}""",
            unnamed);
    }

    // ---- the key -------------------------------------------------------------------------------------

    private sealed class SaidTwiceInOneCall : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var none = "none";
            modelBuilder.Entity<RoleGrant>().DeclarePostgres(pg =>
            [
                pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.Note != "none"),
                pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.Note != none),
            ]);
        }
    }

    private sealed class SaidTwiceInTwoCalls : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.Note != "none")])
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.Note != "none")]);
    }

    /// <summary>
    /// A declaration without a name is refused where it is written when another says the same,
    /// before either is SQL: the lambdas are compared as trees, a captured value as its value.
    /// </summary>
    [TestMethod]
    public void What_is_said_twice_is_refused_at_the_call()
    {
        foreach (var thrown in new[]
                 {
                     Assert.ThrowsExactly<InvalidOperationException>(() => Design<SaidTwiceInOneCall>()),
                     Assert.ThrowsExactly<InvalidOperationException>(() => Design<SaidTwiceInTwoCalls>()),
                 })
        {
            StringAssert.Contains(thrown.Message, "x => (x.Note != \\\"none\\\")");
            StringAssert.Contains(thrown.StackTrace!, nameof(PostgresDeclarationExtensions.DeclarePostgres), "Thrown by the call, inside OnModelCreating.");
        }
    }

    private sealed class OneNameTwice : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null, name: "ex_one")])
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoleId).Overlaps(x => x.Period), where: x => x.Note != "none", name: "ex_one")]);
    }

    /// <summary>A name is a declaration's key whether it is bound or not, and is taken once.</summary>
    [TestMethod]
    public void A_name_taken_by_a_declaration_that_is_not_bound_is_taken()
    {
        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => Design<OneNameTwice>());

        StringAssert.Contains(thrown.Message, "'ex_one'");
        StringAssert.Contains(thrown.StackTrace!, nameof(PostgresDeclarationExtensions.DeclarePostgres));
    }

    private sealed class TwoLambdasOneSql : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null)])
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => !x.RevokedOn.HasValue)]);
    }

    /// <summary>
    /// Two lambdas that differ as trees pass the call. Bound, they are one condition, and one
    /// object declared twice: refused when the model is finalized, naming both in the order
    /// they were written. Stored under one key, the second would have replaced the first.
    /// </summary>
    [TestMethod]
    public void Two_lambdas_that_are_one_sql_are_refused_when_the_model_is_finalized()
    {
        var message = Refused<TwoLambdasOneSql>();

        StringAssert.Contains(message, "Two declarations on RoleGrant are one object");
        var first = message.IndexOf("x => (x.RevokedOn == null)", StringComparison.Ordinal);
        var second = message.IndexOf("x => Not(x.RevokedOn.HasValue)", StringComparison.Ordinal);
        Assert.IsTrue(first >= 0 && second > first, message);
    }

    private sealed class ALambdaAndItsSqlByHand : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period)) with { Where = new Predicate(SqlTemplate.FromPlaceholders("{RevokedOn} IS NULL")) }])
            .DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null)]);
    }

    /// <summary>The same against a declaration that was SQL from the start and is stored already.</summary>
    [TestMethod]
    public void A_lambda_that_binds_to_what_is_stored_already_is_refused_when_the_model_is_finalized()
        => StringAssert.Contains(Refused<ALambdaAndItsSqlByHand>(), "Two declarations on RoleGrant are one object");

    // ---- before the model is finalized ---------------------------------------------------------------

    private sealed class Amended : ITestModel
    {
        public static readonly List<PgExclusion> Before = [];
        public static int Changed;

        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().DeclarePostgres(pg =>
            [
                pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period), where: x => x.RevokedOn == null, name: "ex_named"),
                pg.Exclusion(pg.Equal(x => x.RoleId).Overlaps(x => x.Period), where: x => x.RevokedOn == null),
                pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period)),
            ]);

            // A convention of the consumer's: every constraint with a condition gets one more.
            var noted = PgSchema.Where<RoleGrant>(x => x.Note != null);
            var entityType = modelBuilder.Entity<RoleGrant>().Metadata;
            Before.Clear();
            Before.AddRange(entityType.GetDeclarations<PgExclusion>());
            Changed = entityType.Redeclare<PgExclusion>(d => d.Where is null ? d : d.AndWhere(noted));
            Changed += 10 * entityType.Redeclare<PgExclusion>(d => d.Where is null ? d : d.AndWhere(noted));
        }
    }

    /// <summary>
    /// Before the model is finalized the read model hands out the declarations as they were
    /// written, a lambda not yet SQL, and <c>Redeclare</c> amends them. What is bound is the
    /// amended declaration; amending a second time by the same rule changes nothing.
    /// </summary>
    [TestMethod]
    public void A_declaration_is_read_and_amended_before_it_is_bound()
    {
        var declared = Design<Amended>().FindEntityType(typeof(RoleGrant))!.GetDeclarations<PgExclusion>();

        Assert.HasCount(3, Amended.Before);
        Assert.AreEqual(2, Amended.Before.Count(d => d.Where is { Template.IsBound: false }));
        Assert.AreEqual(2, Amended.Changed, "Two were amended the first time, none the second.");

        CollectionAssert.AreEquivalent(
            new[] { "({RevokedOn} IS NULL) AND ({Note} IS NOT NULL)", "({RevokedOn} IS NULL) AND ({Note} IS NOT NULL)", "" },
            declared.Select(d => d.Where?.Template.ToString() ?? "").ToList());
        Assert.AreEqual("({RevokedOn} IS NULL) AND ({Note} IS NOT NULL)", declared.Single(d => d.Name == "ex_named").Where!.Template.ToString());
    }

    private sealed class CapturedLocal : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var note = "first";
            modelBuilder.Entity<RoleGrant>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), where: x => x.Note == note, name: "ex_captured")]);
            note = "second";
        }
    }

    /// <summary>A local the lambda reads is read at the call. Read when the model is finalized, the declaration would hold what the local became.</summary>
    [TestMethod]
    public void A_captured_local_is_read_where_the_declaration_is_written()
        => Assert.AreEqual(
            "{Note} = 'first'",
            Design<CapturedLocal>().FindEntityType(typeof(RoleGrant))!.FindDeclaration<PgExclusion>("ex_captured")!.Where!.Template.ToString());
}
