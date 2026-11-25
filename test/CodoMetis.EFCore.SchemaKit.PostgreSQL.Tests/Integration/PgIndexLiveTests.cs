using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// The oracles of the plan's section 4 for indexes, on a live server: what the catalog holds
/// after each path that creates an index, whether the rows a unique index must refuse are
/// refused, whether a migration leaves the catalog <c>EnsureCreated()</c> would have built, and
/// whether the planner takes the index for the query EF Core writes.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class PgIndexLiveTests
{
    // As the server writes them back: the access method named, the schema, and its own parentheses.
    private const string Active =
        """public.role_grants | CREATE UNIQUE INDEX ux_role_grant_active ON public.role_grants USING btree ("GranteeId", "RoleId") INCLUDE ("Note") NULLS NOT DISTINCT WHERE ("RevokedOn" IS NULL)""";

    private const string ByNote =
        """public.role_grants | CREATE INDEX ix_role_grant_note ON public.role_grants USING btree (lower("Note"), "RevokedOn" DESC NULLS LAST)""";

    private static RoleGrant Grant(int grantee, int role, string? note = null, bool revoked = false)
        => new() { GranteeId = grantee, RoleId = role, Note = note, RevokedOn = revoked ? new DateOnly(2026, 2, 1) : null };

    // ── Every path that creates the indexes ──

    [TestMethod]
    public void EnsureCreated_creates_the_indexes()
    {
        using var database = new LiveDatabase();
        using var context = Contexts.Of<IndexModels.WithBoth>(database.ConnectionString);

        context.Database.EnsureCreated();

        CollectionAssert.AreEqual(new[] { ByNote, Active }, database.Indexes());
    }

    /// <summary>The script is applied, not read: one that lacks a terminator, or orders an index before its table, fails here.</summary>
    [TestMethod]
    public void The_create_script_creates_the_indexes()
    {
        using var database = new LiveDatabase();
        using var context = Contexts.Of<IndexModels.WithBoth>();

        database.Execute(context.Database.GenerateCreateScript());

        CollectionAssert.AreEqual(new[] { ByNote, Active }, database.Indexes());
    }

    [TestMethod]
    public void A_first_migration_creates_the_indexes()
    {
        using var database = new LiveDatabase();

        database.Create<IndexModels.WithBoth>();

        CollectionAssert.AreEqual(new[] { ByNote, Active }, database.Indexes());
    }

    [TestMethod]
    public void A_default_name_and_an_expression_reach_the_catalog()
    {
        using var database = new LiveDatabase();

        database.Create<IndexModels.Unnamed>();

        CollectionAssert.AreEqual(
            new[] { """public.role_grants | CREATE INDEX "IX_role_grants_GranteeId_lowerNote" ON public.role_grants USING btree ("GranteeId", lower("Note"))""" },
            database.Indexes());
    }

    // ── Behaviour: what must fail does, what must pass does ──

    private static void Save<TModel>(LiveDatabase database, object row) where TModel : ITestModel
    {
        using var context = Contexts.Of<TModel>(database.ConnectionString);
        context.Add(row);
        context.SaveChanges();
    }

    private static void AssertRefused<TModel>(LiveDatabase database, object row, string index) where TModel : ITestModel
    {
        var refusal = Assert.ThrowsExactly<DbUpdateException>(() => Save<TModel>(database, row));
        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, ((PostgresException)refusal.InnerException!).SqlState);
        Assert.AreEqual(index, ((PostgresException)refusal.InnerException!).ConstraintName);
    }

    [TestMethod]
    public void A_second_row_with_the_key_is_refused_and_rows_outside_the_condition_are_not()
    {
        using var database = new LiveDatabase();
        database.Create<IndexModels.WithActive>();

        Save<IndexModels.WithActive>(database, Grant(grantee: 1, role: 1));

        // The same grantee and role, not revoked: the one thing the index is for.
        AssertRefused<IndexModels.WithActive>(database, Grant(1, 1, note: "again"), IndexModels.ActiveName);

        // Each of these differs from the refused row in one thing.
        Save<IndexModels.WithActive>(database, Grant(2, 1));                  // another grantee
        Save<IndexModels.WithActive>(database, Grant(1, 2));                  // another role
        Save<IndexModels.WithActive>(database, Grant(1, 1, revoked: true));   // revoked: outside the condition
        Save<IndexModels.WithActive>(database, Grant(1, 1, revoked: true));   // and a second one like it

        using var read = Contexts.Of<IndexModels.WithActive>(database.ConnectionString);
        Assert.AreEqual(5, read.Grants.Count());
    }

    /// <summary>
    /// <c>nullsDistinct: false</c> is the difference between these two: a second grant without a
    /// note is refused under it, and kept by an index that counts nulls as PostgreSQL does unasked.
    /// </summary>
    [TestMethod]
    public void Nulls_not_distinct_refuses_the_second_null_and_a_plain_unique_index_keeps_it()
    {
        using (var strict = new LiveDatabase())
        {
            strict.Create<IndexModels.OneNotePerGrantee>();
            Save<IndexModels.OneNotePerGrantee>(strict, Grant(1, 1));
            AssertRefused<IndexModels.OneNotePerGrantee>(strict, Grant(1, 2), "ux_note");
            Save<IndexModels.OneNotePerGrantee>(strict, Grant(1, 2, note: "a"));
        }

        using var lenient = new LiveDatabase();
        lenient.Create<IndexModels.OneNotePerGranteeWhereThereIsOne>();
        Save<IndexModels.OneNotePerGranteeWhereThereIsOne>(lenient, Grant(1, 1));
        Save<IndexModels.OneNotePerGranteeWhereThereIsOne>(lenient, Grant(1, 2));
        Save<IndexModels.OneNotePerGranteeWhereThereIsOne>(lenient, Grant(1, 3, note: "a"));
        AssertRefused<IndexModels.OneNotePerGranteeWhereThereIsOne>(lenient, Grant(1, 4, note: "a"), "ux_note");
    }

    /// <summary>
    /// Each table of a TPC hierarchy has its index, under a name of its own, and refuses its own
    /// second row: a stay and a hold of one room are in two tables, and both are kept.
    /// </summary>
    [TestMethod]
    public void On_the_root_of_TPC_each_table_refuses_its_own_second_row()
    {
        using var database = new LiveDatabase();
        database.Create<IndexModels.OneReservationPerRoom>();

        CollectionAssert.AreEqual(
            new[]
            {
                """public.holds | CREATE UNIQUE INDEX ux_reservation_room_holds ON public.holds USING btree ("RoomId")""",
                """public.reservations | CREATE UNIQUE INDEX ux_reservation_room ON public.reservations USING btree ("RoomId")""",
                """public.stays | CREATE UNIQUE INDEX ux_reservation_room_stays ON public.stays USING btree ("RoomId")""",
            },
            database.Indexes());

        Save<IndexModels.OneReservationPerRoom>(database, new Stay { RoomId = 1 });
        Save<IndexModels.OneReservationPerRoom>(database, new Hold { RoomId = 1 });
        Save<IndexModels.OneReservationPerRoom>(database, new Reservation { RoomId = 1 });
        AssertRefused<IndexModels.OneReservationPerRoom>(database, new Stay { RoomId = 1 }, "ux_reservation_room_stays");
        AssertRefused<IndexModels.OneReservationPerRoom>(database, new Hold { RoomId = 1 }, "ux_reservation_room_holds");
        AssertRefused<IndexModels.OneReservationPerRoom>(database, new Reservation { RoomId = 1 }, "ux_reservation_room");
    }

    // ── The planner takes the index for the query EF Core writes ──

    private static string Plan(LiveDatabase database, string query)
    {
        using var connection = database.Open();
        // An empty table is read in sequence whatever indexes it has; without that choice the plan says whether the index can serve the query.
        connection.Execute("SET enable_seqscan = off");
        return string.Join("\n", connection.Column("EXPLAIN " + query));
    }

    /// <summary>
    /// An index on an expression serves a query only when the query's expression is the index's,
    /// to PostgreSQL's parser. So the key is spelled as Npgsql spells the same lambda in a query,
    /// and here the server says they match.
    /// </summary>
    [TestMethod]
    public void The_expression_index_serves_the_query_EF_Core_writes_for_the_same_lambda()
    {
        using var database = new LiveDatabase();
        database.Create<IndexModels.WithByNote>();
        using var context = Contexts.Of<IndexModels.WithByNote>(database.ConnectionString);

        var query = context.Grants.Where(x => x.Note!.ToLower() == "a").ToQueryString();
        StringAssert.Contains(Plan(database, query), IndexModels.ByNoteName, query);

        // The control: another expression over the same column is not served.
        var other = context.Grants.Where(x => x.Note!.ToUpper() == "A").ToQueryString();
        Assert.IsFalse(Plan(database, other).Contains(IndexModels.ByNoteName, StringComparison.Ordinal), other);
    }

    /// <summary>A partial index serves the query whose condition implies the index's, as EF Core writes it.</summary>
    [TestMethod]
    public void The_partial_index_serves_the_query_EF_Core_writes_for_the_same_condition()
    {
        using var database = new LiveDatabase();
        database.Create<IndexModels.WithActive>();
        using var context = Contexts.Of<IndexModels.WithActive>(database.ConnectionString);

        var query = context.Grants.Where(x => x.GranteeId == 1 && x.RoleId == 2 && x.RevokedOn == null).ToQueryString();
        StringAssert.Contains(Plan(database, query), IndexModels.ActiveName, query);

        var all = context.Grants.Where(x => x.GranteeId == 1 && x.RoleId == 2).ToQueryString();
        Assert.IsFalse(Plan(database, all).Contains(IndexModels.ActiveName, StringComparison.Ordinal), all);
    }

    // ── Catalog equivalence: a migration leaves what EnsureCreated would have built ──

    private static void AssertMigratesTo<TFrom, TTo>(Action<LiveDatabase, LiveDatabase>? also = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var migrated = new LiveDatabase();
        using var created = new LiveDatabase();
        using (var context = Contexts.Of<TTo>(created.ConnectionString)) context.Database.EnsureCreated();

        migrated.Create<TFrom>();
        var before = migrated.Indexes();
        migrated.Migrate<TFrom, TTo>();

        CollectionAssert.AreEqual(created.Indexes(), migrated.Indexes(), "Before the migration: " + string.Join("\n", before));
        also?.Invoke(migrated, created);
    }

    [TestMethod]
    public void Adding_indexes_to_a_table_that_exists()
        => AssertMigratesTo<Models.Bare, IndexModels.WithBoth>((migrated, _) => CollectionAssert.AreEqual(new[] { ByNote, Active }, migrated.Indexes()));

    [TestMethod]
    public void Dropping_an_index()
        => AssertMigratesTo<IndexModels.WithBoth, IndexModels.WithByNote>((migrated, _) => CollectionAssert.AreEqual(new[] { ByNote }, migrated.Indexes()));

    /// <summary>In place: the index that is there afterwards is the one that was there before.</summary>
    [TestMethod]
    public void Renaming_an_index_keeps_the_index()
    {
        using var database = new LiveDatabase();
        database.Create<IndexModels.WithActive>();
        var oid = database.IndexOid(IndexModels.ActiveName);

        database.Migrate<IndexModels.WithActive, IndexModels.ActiveRenamed>();

        Assert.AreEqual(oid, database.IndexOid("ux_renamed"));
        CollectionAssert.AreEqual(new[] { Active.Replace(IndexModels.ActiveName, "ux_renamed") }, database.Indexes());
    }

    /// <summary>The same on a table in a schema of its own, where the statement has to say which index it means.</summary>
    [TestMethod]
    public void Renaming_an_index_in_a_schema_of_its_own_keeps_the_index()
    {
        using var database = new LiveDatabase();
        database.Create<IndexModels.ActiveInASchema>();
        var oid = database.IndexOid(IndexModels.ActiveName);

        database.Migrate<IndexModels.ActiveInASchema, IndexModels.ActiveRenamedInASchema>();

        Assert.AreEqual(oid, database.IndexOid("ux_renamed"));
        CollectionAssert.AreEqual(new[] { Active.Replace(IndexModels.ActiveName, "ux_renamed").Replace("public.", "grants.") }, database.Indexes());
    }

    /// <summary>Each of these is another index under the same name, and the catalog's text says which.</summary>
    [TestMethod]
    public void Changing_what_an_index_is()
    {
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveNotUnique>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { Active.Replace("UNIQUE ", "").Replace(" NULLS NOT DISTINCT", "") }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveWithDistinctNulls>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { Active.Replace(" NULLS NOT DISTINCT", "") }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveWithoutTheNote>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { Active.Replace(""" INCLUDE ("Note")""", "") }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveWithoutACondition>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { Active.Replace(""" WHERE ("RevokedOn" IS NULL)""", "") }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveTheOtherWayRound>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { Active.Replace("""("GranteeId", "RoleId")""", """("RoleId", "GranteeId")""") }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveDescending>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { Active.Replace("""("GranteeId", "RoleId")""", """("GranteeId" DESC, "RoleId")""") }, migrated.Indexes()));
    }

    /// <summary>
    /// The reason an order PostgreSQL has anyway is not compared: the catalog holds one index for
    /// a key that says where its nulls go by default and one that does not. A migration between
    /// the two is empty, and the two databases are the same.
    /// </summary>
    [TestMethod]
    public void An_order_PostgreSQL_has_anyway_is_the_same_index_in_the_catalog()
    {
        AssertMigratesTo<IndexModels.Descending, IndexModels.DescendingNullsFirst>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { """public.role_grants | CREATE INDEX ix_revoked ON public.role_grants USING btree ("RevokedOn" DESC)""" }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.WithByNote, IndexModels.ByNoteWithItsDefaultsWritten>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { ByNote }, migrated.Indexes()));
        AssertMigratesTo<IndexModels.Descending, IndexModels.AscendingNullsFirst>((migrated, _) =>
            CollectionAssert.AreEqual(new[] { """public.role_grants | CREATE INDEX ix_revoked ON public.role_grants USING btree ("RevokedOn" NULLS FIRST)""" }, migrated.Indexes()));
    }

    // ── EF Core changes what the indexes read ──

    [TestMethod]
    public void Renaming_the_table_and_columns_under_the_indexes()
        => AssertMigratesTo<IndexModels.WithBoth, IndexModels.BothRenamedAround>((migrated, _) => Assert.AreEqual(2, migrated.Indexes().Count));

    [TestMethod]
    public void Renaming_a_column_a_default_name_is_built_from() => AssertMigratesTo<IndexModels.Unnamed, IndexModels.UnnamedRenamedAround>();

    [TestMethod]
    public void Moving_the_table_to_another_schema()
        => AssertMigratesTo<IndexModels.WithBoth, IndexModels.BothMoved>((migrated, _) => Assert.AreEqual(2, migrated.Indexes().Count));

    [TestMethod]
    public void Moving_the_table_of_a_default_named_index() => AssertMigratesTo<IndexModels.Unnamed, IndexModels.UnnamedMoved>();

    /// <summary>In another schema than the default one an index is dropped by its qualified name.</summary>
    [TestMethod]
    public void Dropping_the_indexes_of_a_table_in_a_schema_of_its_own()
        => AssertMigratesTo<IndexModels.BothMoved, Models.GrantsMovedUndeclared>((migrated, _) => Assert.AreEqual(0, migrated.Indexes().Count));

    /// <summary>
    /// The row of the impact table for an altered column, once for each way an index reads it: a
    /// key, an included column, a column of an expression, the condition's. PostgreSQL builds the
    /// index again by its definition, and the migration writes nothing for it.
    /// </summary>
    [TestMethod]
    public void Altering_the_type_of_a_column_an_index_reads()
    {
        AssertMigratesTo<IndexModels.WithBoth, IndexModels.BothWithALongGrantee>((migrated, _) => Assert.AreEqual(2, migrated.Indexes().Count, "a key"));
        AssertMigratesTo<IndexModels.WithBoth, IndexModels.BothWithAShortNote>((migrated, _) => Assert.AreEqual(2, migrated.Indexes().Count, "an included column, and an expression's"));
        AssertMigratesTo<IndexModels.ForGrantees, IndexModels.ForLongGrantees>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count, "the condition's"));
    }

    /// <summary>What the server does when a column is dropped by hand, with the index in place.</summary>
    private static string DroppingByHand<TModel>(string column) where TModel : ITestModel
    {
        using var database = new LiveDatabase();
        database.Create<TModel>();

        try
        {
            database.Execute($"ALTER TABLE role_grants DROP COLUMN \"{column}\";");
        }
        catch (PostgresException refused)
        {
            return $"refused {refused.SqlState}, {database.Indexes().Count} left";
        }

        return $"dropped, {database.Indexes().Count} left";
    }

    /// <summary>
    /// The measurement behind the rows of <c>PgImpact</c> for a dropped column: an index goes with
    /// any column it reads, in whatever way, and nothing is refused. That is not what an exclusion
    /// constraint does, which holds the column of an expression and of its condition.
    /// </summary>
    [TestMethod]
    public void The_server_drops_an_index_with_any_column_it_reads()
    {
        const string Gone = "dropped, 0 left";

        Assert.AreEqual(Gone, DroppingByHand<IndexModels.WithActive>("RoleId"), "a key");
        Assert.AreEqual(Gone, DroppingByHand<IndexModels.WithActive>("Note"), "an included column");
        Assert.AreEqual(Gone, DroppingByHand<IndexModels.WithActive>("RevokedOn"), "the condition's");
        Assert.AreEqual(Gone, DroppingByHand<IndexModels.WithByNote>("Note"), "in a key's expression");
        Assert.AreEqual(Gone, DroppingByHand<IndexModels.ByHandWrittenSql>("Note"), "read by SQL that names no column");
        Assert.AreEqual("dropped, 1 left", DroppingByHand<IndexModels.ByHandWrittenSql>("RoleId"), "not read at all");
    }

    /// <summary>So each migration writes no drop for the index, which is gone by then, and creates what is still declared.</summary>
    [TestMethod]
    public void Dropping_a_key_column() => AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveWithoutTheRole>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count));

    [TestMethod]
    public void Dropping_the_conditions_column() => AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveWithoutRevocation>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count));

    [TestMethod]
    public void Dropping_an_included_column() => AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveAndNoNote>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count));

    [TestMethod]
    public void Dropping_a_column_an_expression_reads() => AssertMigratesTo<IndexModels.WithByNote, IndexModels.ByNoteAndNoNote>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count));

    [TestMethod]
    public void Dropping_a_column_and_its_index_from_the_model()
        => AssertMigratesTo<IndexModels.WithByNote, IndexModels.NoNoteAndNothingDeclared>((migrated, _) => Assert.AreEqual(0, migrated.Indexes().Count));

    [TestMethod]
    public void Dropping_a_column_beside_SQL_written_by_hand()
        => AssertMigratesTo<IndexModels.ByHandWrittenSql, IndexModels.ByHandWrittenSqlWithoutTheRole>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count));

    // ── Names ──

    /// <summary>
    /// One index name in two schemas, and one of them dropped: the other is still there. Without
    /// the schema the statement drops whichever the search path finds first.
    /// </summary>
    [TestMethod]
    public void Dropping_an_index_leaves_its_namesake_in_another_schema()
        => AssertMigratesTo<IndexModels.TwoSchemas, IndexModels.TwoSchemasOneIndex>((migrated, _) =>
            CollectionAssert.AreEqual(
                new[] { """rooms.bookings | CREATE INDEX ix_period ON rooms.bookings USING btree ("RoomId")""" },
                migrated.Indexes()));

    /// <summary>No <c>IF NOT EXISTS</c>: an index of that name that is there already is a collision, and it fails instead of being taken for the one declared.</summary>
    [TestMethod]
    public void A_name_that_is_taken_fails_and_replaces_nothing()
    {
        using var database = new LiveDatabase();
        database.Create<Models.Bare>();
        database.Execute($"""CREATE INDEX {IndexModels.ActiveName} ON role_grants ("Note")""");

        var refusal = Assert.ThrowsExactly<PostgresException>(database.Migrate<Models.Bare, IndexModels.WithActive>);

        Assert.AreEqual(PostgresErrorCodes.DuplicateTable, refusal.SqlState);
        CollectionAssert.AreEqual(
            new[] { """public.role_grants | CREATE INDEX ux_role_grant_active ON public.role_grants USING btree ("Note")""" },
            database.Indexes());
    }

    /// <summary>EF Core creates an index under the name the declared one gives up, and the other way round.</summary>
    [TestMethod]
    public void An_index_of_EF_Cores_takes_the_name_a_renamed_index_gives_up()
    {
        AssertMigratesTo<IndexModels.WithActive, IndexModels.ActiveRenamedAndItsOldNameReused>((migrated, _) => Assert.AreEqual(2, migrated.Indexes().Count));
        AssertMigratesTo<IndexModels.ActiveRenamedAndItsOldNameReused, IndexModels.WithActive>((migrated, _) => Assert.AreEqual(1, migrated.Indexes().Count));
    }

    [TestMethod]
    public void A_TPC_hierarchy_gets_its_indexes_by_a_migration_as_by_EnsureCreated()
    {
        AssertMigratesTo<Models.ReservationsInThreeTables, IndexModels.OneReservationPerRoom>((migrated, _) => Assert.AreEqual(3, migrated.Indexes().Count));
        AssertMigratesTo<Models.ReservationsInThreeTables, IndexModels.OneReservationPerRoomWithoutAName>((migrated, _) => Assert.AreEqual(3, migrated.Indexes().Count));
        AssertMigratesTo<IndexModels.OneReservationPerRoom, Models.ReservationsInThreeTables>((migrated, _) => Assert.AreEqual(0, migrated.Indexes().Count));
    }
}
