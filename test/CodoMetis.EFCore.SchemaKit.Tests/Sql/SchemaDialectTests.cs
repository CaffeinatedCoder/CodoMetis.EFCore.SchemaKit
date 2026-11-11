using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Tests.Sql;

/// <summary>The checks a value passes before it can be written into a statement.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaDialectTests
{
    private sealed class Plain(DbContextOptions<Plain> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    private static Plain Context(Provider provider)
        => new(new DbContextOptionsBuilder<Plain>().Use(provider).UseSchemaKit().Options);

    /// <summary>
    /// PostgreSQL shortens a longer name without an error, so two names can become one; SQL Server
    /// refuses it at apply. Either way the place to hear about it is here.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql, 63), DataRow(Provider.SqlServer, 128)]
    public void A_name_longer_than_the_database_keeps_is_refused(Provider provider, int limit)
    {
        using var context = Context(provider);
        var dialect = context.GetService<ISchemaDialect>();
        Assert.AreEqual(limit, context.GetService<IDesignTimeModel>().Model.GetMaxIdentifierLength(), "The premise: the limit the provider states.");

        Assert.AreEqual(limit, dialect.Identifier(new string('a', limit)).Name.Length);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => dialect.Identifier(new string('a', limit + 1)));

        StringAssert.Contains(error.Message, $"is {limit + 1} long and this database keeps {limit}");
    }

    /// <summary>
    /// SQLite states no limit, and EF Core's answer for a model without one is 32767. A long name is
    /// simply accepted there, as the database accepts it.
    /// </summary>
    [TestMethod]
    public void A_provider_that_states_no_limit_gets_ef_cores_default()
    {
        using var context = Context(Provider.Sqlite);
        Assert.AreEqual(short.MaxValue, context.GetService<IDesignTimeModel>().Model.GetMaxIdentifierLength(), "The premise: SQLite states no limit.");

        Assert.AreEqual(500, context.GetService<ISchemaDialect>().Identifier(new string('a', 500)).Name.Length);
    }

    [TestMethod]
    public void A_dialect_built_with_no_limit_checks_none()
    {
        using var context = Context(Provider.Npgsql);
        var unlimited = new SchemaDialect(new SchemaDialectDependencies(
            context.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>(),
            context.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>()));

        Assert.AreEqual(100_000, unlimited.Identifier(new string('a', 100_000)).Name.Length);
    }

    [TestMethod]
    [DataRow("="), DataRow("&&"), DataRow("<>"), DataRow("-|-"), DataRow("@>"), DataRow("~~*")]
    public void An_operator_is_accepted(string token)
    {
        using var context = Context(Provider.Npgsql);
        Assert.AreEqual(token, context.GetService<ISchemaDialect>().Operator(token).Token);
    }

    [TestMethod]
    [DataRow("= 1; DROP TABLE t"), DataRow("LIKE"), DataRow("=--"), DataRow("/*"), DataRow("= "), DataRow("(")]
    public void Anything_else_is_not_an_operator(string token)
    {
        using var context = Context(Provider.Npgsql);
        Assert.ThrowsExactly<ArgumentException>(() => context.GetService<ISchemaDialect>().Operator(token));
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_database_name_is_bare_when_it_can_be_and_delimited_otherwise(Provider provider)
    {
        using var context = Context(provider);
        var dialect = context.GetService<ISchemaDialect>();
        var (open, close) = provider == Provider.SqlServer ? ("[", "]") : ("\"", "\"");

        Assert.AreEqual("jsonb_path_ops", dialect.Delimit(dialect.Name("jsonb_path_ops")));
        Assert.AreEqual($"{open}C{close}", dialect.Delimit(dialect.Name("C")));
        Assert.AreEqual($"{open}en-US-x-icu{close}", dialect.Delimit(dialect.Name("en-US-x-icu")));
        Assert.AreEqual($"{open}x; DROP TABLE t{close}", dialect.Delimit(dialect.Name("x; DROP TABLE t")));
    }

    /// <summary>
    /// A closed union: this switch has no discard arm and compiles, and a fourth case would make it
    /// an error here and in every contributor at once.
    /// </summary>
    [TestMethod]
    public void A_scope_is_one_of_three()
    {
        using var context = Context(Provider.Npgsql);
        var dialect = context.GetService<ISchemaDialect>();
        SchemaScope[] scopes =
        [
            new SchemaScope.Table(new TableRef(null, dialect.Identifier("t"))),
            new SchemaScope.Schema(dialect.Identifier("app")),
            new SchemaScope.Database(),
        ];

        var described = scopes.Select(s => s switch
        {
            SchemaScope.Table t => $"table {t.Ref}",
            SchemaScope.Schema s2 => $"schema {s2.Name}",
            SchemaScope.Database => "database",
        }).ToList();

        CollectionAssert.AreEqual(new[] { "table t", "schema app", "database" }, described);
    }
}
