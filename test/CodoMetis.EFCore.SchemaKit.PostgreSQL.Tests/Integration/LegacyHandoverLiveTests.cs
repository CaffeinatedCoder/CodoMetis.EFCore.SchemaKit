using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Booking = Legacy5x.Booking;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// Coming from 5.x on a live server (#92, #18). The database is built by the migration 5.x
/// scaffolded, statement for statement. "No operations" is right only when that database holds
/// what the model declared with SchemaKit says, and the catalog decides that, not the differ.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class LegacyHandoverLiveTests
{
    /// <summary>Runs operations through the provider's generator, as <c>database update</c> does.</summary>
    private static void Apply(DbContext context, IReadOnlyList<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> operations)
    {
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.GetService<IDesignTimeModel>().Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
    }

    [TestMethod]
    public void A_database_built_by_5x_holds_what_the_model_declared_here_creates()
    {
        using var built = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedModels.Declared>(built.ConnectionString))
            Apply(context, LegacyFixture.Up);

        using var created = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedModels.Declared>(created.ConnectionString))
            context.Database.EnsureCreated();

        var expected = created.Exclusions();
        Assert.HasCount(5, expected);
        CollectionAssert.AreEqual(expected, built.Exclusions());

        // And the first migration after the move has nothing to do there.
        CollectionAssert.AreEqual(Array.Empty<string>(), LegacyFixture.DiffTo<MovedModels.Declared>().Shape());
    }

    /// <summary>What the handover is for: the constraints 5.x built keep refusing what they refused, under SchemaKit's model.</summary>
    [TestMethod]
    public void The_constraints_5x_built_still_refuse_an_overlap()
    {
        using var database = new LiveDatabase();
        using var context = LegacyFixture.Context<MovedModels.Declared>(database.ConnectionString);
        Apply(context, LegacyFixture.Up);

        context.AddRange(
            new Booking { RoomId = 1, During = Days(1, 10) },
            new Booking { RoomId = 2, During = Days(1, 10) });
        context.SaveChanges();

        context.Add(new Booking { RoomId = 1, During = Days(5, 15) });
        var refused = Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, ((PostgresException)refused.InnerException!).SqlState);
    }

    /// <summary>A rename and a rebuild written against the snapshot run on the database 5.x built, and leave what the model says.</summary>
    [TestMethod]
    public void A_change_made_with_the_move_applies_to_the_database_5x_built()
    {
        using var built = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedModels.OneRenamed>(built.ConnectionString))
        {
            Apply(context, LegacyFixture.Up);
            Apply(context, LegacyFixture.DiffTo<MovedModels.OneRenamed>());
        }

        using var created = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedModels.OneRenamed>(created.ConnectionString))
            context.Database.EnsureCreated();

        CollectionAssert.AreEqual(created.Exclusions(), built.Exclusions());
    }

    [TestMethod]
    public void A_constraint_the_model_no_longer_declares_is_dropped_from_the_database_5x_built()
    {
        using var database = new LiveDatabase();
        using var context = LegacyFixture.Context<MovedModels.NothingDeclared>(database.ConnectionString);
        Apply(context, LegacyFixture.Up);
        Assert.HasCount(5, database.Exclusions());

        Apply(context, LegacyFixture.DiffTo<MovedModels.NothingDeclared>());

        CollectionAssert.AreEqual(Array.Empty<string>(), database.Exclusions());
    }

    private static NpgsqlRange<DateOnly> Days(int from, int to)
        => new(new DateOnly(2026, 1, from), lowerBoundIsInclusive: true, new DateOnly(2026, 1, to), upperBoundIsInclusive: false);
}
