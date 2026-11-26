using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// Members of a JSON document on a live server (#208): what the catalog holds, whether the
/// planner takes the index for the query EF Core writes, what the server refuses, and what it
/// does to an index when the document's column goes or is renamed.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class PgJsonMemberLiveTests
{
    private static void AssertMigratesTo<TFrom, TTo>(Action<LiveDatabase, LiveDatabase>? also = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var migrated = new LiveDatabase();
        using var created = new LiveDatabase();
        using (var context = Contexts.Of<TTo>(created.ConnectionString)) context.Database.EnsureCreated();

        migrated.Create<TFrom>();
        migrated.Migrate<TFrom, TTo>();

        CollectionAssert.AreEqual(created.Indexes(), migrated.Indexes());
        CollectionAssert.AreEqual(created.Exclusions(), migrated.Exclusions());
        also?.Invoke(migrated, created);
    }

    /// <summary>The server takes every key, and a migration leaves what <c>EnsureCreated()</c> builds.</summary>
    [TestMethod]
    public void Every_member_reaches_the_catalog_by_a_migration_as_by_EnsureCreated()
    {
        AssertMigratesTo<JsonModels.Bare, JsonModels.EveryMember>((migrated, _) => Assert.AreEqual(JsonModels.EveryMember.Declared.Length, migrated.Indexes().Count));
        AssertMigratesTo<JsonModels.Bare, JsonModels.Documents>((migrated, _) => Assert.AreEqual(5, migrated.Indexes().Count));
        AssertMigratesTo<JsonModels.Bare, JsonModels.Conditions>((migrated, _) => Assert.AreEqual(5, migrated.Indexes().Count));
        AssertMigratesTo<JsonModels.Bare, JsonModels.Unnamed>((migrated, _) => Assert.AreEqual(4, migrated.Indexes().Count));
        AssertMigratesTo<JsonModels.Bare, JsonModels.Excluding>((migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));
        AssertMigratesTo<JsonModels.EveryMember, JsonModels.Bare>((migrated, _) => Assert.AreEqual(0, migrated.Indexes().Count));
    }

    private static string Plan(LiveDatabase database, string query)
    {
        using var connection = database.Open();
        connection.Execute("SET enable_seqscan = off");
        return string.Join("\n", connection.Column("EXPLAIN " + query));
    }

    /// <summary>
    /// The point of writing a member as the query writes it: the planner takes the index for the
    /// query EF Core writes for the same lambda, and for no other expression over the member.
    /// </summary>
    [TestMethod]
    public void The_planner_takes_a_members_index_for_the_query_EF_Core_writes()
    {
        using var database = new LiveDatabase();
        database.Create<JsonModels.EveryMember>();
        using var context = Contexts.Of<JsonModels.EveryMember>(database.ConnectionString);
        var people = context.Set<Person>();

        void Uses(string index, IQueryable<Person> query)
            => StringAssert.Contains(Plan(database, query.ToQueryString()), index, query.ToQueryString());

        Uses("ix_rank", people.Where(x => x.Prefs.Rank == 5));
        Uses("ix_theme", people.Where(x => x.Prefs.Theme == "dark"));
        Uses("ix_active", people.Where(x => x.Prefs.Active));
        Uses("ix_money", people.Where(x => x.Prefs.Money == 5.5m));
        Uses("ix_key", people.Where(x => x.Prefs.Key == Guid.Empty));
        Uses("ix_modetext", people.Where(x => x.Prefs.ModeText == Mode.Loud));
        Uses("ix_mode", people.Where(x => x.Prefs.Mode == Mode.Loud));
        Uses("ix_odd", people.Where(x => x.Prefs.Odd == "a"));
        Uses("ix_city", people.Where(x => x.Prefs.Address.City == "a"));
        Uses("ix_oddzip", people.Where(x => x.Prefs.Address.OddZip == 5));
        Uses("ix_extra_rank", people.Where(x => x.Extra!.Rank == 5));
        Uses("ix_tags", people.Where(x => x.Prefs.Tags.Contains(3)));

        // The control: the member as text is another expression, and no index of these is taken for it.
        var asText = Plan(database, """SELECT p."Id" FROM people AS p WHERE (p.prefs ->> 'Rank') = '5'""");
        Assert.IsFalse(asText.Contains("ix_rank", StringComparison.Ordinal), asText);
    }

    /// <summary>A partial index on a member's condition serves the query with that condition.</summary>
    [TestMethod]
    public void The_planner_takes_a_partial_index_whose_condition_reads_a_member()
    {
        using var database = new LiveDatabase();
        database.Create<JsonModels.OneActivePerRank>();
        using var context = Contexts.Of<JsonModels.OneActivePerRank>(database.ConnectionString);

        var query = context.Set<Person>().Where(x => x.Prefs.Rank == 5 && x.Prefs.Active).ToQueryString();
        StringAssert.Contains(Plan(database, query), "ux_rank", query);

        var all = context.Set<Person>().Where(x => x.Prefs.Rank == 5).ToQueryString();
        Assert.IsFalse(Plan(database, all).Contains("ux_rank", StringComparison.Ordinal), all);
    }

    private static void Save<TModel>(LiveDatabase database, Person person) where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>(database.ConnectionString);
        context.Add(person);
        context.SaveChanges();
    }

    private static Person Ranked(int rank, bool active)
        => new() { Name = "n", Period = new NpgsqlRange<DateOnly>(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)), Prefs = new Prefs { Rank = rank, Active = active, Letter = 'a' } };

    /// <summary>Behaviour: the rows a unique index on a member must refuse are refused, and the ones its condition leaves out are not.</summary>
    [TestMethod]
    public void A_unique_index_on_a_member_refuses_the_second_row()
    {
        using var database = new LiveDatabase();
        database.Create<JsonModels.OneActivePerRank>();

        Save<JsonModels.OneActivePerRank>(database, Ranked(1, active: true));
        Save<JsonModels.OneActivePerRank>(database, Ranked(2, active: true));
        Save<JsonModels.OneActivePerRank>(database, Ranked(1, active: false));

        var refusal = Assert.ThrowsExactly<DbUpdateException>(() => Save<JsonModels.OneActivePerRank>(database, Ranked(1, active: true)));
        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, ((PostgresException)refusal.InnerException!).SqlState);
    }

    /// <summary>The same for an exclusion constraint whose element is a member.</summary>
    [TestMethod]
    public void An_exclusion_constraint_on_a_member_refuses_the_overlapping_row()
    {
        using var database = new LiveDatabase();
        database.Create<JsonModels.Excluding>();

        Save<JsonModels.Excluding>(database, Ranked(1, active: true));
        Save<JsonModels.Excluding>(database, Ranked(2, active: true));
        Save<JsonModels.Excluding>(database, Ranked(1, active: false));

        var refusal = Assert.ThrowsExactly<DbUpdateException>(() => Save<JsonModels.Excluding>(database, Ranked(1, active: true)));
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, ((PostgresException)refusal.InnerException!).SqlState);
    }

    /// <summary>
    /// The list of casts an index refuses is the server's: each type on it fails as a key and in
    /// a condition (42P17), and each type a member is cast to that is not on it is taken.
    /// </summary>
    [TestMethod]
    public void The_casts_an_index_refuses_are_the_servers()
    {
        using var database = new LiveDatabase();
        database.Execute("CREATE TABLE t (id int, d jsonb NOT NULL)");
        string[] taken =
        [
            "integer", "bigint", "smallint", "numeric", "numeric(10,2)", "double precision", "real", "boolean", "uuid",
            "character(1)", "character varying(20)", "text",
        ];
        var n = 0;

        string? Refusal(string sql)
        {
            try
            {
                database.Execute(sql);
                return null;
            }
            catch (PostgresException refused)
            {
                return refused.SqlState;
            }
        }

        foreach (var type in PgJsonMembers.Refused)
        {
            Assert.IsTrue(PgJsonMembers.IsRefused(type), type);
            Assert.AreEqual("42P17", Refusal($"CREATE INDEX k{++n} ON t ((CAST(d ->> 'a' AS {type})))"), $"{type} as a key");
            Assert.AreEqual("42P17", Refusal($"CREATE INDEX k{++n} ON t (id) WHERE (CAST(d ->> 'a' AS {type})) IS NULL"), $"{type} in a condition");
        }

        foreach (var type in taken)
        {
            Assert.IsFalse(PgJsonMembers.IsRefused(type), type);
            Assert.IsNull(Refusal($"CREATE INDEX k{++n} ON t ((CAST(d ->> 'a' AS {type})))"), $"{type} as a key");
            Assert.IsNull(Refusal($"CREATE INDEX k{++n} ON t (id) WHERE (CAST(d ->> 'a' AS {type})) IS NULL"), $"{type} in a condition");
        }

        // A precision changes nothing about the rule.
        Assert.IsTrue(PgJsonMembers.IsRefused("timestamp(3) with time zone"));
        Assert.AreEqual("42P17", Refusal("CREATE INDEX kp ON t ((CAST(d ->> 'a' AS timestamp(3) with time zone)))"));
        Assert.AreEqual("0A000", Refusal("CREATE INDEX ki ON t (id) INCLUDE ((d ->> 'a'))"), "an expression among the included columns");
    }

    private static string DroppingTheDocument<TModel>() where TModel : ITestModel
    {
        using var database = new LiveDatabase();
        database.Create<TModel>();

        try
        {
            database.Execute("ALTER TABLE people DROP COLUMN prefs");
        }
        catch (PostgresException refused)
        {
            return $"refused {refused.SqlState}";
        }

        return $"dropped, {database.Indexes().Count} indexes and {database.Exclusions().Count} constraints left";
    }

    /// <summary>
    /// The rows of the impact table a member is read by: its document's column, as an expression.
    /// PostgreSQL drops an index with the column and holds the column under an exclusion
    /// constraint, as for any other expression.
    /// </summary>
    [TestMethod]
    public void The_server_treats_a_members_document_as_an_expressions_column()
    {
        Assert.AreEqual("dropped, 0 indexes and 0 constraints left", DroppingTheDocument<JsonModels.OneActivePerRank>());
        Assert.AreEqual("refused 2BP01", DroppingTheDocument<JsonModels.Excluding>());
    }

    /// <summary>The migrations that follow from it: the constraint rebuilt around the drop, the index gone with the column.</summary>
    [TestMethod]
    public void Dropping_the_documents_column_by_a_migration()
    {
        AssertMigratesTo<JsonModels.Excluding, JsonModels.ExcludingWithoutTheDocument>((migrated, _) => Assert.AreEqual(1, migrated.Exclusions().Count));
        AssertMigratesTo<JsonModels.OneActivePerRank, JsonModels.WithoutTheDocument>((migrated, _) => Assert.AreEqual(0, migrated.Indexes().Count));
    }

    /// <summary>
    /// The document's column renamed: the indexes follow, in place, and a default name, which is
    /// built from the column's name, is renamed with it.
    /// </summary>
    [TestMethod]
    public void Renaming_the_documents_column()
    {
        AssertMigratesTo<JsonModels.EveryMember, JsonModels.EveryMemberInARenamedColumn>((migrated, _) =>
            Assert.AreEqual(JsonModels.EveryMember.Declared.Length, migrated.Indexes().Count));

        using var database = new LiveDatabase();
        database.Create<JsonModels.UnnamedOnRank>();
        var oid = database.IndexOid("IX_people_prefsRank");

        database.Migrate<JsonModels.UnnamedOnRank, JsonModels.UnnamedOnRankInARenamedColumn>();

        Assert.AreEqual(oid, database.IndexOid("IX_people_preferencesRank"));
    }
}
