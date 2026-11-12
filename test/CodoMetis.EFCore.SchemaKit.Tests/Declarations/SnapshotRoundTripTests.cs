using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Tests.Declarations;

public sealed class SnapshotContext(DbContextOptions<SnapshotContext> options) : DbContext(options)
{
    public static readonly TestGuard[] Declared =
    [
        new TestGuard.Unique([new GuardKey("Name", Descending: true)])
        {
            Name = "ux \"quoted\" \\ name",
            NullsDistinct = false,
            Include = ["Id"],
            // With what the runtime's own encoders escape and the stored form writes as itself: a
            // control character on each side of U+0020, a line separator, a code point no Unicode version has
            // assigned, one that .NET 10 was the first to know, and one outside the BMP.
            Where = new GuardFilter("{Name} <> 'it''s' AND {Id} > 0\n-- a second line \u0001\u007F\u2028\u0378\uA7DB\U0001F600"),
            Concurrently = true,
        },
        new TestGuard.Unique([new GuardKey("Id")]) { FillFactor = 0 },
        new TestGuard.Trigger("Name") { Epoch = 2 },
    ];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Thing>().Metadata.Declare(Declared);
}

/// <summary>
/// What a consumer's next diff reads is not the model but the model snapshot. So the round trip
/// that counts goes through one (<see cref="Snapshots" />), and the declarations are read from the
/// model it builds.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SnapshotRoundTripTests
{
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Declarations_read_from_a_compiled_snapshot_are_the_values_that_were_declared(Provider provider)
    {
        using var context = new SnapshotContext(new DbContextOptionsBuilder<SnapshotContext>().UseProvider(provider).UseSchemaKit().Options);
        var declared = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Thing))!;

        var (model, code) = Snapshots.Of(context);
        var snapshot = model.FindEntityType(typeof(Thing).FullName!)!;

        CollectionAssert.AreEquivalent(SnapshotContext.Declared, declared.GetDeclarations<TestGuard>().ToList());
        CollectionAssert.AreEqual(declared.GetDeclarations<TestGuard>().ToList(), snapshot.GetDeclarations<TestGuard>().ToList(), code);

        // One HasAnnotation per declaration, under the key the model has.
        foreach (var annotation in declared.GetAnnotations().Where(a => a.Name.StartsWith("CodoMetis:Test.Guard:", StringComparison.Ordinal)))
        {
            StringAssert.Contains(code, $"HasAnnotation(\"{annotation.Name}\"", code);
            Assert.AreEqual(annotation.Value, snapshot.FindAnnotation(annotation.Name)?.Value);
        }
        Assert.AreEqual(3, snapshot.GetAnnotations().Count(a => a.Name.StartsWith("CodoMetis:Test.Guard:", StringComparison.Ordinal)));
    }
}
