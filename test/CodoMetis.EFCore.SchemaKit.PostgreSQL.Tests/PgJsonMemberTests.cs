using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// Members of a JSON document as keys and in conditions (#208). PostgreSQL uses an expression
/// index only for a query whose expression is the index's own, so the oracle of a key is the
/// query: what EF Core writes for the same lambda, on the same model.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PgJsonMemberTests
{
    private static string Ordered<TValue>(Expression<Func<Person, TValue>> key)
    {
        using var context = Contexts.Of<JsonModels.Bare>();
        var sql = context.Set<Person>().OrderBy(key).Select(x => x.Id).ToQueryString();
        return Normalized(sql[(sql.IndexOf("ORDER BY ", StringComparison.Ordinal) + "ORDER BY ".Length)..].Replace("p.", ""));
    }

    // A query separates the names of a path with a comma and the vocabulary with a comma and a
    // space, which is one expression to PostgreSQL.
    private static string Normalized(string sql) => sql.Trim().Replace(", ", ",");

    /// <summary>The key of the index of that name, as its statement writes it, without the parentheses a key that is no column stands in.</summary>
    private static string Key(IEnumerable<MigrationOperation> operations, string index)
    {
        var statement = operations.OfType<SqlOperation>().Single(o => o.Sql.Contains($" {index} ON ", StringComparison.Ordinal)).Sql;
        var open = statement.IndexOf('(', statement.IndexOf(" ON people", StringComparison.Ordinal));
        var key = statement[(open + 1)..statement.LastIndexOf(')')];
        return Normalized(key.StartsWith('(') && key.EndsWith(')') ? key[1..^1] : key);
    }

    private static IEnumerable<object[]> Members() =>
    [
        ["ix_rank", Ordered(x => x.Prefs.Rank)],
        ["ix_theme", Ordered(x => x.Prefs.Theme)],
        ["ix_nick", Ordered(x => x.Prefs.Nick)],
        ["ix_active", Ordered(x => x.Prefs.Active)],
        ["ix_flag", Ordered(x => x.Prefs.Flag)],
        ["ix_level", Ordered(x => x.Prefs.Level)],
        ["ix_big", Ordered(x => x.Prefs.Big)],
        ["ix_small", Ordered(x => x.Prefs.Small)],
        ["ix_price", Ordered(x => x.Prefs.Price)],
        ["ix_money", Ordered(x => x.Prefs.Money)],
        ["ix_ratio", Ordered(x => x.Prefs.Ratio)],
        ["ix_share", Ordered(x => x.Prefs.Share)],
        ["ix_key", Ordered(x => x.Prefs.Key)],
        ["ix_mode", Ordered(x => x.Prefs.Mode)],
        ["ix_modetext", Ordered(x => x.Prefs.ModeText)],
        ["ix_letter", Ordered(x => x.Prefs.Letter)],
        ["ix_blob", Ordered(x => x.Prefs.Blob)],
        ["ix_renamed", Ordered(x => x.Prefs.Renamed)],
        ["ix_odd", Ordered(x => x.Prefs.Odd)],
        ["ix_code", Ordered(x => x.Prefs.Code)],
        ["ix_mail", Ordered(x => x.Prefs.Mail)],
        ["ix_tags", Ordered(x => x.Prefs.Tags)],
        ["ix_city", Ordered(x => x.Prefs.Address.City)],
        ["ix_zip", Ordered(x => x.Prefs.Address.Zip)],
        ["ix_oddzip", Ordered(x => x.Prefs.Address.OddZip)],
        ["ix_extra_rank", Ordered(x => x.Extra!.Rank)],
    ];

    private static IRelationalModel Snapshot(DbContext context)
        => context.GetService<IModelRuntimeInitializer>()
            .Initialize(Snapshots.Of(context).Model, designTime: true, validationLogger: null)
            .GetRelationalModel();

    /// <summary>
    /// Each member's key is the expression the query orders by, on the configured model and on
    /// its snapshot alike: a key that differed between the two would be another index in every diff.
    /// </summary>
    [TestMethod, DynamicData(nameof(Members))]
    public void A_member_is_written_as_the_query_writes_it(string index, string query)
    {
        using var context = Contexts.Of<JsonModels.EveryMember>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        Assert.AreEqual(query, Key(differ.GetDifferences(null, context.Relational()), index), "the configured model");
        Assert.AreEqual(query, Key(differ.GetDifferences(null, Snapshot(context)), index), "its snapshot");
    }

    [TestMethod]
    public void Every_declared_member_has_its_row()
        => CollectionAssert.AreEquivalent(
            JsonModels.EveryMember.Declared.Select(index => index.Name).ToList(),
            Members().Select(row => (string)row[0]).ToList());

    /// <summary>A model against its own snapshot is no migration, either way.</summary>
    [TestMethod]
    public void A_model_with_members_against_its_snapshot_is_no_operation()
    {
        using var context = Contexts.Of<JsonModels.EveryMember>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var snapshot = Snapshot(context);

        Assert.AreEqual(0, differ.GetDifferences(snapshot, context.Relational()).Count);
        Assert.AreEqual(0, differ.GetDifferences(context.Relational(), snapshot).Count);
        Assert.IsFalse(differ.HasDifferences(snapshot, context.Relational()));
    }

    // ── The document itself, and what is nested in it ──

    private static List<string> Created<TModel>() where TModel : ITestModel
        => Contexts.Diff<JsonModels.Bare, TModel>().Sql();

    /// <summary>
    /// A path that ends at the document is the column that holds it, a plain column of type
    /// <c>jsonb</c>, and so is a collection of documents. A document or a collection inside the
    /// document is taken out of it as <c>jsonb</c>.
    /// </summary>
    [TestMethod]
    public void The_document_is_its_column_and_what_is_nested_is_taken_out_of_it()
        => CollectionAssert.AreEquivalent(
            new[]
            {
                "CREATE INDEX ix_doc ON people USING gin (prefs jsonb_path_ops);",
                """CREATE INDEX ix_extra ON people USING gin ("Extra");""",
                """CREATE INDEX ix_items ON people USING gin ("Items");""",
                "CREATE INDEX ix_addr ON people USING gin ((prefs -> 'addr'));",
                """CREATE INDEX ix_doc_incl ON people ("Name") INCLUDE (prefs);""",
            },
            Created<JsonModels.Documents>());

    /// <summary>A class of GIN's for documents is checked against the document's own type, which only the model knows.</summary>
    [TestMethod]
    public void An_operator_class_is_judged_by_the_documents_store_type()
    {
        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => Created<JsonModels.ADocumentsClassOnAMember>());
        StringAssert.Contains(refusal.Message, "jsonb_path_ops");
        StringAssert.Contains(refusal.Message, "'integer'");
    }

    /// <summary>The default name takes the document's column and the JSON names, never the SQL: 5.x's rule.</summary>
    [TestMethod]
    public void A_default_name_is_the_container_and_the_names()
        => CollectionAssert.AreEquivalent(
            new[]
            {
                """CREATE INDEX "IX_people_prefsRank" ON people ((CAST(prefs ->> 'Rank' AS integer)));""",
                """CREATE INDEX "IX_people_prefsaddrCity_Name" ON people ((prefs #>> '{addr,City}'), "Name");""",
                """CREATE INDEX "IX_people_prefs" ON people USING gin (prefs);""",
                """CREATE INDEX "IX_people_lowerprefsTheme" ON people ((lower((prefs ->> 'Theme'))));""",
            },
            Created<JsonModels.Unnamed>());

    // ── Conditions and expressions ──

    /// <summary>
    /// In a template a member stands in parentheses of its own, whatever is around it, and its
    /// nullability is the member's and the document's: a member of an optional document can be
    /// null though its type cannot.
    /// </summary>
    [TestMethod]
    public void A_condition_reads_members_as_the_query_does()
        => CollectionAssert.AreEquivalent(
            new[]
            {
                """CREATE INDEX ix_cond ON people ("Name") WHERE ((CAST(prefs ->> 'Active' AS boolean)) AND (CAST(prefs ->> 'Rank' AS integer)) > 5);""",
                """CREATE INDEX ix_nick ON people ("Name") WHERE ((prefs ->> 'Nick') <> 'a' OR (prefs ->> 'Nick') IS NULL);""",
                """CREATE INDEX ix_mode ON people ("Name") WHERE ((prefs ->> 'ModeText') = 'Loud' AND (CAST(prefs ->> 'Mode' AS integer)) = 1);""",
                """CREATE INDEX ix_optional ON people ("Name") WHERE ((CAST("Extra" ->> 'Rank' AS integer)) <> 5 OR (CAST("Extra" ->> 'Rank' AS integer)) IS NULL);""",
                """CREATE INDEX ix_expr ON people ((lower((prefs ->> 'Theme'))));""",
            },
            Created<JsonModels.Conditions>());

    /// <summary>An element of an exclusion constraint too, with the extension a scalar needs under GiST.</summary>
    [TestMethod]
    public void An_exclusion_constraint_reads_a_member()
        => CollectionAssert.AreEqual(
            new[]
            {
                "CREATE EXTENSION IF NOT EXISTS btree_gist;",
                """ALTER TABLE people ADD CONSTRAINT ex_rank EXCLUDE USING gist ((CAST(prefs ->> 'Rank' AS integer)) WITH =, "Period" WITH &&) WHERE ((CAST(prefs ->> 'Active' AS boolean)));""",
            },
            Created<JsonModels.Excluding>());

    /// <summary>
    /// A member reads its document's column as an expression, and that is what decides when the
    /// column goes: PostgreSQL holds a column under an exclusion constraint whose element is an
    /// expression over it (2BP01), so the constraint is out of its way before the drop. Read as a
    /// key, the constraint would be taken for gone with the column and the migration would fail
    /// when it is applied.
    /// </summary>
    [TestMethod]
    public void A_dropped_document_under_a_constraint_is_rebuilt_around()
        => CollectionAssert.AreEqual(
            new[]
            {
                "ALTER TABLE people DROP CONSTRAINT ex_rank;",
                "DropColumnOperation",
                """ALTER TABLE people ADD CONSTRAINT ex_rank EXCLUDE USING gist ("Period" WITH &&);""",
            },
            Contexts.Diff<JsonModels.Excluding, JsonModels.ExcludingWithoutTheDocument>().Shape());

    /// <summary>An index that is no longer declared is dropped before the column it read, as with any column.</summary>
    [TestMethod]
    public void An_index_on_a_member_is_dropped_before_its_document()
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX ux_rank;", "DropColumnOperation" },
            Contexts.Diff<JsonModels.OneActivePerRank, JsonModels.WithoutTheDocument>().Shape());

    // ── What is refused ──

    private static string Refused<TModel>() where TModel : ITestModel
        => Assert.ThrowsExactly<InvalidOperationException>(() => Created<TModel>()).Message;

    /// <summary>
    /// A date or a time out of a document is a cast PostgreSQL takes neither as a key nor in a
    /// condition (42P17), of an index or of a constraint. Refused where the model is resolved,
    /// with the member and its type: read as text it would be built and serve no query.
    /// </summary>
    [TestMethod]
    public void A_member_whose_cast_is_not_immutable_is_refused_as_a_key_and_in_a_condition()
    {
        StringAssert.Contains(Refused<JsonModels.ADateAsAKey>(), "The index 'ix_day' reads 'Prefs.Day', a member of a JSON document that is a 'date'");
        StringAssert.Contains(Refused<JsonModels.ATimestampInACondition>(), "The index 'ix_seen' reads 'Extra.Seen', a member of a JSON document that is a 'timestamp with time zone'");
        StringAssert.Contains(Refused<JsonModels.ATimeInAnExpression>(), "'Prefs.At'");
        StringAssert.Contains(Refused<JsonModels.AnIntervalInAnElement>(), "The exclusion constraint 'ex_span' reads 'Prefs.Span'");
        StringAssert.Contains(Refused<JsonModels.AnOffsetInAConstraintsCondition>(), "The exclusion constraint 'ex_stamp' reads 'Prefs.Stamp'");
    }

    /// <summary>A column of such a type is no cast: only a member of a document is refused.</summary>
    [TestMethod]
    public void A_date_column_is_no_member()
        => CollectionAssert.AreEqual(
            new[] { """CREATE INDEX ix_born ON dated ("Born") WHERE "Born" IS NOT NULL;""" },
            Contexts.Diff<JsonModels.DatedBare, JsonModels.ADateColumn>().Sql());

    /// <summary>
    /// A snapshot does not keep a column type that was stated for a member, so the model would
    /// cast to one type and its snapshot to another in every diff. Refused, with what a snapshot
    /// does keep.
    /// </summary>
    [TestMethod]
    public void A_member_that_states_its_column_type_is_refused()
    {
        var refusal = Refused<JsonModels.AMemberWithAColumnType>();

        StringAssert.Contains(refusal, "'Prefs.Price'");
        StringAssert.Contains(refusal, "HasColumnType('numeric(12,4)')");
        StringAssert.Contains(refusal, "HasPrecision");
    }

    [TestMethod]
    public void A_member_cannot_be_included()
        => StringAssert.Contains(Refused<JsonModels.AMemberIncluded>(), "The index 'ix_incl' includes 'Prefs.Rank', which is a member of a JSON document and no column");

    /// <summary>
    /// Npgsql writes a path of more than one name as an array literal, where PostgreSQL reads an
    /// unquoted null as a NULL element: the member reads as NULL, and a unique index on it took
    /// two equal rows (#235). Another form would serve no query, so it is refused.
    /// </summary>
    [TestMethod]
    public void A_name_the_path_literal_reads_as_null_is_refused()
    {
        StringAssert.Contains(Refused<JsonModels.AKeyNamedNull>(), "at 'addr.null' has the JSON name 'null'");
        StringAssert.Contains(Refused<JsonModels.AConditionNamedNull>(), "at 'addr.NULL' has the JSON name 'NULL'");
    }

    /// <summary>The twin: one step is a string literal, which is the query's too.</summary>
    [TestMethod]
    public void A_member_named_null_at_the_top_is_the_querys_expression()
    {
        using var context = Contexts.Of<JsonModels.AMemberNamedNullAtTheTop>();
        var query = context.Set<Person>().OrderBy(x => x.Prefs.Nick).Select(x => x.Id).ToQueryString();
        StringAssert.Contains(query, "p.prefs ->> 'null'");
        CollectionAssert.Contains(Created<JsonModels.AMemberNamedNullAtTheTop>(), "CREATE UNIQUE INDEX ix_nick ON people ((prefs ->> 'null'));");
    }

    /// <summary>
    /// A document is in the table it is mapped to. On a split entity a member had a column in
    /// every table, so an index that also read the second table's column landed there, where the
    /// document is not, and failed at apply (#243). It reads a column of each table, and is refused
    /// as such a declaration is (#191). Alone it is in the first table, on the model and on its
    /// snapshot.
    /// </summary>
    [TestMethod]
    public void A_member_on_a_split_entity_is_in_the_table_of_its_document()
    {
        CollectionAssert.AreEqual(
            new[] { "CREATE UNIQUE INDEX ux_nick ON subscribers ((prefs ->> 'Nick'));" },
            Contexts.Diff<JsonModels.SubscribersBare, JsonModels.AMemberOfTheFirstTable>().Sql());

        using (var context = Contexts.Of<JsonModels.AMemberOfTheFirstTable>())
        {
            var differ = context.GetService<IMigrationsModelDiffer>();
            StringAssert.Contains(differ.GetDifferences(null, Snapshot(context)).OfType<SqlOperation>().Single().Sql, "ux_nick ON subscribers ", "its snapshot");
        }

        foreach (var refused in new[]
                 {
                     Assert.ThrowsExactly<InvalidOperationException>(() => Contexts.Diff<JsonModels.SubscribersBare, JsonModels.AMemberIncludingAColumnOfTheSecond>()),
                     Assert.ThrowsExactly<InvalidOperationException>(() => Contexts.Diff<JsonModels.SubscribersBare, JsonModels.AMemberUnderAConditionOnTheSecond>()),
                 })
            StringAssert.Contains(refused.Message, "Subscriber is split over the tables subscribers, subscriber_details. No one of them has a column for everything");
    }

    [TestMethod]
    public void A_path_through_a_collection_is_refused()
        => StringAssert.Contains(Refused<JsonModels.ThroughACollection>(), "goes through 'Items', which is a collection");

    /// <summary>An owned type mapped to JSON is a navigation and no complex property: a path into it names no property here.</summary>
    [TestMethod]
    public void A_path_into_an_owned_document_is_refused()
        => StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => Contexts.Diff<JsonModels.CustomersBare, JsonModels.IntoAnOwnedDocument>()).Message,
            "'Customer' has no property 'Owned'");

    /// <summary>The statements the README shows for its sample.</summary>
    [TestMethod]
    public void The_READMEs_sample_scaffolds_what_the_README_says()
        => CollectionAssert.AreEquivalent(
            new[]
            {
                "CREATE INDEX ix_customer_rank ON customers ((CAST(prefs ->> 'Rank' AS integer)));",
                """CREATE INDEX ix_customer_active ON customers ("Name") WHERE (CAST(prefs ->> 'Active' AS boolean));""",
                "CREATE INDEX ix_customer_prefs ON customers USING gin (prefs jsonb_path_ops);",
            },
            Contexts.Diff<JsonModels.CustomersBare, JsonModels.AsInTheReadme>().Sql());
}

