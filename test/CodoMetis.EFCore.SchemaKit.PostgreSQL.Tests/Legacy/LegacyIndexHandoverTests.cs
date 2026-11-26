using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// A project that comes from 5.x with indexes (#218): the snapshot the published 5.6.0 scaffolded
/// against the same model declared with SchemaKit. Offline both sides go through one resolver, so
/// this says the reader and the declarations agree; that they agree on what 5.x built is
/// <c>LegacyIndexHandoverLiveTests</c>, which applies 5.x's own script.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LegacyIndexHandoverTests
{
    [TestMethod]
    public void The_fixture_is_what_5x_wrote()
    {
        var snapshot = File.ReadAllText(Path.Combine(LegacyFixture.IndexesFolder, "IndexesContextModelSnapshot.cs"));

        Assert.AreEqual(2, snapshot.Split("\"CustomIndex:CompositeIndexes\"").Length - 1);
        Assert.AreEqual(3, snapshot.Split("\"CustomIndex:IsIndexed\"").Length - 1);
        StringAssert.Contains(snapshot, "\"CustomIndex:RenderingVersion\", 3");
        Assert.AreEqual(32, LegacyFixture.IndexesScript.Count(statement => statement.Contains(" INDEX ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Every index 5.x stored is read as the declaration that is the same object, on every table
    /// it is in: nothing to do, from the snapshot to the model and back.
    /// </summary>
    [TestMethod]
    public void The_5x_snapshot_against_the_same_model_declared_here_is_no_operations()
    {
        using var context = LegacyFixture.Context<MovedIndexes.Declared>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var (snapshot, model) = (LegacyFixture.IndexesSnapshot(context), context.Relational());

        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(snapshot, model).Shape());
        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(model, snapshot).Shape());
        Assert.IsFalse(differ.HasDifferences(snapshot, model));
    }

    /// <summary>
    /// The way back, which EF Core scaffolds as Down: from a model that declares nothing to the
    /// snapshot, each index is created as 5.x declared it, with how it is built as well, which no
    /// comparison of two indexes shows.
    /// </summary>
    [TestMethod]
    public void The_way_back_creates_each_index_as_5x_declared_it()
    {
        using var context = LegacyFixture.Context<MovedIndexes.NothingDeclared>();
        var creates = context.GetService<IMigrationsModelDiffer>().GetDifferences(context.Relational(), LegacyFixture.IndexesSnapshot(context)).Sql();

        Assert.AreEqual(32, creates.Count(sql => sql.Contains(" INDEX ", StringComparison.Ordinal)));
        CollectionAssert.Contains(creates, """CREATE INDEX CONCURRENTLY ix_account_rank ON accounts ("Rank");""");
        CollectionAssert.Contains(creates, """CREATE UNIQUE INDEX ux_account_nick ON accounts ("Nick" text_pattern_ops) NULLS NOT DISTINCT;""");
        CollectionAssert.Contains(creates, """CREATE INDEX "IX_accounts_Title_Nick" ON accounts ("Title" COLLATE "C", "Nick") INCLUDE ("Rank", "Contact_City") WITH (fillfactor = 70, deduplicate_items = off);""");
        CollectionAssert.Contains(creates, """CREATE UNIQUE INDEX ux_account_kind_title ON accounts (("Kind"), (upper("Title")) DESC NULLS LAST) WHERE "Rank" > 0;""");
        CollectionAssert.Contains(creates, """CREATE INDEX "IX_accounts_CreatedAt" ON accounts USING brin ("CreatedAt") WITH (pages_per_range = 16);""");
        CollectionAssert.Contains(creates, """CREATE INDEX "IX_accounts_coalesceNickKindTitle" ON accounts ((((coalesce("Nick", "Kind") || ' ') || "Title")));""");
        CollectionAssert.Contains(creates, """CREATE UNIQUE INDEX ux_account_phone ON accounts ("Contact_Phone") WHERE "DeletedOn" IS NULL;""");
    }

    /// <summary>
    /// SQL 5.x took as written, a filter or an expression, declared here as the lambda that becomes
    /// the same SQL: the same index, since a condition and a key compare by their SQL. The other
    /// way, the SQL kept with <c>Sql.Raw</c>, is how <see cref="MovedIndexes.Declared" /> says it (#220).
    /// </summary>
    [TestMethod]
    public void SQL_of_5x_declared_as_the_lambda_that_becomes_it_is_no_operations()
    {
        using var context = LegacyFixture.Context<MovedIndexes.DeclaredWithLambdasForTheSql>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var (snapshot, model) = (LegacyFixture.IndexesSnapshot(context), context.Relational());

        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(snapshot, model).Shape());
        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(model, snapshot).Shape());
    }

    /// <summary>
    /// 5.x's migration itself is not replayed where it holds an index only 5.x's generator
    /// rendered (#110, #28): the first of them is refused, by name.
    /// </summary>
    [TestMethod]
    public void The_migration_5x_scaffolded_is_refused_at_its_first_index_on_an_expression()
    {
        using var context = LegacyFixture.Context<MovedIndexes.Declared>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsSqlGenerator>()
            .Generate(LegacyFixture.IndexesUp, context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model));

        StringAssert.Contains(refused.Message, "CustomIndex:IndexParts");
    }

    /// <summary>
    /// The control, and what a consumer gets who has not declared anything yet: every index of the
    /// snapshot is seen and dropped, under the name the script of 5.x created it by.
    /// </summary>
    [TestMethod]
    public void Against_a_model_that_declares_nothing_every_index_of_the_snapshot_is_dropped_by_its_name()
    {
        using var context = LegacyFixture.Context<MovedIndexes.NothingDeclared>();
        var drops = context.GetService<IMigrationsModelDiffer>().GetDifferences(LegacyFixture.IndexesSnapshot(context), context.Relational()).Shape();

        // The names as 5.x's own generator wrote them, quoted or not.
        var created = LegacyFixture.IndexesScript
            .Where(statement => statement.Contains(" INDEX ", StringComparison.Ordinal))
            .Select(statement => statement.Split(" ON ")[0].Split(' ')[^1].Trim('"'))
            .Order(StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(
            created,
            drops.Select(drop => drop["DROP INDEX ".Length..].TrimEnd(';').Trim('"')).Order(StringComparer.Ordinal).ToList(),
            string.Join("\n", drops));
    }
}
