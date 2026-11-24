using System.Reflection;
using System.Runtime.CompilerServices;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests;

/// <summary>
/// The audit of #119, where a row of it is a fact about the contract's types and needs no server:
/// what a package outside the repository is handed, and what it is not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PublicContractTests
{
    private static readonly Assembly Kernel = typeof(ISchemaDialect).Assembly;

    /// <summary>
    /// The premise of the whole project: what compiles here compiles against the public contract.
    /// The kernel grants its own tests its internals and nobody else, the PostgreSQL package and
    /// this assembly included.
    /// </summary>
    [TestMethod]
    public void The_kernel_grants_this_assembly_no_internals()
        => CollectionAssert.AreEqual(
            new[] { "CodoMetis.EFCore.SchemaKit.Tests" },
            Kernel.GetCustomAttributes<InternalsVisibleToAttribute>().Select(grant => grant.AssemblyName).ToList());

    // ── Identifier, SqlName, SqlOperator, SqlFragment ──

    /// <summary>
    /// None of the values a dialect returns can be made outside the kernel. So a dialect is a
    /// class derived from <see cref="SchemaDialect" /> that calls its base for every value: one
    /// that implements <see cref="ISchemaDialect" /> itself has nothing to return.
    /// </summary>
    [TestMethod]
    [DataRow(typeof(Identifier))]
    [DataRow(typeof(SqlName))]
    [DataRow(typeof(SqlOperator))]
    [DataRow(typeof(SqlFragment))]
    public void A_value_of_a_statement_has_no_public_constructor(Type value)
        => Assert.AreEqual(0, value.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length);

    /// <summary>
    /// The operator grammar of every dialect is the base's, which is PostgreSQL's: neither probe
    /// overrides it, and on both providers PostgreSQL's operators get through and the operators
    /// that are words do not. An override could refuse more. It could accept nothing the base
    /// refuses, since only the base can make the value
    /// (<see cref="A_value_of_a_statement_has_no_public_constructor" />).
    /// </summary>
    [TestMethod]
    public void The_operator_grammar_of_both_providers_is_PostgreSQLs()
    {
        using var sqlServer = Contexts.SqlServer<Sales.Bare>();
        using var sqlite = Contexts.Sqlite<People.Bare>();

        foreach (var dialect in new[] { sqlServer.GetService<ISchemaDialect>(), sqlite.GetService<ISchemaDialect>() })
        {
            Assert.AreEqual("&&", dialect.Operator("&&").Token, "PostgreSQL's overlap, which neither database has.");
            Assert.AreEqual("-|-", dialect.Operator("-|-").Token);

            Assert.ThrowsExactly<ArgumentException>(() => dialect.Operator("LIKE"));
            Assert.ThrowsExactly<ArgumentException>(() => dialect.Operator("IS NOT"));
            Assert.ThrowsExactly<ArgumentException>(() => dialect.Operator("GLOB"));
        }
    }

    // ── KeyTarget.Of, Predicate.Of ──

    private sealed class Flagged
    {
        public string Name { get; set; } = "";
        public bool Active { get; set; }
    }

    /// <summary>
    /// A lambda is translated for one provider, and the factories take that provider's spelling
    /// (#132). There is one spelling, behind a private constructor: a second provider's package
    /// has nothing to pass, and what it gets by passing the one there is, is PostgreSQL's SQL.
    /// </summary>
    [TestMethod]
    public void There_is_one_spelling_and_a_package_cannot_make_another()
    {
        Assert.AreEqual(0, typeof(SqlSpelling).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length);
        CollectionAssert.AreEqual(
            new[] { nameof(SqlSpelling.PostgreSql) },
            typeof(SqlSpelling).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(SqlSpelling)).Select(p => p.Name).ToList());

        // SQL Server has no boolean expression that is a column (SqlServerSpellingLiveTests), and no TRUE.
        Assert.AreEqual("{Active}", Predicate.Of<Flagged>(x => x.Active, SqlSpelling.PostgreSql).Template.ToString());
        Assert.AreEqual("{Active} = TRUE", Predicate.Of<Flagged>(x => x.Active == true, SqlSpelling.PostgreSql).Template.ToString());

        // Neither SQL Server nor SQLite has btrim, nor a string that begins with E.
        Assert.AreEqual(new KeyTarget.Template(SqlTemplate.FromPlaceholders("""btrim({Name}, E' \t\n\r')""")), KeyTarget.Of((Flagged x) => x.Name.Trim(), SqlSpelling.PostgreSql));
    }

    /// <summary>
    /// Two things in it are PostgreSQL's, and SQLite refuses each by itself: the string that
    /// begins with <c>E</c> is a syntax error, and with a string of its own SQLite has no function
    /// called <c>btrim</c>. Its own word is <c>trim</c>.
    /// </summary>
    [TestMethod]
    public void SQLite_refuses_what_the_one_spelling_writes_for_Trim()
    {
        var trimmed = ((KeyTarget.Template)KeyTarget.Of((Person x) => x.Name.Trim(), SqlSpelling.PostgreSql)).Sql.ToString().Replace("{Name}", "\"Name\"");
        Assert.AreEqual("""btrim("Name", E' \t\n\r')""", trimmed);

        using var database = new SqliteDatabase();
        database.Execute("""CREATE TABLE "People" ("Id" INTEGER PRIMARY KEY, "Name" TEXT)""");
        string Refused(string key) => Assert.ThrowsExactly<SqliteException>(() => database.Execute($"""CREATE INDEX "IX_trimmed" ON "People" ({key})""")).Message;

        StringAssert.Contains(Refused(trimmed), """near "' \t\n\r'": syntax error""");
        StringAssert.Contains(Refused("""btrim("Name", ' ')"""), "no such function: btrim");

        database.Execute("""CREATE INDEX "IX_trimmed" ON "People" (trim("Name"))""");
    }

    // ── Dependency.SchemaObject, QualifiedName, SchemaScope ──

    /// <summary>
    /// An index of SQL Server is named within its table, so its descriptor's name has no schema
    /// and its scope is the table: the two together are its identity, and the planner tells two
    /// indexes of one name apart by them. A dependency on a schema object holds an owner and a
    /// name and no scope, so it cannot say which of the two it means.
    /// </summary>
    [TestMethod]
    public void A_dependency_on_an_index_cannot_say_which_tables_index_it_means()
    {
        using var context = Contexts.SqlServer<Sales.TwoTables>();
        _ = context.FirstMigration();
        var resolved = context.Contributor<ProbeIndexContributor>().Resolved;
        var (onOrders, onInvoices) = (resolved.First(index => index.Table.Name.Name == "Orders"), resolved.First(index => index.Table.Name.Name == "Invoices"));

        Assert.AreNotEqual(onOrders.Scope, onInvoices.Scope);
        Assert.AreEqual(onOrders.Name, onInvoices.Name);
        Assert.AreEqual(
            new Dependency.SchemaObject(ProbeIndex.OwnerId, onOrders.Name),
            new Dependency.SchemaObject(ProbeIndex.OwnerId, onInvoices.Name));
    }

    // ── NameClaim, SchemaDialect.Claims ──

    /// <summary>
    /// The dialect is asked where the names of EF Core's objects are taken, for the kinds of a
    /// closed list. A default of SQL Server is a constraint with a name of its own in the schema,
    /// EF Core names it when the model says so, and the list has no kind for it: the dialect is
    /// not asked, and an object of the schema that takes the name could not be seen colliding.
    /// </summary>
    [TestMethod]
    public void The_dialect_is_never_asked_about_a_named_default()
    {
        using var context = Contexts.SqlServer<Sales.WithANamedDefault>();
        var total = context.Relational().Tables.Single().Columns.Single(column => column.Name == "Total");
        var name = total.PropertyMappings.Single().Property.GetDefaultConstraintName(StoreObjectIdentifier.Table("Orders"));
        Assert.AreEqual("DF_Orders_Total", name);

        _ = context.FirstMigration();
        var asked = ((ProbeSqlServerDialect)context.GetService<ISchemaDialect>()).Asked;

        CollectionAssert.Contains(asked, (SchemaModelObject.PrimaryKey, "PK_Orders"));
        Assert.IsFalse(asked.Any(question => question.Name == name), string.Join(", ", asked));
    }
}
