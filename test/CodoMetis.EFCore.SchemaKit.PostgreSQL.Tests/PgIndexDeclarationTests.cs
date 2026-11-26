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
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false,"tablespace":"fast"}],"include":[]}""", "tablespace")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false}],"unique":{},"include":[]}""", "nullsDistinct")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false}],"include":[],"fillFactor":7}""", "from 10 to 100")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[{"column":"Note","desc":false}],"include":[],"pagesPerRange":8}""", "pagesPerRange")]
    [DataRow("""{"$kind":"gist","v":3,"r":1,"keys":[{"column":"Period"}],"include":[],"buffering":"maybe"}""", "'maybe'")]
    [DataRow("""{"$kind":"gin","v":3,"r":1,"keys":[{"column":"Note","desc":false}]}""", "desc")]
    [DataRow("""{"$kind":"hash","v":3,"r":1,"keys":[{"column":"Note"}]}""", "key")]
    [DataRow("""{"$kind":"btree","v":3,"r":1,"keys":[],"include":[]}""", "at least one key")]
    public void A_stored_form_this_version_does_not_know_is_refused(string value, string expected)
    {
        var refusal = Assert.Throws<Exception>(() => ReadBack(value));
        StringAssert.Contains(refusal.Message, expected);
    }

    // ── The other access methods ──

    private static readonly PgSchema<Document> Docs = PgSchema.For<Document>();

    /// <summary>A key is the key of the method it is given to, and what it was given comes along.</summary>
    [TestMethod]
    public void A_key_becomes_the_key_of_its_method()
    {
        var title = new KeyTarget.Column("Title");

        Assert.AreEqual(new HashKey(title), Docs.Hash(x => x.Title).Key);
        Assert.AreEqual(new HashKey(title) { Collation = "C", OpClass = new HashOpClass("text_ops") }, Docs.Hash(Docs.Key(x => x.Title).Collate("C").Ops(new HashOpClass("text_ops"))).Key);
        Assert.AreEqual(new GinKey(new KeyTarget.Column("Payload")) { OpClass = GinOpClass.JsonbPathOps }, Docs.Gin(x => x.Payload, GinOpClass.JsonbPathOps).Keys[0]);
        Assert.AreEqual(new GinKey(new KeyTarget.Column("Tags")), Docs.Gin(x => x.Tags).Keys[0]);
        Assert.AreEqual(new GistKey(new KeyTarget.Column("Period")) { OpClass = GistOpClass.RangeOps }, Docs.Gist([Docs.Key(x => x.Period).Ops(GistOpClass.RangeOps)]).Keys[0]);
        Assert.AreEqual(new SpGistKey(title) { Collation = "C", OpClass = SpGistOpClass.TextOps }, Docs.SpGist(Docs.Key(x => x.Title).Collate("C").Ops(SpGistOpClass.TextOps)).Key);
        Assert.AreEqual(new BrinKey(new KeyTarget.Column("Rank")) { OpClass = new BrinOpClass("int4_bloom_ops") }, Docs.Brin([Docs.Key(x => x.Rank).Ops(new BrinOpClass("int4_bloom_ops"))]).Keys[0]);
        Assert.AreEqual(
            new BTreeKey(title) { Collation = "C", OpClass = BTreeOpClass.TextPatternOps, Descending = true },
            Docs.BTree([Docs.Key(x => x.Title).Collate("C").Ops(BTreeOpClass.TextPatternOps).Desc()]).Keys[0]);
        Assert.AreEqual(new BTreeKey(title) { Collation = "C", Nulls = NullOrder.Last }, Docs.BTree([Docs.Key(x => x.Title).Collate("C").NullsLast()]).Keys[0]);
        Assert.AreEqual(new GistKey(title) { Collation = "C" }, Docs.Gist([Docs.Key(x => x.Title).Collate("C")]).Keys[0]);
        Assert.AreEqual(new GinKey(title) { Collation = "C" }, Docs.Gin([Docs.Key(x => x.Title).Collate("C")]).Keys[0]);
        Assert.AreEqual(new BrinKey(title) { Collation = "C" }, Docs.Brin([Docs.Key(x => x.Title).Collate("C")]).Keys[0]);
        Assert.AreEqual(new SpGistKey(title) { Collation = "C" }, Docs.SpGist(Docs.Key(x => x.Title).Collate("C")).Key);
    }

    [TestMethod]
    public void Each_method_has_its_kind_and_its_options()
    {
        Assert.AreEqual("hash", Docs.Hash(x => x.Title).Kind);
        Assert.AreEqual("gist", Docs.Gist(x => x.Period).Kind);
        Assert.AreEqual("spgist", Docs.SpGist(x => x.Title).Kind);
        Assert.AreEqual("gin", Docs.Gin(x => x.Tags).Kind);
        Assert.AreEqual("brin", Docs.Brin(x => x.Rank).Kind);

        Assert.AreEqual(80, Docs.Hash(x => x.Title, fillFactor: 80).FillFactor);
        Assert.AreEqual((90, GistBuffering.Off), (Docs.Gist(x => x.Period, fillFactor: 90, buffering: GistBuffering.Off) is var gist ? (gist.FillFactor, gist.Buffering) : default));
        Assert.AreEqual(70, Docs.SpGist(x => x.Title, fillFactor: 70).FillFactor);
        Assert.AreEqual((false, 128), (Docs.Gin(x => x.Tags, fastUpdate: false, pendingListLimit: 128) is var gin ? (gin.FastUpdate, gin.PendingListLimit) : default));
        Assert.AreEqual((16, true), (Docs.Brin(x => x.Rank, pagesPerRange: 16, autosummarize: true) is var brin ? (brin.PagesPerRange, brin.Autosummarize) : default));
        Assert.AreEqual((70, false), (Docs.BTree(x => x.Title, fillFactor: 70, deduplicateItems: false) is var btree ? (btree.FillFactor, btree.DeduplicateItems) : default));
        Assert.AreEqual(60, Docs.Unique(x => x.Title, fillFactor: 60).FillFactor);

        CollectionAssert.AreEqual(new[] { "Title" }, Docs.Gist(x => x.Period, include: [x => x.Title]).Include.ToList());
        CollectionAssert.AreEqual(new[] { "Rank" }, Docs.SpGist(x => x.Title, include: [x => x.Rank]).Include.ToList());
        Assert.AreEqual("{Rank} > 0", Rebound(Docs.Hash(x => x.Title, where: x => x.Rank > 0, name: "ix_bound")).Where!.Template.ToString());
    }

    private sealed class OneIndex : ITestModel
    {
        public static PgIndex? Index;

        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Document>().DeclarePostgres(_ => [Index!]);
    }

    private static PgIndex Rebound(PgIndex index)
    {
        OneIndex.Index = index;
        using var context = Contexts.Of<OneIndex>();
        return context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Document))!.GetDeclarations<PgIndex>().Single();
    }

    /// <summary>
    /// The bounds are PostgreSQL's, and a value outside them fails where it is written, through
    /// the factory and around it. <c>PgIndexLiveTests</c> holds each bound against the server.
    /// </summary>
    [TestMethod]
    public void A_storage_parameter_outside_PostgreSQLs_bounds_is_refused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.BTree(x => x.Title, fillFactor: 9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.BTree(x => x.Title, fillFactor: 101));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Unique(x => x.Title, fillFactor: 9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Hash(x => x.Title, fillFactor: 9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Hash(x => x.Title, fillFactor: 101));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Gist(x => x.Period, fillFactor: 9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Gist(x => x.Period, fillFactor: 101));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.SpGist(x => x.Title, fillFactor: 9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.SpGist(x => x.Title, fillFactor: 101));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Gin(x => x.Tags, pendingListLimit: 63));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Brin(x => x.Rank, pagesPerRange: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Brin(x => x.Rank, pagesPerRange: 131073));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Docs.Hash(x => x.Title) with { FillFactor = 9 });

        Assert.AreEqual(10, Docs.BTree(x => x.Title, fillFactor: 10).FillFactor);
        Assert.AreEqual(10, Docs.Hash(x => x.Title, fillFactor: 10).FillFactor);
        Assert.AreEqual(64, Docs.Gin(x => x.Tags, pendingListLimit: 64).PendingListLimit);
        Assert.AreEqual(131072, Docs.Brin(x => x.Rank, pagesPerRange: 131072).PagesPerRange);
    }

    [TestMethod]
    public void No_way_around_the_factory_builds_a_key_without_what_it_needs()
    {
        var title = new KeyTarget.Column("Title");

        Assert.ThrowsExactly<ArgumentNullException>(() => new PgIndex.Hash(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgIndex.SpGist(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new PgIndex.Gist([]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgIndex.Gin([]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgIndex.Brin([]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new HashKey(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new GistKey(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new SpGistKey(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new GinKey(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new BrinKey(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new GinKey(title) { Collation = " " });
        Assert.ThrowsExactly<ArgumentException>(() => new BTreeKey(title) { Collation = "" });
        Assert.ThrowsExactly<ArgumentException>(() => Docs.Key(x => x.Title).Collate(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new GinOpClass(" "));
        Assert.ThrowsExactly<ArgumentException>(() => GinOpClass.JsonbOps with { Name = "" });
        Assert.ThrowsExactly<ArgumentException>(() => new BTreeOpClass(""));
        Assert.ThrowsExactly<ArgumentException>(() => new HashOpClass(""));
        Assert.ThrowsExactly<ArgumentException>(() => new GistOpClass(""));
        Assert.ThrowsExactly<ArgumentException>(() => new SpGistOpClass(""));
        Assert.ThrowsExactly<ArgumentException>(() => new BrinOpClass(""));
        Assert.ThrowsExactly<ArgumentNullException>(() => Docs.Key(x => x.Title).Ops((GinOpClass)null!));
    }

    private static string StoredValue(string name)
    {
        using var context = Contexts.Of<IndexModels.EveryMethod>();
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Document))!;
        return (string)entity.GetAnnotations()
            .Single(a => a.Name.StartsWith("CodoMetis:Pg.Index:", StringComparison.Ordinal) && ((string)a.Value!).Contains($"\"name\":\"{name}\"", StringComparison.Ordinal))
            .Value!;
    }

    /// <summary>Each method's stored form, to the character: what a snapshot holds of it.</summary>
    [TestMethod]
    [DataRow("ix_doc_title_hash", """{"$kind":"hash","v":3,"r":1,"name":"ix_doc_title_hash","key":{"column":"Title"},"fillFactor":80}""")]
    [DataRow("ix_doc_payload", """{"$kind":"gin","v":3,"r":1,"name":"ix_doc_payload","keys":[{"column":"Payload","ops":"jsonb_path_ops"}],"fastUpdate":false,"pendingListLimit":128}""")]
    [DataRow("ix_doc_tags", """{"$kind":"gin","v":3,"r":1,"name":"ix_doc_tags","keys":[{"column":"Tags"}]}""")]
    [DataRow("ix_doc_rank_period", """{"$kind":"gist","v":3,"r":1,"name":"ix_doc_rank_period","keys":[{"column":"Rank"},{"column":"Period"}],"include":["Title"],"fillFactor":90,"buffering":"off"}""")]
    [DataRow("ix_doc_title_sp", """{"$kind":"spgist","v":3,"r":1,"name":"ix_doc_title_sp","key":{"column":"Title","ops":"text_ops"},"include":["Rank"],"fillFactor":70}""")]
    [DataRow("ix_doc_rank_brin", """{"$kind":"brin","v":3,"r":1,"name":"ix_doc_rank_brin","keys":[{"column":"Rank"}],"pagesPerRange":16,"autosummarize":true,"where":[{"col":"Rank"}," > 0"]}""")]
    [DataRow("ix_doc_title", """{"$kind":"btree","v":3,"r":1,"name":"ix_doc_title","keys":[{"sql":["lower(",{"col":"Title"},")"],"desc":true,"collation":"C","ops":"text_pattern_ops"}],"include":[],"fillFactor":70,"deduplicateItems":false}""")]
    public void Each_method_is_stored_as_this(string name, string expected) => Assert.AreEqual(expected, StoredValue(name));

    /// <summary>Every case and every option of it comes back from the read model as the value that was declared.</summary>
    [TestMethod]
    public void The_read_model_returns_every_method_as_it_was_declared()
    {
        using var context = Contexts.Of<IndexModels.EveryMethod>();
        var read = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Document))!.GetDeclarations<PgIndex>();

        foreach (var declared in IndexModels.EveryMethod.Declared)
        {
            var found = read.Single(index => index.Name == declared.Name);
            Assert.AreEqual(declared.Kind, found.Kind);

            // What was a lambda is SQL by now; everything else is the value as it was.
            if (declared.Name is "ix_doc_rank_brin" or "ix_doc_title") continue;
            Assert.AreEqual(declared, found, declared.Name);
        }

        var brin = (PgIndex.Brin)read.Single(index => index.Name == "ix_doc_rank_brin");
        Assert.AreEqual((PgIndex.Brin)IndexModels.EveryMethod.Declared.Single(index => index.Name == "ix_doc_rank_brin") with { Where = new Predicate(SqlTemplate.FromPlaceholders("{Rank} > 0")) }, brin);

        var btree = (PgIndex.BTree)read.Single(index => index.Name == "ix_doc_title");
        Assert.AreEqual(
            new BTreeKey(new KeyTarget.Template(SqlTemplate.FromPlaceholders("lower({Title})"))) { Descending = true, Collation = "C", OpClass = BTreeOpClass.TextPatternOps },
            btree.Keys.Single());
        Assert.AreEqual((70, false), (btree.FillFactor, btree.DeduplicateItems));
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
