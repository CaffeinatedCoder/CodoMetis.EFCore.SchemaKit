using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.Tests.Integration;

/// <summary>
/// Statements from the builder, run on a live PostgreSQL server and judged by its catalog. What a
/// statement's text looks like says nothing about what it does: the prototype's unqualified
/// <c>DROP INDEX</c> read correctly and removed another schema's index.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class PostgresStatementTests
{
    private sealed class Plain(DbContextOptions<Plain> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private NpgsqlConnection _server = null!;
    private Plain _context = null!;
    private ISchemaDialect _dialect = null!;
    private string _suffix = null!;

    // One index name in two schemas: the shape the rule is about.
    private Identifier Index => _dialect.Identifier($"ix_{_suffix}");
    private TableRef Tenant => new(_dialect.Identifier($"tenant_{_suffix}"), _dialect.Identifier("things"));
    private TableRef Public => new(_dialect.Identifier("public"), _dialect.Identifier($"things_{_suffix}"));

    [TestInitialize]
    public void Create_the_two_tables()
    {
        _server  = PostgresServer.Open();
        _suffix  = Guid.NewGuid().ToString("N")[..12];
        // The context is here for its services only (the dialect); the statements run on _server.
        _context = new Plain(new DbContextOptionsBuilder<Plain>().Use(Provider.Npgsql).UseSchemaKit().Options);
        _dialect = _context.GetService<ISchemaDialect>();

        var setup = new SchemaSql(_dialect);
        setup.Statement($"CREATE SCHEMA {Tenant.Schema!}");
        setup.Statement($"CREATE TABLE {Tenant} (name text)");
        setup.Statement($"CREATE TABLE {Public} (name text)");
        setup.Statement($"CREATE INDEX {Index} ON {Tenant} (name)");
        setup.Statement($"CREATE INDEX {Index} ON {Public} (name)");

        // One batch, as a generated script is one text: it runs only if every statement is terminated.
        _server.Execute(string.Join("\n", setup.Operations.Select(o => o.Sql)));
        CollectionAssert.AreEquivalent(new[] { "public", Tenant.Schema!.Name }, SchemasHoldingTheIndex(), "The premise: the name exists in both schemas.");
    }

    [TestCleanup]
    public void Drop_them()
    {
        if (_server is null) return;
        _server.Execute($"DROP SCHEMA IF EXISTS \"tenant_{_suffix}\" CASCADE; DROP TABLE IF EXISTS public.\"things_{_suffix}\"");
        _context.Dispose();
        _server.Dispose();
    }

    private List<string> SchemasHoldingTheIndex()
        => _server.Column($"SELECT schemaname FROM pg_indexes WHERE indexname = 'ix_{_suffix}' ORDER BY schemaname");

    [TestMethod]
    public void A_qualified_drop_removes_the_index_of_the_schema_it_names()
    {
        var sql = new SchemaSql(_dialect);
        sql.Statement($"DROP INDEX {Tenant.Qualify(Index)}");

        _server.Execute(sql.Operations.Single().Sql);

        CollectionAssert.AreEqual(new[] { "public" }, SchemasHoldingTheIndex());
    }

    /// <summary>
    /// Why the rule exists, held as a fact about the server: the unqualified form resolves through
    /// <c>search_path</c> and removes the public schema's index, although the tenant's was meant.
    /// The statement succeeds, and the tenant's index is still there.
    /// </summary>
    [TestMethod]
    public void An_unqualified_drop_removes_another_schemas_index()
    {
        var sql = new SchemaSql(_dialect);
        sql.Statement($"DROP INDEX {Index}");

        _server.Execute(sql.Operations.Single().Sql);

        CollectionAssert.AreEqual(new[] { Tenant.Schema!.Name }, SchemasHoldingTheIndex());
    }
}
