using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.Tests.Sql;

/// <summary>
/// What the statement builder writes, on the three providers. Expectations are built with the
/// provider's own <see cref="ISqlGenerationHelper" /> where the point is "as the provider does", and
/// spelled out where the point is that something was delimited or escaped at all.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaSqlTests
{
    private sealed class Plain(DbContextOptions<Plain> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private static Plain Context(Provider provider)
        => new(new DbContextOptionsBuilder<Plain>().Use(provider).UseSchemaKit().Options);

    private static string Only(SchemaSql sql) => sql.Operations.Single().Sql;

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_statement_delimits_as_the_provider_does_and_ends_with_its_terminator(Provider provider)
    {
        using var context = Context(provider);
        var dialect = context.GetService<ISchemaDialect>();
        var helper  = context.GetService<ISqlGenerationHelper>();
        var sql     = new SchemaSql(dialect);

        var index  = dialect.Identifier("ix people");
        var table  = new TableRef(dialect.Identifier("app"), dialect.Identifier("people"));
        var column = dialect.Identifier("Name");
        sql.Statement($"CREATE INDEX {index} ON {table} ({column})");

        Assert.AreEqual(
            $"CREATE INDEX {helper.DelimitIdentifier("ix people")} ON {helper.DelimitIdentifier("app")}.{helper.DelimitIdentifier("people")} ({helper.DelimitIdentifier("Name")}){helper.StatementTerminator}",
            Only(sql));
        StringAssert.Contains(Only(sql), provider == Provider.SqlServer ? "[ix people]" : "\"ix people\"");
        Assert.IsTrue(Only(sql).EndsWith(';'), "Every provider here ends a statement with a semicolon.");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_name_cannot_break_out_of_its_delimiters(Provider provider)
    {
        using var context = Context(provider);
        var dialect = context.GetService<ISchemaDialect>();
        var sql     = new SchemaSql(dialect);

        var hostile = dialect.Identifier(provider == Provider.SqlServer ? "x]; DROP TABLE t; --" : "x\"; DROP TABLE t; --");
        sql.Statement($"CREATE INDEX {hostile} ON t (c)");

        StringAssert.Contains(Only(sql), provider == Provider.SqlServer ? "[x]]; DROP TABLE t; --]" : "\"x\"\"; DROP TABLE t; --\"");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_qualified_name_carries_its_schema_and_a_default_schema_carries_none(Provider provider)
    {
        using var context = Context(provider);
        var dialect = context.GetService<ISchemaDialect>();
        var helper  = context.GetService<ISqlGenerationHelper>();
        var sql     = new SchemaSql(dialect);

        var index   = dialect.Identifier("ix");
        var tenant  = new TableRef(dialect.Identifier("tenant"), dialect.Identifier("things"));
        var @default = new TableRef(null, dialect.Identifier("things"));
        sql.Statement($"DROP INDEX {tenant.Qualify(index)}");
        sql.Statement($"DROP INDEX {@default.Qualify(index)}");

        Assert.AreEqual($"DROP INDEX {helper.DelimitIdentifier("tenant")}.{helper.DelimitIdentifier("ix")};", sql.Operations[0].Sql);
        Assert.AreEqual($"DROP INDEX {helper.DelimitIdentifier("ix")};", sql.Operations[1].Sql);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_literal_is_escaped_by_the_provider(Provider provider)
    {
        using var context = Context(provider);
        var sql = new SchemaSql(context.GetService<ISchemaDialect>());

        sql.Statement($"COMMENT ON TABLE t IS {sql.Literal("it's; DROP TABLE t")}");

        StringAssert.Contains(Only(sql), provider == Provider.SqlServer ? "N'it''s; DROP TABLE t'" : "'it''s; DROP TABLE t'");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Fragments_lists_names_operators_and_numbers_compose(Provider provider)
    {
        using var context = Context(provider);
        var dialect = context.GetService<ISchemaDialect>();
        var helper  = context.GetService<ISqlGenerationHelper>();
        var sql     = new SchemaSql(dialect);

        var a = dialect.Identifier("A");
        var b = dialect.Identifier("B");
        var element = sql.Fragment($"{a} WITH {dialect.Operator("&&")}");
        var include = sql.List([a, b]);
        var both    = sql.List([element, sql.Fragment($"{b} {dialect.Name("jsonb_path_ops")}")]);
        sql.Statement($"X ({both}) INCLUDE ({include}) WITH (fillfactor = {70}, pages = {123456789012L}) COLLATE {dialect.Name("C")}");

        var qa = helper.DelimitIdentifier("A");
        var qb = helper.DelimitIdentifier("B");
        Assert.AreEqual(
            $"X ({qa} WITH &&, {qb} jsonb_path_ops) INCLUDE ({qa}, {qb}) WITH (fillfactor = 70, pages = 123456789012) COLLATE {helper.DelimitIdentifier("C")};",
            Only(sql));
    }

    [TestMethod]
    public void The_transaction_requirement_is_carried()
    {
        using var context = Context(Provider.Npgsql);
        var sql = new SchemaSql(context.GetService<ISchemaDialect>());

        sql.Statement($"CREATE INDEX CONCURRENTLY x ON t (c)", suppressTransaction: true);
        sql.Statement($"CREATE INDEX y ON t (c)");

        Assert.IsTrue(sql.Operations[0].SuppressTransaction);
        Assert.IsFalse(sql.Operations[1].SuppressTransaction);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_statement_that_brings_its_own_terminator_is_refused(Provider provider)
    {
        using var context = Context(provider);
        var sql = new SchemaSql(context.GetService<ISchemaDialect>());

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => sql.Statement($"CREATE INDEX x ON t (c);"));

        StringAssert.Contains(error.Message, "appends the provider's statement terminator");
        Assert.IsEmpty(sql.Operations);
    }

    [TestMethod]
    public void An_empty_statement_is_refused()
    {
        using var context = Context(Provider.Sqlite);
        var sql = new SchemaSql(context.GetService<ISchemaDialect>());

        Assert.ThrowsExactly<InvalidOperationException>(() => sql.Statement($"  "));
    }

    [TestMethod]
    [DataRow("DROP INDEX IF EXISTS x")]
    [DataRow("ALTER TABLE t DROP CONSTRAINT IF EXISTS c")]
    [DataRow("drop trigger if  exists tr ON t")]
    public void A_drop_that_cannot_fail_is_refused(string statement)
    {
        using var context = Context(Provider.Npgsql);
        var sql = new SchemaSql(context.GetService<ISchemaDialect>());

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Add(sql, statement));

        StringAssert.Contains(error.Message, "IF EXISTS");
    }

    [TestMethod]
    [DataRow("DROP INDEX x")]
    [DataRow("CREATE EXTENSION IF NOT EXISTS btree_gist")]
    [DataRow("ALTER TABLE t ADD CONSTRAINT drop_if_exists_c CHECK (c > 0)")]
    public void Other_statements_are_not_mistaken_for_one(string statement)
    {
        using var context = Context(Provider.Npgsql);
        var sql = new SchemaSql(context.GetService<ISchemaDialect>());

        Add(sql, statement);

        Assert.AreEqual(statement + ";", Only(sql));
    }

    // Test-only: the text of a statement as one literal. A contributor cannot do this, having no hole
    // for a string; here the handler is driven by hand.
    private static void Add(SchemaSql sql, string text)
    {
        var handler = new SqlHandler(text.Length, 0, sql);
        handler.AppendLiteral(text);
        sql.Statement(ref handler);
    }
}
