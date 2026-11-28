using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;
using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// A condition a convention adds, coming from 5.x, on a live server (#249). Offline the snapshot
/// and the model go through one resolver, so "no operations" says only that the two agree with
/// each other. Whether the condition <c>Redeclare</c> and <c>AndWhere</c> wrote is the one 5.x's
/// <c>AddComplexIndexFilter</c> and <c>AddExclusionConstraintFilter</c> wrote into the database
/// is the catalog's to say: the database is built by the script the published 5.6.0 wrote.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class LegacyConventionLiveTests
{
    private static LiveDatabase BuiltBy5x()
    {
        var database = new LiveDatabase();
        using var connection = database.Open();
        foreach (var statement in LegacyFixture.ConventionScript) connection.Execute(statement);
        return database;
    }

    [TestMethod]
    public void A_database_built_by_5x_holds_what_the_model_with_the_convention_creates()
    {
        using var built = BuiltBy5x();
        using var created = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedConvention.Declared>(created.ConnectionString))
            context.Database.EnsureCreated();

        var indexes = created.Indexes();
        var constraints = created.Exclusions();
        // Nine indexes and the four constraints' own.
        Assert.HasCount(13, indexes);
        Assert.HasCount(4, constraints);
        CollectionAssert.AreEqual(indexes, built.Indexes());
        CollectionAssert.AreEqual(constraints, built.Exclusions());
    }

    /// <summary>The control: the catalog tells the model without the convention from the database 5.x built.</summary>
    [TestMethod]
    public void Without_the_convention_the_catalog_differs()
    {
        using var built = BuiltBy5x();
        using var created = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedConvention.DeclaredWithoutTheConvention>(created.ConnectionString))
            context.Database.EnsureCreated();

        CollectionAssert.AreNotEqual(created.Indexes(), built.Indexes());
        CollectionAssert.AreNotEqual(created.Exclusions(), built.Exclusions());
    }

    /// <summary>5.x's migration itself, applied by a context that has SchemaKit and no 5.x: one of its indexes is on an expression (#28).</summary>
    [TestMethod]
    public void The_migration_5x_scaffolded_leaves_what_its_script_built()
    {
        using var built = BuiltBy5x();
        using var replayed = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedConvention.Declared>(replayed.ConnectionString))
        {
            var commands = context.GetService<IMigrationsSqlGenerator>().Generate(LegacyFixture.ConventionUp);
            context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
        }

        CollectionAssert.AreEqual(built.Indexes(), replayed.Indexes());
        CollectionAssert.AreEqual(built.Exclusions(), replayed.Exclusions());
    }

    /// <summary>
    /// What the convention's condition is for: a row that is marked as deleted does not stand in
    /// the way of another, and a row that is not does. On the database 5.x built, and on the one
    /// the model declared here creates.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void A_deleted_row_is_outside_the_index_and_the_constraint_and_a_live_one_is_not(bool builtBy5x)
    {
        using var database = builtBy5x ? BuiltBy5x() : new LiveDatabase();
        if (!builtBy5x)
        {
            using var context = LegacyFixture.Context<MovedConvention.Declared>(database.ConnectionString);
            context.Database.EnsureCreated();
        }

        // Every other unique index and constraint is kept out of the way: each row has an owner, a
        // queue, a title and a label of its own. Two rows share the code, or the owner and the period.
        static string Ticket(int n, string code, int owner, string period, string? deletedOn)
            => $"""
                INSERT INTO tickets ("Code", "Title", "OwnerId", "QueueId", "Period", "ClosedOn", "Label_Name", "Label_Code", "DeletedOn")
                VALUES ('{code}', 't{n}', {owner}, {n}, '{period}', NULL, 'n{n}', 'c{n}', {(deletedOn is null ? "NULL" : $"'{deletedOn}'")})
                """;

        database.Execute(Ticket(1, "A", owner: 1, "[2026-01-01,2026-02-01)", deletedOn: "2026-01-15"));
        database.Execute(Ticket(2, "A", owner: 2, "[2026-01-01,2026-02-01)", deletedOn: null));
        var code = Assert.ThrowsExactly<PostgresException>(() => database.Execute(Ticket(3, "A", owner: 3, "[2026-01-01,2026-02-01)", deletedOn: null)));
        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, code.SqlState);
        Assert.AreEqual("ux_ticket_code", code.ConstraintName);

        database.Execute(Ticket(4, "B", owner: 1, "[2026-01-10,2026-01-20)", deletedOn: null));
        var period = Assert.ThrowsExactly<PostgresException>(() => database.Execute(Ticket(5, "C", owner: 2, "[2026-01-10,2026-01-20)", deletedOn: null)));
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, period.SqlState);
        Assert.AreEqual("ex_ticket_owner", period.ConstraintName);
    }
}
