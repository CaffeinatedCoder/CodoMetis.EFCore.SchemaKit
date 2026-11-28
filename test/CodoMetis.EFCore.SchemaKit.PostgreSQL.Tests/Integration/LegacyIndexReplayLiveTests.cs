using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;
using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Legacy5x.Replay;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// Migrations 5.x scaffolded, applied to a fresh database by a context that has SchemaKit and no
/// 5.x (#28). The context applies them itself, through EF Core's own migrator, as an application
/// and a bundle do. What it leaves is compared with the database the published 5.6.0's scripts
/// build, by the catalog's own text: a statement that reads like 5.x's proves nothing about what
/// the server made of it.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class LegacyIndexReplayLiveTests
{
    private const string Initial = "20260101000000_Initial";

    private const string Changed = "20260102000000_Changed";

    private static ReplayContext Context(LiveDatabase database) => LegacyFixture.ReplayContext(database.ConnectionString);

    private static void Run(LiveDatabase database, IEnumerable<string> statements)
    {
        using var connection = database.Open();
        foreach (var statement in statements) connection.Execute(statement);
    }

    /// <summary>
    /// A script of 5.x's up to and with the statement that records, or forgets, the migration, and
    /// the <c>COMMIT</c> behind it where it has one: after a statement outside a transaction the
    /// script records the migration outside one too.
    /// </summary>
    private static List<string> Through(IReadOnlyList<string> script, string migration)
    {
        var recorded = script.ToList().FindIndex(statement => statement.Contains("__EFMigrationsHistory", StringComparison.Ordinal)
                                                             && statement.Contains($"'{migration}'", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, recorded, $"5.x's script records {migration}.");
        var committed = recorded + 1 < script.Count && script[recorded + 1] == "COMMIT;";
        return [.. script.Take(recorded + (committed ? 2 : 1))];
    }

    private static List<string> History(LiveDatabase database)
    {
        using var connection = database.Open();
        return connection.Column("""SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1""");
    }

    private static void AssertTheSame(LiveDatabase built, LiveDatabase replayed, int indexes)
    {
        var expected = built.Indexes();
        Assert.HasCount(indexes, expected);
        CollectionAssert.AreEqual(expected, replayed.Indexes());
        CollectionAssert.AreEqual(History(built), History(replayed));
    }

    [TestMethod]
    public void The_first_migration_leaves_the_indexes_5x_built()
    {
        using var built = new LiveDatabase();
        using var replayed = new LiveDatabase();
        Run(built, Through(LegacyFixture.ReplayScripts.Up, Initial));

        using (var context = Context(replayed))
            context.Database.Migrate(Initial);

        AssertTheSame(built, replayed, 10);
    }

    [TestMethod]
    public void Both_migrations_leave_the_indexes_5x_built()
    {
        using var built = new LiveDatabase();
        using var replayed = new LiveDatabase();
        Run(built, LegacyFixture.ReplayScripts.Up);

        using (var context = Context(replayed))
            context.Database.Migrate();

        AssertTheSame(built, replayed, 9);
    }

    /// <summary>The way back creates what the second migration dropped: three indexes only 5.x's generator wrote, one of them outside a transaction.</summary>
    [TestMethod]
    public void The_way_back_leaves_what_5x_left()
    {
        using var built = new LiveDatabase();
        using var replayed = new LiveDatabase();
        Run(built, LegacyFixture.ReplayScripts.Up);
        Run(built, Through(LegacyFixture.ReplayScripts.Down, Changed));

        using var context = Context(replayed);
        context.Database.Migrate();
        context.Database.Migrate(Initial);

        AssertTheSame(built, replayed, 10);

        // And to nothing: the table is gone, and its indexes with it.
        context.Database.Migrate("0");
        Assert.IsEmpty(replayed.Indexes());
        Assert.IsEmpty(History(replayed));
    }

    /// <summary>The script a consumer writes with <c>migrations script</c>, run statement by statement as psql runs it.</summary>
    [TestMethod]
    public void The_script_of_both_migrations_leaves_the_indexes_5x_built()
    {
        using var built = new LiveDatabase();
        using var replayed = new LiveDatabase();
        Run(built, LegacyFixture.ReplayScripts.Up);
        string script;
        using (var context = Context(replayed))
            script = context.GetService<IMigrator>().GenerateScript();

        Run(replayed, System.Text.RegularExpressions.Regex.Split(script, @"\r?\n\s*\r?\n").Select(s => s.Trim()).Where(s => s.Length > 0));

        AssertTheSame(built, replayed, 9);
    }

    /// <summary>The fixture of #218, whose database came from 5.x's script until now: 32 indexes, ten of them replayed.</summary>
    [TestMethod]
    public void The_migration_of_every_kind_of_index_leaves_the_indexes_5x_built()
    {
        using var built = new LiveDatabase();
        using var replayed = new LiveDatabase();
        Run(built, LegacyFixture.IndexesScript);

        using (var context = LegacyFixture.Context<MovedModels.SlotAlone>(replayed.ConnectionString))
        {
            var commands = context.GetService<IMigrationsSqlGenerator>().Generate(LegacyFixture.IndexesUp);
            context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>());
        }

        var expected = built.Indexes();
        Assert.HasCount(32, expected);
        CollectionAssert.AreEqual(expected, replayed.Indexes());
    }

    /// <summary>
    /// What the index is for. Unique over an expression, among the rows of its condition, with
    /// nulls that are not distinct: the provider's generator would have failed on the operation's
    /// last column, and an index over the operation's columns alone would take these rows.
    /// </summary>
    [TestMethod]
    public void A_replayed_unique_index_refuses_what_it_is_there_to_refuse()
    {
        using var replayed = new LiveDatabase();
        using (var context = Context(replayed))
            context.Database.Migrate(Initial);

        static string Entry(int id, string title, int rank)
            => $"""
                INSERT INTO ledger.entries ("Id", "Code", "Kind", "Title", "Rank", "Period", "Amount_Currency", "Amount_Cents")
                VALUES ({id}, 'c{id}', 'k', '{title}', {rank}, '[2026-01-01,2026-02-01)', 'EUR', 1)
                """;

        replayed.Execute(Entry(1, "Rent", rank: 1));
        // The condition: a row outside it is no duplicate.
        replayed.Execute(Entry(2, "RENT", rank: 0));

        var refusal = Assert.ThrowsExactly<PostgresException>(() => replayed.Execute(Entry(3, "rent", rank: 2)));

        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, refusal.SqlState);
        Assert.AreEqual("ux_entry_kind_title", refusal.ConstraintName);
    }
}
