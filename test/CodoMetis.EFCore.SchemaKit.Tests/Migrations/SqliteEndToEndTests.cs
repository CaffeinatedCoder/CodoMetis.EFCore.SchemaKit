using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>
/// The one database a unit run always has. <c>EnsureCreated()</c> is judged by what the catalog
/// holds afterwards: a create script that looks right says nothing about whether it ran.
/// PostgreSQL and SQL Server get the same check when their integration lanes exist.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SqliteEndToEndTests
{
    private sealed class Live(DbContextOptions<Live> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>().DeclareTestObject("thing_name");
    }

    [TestMethod]
    public void Ensure_created_creates_the_declared_index()
    {
        var file = Path.Combine(Path.GetTempPath(), $"schemakit-{Guid.NewGuid():N}.db");
        try
        {
            using var context = new Live(new DbContextOptionsBuilder<Live>().Use(Provider.Sqlite, $"Data Source={file};Pooling=False").UseSchemaKit().Options);
            context.Database.EnsureCreated();

            var indexes = context.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Thing'")
                .ToList();

            CollectionAssert.Contains(indexes, "test_thing_name");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
