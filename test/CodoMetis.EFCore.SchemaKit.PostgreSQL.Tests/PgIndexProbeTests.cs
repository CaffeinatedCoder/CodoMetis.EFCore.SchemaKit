namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// The design's probes for an index (<c>design/schemakit-api.md</c>, P1, P3 and P5), compiled against
/// the shipped assembly: what PostgreSQL has no statement for does not compile, and its twin,
/// which differs in the one thing, does. The prototype in <c>test/SchemaKit.ApiProbes</c> holds
/// the same probes against itself; these hold them against what a consumer references.
/// </summary>
[TestClass]
public sealed class PgIndexProbeTests
{
    private const string Model = """
        using System;
        using Microsoft.EntityFrameworkCore;
        using NpgsqlTypes;
        using CodoMetis.EFCore.SchemaKit;
        using CodoMetis.EFCore.SchemaKit.PostgreSQL;

        public sealed class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int Rank { get; set; }
            public string[] Tags { get; set; } = [];
            public string Payload { get; set; } = "{}";
            public NpgsqlRange<DateOnly> Period { get; set; }
        }
        """;

    private static string Declares(string declaration) => $$"""
        using Microsoft.EntityFrameworkCore;
        using CodoMetis.EFCore.SchemaKit;
        using CodoMetis.EFCore.SchemaKit.PostgreSQL;

        public static class Probe
        {
            public static void Apply(ModelBuilder modelBuilder) => modelBuilder.Entity<Person>().DeclarePostgres(pg => [{{declaration}}]);
        }
        """;

    [TestMethod]
    // P1: the access method decides which options exist.
    [DataRow("P1.gin-unique", "pg.Gin(x => x.Tags)", "pg.Gin(x => x.Tags, unique: true)", "CS1739")]
    [DataRow("P1.gin-unique-key", "pg.Unique([pg.Key(x => x.Name)])", "pg.Unique([pg.Key(x => x.Tags).Ops(GinOpClass.ArrayOps)])", "CS0029")]
    [DataRow("P1.gin-include", "pg.BTree(x => x.Name, include: [x => x.Rank])", "pg.Gin(x => x.Tags, include: [x => x.Rank])", "CS1739")]
    [DataRow("P1.hash-include", "pg.SpGist(pg.Key(x => x.Name), include: [x => x.Rank])", "pg.Hash(x => x.Name, include: [x => x.Rank])", "CS1739")]
    [DataRow("P1.gin-desc", "pg.BTree([pg.Key(x => x.Rank).Desc()])", "pg.Gin([pg.Key(x => x.Tags).Desc()])", "CS0029")]
    [DataRow("P1.gist-nulls", "pg.Gist([pg.Key(x => x.Period)])", "pg.Gist([pg.Key(x => x.Period).NullsLast()])", "CS0029")]
    [DataRow("P1.hash-two-keys", "pg.Hash(pg.Key(x => x.Name))", "pg.Hash([pg.Key(x => x.Name), pg.Key(x => x.Rank)])", "CS9174")]
    [DataRow("P1.nulls-not-distinct-without-unique", "pg.Unique(x => x.Name, nullsDistinct: false)", "pg.BTree(x => x.Name, nullsDistinct: false)", "CS1739")]
    [DataRow("P1.opclass-of-other-method", "pg.Gin([pg.Key(x => x.Payload).Ops(GinOpClass.JsonbPathOps)])", "pg.BTree([pg.Key(x => x.Payload).Ops(GinOpClass.JsonbPathOps)])", "CS0029")]
    // P3: an option is typed by the CLR type where that decides the store type.
    [DataRow("P3.collate-on-integer", """pg.BTree([pg.Key(x => x.Name).Collate("C")])""", """pg.BTree([pg.Key(x => x.Rank).Collate("C")])""", "CS1929")]
    // P5: SQL is a call written inside a lambda, never a string passed in its place.
    [DataRow("P5.string-filter", """pg.BTree(x => x.Name, where: x => Sql.Raw<bool>("{Rank} > 0"))""", """pg.BTree(x => x.Name, where: "{Rank} > 0")""", "CS1503")]
    [DataRow("P5.string-key", """pg.BTree(x => Sql.Raw<string>("lower({Name})"))""", """pg.BTree("lower({Name})")""", "CS1503")]
    // Beyond the design's ten: each option of a method that another method lacks.
    [DataRow("deduplicate-items-on-hash", "pg.BTree(x => x.Name, deduplicateItems: false)", "pg.Hash(x => x.Name, deduplicateItems: false)", "CS1739")]
    [DataRow("buffering-on-spgist", "pg.Gist(x => x.Period, buffering: GistBuffering.On)", "pg.SpGist(x => x.Name, buffering: GistBuffering.On)", "CS1739")]
    [DataRow("fast-update-on-brin", "pg.Gin(x => x.Tags, fastUpdate: false)", "pg.Brin(x => x.Rank, fastUpdate: false)", "CS1739")]
    [DataRow("pages-per-range-on-gin", "pg.Brin(x => x.Rank, pagesPerRange: 8)", "pg.Gin(x => x.Tags, pagesPerRange: 8)", "CS1739")]
    [DataRow("fill-factor-on-gin", "pg.Gist(x => x.Period, fillFactor: 90)", "pg.Gin(x => x.Tags, fillFactor: 90)", "CS1739")]
    [DataRow("include-on-brin", "pg.Gist(x => x.Period, include: [x => x.Rank])", "pg.Brin(x => x.Rank, include: [x => x.Name])", "CS1739")]
    [DataRow("unique-hash", "pg.Unique(x => x.Name)", "pg.Hash(x => x.Name, nullsDistinct: false)", "CS1739")]
    [DataRow("spgist-two-keys", "pg.SpGist(pg.Key(x => x.Name))", "pg.SpGist([pg.Key(x => x.Name), pg.Key(x => x.Rank)])", "CS9174")]
    public void What_PostgreSQL_has_no_statement_for_does_not_compile(string probe, string allowed, string forbidden, string diagnostic)
    {
        var compiles = ConsumerCompileTests.Compile(Model, Declares(allowed));
        Assert.AreEqual(0, compiles.Count, $"{probe}, the allowed twin:\n{string.Join("\n", compiles)}");

        var refused = ConsumerCompileTests.Compile(Model, Declares(forbidden));
        Assert.IsTrue(refused.Count > 0, $"{probe} compiles: {forbidden}");
        Assert.IsTrue(refused.All(d => d.Id == diagnostic), $"{probe}:\n{string.Join("\n", refused)}");
    }
}
