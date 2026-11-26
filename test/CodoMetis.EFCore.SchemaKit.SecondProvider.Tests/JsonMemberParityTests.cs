using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests;

/// <summary>
/// A member of a JSON document on a second and a third provider (issue 208): the contract's
/// <c>ISchemaDialect.JsonMember</c> is given a container, the names, a kind and a store type, and
/// a dialect written against the public contract alone turns that into what its provider's
/// queries write. PostgreSQL's operators, SQL Server's <c>JSON_VALUE</c> and SQLite's path are
/// three spellings of one shape.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class JsonMemberParityTests
{
    public sealed class Place
    {
        public string City { get; set; } = "";
        public int Zip { get; set; }
    }

    public sealed class Prefs
    {
        public int Rank { get; set; }
        public string Theme { get; set; } = "";
        public long Big { get; set; }
        public decimal Price { get; set; }
        public Guid Key { get; set; }
        public Place Address { get; set; } = new();
    }

    public sealed class Holder
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Prefs Prefs { get; set; } = new();
    }

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Holder> Holders(ModelBuilder modelBuilder)
    {
        var holders = modelBuilder.Entity<Holder>().ToTable("Holders");
        holders.ComplexProperty(x => x.Prefs, prefs => prefs.ToJson());
        return holders;
    }

    private sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Holders(modelBuilder);
    }

    private static readonly string[] Paths = ["Prefs.Rank", "Prefs.Theme", "Prefs.Big", "Prefs.Address.City", "Prefs.Address.Zip"];

    private sealed class OnSqlite : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Holders(modelBuilder).DeclareProbe([.. Paths.Select(path => new ProbeSqliteIndex(new KeyTarget.Column(path)) { Name = "IX_" + path.Replace(".", "_") })]);
    }

    private sealed class OnSqlServer : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Holders(modelBuilder).DeclareProbe(
        [
            Filtered("IX_rank", "{Prefs.Rank} > 5"),
            Filtered("IX_theme", "{Prefs.Theme} = N'dark'"),
            Filtered("IX_big", "{Prefs.Big} > CAST(5 AS bigint)"),
            Filtered("IX_price", "{Prefs.Price} > 5.5"),
            Filtered("IX_city", "{Prefs.Address.City} = N'a'"),
            Filtered("IX_zip", "{Prefs.Address.Zip} > 5"),
        ]);

        private static ProbeIndex Filtered(string name, string filter)
            => new(["Name"]) { Name = name, Where = new Predicate(SqlTemplate.FromPlaceholders(filter)) };
    }

    private static string After(string sql, string keyword) => sql[(sql.IndexOf(keyword, StringComparison.Ordinal) + keyword.Length)..].Trim();

    private static string Ordered<TValue>(DbContext context, Expression<Func<Holder, TValue>> key, string alias)
        => After(context.Set<Holder>().OrderBy(key).Select(x => x.Id).ToQueryString(), "ORDER BY ").Replace(alias, "");

    private static string Filtered(DbContext context, Expression<Func<Holder, bool>> condition, string alias)
        => After(context.Set<Holder>().Where(condition).Select(x => x.Id).ToQueryString(), "WHERE ").Replace(alias, "");

    /// <summary>On SQLite a member is a key as the query orders by it: the name, or the path from the document's root.</summary>
    [TestMethod]
    public void On_SQLite_a_member_is_written_as_the_query_writes_it()
    {
        using var bare = Contexts.Sqlite<Bare>();
        using var context = Contexts.Sqlite<OnSqlite>();
        var statements = bare.DiffTo(context).OfType<SqlOperation>().Select(o => o.Sql).ToList();

        string Key(string path)
            => After(statements.Single(sql => sql.Contains($"\"IX_{path.Replace(".", "_")}\"", StringComparison.Ordinal)), "ON \"Holders\" ").TrimEnd(';')[1..^1];

        Assert.AreEqual(Ordered(bare, x => x.Prefs.Rank, "\"h\"."), Key("Prefs.Rank"));
        Assert.AreEqual(Ordered(bare, x => x.Prefs.Theme, "\"h\"."), Key("Prefs.Theme"));
        Assert.AreEqual(Ordered(bare, x => x.Prefs.Big, "\"h\"."), Key("Prefs.Big"));
        Assert.AreEqual(Ordered(bare, x => x.Prefs.Address.City, "\"h\"."), Key("Prefs.Address.City"));
        Assert.AreEqual(Ordered(bare, x => x.Prefs.Address.Zip, "\"h\"."), Key("Prefs.Address.Zip"));
    }

    /// <summary>
    /// On SQL Server a member in a condition is what the query's condition reads, but for the
    /// path's literal, which the contract writes as the provider's string literal (the audit in
    /// <see cref="ProbeSqlServerDialect" />). SQL Server takes no function in a filtered index's
    /// condition, so this says what the dialect can write, not that such an index can be built.
    /// </summary>
    [TestMethod]
    public void On_SQL_Server_a_member_is_written_as_the_query_writes_it_but_for_the_paths_literal()
    {
        using var bare = Contexts.SqlServer<Bare>();
        using var context = Contexts.SqlServer<OnSqlServer>();

        // The statement is inside EXEC(N'…'), where every quote is doubled.
        var statements = bare.DiffTo(context).OfType<SqlOperation>().Select(o => o.Sql.Replace("''", "'")).ToList();

        // What stands behind WHERE, without the end of the EXEC around it, and with the path's
        // literal as the query has it.
        string Condition(string index)
        {
            var condition = After(statements.Single(sql => sql.Contains($"[{index}]", StringComparison.Ordinal)), "WHERE ").TrimEnd(';').Trim();
            return condition[..^"')".Length].Replace("N'$.", "'$.");
        }

        // In a template the resolver puts a member into parentheses of its own, which the query
        // does not need where the member is one operand: the same expression to the parser.
        static string Parenthesized(string query, string op)
        {
            var at = query.LastIndexOf(op, StringComparison.Ordinal);
            return $"({query[..at]}){query[at..]}";
        }

        Assert.AreEqual(Parenthesized(Filtered(bare, x => x.Prefs.Rank > 5, "[h]."), " > "), Condition("IX_rank"));
        Assert.AreEqual(Parenthesized(Filtered(bare, x => x.Prefs.Theme == "dark", "[h]."), " = "), Condition("IX_theme"));
        Assert.AreEqual(Parenthesized(Filtered(bare, x => x.Prefs.Big > 5, "[h]."), " > "), Condition("IX_big"));
        Assert.AreEqual(Parenthesized(Filtered(bare, x => x.Prefs.Price > 5.5m, "[h]."), " > "), Condition("IX_price"));
        Assert.AreEqual(Parenthesized(Filtered(bare, x => x.Prefs.Address.City == "a", "[h]."), " = "), Condition("IX_city"));
        Assert.AreEqual(Parenthesized(Filtered(bare, x => x.Prefs.Address.Zip > 5, "[h]."), " > "), Condition("IX_zip"));
    }

    /// <summary>Without a dialect of its own a provider has the kernel's, which has no answer for a member.</summary>
    [TestMethod]
    public void The_kernels_own_dialect_writes_no_member()
    {
        using var bare = Contexts.Sqlite<Bare>(dialect: false);
        using var context = Contexts.Sqlite<OnSqlite>(dialect: false);

        var refusal = Assert.ThrowsExactly<InvalidOperationException>(() => bare.DiffTo(context));
        StringAssert.Contains(refusal.Message, "no SchemaKit package for this provider says it");
    }
}
