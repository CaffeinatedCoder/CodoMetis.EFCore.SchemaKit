using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// SQL written by hand in a lambda, <see cref="Sql.Raw{T}" /> (issue 220), as a consumer declares
/// it: what is stored, what the migration holds, and that it is the object the contract's own
/// template makes, which is what a 5.x snapshot is read into. Whether the statements do what
/// they say is <see cref="Integration.SqlRawLiveTests" />'s.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SqlRawDeclarationTests
{
    private static List<string> Stored<TModel>(string owner) where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>();
        return [.. context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(RoleGrant))!.GetAnnotations()
            .Where(a => a.Name.StartsWith($"CodoMetis:{owner}:", StringComparison.Ordinal))
            .Select(a => $"{a.Name} = {a.Value}")];
    }

    /// <summary>
    /// The stored form is a file format. SQL written by hand is <c>raw</c>, also as a part of a
    /// lambda, where the package's own text around it stays text.
    /// </summary>
    [TestMethod]
    public void The_stored_form_keeps_what_was_written_by_hand_apart()
    {
        var stored = Stored<SqlRawModels.Mixed>("Pg.Index").Single();

        StringAssert.EndsWith(
            stored,
            """ = {"$kind":"btree","v":3,"r":1,"name":"ix_role_grant_mixed","keys":[{"sql":[{"raw":"upper("},{"col":"Note"},{"raw":")"}],"desc":true},{"column":"RoleId","desc":false}],"include":[],"where":["(",{"col":"GranteeId"}," > 0 AND (",{"col":"RoleId"},{"raw":" % 2 = 0 OR "},{"col":"Note"},{"raw":" ~ '^x'"},"))"],"concurrently":false}""");
    }

    [TestMethod]
    public void The_SQL_reaches_the_statement_as_written()
        => CollectionAssert.AreEqual(
            new[] { """CREATE INDEX ix_role_grant_mixed ON role_grants ((upper("Note")) DESC, "RoleId") WHERE ("GranteeId" > 0 AND ("RoleId" % 2 = 0 OR "Note" ~ '^x'));""" },
            Contexts.Diff<Models.Bare, SqlRawModels.Mixed>().Shape());

    /// <summary>
    /// One object with what the contract's template declares, key and value: a model moved from
    /// 5.x compares with its snapshot through exactly this.
    /// </summary>
    [TestMethod]
    public void It_is_the_object_the_contracts_template_declares()
    {
        CollectionAssert.AreEqual(Stored<IndexModels.ByHandWrittenSql>("Pg.Index"), Stored<SqlRawModels.IndexByHand>("Pg.Index"));
        Assert.AreEqual(0, Contexts.Diff<IndexModels.ByHandWrittenSql, SqlRawModels.IndexByHand>().Count);
        Assert.AreEqual(0, Contexts.Diff<SqlRawModels.IndexByHand, IndexModels.ByHandWrittenSql>().Count);

        CollectionAssert.AreEqual(Stored<Models.WithTheGranteeBesideSqlThatNamesNoColumn>("Pg.Exclusion"), Stored<SqlRawModels.ExclusionByHand>("Pg.Exclusion"));
        Assert.AreEqual(0, Contexts.Diff<Models.WithTheGranteeBesideSqlThatNamesNoColumn, SqlRawModels.ExclusionByHand>().Count);
        Assert.AreEqual(0, Contexts.Diff<SqlRawModels.ExclusionByHand, Models.WithTheGranteeBesideSqlThatNamesNoColumn>().Count);
    }

    /// <summary>
    /// A model against its own snapshot is no migration: the snapshot holds text, the model a
    /// lambda that was bound to it.
    /// </summary>
    [TestMethod]
    public void A_model_and_its_snapshot_differ_in_nothing()
    {
        using var context = Contexts.Of<SqlRawModels.Mixed>();
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var snapshot = context.GetService<IModelRuntimeInitializer>()
            .Initialize(Snapshots.Of(context).Model, designTime: true, validationLogger: null)
            .GetRelationalModel();
        var differ = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsModelDiffer>();

        Assert.AreEqual(0, differ.GetDifferences(snapshot, model).Count);
        Assert.AreEqual(0, differ.GetDifferences(model, snapshot).Count);
        Assert.IsTrue(differ.GetDifferences(null, snapshot).Count > 0, "The snapshot holds the index.");
    }

    /// <summary>A placeholder follows a rename of its column, and the index is rebuilt around it, as for all SQL written by hand (#209).</summary>
    [TestMethod]
    public void A_renamed_column_is_followed_by_the_placeholder_and_rebuilds_the_index()
        => CollectionAssert.AreEqual(
            new[]
            {
                "DROP INDEX ix_role_grant_mixed;",
                "RenameColumnOperation",
                """CREATE INDEX ix_role_grant_mixed ON role_grants ((upper(memo)) DESC, "RoleId") WHERE ("GranteeId" > 0 AND ("RoleId" % 2 = 0 OR memo ~ '^x'));""",
            },
            Contexts.Diff<SqlRawModels.Mixed, SqlRawModels.MixedWithTheNoteRenamed>().Shape());

    /// <summary>
    /// Where a lambda becomes SQL, not at the first <c>migrations add</c>: the application's own
    /// model is refused too, which is the first thing a developer starts.
    /// </summary>
    [TestMethod]
    public void A_path_that_names_no_property_is_refused_when_the_model_is_finalized()
    {
        using var context = Contexts.Of<SqlRawModels.WithAPathThatNamesNothing>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);

        StringAssert.Contains(refused.Message, "Remark");
        StringAssert.Contains(refused.Message, "RoleGrant");
    }
}
