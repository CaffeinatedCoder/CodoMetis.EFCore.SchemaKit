using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// The declaration as a value: what the factory builds from lambdas, what it refuses, and what is
/// stored of it. The stored form is in consumers' snapshots, so it is held here to the character.
/// </summary>
[TestClass]
public sealed class PgExclusionDeclarationTests
{
    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    private static ExclusionElement Column(string path, ExclusionOperator op) => new(new KeyTarget.Column(path), op);

    [TestMethod]
    public void The_chain_keeps_the_elements_in_the_order_they_are_written()
    {
        var constraint = Pg.Exclusion(Pg.Equal(x => x.GranteeId).Overlaps(x => x.Period).Equal(x => x.RoleId));

        CollectionAssert.AreEqual(
            new[] { Column("GranteeId", ExclusionOperator.Equal), Column("Period", ExclusionOperator.Overlaps), Column("RoleId", ExclusionOperator.Equal) },
            constraint.Elements.ToList());
        Assert.IsNull(constraint.Name);
        Assert.IsNull(constraint.Where);
        Assert.AreEqual(Deferral.NotDeferrable, constraint.Deferral);
    }

    [TestMethod]
    public void A_lambda_that_is_more_than_a_property_is_an_expression()
    {
        var constraint = Pg.Exclusion(Pg.Equal(x => x.Note!.ToLower()).Overlaps(x => x.Period));

        Assert.AreEqual(new KeyTarget.Template("lower({Note})"), constraint.Elements[0].Target);
        Assert.AreEqual(new KeyTarget.Column("Period"), constraint.Elements[1].Target);
    }

    [TestMethod]
    public void The_filter_is_translated_where_it_is_written()
        => Assert.AreEqual("{RevokedOn} IS NULL", Models.ActivePeriod.Where!.Template);

    /// <summary>The translator's refusal reaches the declaration: nothing is stored that no SQL was written for.</summary>
    [TestMethod]
    public void A_lambda_the_translator_has_no_sql_for_fails_at_the_declaration()
        => Assert.ThrowsExactly<NotSupportedException>(() => Pg.Exclusion(Pg.Overlaps(x => x.Period), where: x => x.Note!.Normalize() == "a"));

    /// <summary>
    /// Issue 103: a cast that can change the value was left out, and the constraint was created
    /// over other rows than the lambda names. In an element as in the condition.
    /// </summary>
    [TestMethod]
    public void A_cast_that_can_change_the_value_fails_at_the_declaration()
    {
        var message = Assert.ThrowsExactly<NotSupportedException>(
            () => Pg.Exclusion(Pg.Overlaps(x => x.Period), where: x => (short)x.GranteeId == 2)).Message;

        StringAssert.Contains(message, "converts Int32 to Int16.");
        Assert.ThrowsExactly<NotSupportedException>(() => Pg.Exclusion(Pg.Overlaps(x => x.Period).Equal(x => (short)x.GranteeId)));
    }

    [TestMethod]
    public void An_unchecked_operator_is_any_operator_but_equality()
    {
        var adjacent = Pg.Exclusion(Pg.Equal(x => x.GranteeId).WithUncheckedOperator(x => x.Period, "-|-"));

        Assert.AreEqual(Column("Period", new ExclusionOperator("-|-")), adjacent.Elements[1]);
        // With "=" the draft of equality elements would have become a constraint's elements.
        Assert.ThrowsExactly<ArgumentException>(() => Pg.Equal(x => x.GranteeId).WithUncheckedOperator(x => x.RoleId, "="));
        Assert.ThrowsExactly<ArgumentException>(() => Pg.Equal(x => x.GranteeId).WithUncheckedOperator(x => x.RoleId, " "));
        Assert.ThrowsExactly<ArgumentException>(() => ExclusionOperator.Overlaps with { Token = "" });
    }

    /// <summary>
    /// The stage types keep the factory from building a constraint of equality elements only. What
    /// goes around the factory meets the same rule at run time.
    /// </summary>
    [TestMethod]
    public void No_way_around_the_factory_builds_a_constraint_of_equality_elements_only()
    {
        var equality = Column("GranteeId", ExclusionOperator.Equal);

        Assert.ThrowsExactly<ArgumentException>(() => new PgExclusion.Gist([equality]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgExclusion.Gist([]));
        Assert.ThrowsExactly<ArgumentException>(() => Models.ActivePeriod with { Elements = [equality] });
        Assert.ThrowsExactly<ArgumentException>(() => new PgExclusion.SpGist(equality));
        Assert.ThrowsExactly<ArgumentException>(() => new PgExclusion.SpGist(Column("Period", ExclusionOperator.Overlaps)) with { Element = equality });
    }

    [TestMethod]
    public void A_declaration_compares_by_value()
    {
        var again = Pg.Exclusion(
            Pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
            where: x => x.RevokedOn == null, deferral: Deferral.InitiallyDeferred, name: Models.Name);

        Assert.AreEqual(Models.ActivePeriod, again);
        Assert.AreNotEqual(Models.ActivePeriod, again with { Deferral = Deferral.InitiallyImmediate });
        Assert.AreNotEqual<PgExclusion>(new PgExclusion.Gist([Column("Period", ExclusionOperator.Overlaps)]), new PgExclusion.SpGist(Column("Period", ExclusionOperator.Overlaps)));
    }

    /// <summary>5.x's rule for a convention that may run twice.</summary>
    [TestMethod]
    public void A_condition_is_added_once()
    {
        var active = new Predicate("{Note} IS NOT NULL");
        var unfiltered = Models.ActivePeriod with { Where = null };

        Assert.AreEqual(active, unfiltered.AndWhere(active).Where);
        Assert.AreEqual(unfiltered.AndWhere(active), unfiltered.AndWhere(active).AndWhere(active));

        var both = Models.ActivePeriod.AndWhere(active);
        Assert.AreEqual("({RevokedOn} IS NULL) AND ({Note} IS NOT NULL)", both.Where!.Template);
        Assert.AreEqual(both, both.AndWhere(active));
    }

    // ── What is stored ──

    private static IEntityType Entity<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(RoleGrant))!;
    }

    private static List<IAnnotation> Stored(IEntityType entity)
        => [.. entity.GetAnnotations().Where(a => a.Name.StartsWith("CodoMetis:Pg.Exclusion:", StringComparison.Ordinal))];

    /// <summary>
    /// The key and the value as they are written into a snapshot. A change that moves either is a
    /// change of the format: every consumer's next diff would see another declaration.
    /// </summary>
    [TestMethod]
    public void A_named_gist_constraint_is_stored_as_this()
    {
        var annotation = Stored(Entity<Models.Scheduling>()).Single();

        Assert.AreEqual("CodoMetis:Pg.Exclusion:6c399f7765274bfa", annotation.Name);
        Assert.AreEqual(
            """{"$kind":"gist","v":2,"r":1,"name":"ex_role_grant_active_period","elements":[{"column":"GranteeId","op":"="},{"column":"RoleId","op":"="},{"column":"Period","op":"&&"}],"where":"{RevokedOn} IS NULL","deferral":"initiallyDeferred"}""",
            annotation.Value);
    }

    [TestMethod]
    public void An_unnamed_constraint_with_an_expression_is_stored_as_this()
    {
        var annotation = Stored(Entity<Models.Unnamed>()).Single();

        Assert.AreEqual("CodoMetis:Pg.Exclusion:b96083373ed84f8f", annotation.Name);
        Assert.AreEqual(
            """{"$kind":"gist","v":2,"r":1,"elements":[{"column":"GranteeId","op":"="},{"sql":"lower({Note})","op":"="},{"column":"Period","op":"&&"}],"deferral":"notDeferrable"}""",
            annotation.Value);
    }

    /// <summary>
    /// The condition is part of what an unnamed constraint is. Two that differ in it alone are two
    /// declarations under two keys; were it left out of the key, the second would be refused as
    /// the first declared again, or one would replace the other in a snapshot.
    /// </summary>
    [TestMethod]
    public void The_condition_is_in_an_unnamed_constraints_key()
    {
        var stored = Stored(Entity<Models.TwoUnnamedThatDifferInTheCondition>()).Select(a => a.Name).Order(StringComparer.Ordinal).ToList();

        CollectionAssert.AreEqual(new[] { "CodoMetis:Pg.Exclusion:a689767db5f37c5e", "CodoMetis:Pg.Exclusion:fe399bda900e9b95" }, stored);
    }

    private sealed class EveryCase : ITestModel
    {
        public static readonly PgExclusion[] Declared =
        [
            Models.ActivePeriod,
            Models.ActivePeriod with { Name = null, Deferral = Deferral.InitiallyImmediate, Where = null },
            new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Template("({Period})"), new ExclusionOperator("-|-"))) { Name = "ex_adjacent", Where = new Predicate("{Note} = 'it''s'") },
        ];

        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().DeclarePostgres(_ => Declared);
    }

    /// <summary>Every case and every option comes back from the read model as the value that was declared.</summary>
    [TestMethod]
    public void The_read_model_returns_the_values_that_were_declared()
        => CollectionAssert.AreEquivalent(EveryCase.Declared, Entity<EveryCase>().GetDeclarations<PgExclusion>().ToList());

    private static PgExclusion ReadBack(string value)
    {
        using var context = Contexts.Of<Models.Bare>();
        var entity = (IMutableEntityType)new ModelBuilder().Entity<RoleGrant>().Metadata;
        entity.SetAnnotation("CodoMetis:Pg.Exclusion:0000000000000000", value);
        return entity.GetDeclarations<PgExclusion>().Single();
    }

    /// <summary>
    /// What a newer version wrote and this one does not know is refused, never read as something
    /// else: a declaration that comes out of a snapshot as another one is a migration nobody asked for.
    /// </summary>
    [TestMethod]
    [DataRow("""{"$kind":"brin","v":2,"r":1,"elements":[{"column":"Period","op":"&&"}],"deferral":"notDeferrable"}""", "kind 'brin'")]
    [DataRow("""{"$kind":"gist","v":2,"r":1,"elements":[{"column":"Period","op":"&&"}],"deferral":"sometimes"}""", "'sometimes'")]
    [DataRow("""{"$kind":"gist","v":2,"r":1,"elements":[{"column":"Period","op":"&&"}]}""", "deferral")]
    [DataRow("""{"$kind":"gist","v":2,"r":1,"elements":[{"column":"Period","sql":"{Period}","op":"&&"}],"deferral":"notDeferrable"}""", "exactly one")]
    [DataRow("""{"$kind":"gist","v":2,"r":1,"elements":[{"op":"&&"}],"deferral":"notDeferrable"}""", "exactly one")]
    [DataRow("""{"$kind":"gist","v":2,"r":1,"elements":[{"column":"Period","op":"&&","ops":"range_ops"}],"deferral":"notDeferrable"}""", "ops")]
    public void A_stored_form_this_version_does_not_know_is_refused(string value, string expected)
    {
        var refusal = Assert.Throws<Exception>(() => ReadBack(value));
        StringAssert.Contains(refusal.Message, expected);
    }

    private sealed class Unwired(DbContextOptions<Unwired> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().DeclarePostgres(_ => [Models.ActivePeriod]);
    }

    /// <summary>The first of the three guards, through this package's entry point: no wiring, no declaration.</summary>
    [TestMethod]
    public void Without_UseSchemaKit_the_declaration_fails_where_it_is_written()
    {
        using var context = new Unwired(new DbContextOptionsBuilder<Unwired>().UseNpgsql(Contexts.Offline).Options);

        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);
        StringAssert.Contains(refusal.Message, "UseSchemaKit");
    }

    private sealed class Twice : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(_ => [Models.ActivePeriod])
            .DeclarePostgres(_ => [Models.ActivePeriod with { Where = null }]);
    }

    /// <summary>The kernel's rule holds through <c>DeclarePostgres</c>: a second declaration under a name is no update.</summary>
    [TestMethod]
    public void A_name_declared_twice_is_refused()
    {
        using var context = Contexts.Of<Twice>();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);
    }
}
