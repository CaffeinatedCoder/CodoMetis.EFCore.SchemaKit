using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// SQL written by hand in a lambda (issue 220), on a live server: what a migration leaves is what
/// <c>EnsureCreated()</c> builds for the same model, by the catalog's own text, and the
/// constraint refuses what it says it refuses.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SqlRawLiveTests
{
    private static (LiveDatabase Migrated, LiveDatabase Created) Both<TFrom, TTo>()
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        var migrated = new LiveDatabase();
        var created = new LiveDatabase();
        using (var context = Contexts.Of<TTo>(created.ConnectionString)) context.Database.EnsureCreated();

        migrated.Create<TFrom>();
        migrated.Migrate<TFrom, TTo>();
        return (migrated, created);
    }

    /// <summary>The key's SQL and the condition's reach the catalog, with each placeholder as its column.</summary>
    [TestMethod]
    public void An_index_with_SQL_written_by_hand_is_the_index_EnsureCreated_builds()
    {
        var (migrated, created) = Both<Models.Bare, SqlRawModels.Mixed>();
        using (migrated)
        using (created)
        {
            CollectionAssert.AreEqual(created.Indexes(), migrated.Indexes());
            CollectionAssert.AreEqual(
                new[] { """public.role_grants | CREATE INDEX ix_role_grant_mixed ON public.role_grants USING btree (upper("Note") DESC, "RoleId") WHERE (("GranteeId" > 0) AND ((("RoleId" % 2) = 0) OR ("Note" ~ '^x'::text)))""" },
                migrated.Indexes());
        }
    }

    /// <summary>The rebuild around a renamed column: the placeholder names the column as it is afterwards.</summary>
    [TestMethod]
    public void A_placeholder_follows_its_columns_rename()
    {
        var (migrated, created) = Both<SqlRawModels.Mixed, SqlRawModels.MixedWithTheNoteRenamed>();
        using (migrated)
        using (created)
        {
            CollectionAssert.AreEqual(created.Indexes(), migrated.Indexes());
            StringAssert.Contains(migrated.Indexes().Single(), "upper(memo)");
        }
    }

    /// <summary>An element that is SQL written by hand: the constraint is there, and refuses the second of two rows it says cannot both be.</summary>
    [TestMethod]
    public void An_exclusion_constraint_with_SQL_written_by_hand_refuses_what_it_says()
    {
        var (migrated, created) = Both<Models.Bare, SqlRawModels.ExclusionByHand>();
        using (migrated)
        using (created)
        {
            CollectionAssert.AreEqual(created.Exclusions(), migrated.Exclusions());
            Assert.HasCount(1, migrated.Exclusions());

            const string Insert = """INSERT INTO role_grants ("GranteeId", "RoleId", "Period", "Note") VALUES (1, 1, '[2026-01-01,2026-02-01)', """;
            migrated.Execute(Insert + "'Shift')");
            migrated.Execute(Insert + "'other')");

            var refused = Assert.ThrowsExactly<PostgresException>(() => migrated.Execute(Insert + "'SHIFT')"));
            Assert.AreEqual("23P01", refused.SqlState);
        }
    }
}
