using CodoMetis.EFCore.SchemaKit.PostgreSQL.Legacy.Internal;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Legacy5x.Indexes;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// What 5.x could store of an index and the fixture does not hold (#218). Each annotation is
/// written as 5.x's serializer wrote it: <c>paths</c> and <c>unique</c> always, a null left out.
/// An index that is read shows as its drop against a model that declares nothing, under the name
/// it has in the database, or as no operations against the model that declares it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ComplexIndexes5xIndexReaderTests
{
    private const string Annotation = "CustomIndex:CompositeIndexes";

    private static void Source(ModelBuilder source, string json, int? version, Action<ModelBuilder>? more)
    {
        MovedIndexes.Tables(source).HasAnnotation(Annotation, json);
        if (version is { } stated) source.HasAnnotation("CustomIndex:RenderingVersion", stated);
        more?.Invoke(source);
    }

    /// <summary>The statements of the first migration after the move, with nothing declared here.</summary>
    private static List<string> Dropped(string json, int? version = 3, Action<ModelBuilder>? more = null)
        => LegacyFixture.DiffFrom<MovedIndexes.NothingDeclared>(source => Source(source, json, version, more)).Shape();

    private static string Refused(string json, int? version = 3, Action<ModelBuilder>? more = null)
        => Assert.ThrowsExactly<InvalidOperationException>(() => Dropped(json, version, more)).Message;

    private static List<string> Against<TModel>(string json) where TModel : ITestModel
        => LegacyFixture.DiffFrom<TModel>(source => Source(source, json, 3, null)).Shape();

    [TestMethod]
    [DataRow(""), DataRow("[]")]
    public void An_annotation_that_holds_nothing_holds_no_index(string json)
        => CollectionAssert.AreEqual(Array.Empty<string>(), Dropped(json));

    // ── The same index, however 5.x stored it ──

    private sealed class TitleAndNick : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => MovedIndexes.Tables(modelBuilder).DeclarePostgres(pg =>
        [
            pg.BTree(
                [pg.Key(x => x.Title).Collate("C"), pg.Key(x => x.Nick).Ops(BTreeOpClass.TextPatternOps)],
                include: [x => x.Contact.City],
                fillFactor: 70,
                deduplicateItems: true),
        ]);
    }

    /// <summary>
    /// A storage parameter as a number or as text, a flag as a flag or as a word; an included
    /// entry as a property path or as its column; a list of classes and collations with an empty
    /// entry where a key has none. 5.x took each of these, and each is the one index.
    /// </summary>
    [TestMethod]
    [DataRow("""{"Npgsql:IndexCollation":["C",""],"Npgsql:IndexOperators":["","text_pattern_ops"],"Npgsql:IndexInclude":["Contact.City"],"Npgsql:StorageParameter:fillfactor":70,"Npgsql:StorageParameter:deduplicate_items":true}""")]
    [DataRow("""{"Npgsql:IndexCollation":["C"],"Npgsql:IndexOperators":[null,"text_pattern_ops"],"Npgsql:IndexInclude":["Contact_City"],"Npgsql:StorageParameter:fillfactor":"70","Npgsql:StorageParameter:deduplicate_items":"on"}""")]
    public void Options_are_read_as_5x_took_them(string options)
        => CollectionAssert.AreEqual(
            Array.Empty<string>(),
            Against<TitleAndNick>($$"""[{"paths":["Title","Nick"],"unique":false,"props":{{options}}}]"""));

    private sealed class OnRank : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => MovedIndexes.Tables(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.Rank, name: "ix_both")]);
    }

    /// <summary>
    /// 5.x's own rule for an index that holds both, which its public <c>AddComplexIndex</c> could
    /// store: the parts are the index, and the paths are not looked at.
    /// </summary>
    [TestMethod]
    public void Parts_are_the_index_where_paths_are_there_too()
        => CollectionAssert.AreEqual(
            Array.Empty<string>(),
            Against<OnRank>("""[{"paths":["Kind"],"parts":[{"path":"Rank"}],"unique":false,"name":"ix_both"}]"""));

    private sealed class KindWithBraces : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => MovedIndexes.Tables(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.Kind + "{x}", name: "ix_braces")]);
    }

    /// <summary>
    /// 5.x wrote a brace of a string literal twice in a lambda's template, for a second grammar
    /// this package does not have. Read back it is the literal as the lambda had it.
    /// </summary>
    [TestMethod]
    public void A_doubled_brace_in_a_templates_literal_is_one()
    {
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            Against<KindWithBraces>("""[{"paths":[],"parts":[{"tmpl":"({Kind} || '{{x}}')"}],"unique":false,"name":"ix_braces"}]"""));

        Assert.AreEqual("({Kind} || '{x}')", ComplexIndexes5xIndexes.WithoutDoubledBraces("({Kind} || '{{x}}')"));
        Assert.AreEqual("'it''s {a}' || {Kind}", ComplexIndexes5xIndexes.WithoutDoubledBraces("'it''s {{a}}' || {Kind}"));
        Assert.AreEqual("{Kind} || {Title}", ComplexIndexes5xIndexes.WithoutDoubledBraces("{Kind} || {Title}"));
    }

    private sealed class CityByHash : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => MovedIndexes.Tables(modelBuilder).DeclarePostgres(pg => [pg.Hash(x => x.Contact.City, where: x => x.Rank > 0, fillFactor: 80, name: "ix_city_hash")]);
    }

    /// <summary>An index on a property: the marker, and beside it what describes the index, Npgsql's options under their own names.</summary>
    [TestMethod]
    public void An_index_on_a_property_is_read_with_the_annotations_beside_its_marker()
    {
        static void OnTheProperty(ModelBuilder source) => MovedIndexes.Tables(source).ComplexProperty(x => x.Contact, contact => contact.Property(x => x.City)
            .HasAnnotation("CustomIndex:IsIndexed", true)
            .HasAnnotation("CustomIndex:IsUnique", false)
            .HasAnnotation("CustomIndex:Filter", "{Rank} > 0")
            .HasAnnotation("CustomIndex:Name", "ix_city_hash")
            .HasAnnotation("Npgsql:IndexMethod", "hash")
            .HasAnnotation("Npgsql:StorageParameter:fillfactor", 80));

        CollectionAssert.AreEqual(Array.Empty<string>(), LegacyFixture.DiffFrom<CityByHash>(OnTheProperty).Shape());
        CollectionAssert.AreEqual(new[] { "DROP INDEX ix_city_hash;" }, LegacyFixture.DiffFrom<MovedIndexes.NothingDeclared>(OnTheProperty).Shape());

        // A marker that is not true declares nothing, as 5.x read it.
        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            LegacyFixture.DiffFrom<MovedIndexes.NothingDeclared>(source => MovedIndexes.Tables(source)
                .ComplexProperty(x => x.Contact, contact => contact.Property(x => x.City).HasAnnotation("CustomIndex:IsIndexed", false))).Shape());
    }

    // ── What a declaration cannot say ──

    private const string OnAMember = """[{"paths":["Prefs.Level"],"unique":false,"name":"ix_level"}]""";

    private const string AnExpressionWithoutAName = """[{"paths":[],"parts":[{"expr":"lower(\"Email\")"}],"unique":false}]""";

    /// <summary>
    /// A snapshot scaffolded before 5.4.0 has no rendering version. 5.x resolved it by its first
    /// rules: a member of a document as text, step by step, and an expression's default name from
    /// the SQL as it stood. A declaration here resolves to another expression and another name, so
    /// both are refused, and everything else of such a snapshot is read (decided with #27).
    /// </summary>
    [TestMethod]
    public void A_snapshot_from_before_5_4_is_read_but_for_members_and_unnamed_expressions()
    {
        var member = Refused(OnAMember, version: null);
        StringAssert.Contains(member, "the index 'ix_level' of Account");
        StringAssert.Contains(member, "reads 'Prefs.Level', a member of a JSON document, in a snapshot scaffolded before 5.4.0");
        StringAssert.Contains(member, "Move the project to EFCore.ComplexIndexes 5.6.0 first");

        StringAssert.Contains(
            Refused("""[{"paths":["Kind"],"unique":false,"filter":"{Prefs.Level} > 3","name":"ix_kind"}]""", version: null),
            "reads 'Prefs.Level', a member of a JSON document");
        StringAssert.Contains(Refused(AnExpressionWithoutAName, version: null), "is on an expression and has no name of its own, in a snapshot scaffolded before 5.4.0");

        // With the version, both are read.
        CollectionAssert.AreEqual(new[] { "DROP INDEX ix_level;" }, Dropped(OnAMember, version: 2));
        CollectionAssert.AreEqual(new[] { """DROP INDEX "IX_accounts_lowerEmail";""" }, Dropped(AnExpressionWithoutAName, version: 2));

        // And without it, a column and an expression under a name of its own.
        CollectionAssert.AreEqual(
            new[] { "DROP INDEX ix_e;", """DROP INDEX "IX_accounts_Kind_Rank";""" },
            Dropped("""[{"paths":["Kind","Rank"],"unique":false},{"paths":[],"parts":[{"expr":"lower(\"Email\")"}],"unique":false,"name":"ix_e"}]""", version: null));
    }

    /// <summary>Before 5.5.0 an index on a type of a TPC hierarchy was in that type's table alone.</summary>
    [TestMethod]
    public void An_index_on_a_TPC_type_with_derived_types_is_read_from_5_5_on()
    {
        const string plate = """[{"paths":["Plate"],"unique":true,"name":"ux_plate"}]""";

        List<string> Read(int version) => LegacyFixture.DiffFrom<MovedIndexes.NothingDeclared>(source =>
        {
            MovedIndexes.Tables(source);
            source.Entity<Vehicle>().HasAnnotation(Annotation, plate);
            source.HasAnnotation("CustomIndex:RenderingVersion", version);
        }).Shape();

        CollectionAssert.AreEquivalent(new[] { "DROP INDEX ux_plate;", "DROP INDEX ux_plate_boats;", "DROP INDEX ux_plate_cars;" }, Read(3));
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => Read(2)).Message,
            "is on a type of a TPC hierarchy with derived types, in a snapshot scaffolded before 5.5.0");
    }

    /// <summary>
    /// 5.x indexed a date or a time out of a document as the member's text, since the cast a query
    /// uses is none an index takes. A path here is that cast, so no declaration is that index.
    /// </summary>
    [TestMethod]
    public void A_date_in_a_document_which_5x_indexed_as_text_is_refused()
    {
        var refusal = Refused(
            """[{"paths":["Prefs.Seen"],"unique":true,"name":"ux_seen"}]""",
            more: source => source.Entity<Account>().ComplexProperty(x => x.Prefs).Property<DateTime>("Seen"));

        StringAssert.Contains(refusal, "the index 'ux_seen' of Account");
        StringAssert.Contains(refusal, "reads 'Prefs.Seen', a date or a time in a JSON document, which 5.x indexed as the member's text");
        StringAssert.Contains(refusal, "while the project is on 5.x");
    }

    [TestMethod]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:NullsDistinct":false}}]""", "says NULLS NOT DISTINCT and is not unique")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:StorageParameter:autovacuum_enabled":true}}]""", "sets the storage parameter 'autovacuum_enabled', which a btree index does not have")]
    [DataRow("""[{"paths":["Tags"],"unique":false,"props":{"Npgsql:IndexMethod":"gin","Npgsql:StorageParameter:fillfactor":70}}]""", "sets the storage parameter 'fillfactor', which a gin index does not have")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:StorageParameter:fillfactor":"many"}}]""", "sets the storage parameter 'fillfactor' to 'many', which is no whole number")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"SqlServer:Clustered":true}}]""", "carries the option 'SqlServer:Clustered'")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:IndexSortOrder":["Descending"]}}]""", "carries the option 'Npgsql:IndexSortOrder'")]
    [DataRow("""[{"paths":[],"parts":[{"path":"Period","desc":true}],"unique":false,"props":{"Npgsql:IndexMethod":"gist"}}]""", "states an order on a key of a gist index")]
    [DataRow("""[{"paths":["Tags"],"unique":true,"props":{"Npgsql:IndexMethod":"gin"}}]""", "is unique and a gin index")]
    [DataRow("""[{"paths":["Kind","Title"],"unique":false,"props":{"Npgsql:IndexMethod":"hash"}}]""", "has several keys, which a hash index does not take")]
    [DataRow("""[{"paths":["Tags"],"unique":false,"props":{"Npgsql:IndexMethod":"gin","Npgsql:IndexInclude":["Rank"]}}]""", "includes columns, which a gin index does not take")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:IndexInclude":["no_such_column"]}}]""", "includes 'no_such_column', which is neither a property of Account nor the column of one")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:IndexOperators":["int4_ops","text_ops"]}}]""", "names more operator classes or collations than it has keys")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:IndexMethod":"bloom"}}]""", "uses the access method 'bloom'")]
    public void What_an_index_declaration_cannot_say_is_refused_by_name(string json, string expected)
    {
        var refusal = Refused(json);

        StringAssert.Contains(refusal, "of Account as EFCore.ComplexIndexes 5.x stored it");
        StringAssert.Contains(refusal, expected);
        StringAssert.Contains(refusal, "It is in the database, so it is not passed over");
    }

    /// <summary>What 5.x did not write is not read as something else.</summary>
    [TestMethod]
    [DataRow("""{"paths":["Rank"]}""", "it is not a list")]
    [DataRow("""[["Rank"]]""", "an index is not an object")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"tablespace":"fast"}]""", "an index holds 'tablespace'")]
    [DataRow("""[{"paths":["Rank"],"unique":"yes"}]""", "an index holds 'unique'")]
    [DataRow("""[{"paths":[],"unique":false}]""", "an index has no parts")]
    [DataRow("""[{"paths":[],"parts":[{"path":"Rank","expr":"x"}],"unique":false}]""", "a part needs exactly one of 'path', 'expr' and 'tmpl'")]
    [DataRow("""[{"paths":[],"parts":[{"path":"Rank","nulls":"Middle"}],"unique":false}]""", "a part puts its nulls 'Middle'")]
    [DataRow("""[{"paths":[],"parts":[{"path":"Rank","weight":1}],"unique":false}]""", "a part holds 'weight'")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:IndexMethod":{"a":1}}}]""", "the option 'Npgsql:IndexMethod' is neither text")]
    [DataRow("""[{"paths":["Rank"],"unique":false,"props":{"Npgsql:NullsDistinct":"no"}}]""", "the option 'Npgsql:NullsDistinct' is not of the type 5.x wrote")]
    [DataRow("not json", "is not what 5.x wrote")]
    public void An_annotation_5x_did_not_write_is_refused(string json, string expected)
    {
        var refusal = Refused(json);

        StringAssert.Contains(refusal, "indexes of Account as EFCore.ComplexIndexes 5.x stored them");
        StringAssert.Contains(refusal, expected);
    }
}
