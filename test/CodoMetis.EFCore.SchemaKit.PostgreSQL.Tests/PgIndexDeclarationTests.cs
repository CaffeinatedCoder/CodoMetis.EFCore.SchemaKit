using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// The index as a value: what the factory builds from lambdas, what it refuses, and what is stored
/// of it. The stored form is in consumers' snapshots, so it is held here to the character.
/// </summary>
[TestClass]
public sealed class PgIndexDeclarationTests
{
    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    private static BTreeKey Column(string path) => new(new KeyTarget.Column(path));

    [TestMethod]
    public void One_lambda_is_one_ascending_key()
    {
        var index = Pg.BTree(x => x.GranteeId);

        CollectionAssert.AreEqual(new[] { Column("GranteeId") }, index.Keys.ToList());
        Assert.IsNull(index.Name);
        Assert.IsNull(index.Where);
        Assert.IsNull(index.Unique);
        Assert.AreEqual(0, index.Include.Count);
        Assert.AreEqual("btree", index.Kind);
    }

    [TestMethod]
    public void A_list_keeps_the_keys_in_their_order_with_the_order_of_each()
    {
        var index = Pg.BTree([Pg.Key(x => x.RoleId), Pg.Key(x => x.GranteeId).Desc(), Pg.Key(x => x.RevokedOn).NullsFirst(), Pg.Key(x => x.Note).Desc().NullsLast()]);

        CollectionAssert.AreEqual(
            new[]
            {
                Column("RoleId"),
                Column("GranteeId") with { Descending = true },
                Column("RevokedOn") with { Nulls = NullOrder.First },
                Column("Note") with { Descending = true, Nulls = NullOrder.Last },
            },
            index.Keys.ToList());
    }

    [TestMethod]
    public void A_lambda_that_is_more_than_a_property_is_an_expression()
    {
        // An expression, and not SQL yet: it is bound when its model is finalized.
        var expression = (KeyTarget.Template)IndexModels.ByNote.Keys[0].Target;

        Assert.IsFalse(expression.Sql.IsBound);
        Assert.AreEqual("x => x.Note.ToLower()", expression.Sql.ToString());
        Assert.AreEqual(Column("RevokedOn") with { Descending = true, Nulls = NullOrder.Last }, IndexModels.ByNote.Keys[1]);
    }

    [TestMethod]
    public void A_unique_index_says_how_it_counts_nulls_and_no_other_index_can()
    {
        Assert.AreEqual(new Uniqueness(NullsDistinct: true), Pg.Unique(x => x.GranteeId).Unique);
        Assert.AreEqual(new Uniqueness(NullsDistinct: false), IndexModels.Active.Unique);
        Assert.AreEqual(new Uniqueness(NullsDistinct: false), Pg.Unique(x => x.GranteeId, nullsDistinct: false).Unique);
        Assert.IsNull(Pg.BTree(x => x.GranteeId).Unique);
    }

    /// <summary>
    /// A lambda over <c>object</c> boxes a value type, which is no conversion of the column. More
    /// than a property is refused: PostgreSQL carries columns, and an expression there is a syntax error.
    /// </summary>
    [TestMethod]
    public void An_included_column_is_a_property_and_nothing_more()
    {
        var index = Pg.BTree(x => x.RoleId, include: [x => x.Note, x => x.GranteeId, x => x.RevokedOn]);

        CollectionAssert.AreEqual(new[] { "Note", "GranteeId", "RevokedOn" }, index.Include.ToList());

        var refusal = Assert.ThrowsExactly<ArgumentException>(() => Pg.BTree(x => x.RoleId, include: [x => x.Note!.ToLower()]));
        StringAssert.Contains(refusal.Message, "x => x.Note.ToLower()");
    }

    /// <summary>The translator's refusal reaches the declaration: nothing is stored that no SQL was written for.</summary>
    [TestMethod]
    public void A_lambda_the_translator_has_no_sql_for_fails_at_the_declaration()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Pg.BTree(x => x.Note!.Normalize()));
        Assert.ThrowsExactly<NotSupportedException>(() => Pg.BTree(x => x.RoleId, where: x => x.Note!.Normalize() == "a"));
        Assert.ThrowsExactly<NotSupportedException>(() => Pg.Key(x => (short)x.GranteeId));
    }

    /// <summary>What goes around the factory meets its rules at run time: <c>with</c>, a constructor call.</summary>
    [TestMethod]
    public void No_way_around_the_factory_builds_an_index_PostgreSQL_has_no_statement_for()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PgIndex.BTree([]));
        Assert.ThrowsExactly<ArgumentException>(() => IndexModels.Active with { Keys = [] });
        Assert.ThrowsExactly<ArgumentException>(() => new PgIndex.BTree([null!]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgIndex.BTree(null!));
        Assert.ThrowsExactly<ArgumentException>(() => IndexModels.Active with { Include = [" "] });
        Assert.ThrowsExactly<ArgumentNullException>(() => new BTreeKey(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Column("Note") with { Nulls = (NullOrder)2 });
    }

    [TestMethod]
    public void A_declaration_compares_by_value()
    {
        var again = Pg.Unique(
            [Pg.Key(x => x.GranteeId), Pg.Key(x => x.RoleId)],
            where: x => x.RevokedOn == null, include: [x => x.Note], nullsDistinct: false, name: IndexModels.ActiveName);

        Assert.AreEqual(IndexModels.Active, again);
        Assert.AreNotEqual(IndexModels.Active, again with { Unique = new Uniqueness() });
        Assert.AreNotEqual(IndexModels.Active, again with { Include = [] });
        Assert.AreNotEqual(IndexModels.Active, again with { Keys = [again.Keys[0].Desc(), again.Keys[1]] });
    }

    /// <summary>5.x's rule for a convention that may run twice.</summary>
    [TestMethod]
    public void A_condition_is_added_once()
    {
        var noted = new Predicate(SqlTemplate.FromPlaceholders("{Note} IS NOT NULL"));
        var unfiltered = IndexModels.Active with { Where = null };

        Assert.AreEqual(noted, unfiltered.AndWhere(noted).Where);
        Assert.AreEqual(unfiltered.AndWhere(noted), unfiltered.AndWhere(noted).AndWhere(noted));

        var both = IndexModels.Active.AndWhere(noted);
        Assert.AreEqual("(x => (x.RevokedOn == null)) AND ({Note} IS NOT NULL)", both.Where!.Template.ToString());
        Assert.AreEqual(both, both.AndWhere(noted));
        // The case is kept: what comes back is the index it was, with its keys and its uniqueness.
        Assert.AreEqual(IndexModels.Active.Unique, ((PgIndex.BTree)both).Unique);
    }

    // ── What is stored ──

    private static IEntityType Entity<TModel>() where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(RoleGrant))!;
    }

    private static List<IAnnotation> Stored(IEntityType entity)
        => [.. entity.GetAnnotations().Where(a => a.Name.StartsWith("CodoMetis:Pg.Index:", StringComparison.Ordinal))];

    /// <summary>
    /// The key and the value as they are written into a snapshot. A change that moves either is a
    /// change of the format: every consumer's next diff would see another declaration.
    /// </summary>
    [TestMethod]
    public void A_named_unique_index_is_stored_as_this()
    {
        var annotation = Stored(Entity<IndexModels.WithActive>()).Single();

        Assert.AreEqual("CodoMetis:Pg.Index:58046382c460ad72", annotation.Name);
        Assert.AreEqual(
            """{"$kind":"btree","v":3,"r":1,"name":"ux_role_grant_active","keys":[{"column":"GranteeId","desc":false},{"column":"RoleId","desc":false}],"unique":{"nullsDistinct":false},"include":["Note"],"where":[{"col":"RevokedOn"}," IS NULL"]}""",
            annotation.Value);
    }

    [TestMethod]
    public void An_unnamed_index_with_an_expression_is_stored_as_this()
    {
        var annotation = Stored(Entity<IndexModels.Unnamed>()).Single();

        Assert.AreEqual("CodoMetis:Pg.Index:cfda91df53b86f5e", annotation.Name);
        Assert.AreEqual(
            """{"$kind":"btree","v":3,"r":1,"keys":[{"column":"GranteeId","desc":false},{"sql":["lower(",{"col":"Note"},")"],"desc":false}],"include":[]}""",
            annotation.Value);
    }

    /// <summary>A key's order and where its nulls go, each as it is stored.</summary>
    [TestMethod]
    public void An_order_is_stored_as_this()
        => Assert.AreEqual(
            """{"$kind":"btree","v":3,"r":1,"name":"ix_role_grant_note","keys":[{"sql":["lower(",{"col":"Note"},")"],"desc":false},{"column":"RevokedOn","desc":true,"nulls":"last"}],"include":[]}""",
            Stored(Entity<IndexModels.WithByNote>()).Single().Value);

    private sealed class EveryCase : ITestModel
    {
        public static readonly PgIndex[] Declared =
        [
            IndexModels.Active,
            IndexModels.ByNote,
            Pg.BTree([Pg.Key(x => x.RoleId).NullsFirst()], include: [x => x.GranteeId]),
            new PgIndex.BTree([new BTreeKey(new KeyTarget.Template(SqlTemplate.Raw("""upper("Note")""")))])
            {
                Name = "ix_by_hand", Unique = new Uniqueness(), Where = new Predicate(SqlTemplate.FromPlaceholders("{Note} = 'it''s'")),
            },
        ];

        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().DeclarePostgres(_ => Declared);
    }

    /// <summary>
    /// Every option comes back from the read model as the value that was declared, with a lambda
    /// as the SQL it was bound to.
    /// </summary>
    [TestMethod]
    public void The_read_model_returns_the_values_that_were_declared()
        => CollectionAssert.AreEquivalent(
            new[]
            {
                EveryCase.Declared[0] with { Where = new Predicate(SqlTemplate.FromPlaceholders("{RevokedOn} IS NULL")) },
                (PgIndex.BTree)EveryCase.Declared[1] with
                {
                    Keys = [new BTreeKey(new KeyTarget.Template(SqlTemplate.FromPlaceholders("lower({Note})"))), IndexModels.ByNote.Keys[1]],
                },
                EveryCase.Declared[2],
                EveryCase.Declared[3],
            },
            Entity<EveryCase>().GetDeclarations<PgIndex>().ToList());

    private static PgIndex ReadBack(string value)
    {
        var entity = (IMutableEntityType)new ModelBuilder().Entity<RoleGrant>().Metadata;
        entity.SetAnnotation("CodoMetis:Pg.Index:0000000000000000", value);
        return entity.GetDeclarations<PgIndex>().Single();
    }

    /// <summary>
    /// What a newer version wrote and this one does not know is refused, never read as something
    /// else: a declaration that comes out of a snapshot as another one is a migration nobody asked for.
    /// </summary>
    [TestMethod]
    [DataRow("""{"$kind":"bloom","v":3,"r":1,"keys":[{"column":"Note","desc":false}],"include":[]}""", "kind 'bloom'")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note"}],"include":[]}""", "desc")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false}]}""", "include")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false,"nulls":"middle"}],"include":[]}""", "'middle'")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"desc":false}],"include":[]}""", "exactly one")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","sql":[{"col":"Note"}],"desc":false}],"include":[]}""", "exactly one")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false,"ops":"text_pattern_ops"}],"include":[]}""", "ops")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false}],"unique":{},"include":[]}""", "nullsDistinct")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false}],"include":[],"fillFactor":70}""", "fillFactor")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[],"include":[]}""", "at least one key")]
    public void A_stored_form_this_version_does_not_know_is_refused(string value, string expected)
    {
        var refusal = Assert.Throws<Exception>(() => ReadBack(value));
        StringAssert.Contains(refusal.Message, expected);
    }

    private sealed class Twice : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(_ => [IndexModels.Active])
            .DeclarePostgres(_ => [IndexModels.Active with { Where = null }]);
    }

    private sealed class TheSameTwice : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>()
            .DeclarePostgres(pg => [pg.BTree(x => x.GranteeId), pg.BTree(x => x.GranteeId)]);
    }

    /// <summary>The kernel's rule holds through <c>DeclarePostgres</c>: a second declaration under a name is no update, and neither is the same one again.</summary>
    [TestMethod]
    public void An_index_declared_twice_is_refused()
    {
        using var named = Contexts.Of<Twice>();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = named.Model);

        using var unnamed = Contexts.Of<TheSameTwice>();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = unnamed.Model);
    }

    /// <summary>An index and an exclusion constraint are two owners: one list declares both, and each is read back by its type.</summary>
    [TestMethod]
    public void Indexes_and_exclusion_constraints_are_declared_in_one_list()
    {
        var entity = Entity<Mixed>();

        Assert.AreEqual(1, entity.GetDeclarations<PgIndex>().Count);
        Assert.AreEqual(1, entity.GetDeclarations<PgExclusion>().Count);
    }

    /// <summary>Nothing is lowered into EF Core's own index metadata: the index is SchemaKit's from the model to the statement.</summary>
    [TestMethod]
    public void EF_Cores_index_metadata_does_not_hold_a_declared_index()
    {
        var entity = Entity<IndexModels.WithBoth>();

        Assert.AreEqual(0, entity.GetIndexes().Count());
        Assert.AreEqual(2, entity.GetDeclarations<PgIndex>().Count);
    }

    private sealed class Mixed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().DeclarePostgres(_ => [IndexModels.Active, Models.ActivePeriod]);
    }
}
