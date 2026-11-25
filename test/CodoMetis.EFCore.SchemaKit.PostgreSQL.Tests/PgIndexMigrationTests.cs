using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// What a migration holds for an index, without a server: the statements and their place among
/// EF Core's operations. Whether they do what they say is
/// <see cref="Integration.PgIndexLiveTests" />'s, which applies each of these pairs.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PgIndexMigrationTests
{
    private const string CreateActive =
        """CREATE UNIQUE INDEX ux_role_grant_active ON role_grants ("GranteeId", "RoleId") INCLUDE ("Note") NULLS NOT DISTINCT WHERE "RevokedOn" IS NULL;""";

    private const string DropActive = "DROP INDEX ux_role_grant_active;";

    /// <summary>An expression is written in parentheses of its own, and an order only where it is not the direction's own.</summary>
    private const string CreateByNote =
        """CREATE INDEX ix_role_grant_note ON role_grants ((lower("Note")), "RevokedOn" DESC NULLS LAST);""";

    private const string DropByNote = "DROP INDEX ix_role_grant_note;";

    [TestMethod]
    public void The_create_script_creates_the_indexes_after_their_table()
    {
        using var context = Contexts.Of<IndexModels.WithBoth>();
        var script = context.Database.GenerateCreateScript();

        StringAssert.Contains(script, CreateActive);
        StringAssert.Contains(script, CreateByNote);
        Assert.IsTrue(script.IndexOf("CREATE TABLE role_grants", StringComparison.Ordinal) < script.IndexOf(CreateActive, StringComparison.Ordinal), script);
        Assert.IsFalse(script.Contains("IF NOT EXISTS", StringComparison.Ordinal), script);
    }

    /// <summary>One statement each, and nothing in EF Core's own index metadata: no <c>CreateIndexOperation</c>.</summary>
    [TestMethod]
    public void A_new_index_is_one_statement()
        => CollectionAssert.AreEqual(new[] { CreateActive, CreateByNote }, Contexts.Diff<Models.Bare, IndexModels.WithBoth>().Shape());

    /// <summary>A drop names the index and says no <c>IF EXISTS</c>: one that is gone already is a database that differs from the snapshot.</summary>
    [TestMethod]
    public void An_index_no_longer_declared_is_dropped_by_name()
        => CollectionAssert.AreEqual(new[] { DropActive }, Contexts.Diff<IndexModels.WithBoth, IndexModels.WithByNote>().Shape());

    [TestMethod]
    public void A_model_against_itself_is_no_operation()
        => Assert.AreEqual(0, Contexts.Diff<IndexModels.WithBoth, IndexModels.WithBoth>().Count);

    [TestMethod]
    public void Another_name_is_a_rename_in_place()
        => CollectionAssert.AreEqual(
            new[] { "ALTER INDEX ux_role_grant_active RENAME TO ux_renamed;" },
            Contexts.Diff<IndexModels.WithActive, IndexModels.ActiveRenamed>().Shape());

    /// <summary>The index is found by its schema, and the new name has none: a rename leaves an index in the schema it is in.</summary>
    [TestMethod]
    public void A_rename_in_a_schema_names_the_schema_once()
        => CollectionAssert.AreEqual(
            new[] { "ALTER INDEX grants.ux_role_grant_active RENAME TO ux_renamed;" },
            Contexts.Diff<IndexModels.ActiveInASchema, IndexModels.ActiveRenamedInASchema>().Shape());

    /// <summary>
    /// PostgreSQL changes nothing about an index that is declared here but its name. Whatever
    /// else differs under one name takes a drop and a create.
    /// </summary>
    [TestMethod]
    public void Anything_else_that_differs_is_a_rebuild()
    {
        static void Rebuilt<TTo>(string create) where TTo : ITestModel
            => CollectionAssert.AreEqual(new[] { DropActive, create }, Contexts.Diff<IndexModels.WithActive, TTo>().Shape(), typeof(TTo).Name);

        Rebuilt<IndexModels.ActiveNotUnique>(CreateActive.Replace("UNIQUE ", "").Replace(" NULLS NOT DISTINCT", ""));
        Rebuilt<IndexModels.ActiveWithDistinctNulls>(CreateActive.Replace(" NULLS NOT DISTINCT", ""));
        Rebuilt<IndexModels.ActiveWithoutTheNote>(CreateActive.Replace(""" INCLUDE ("Note")""", ""));
        Rebuilt<IndexModels.ActiveWithoutACondition>(CreateActive.Replace(""" WHERE "RevokedOn" IS NULL""", ""));
        Rebuilt<IndexModels.ActiveTheOtherWayRound>(CreateActive.Replace("""("GranteeId", "RoleId")""", """("RoleId", "GranteeId")"""));
        Rebuilt<IndexModels.ActiveDescending>(CreateActive.Replace("""("GranteeId", "RoleId")""", """("GranteeId" DESC, "RoleId")"""));
    }

    /// <summary>
    /// An ascending key has its nulls last and a descending one first, and the catalog says
    /// neither. A key that says so is the same index: no statement, either way.
    /// </summary>
    [TestMethod]
    public void An_order_PostgreSQL_has_anyway_is_not_another_index()
    {
        Assert.AreEqual(0, Contexts.Diff<IndexModels.WithByNote, IndexModels.ByNoteWithItsDefaultsWritten>().Count);
        Assert.AreEqual(0, Contexts.Diff<IndexModels.ByNoteWithItsDefaultsWritten, IndexModels.WithByNote>().Count);
        Assert.AreEqual(0, Contexts.Diff<IndexModels.Descending, IndexModels.DescendingNullsFirst>().Count);

        CollectionAssert.AreEqual(
            new[] { """CREATE INDEX ix_revoked ON role_grants ("RevokedOn" DESC);""" },
            Contexts.Diff<Models.Bare, IndexModels.DescendingNullsFirst>().Shape());

        CollectionAssert.AreEqual(
            new[] { "DROP INDEX ix_revoked;", """CREATE INDEX ix_revoked ON role_grants ("RevokedOn" NULLS FIRST);""" },
            Contexts.Diff<IndexModels.Descending, IndexModels.AscendingNullsFirst>().Shape());
    }

    /// <summary>
    /// The default name is EF Core's for an index, and 5.x's: <c>IX</c>, the table, then a
    /// column's name and an expression's letters and digits. That an index is unique is not in it.
    /// </summary>
    [TestMethod]
    public void An_index_without_a_name_gets_the_default_one()
    {
        CollectionAssert.AreEqual(
            new[] { """CREATE INDEX "IX_role_grants_GranteeId" ON role_grants ("GranteeId");""" },
            Contexts.Diff<Models.Bare, IndexModels.OnGrantee>().Shape());

        CollectionAssert.AreEqual(
            new[] { """CREATE INDEX "IX_role_grants_GranteeId_lowerNote" ON role_grants ("GranteeId", (lower("Note")));""" },
            Contexts.Diff<Models.Bare, IndexModels.Unnamed>().Shape());
    }

    // ── EF Core changes what the indexes read ──

    /// <summary>A rename of the table and of a column of every kind leaves both indexes alone: PostgreSQL rewrites what reads them.</summary>
    [TestMethod]
    public void Renames_under_an_index_are_followed()
    {
        var shape = Contexts.Diff<IndexModels.WithBoth, IndexModels.BothRenamedAround>().Shape();

        Assert.IsFalse(shape.Any(operation => operation.Contains("INDEX", StringComparison.Ordinal)), string.Join("\n", shape));
        Assert.AreEqual(3, shape.Count(operation => operation == "RenameColumnOperation"), string.Join("\n", shape));
    }

    /// <summary>The default name is built from the column's name, so a renamed column is one rename of the index, in place.</summary>
    [TestMethod]
    public void A_renamed_column_renames_a_default_named_index()
        => CollectionAssert.AreEqual(
            new[] { "RenameColumnOperation", """ALTER INDEX "IX_role_grants_GranteeId_lowerNote" RENAME TO "IX_role_grants_grantee_lowerNote";""" },
            Contexts.Diff<IndexModels.Unnamed, IndexModels.UnnamedRenamedAround>().Shape());

    /// <summary>An index moves with its table, and is named in the schema it is in by then.</summary>
    [TestMethod]
    public void A_table_that_moves_takes_its_indexes_along()
    {
        CollectionAssert.AreEqual(
            new[] { "EnsureSchemaOperation", "RenameTableOperation" },
            Contexts.Diff<IndexModels.WithBoth, IndexModels.BothMoved>().Shape());

        CollectionAssert.AreEqual(
            new[] { "DROP INDEX grants.ix_role_grant_note;", "DROP INDEX grants.ux_role_grant_active;" },
            Contexts.Diff<IndexModels.BothMoved, Models.GrantsMovedUndeclared>().Shape());
    }

    [TestMethod]
    public void An_altered_column_leaves_the_indexes_alone()
    {
        CollectionAssert.AreEqual(new[] { "AlterColumnOperation" }, Contexts.Diff<IndexModels.WithBoth, IndexModels.BothWithALongGrantee>().Shape());
        CollectionAssert.AreEqual(new[] { "AlterColumnOperation" }, Contexts.Diff<IndexModels.WithBoth, IndexModels.BothWithAShortNote>().Shape());
        CollectionAssert.AreEqual(new[] { "AlterColumnOperation" }, Contexts.Diff<IndexModels.ForGrantees, IndexModels.ForLongGrantees>().Shape());
    }

    /// <summary>
    /// PostgreSQL drops an index with any column it reads. So no drop is written, which would
    /// fail on an index that is gone, and what the model still declares is created again.
    /// </summary>
    [TestMethod]
    public void A_dropped_column_takes_the_index_with_it_and_what_is_still_declared_is_created_again()
    {
        CollectionAssert.AreEqual(
            new[] { "DropColumnOperation", """CREATE UNIQUE INDEX ux_role_grant_active ON role_grants ("GranteeId") INCLUDE ("Note") NULLS NOT DISTINCT WHERE "RevokedOn" IS NULL;""" },
            Contexts.Diff<IndexModels.WithActive, IndexModels.ActiveWithoutTheRole>().Shape(),
            "a key");

        CollectionAssert.AreEqual(
            new[] { "DropColumnOperation", CreateActive.Replace(""" WHERE "RevokedOn" IS NULL""", "") },
            Contexts.Diff<IndexModels.WithActive, IndexModels.ActiveWithoutRevocation>().Shape(),
            "the condition's");

        CollectionAssert.AreEqual(
            new[] { "DropColumnOperation", CreateActive.Replace(""" INCLUDE ("Note")""", "") },
            Contexts.Diff<IndexModels.WithActive, IndexModels.ActiveAndNoNote>().Shape(),
            "an included one");

        CollectionAssert.AreEqual(
            new[] { "DropColumnOperation", """CREATE INDEX ix_role_grant_note ON role_grants ("RevokedOn" DESC NULLS LAST);""" },
            Contexts.Diff<IndexModels.WithByNote, IndexModels.ByNoteAndNoNote>().Shape(),
            "an expression's");

        CollectionAssert.AreEqual(
            new[] { DropByNote, "DropColumnOperation" },
            Contexts.Diff<IndexModels.WithByNote, IndexModels.NoNoteAndNothingDeclared>().Shape(),
            "and an index nothing declares any more is dropped first, as any other");
    }

    /// <summary>
    /// SQL written by hand names no column, so nothing says whether it reads the one that goes.
    /// The index is taken out of the way and created again: were it read, the create fails.
    /// </summary>
    [TestMethod]
    public void A_dropped_column_under_SQL_written_by_hand_is_rebuilt_around()
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX ix_by_hand;", "DropColumnOperation", """CREATE INDEX ix_by_hand ON role_grants ((upper("Note")));""" },
            Contexts.Diff<IndexModels.ByHandWrittenSql, IndexModels.ByHandWrittenSqlWithoutTheRole>().Shape());

    // ── Names ──

    /// <summary>
    /// An index is a relation of its schema, as EF Core's own indexes are, and the default name
    /// is the one EF Core gives its own: declared both ways, one index is refused as two.
    /// </summary>
    [TestMethod]
    public void An_index_under_the_name_of_one_of_EF_Cores_is_a_collision()
    {
        using var context = Contexts.Of<IndexModels.OnGranteeBesideEfCoresOwn>();

        var refusal = Assert.ThrowsExactly<SchemaValidationException>(() => context.Database.GenerateCreateScript());

        Assert.AreEqual("SK1001", refusal.Diagnostics.Single().Id);
        StringAssert.Contains(refusal.Message, "IX_role_grants_GranteeId");
    }

    /// <summary>A rename in place runs after EF Core's operations, where the old name is taken by then: a drop before and a create after.</summary>
    [TestMethod]
    public void A_rename_whose_old_name_EF_Core_takes_is_a_rebuild()
        => CollectionAssert.AreEqual(
            new[] { DropActive, "CreateIndexOperation", CreateActive.Replace(IndexModels.ActiveName, "ux_renamed") },
            Contexts.Diff<IndexModels.WithActive, IndexModels.ActiveRenamedAndItsOldNameReused>().Shape());

    /// <summary>One name in two schemas is two indexes, and each is dropped by its schema.</summary>
    [TestMethod]
    public void A_drop_names_the_schema()
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX grants.ix_period;" },
            Contexts.Diff<IndexModels.TwoSchemas, IndexModels.TwoSchemasOneIndex>().Shape());

    // ── A table per type ──

    /// <summary>
    /// Declared on the root of a TPC hierarchy, the index is in every table of it: a unique index
    /// of one table says nothing about another's rows. A stated name is a relation's, taken in
    /// the whole schema, so a derived type's copy has its table behind it.
    /// </summary>
    [TestMethod]
    public void On_the_root_of_TPC_every_table_gets_its_index()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                """CREATE UNIQUE INDEX ux_reservation_room ON reservations ("RoomId");""",
                """CREATE UNIQUE INDEX ux_reservation_room_stays ON stays ("RoomId");""",
                """CREATE UNIQUE INDEX ux_reservation_room_holds ON holds ("RoomId");""",
            },
            Contexts.Diff<Models.ReservationsInThreeTables, IndexModels.OneReservationPerRoom>().Shape());

        CollectionAssert.AreEquivalent(
            new[]
            {
                """CREATE UNIQUE INDEX "IX_reservations_RoomId" ON reservations ("RoomId");""",
                """CREATE UNIQUE INDEX "IX_stays_RoomId" ON stays ("RoomId");""",
                """CREATE UNIQUE INDEX "IX_holds_RoomId" ON holds ("RoomId");""",
            },
            Contexts.Diff<Models.ReservationsInThreeTables, IndexModels.OneReservationPerRoomWithoutAName>().Shape());
    }
}
