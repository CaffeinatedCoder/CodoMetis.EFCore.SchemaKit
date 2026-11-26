using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;
using CodoMetis.EFCore.SchemaKit.Tests.Integration;
using Legacy5x.Indexes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// Coming from 5.x with indexes, on a live server (#218). The database is built by the script the
/// published 5.6.0 wrote with its own generator, statement for statement. "No operations" is right
/// only when that database holds what the model declared with SchemaKit says, and the catalog
/// decides that: offline both sides go through one resolver, and a default name or an expression
/// that drifted from 5.x's would still diff to nothing.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed partial class LegacyIndexHandoverLiveTests
{
    private static LiveDatabase BuiltBy5x()
    {
        var database = new LiveDatabase();
        using var connection = database.Open();
        foreach (var statement in LegacyFixture.IndexesScript) connection.Execute(statement);
        return database;
    }

    /// <summary>
    /// A storage parameter is kept as it was written: 5.x wrote <c>deduplicate_items=false</c>
    /// and its parameters in the order of their names, this package writes <c>off</c> and its own
    /// order. Both are one setting to the server, so the parameters are compared as a set and a
    /// flag by what it means.
    /// </summary>
    private static string WithItsParametersAsASet(string index)
        => StorageParameters().Replace(index, match => "WITH (" + string.Join(", ", match.Groups[1].Value.Split(", ")
            .Select(parameter => parameter.Replace("'false'", "off").Replace("'true'", "on").Replace("=false", "=off").Replace("=true", "=on").Replace("'", ""))
            .Order(StringComparer.Ordinal)) + ")");

    [GeneratedRegex(@"WITH \(([^)]*)\)")]
    private static partial Regex StorageParameters();

    [TestMethod]
    public void A_database_built_by_5x_holds_the_indexes_the_model_declared_here_creates()
    {
        using var built = BuiltBy5x();
        using var created = new LiveDatabase();
        using (var context = LegacyFixture.Context<MovedIndexes.Declared>(created.ConnectionString))
            context.Database.EnsureCreated();

        var expected = created.Indexes().Select(WithItsParametersAsASet).ToList();
        Assert.HasCount(32, expected);
        CollectionAssert.AreEqual(expected, built.Indexes().Select(WithItsParametersAsASet).ToList());
    }

    /// <summary>What the handover is for: an index 5.x built keeps refusing what it refused, under SchemaKit's model, and the first migration after the move finds nothing to do.</summary>
    [TestMethod]
    public void The_indexes_5x_built_enforce_what_the_model_declared_here_says()
    {
        using var built = BuiltBy5x();
        using var context = LegacyFixture.Context<MovedIndexes.Declared>(built.ConnectionString);

        static Account Of(string email, int level) => new()
        {
            Email = email, Nick = email, Title = email, Kind = email, GranteeId = email.Length + email[0], Contact = new Contact { Phone = email, City = "c" },
            Prefs = new Settings { Level = level, Theme = "t" }, Payload = new Settings { Theme = "p" }, CreatedAt = DateTime.UtcNow,
        };

        context.Add(Of("a", 1));
        context.SaveChanges();

        // The unique index on a member of the document, which 5.x created as an expression.
        context.Add(Of("b", 1));
        var refusal = (PostgresException)Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges()).InnerException!;
        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, refusal.SqlState);
        Assert.AreEqual("ux_account_level", refusal.ConstraintName);

        var differ = context.GetService<IMigrationsModelDiffer>();
        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(LegacyFixture.IndexesSnapshot(context), context.Relational()).Shape());
    }
}
