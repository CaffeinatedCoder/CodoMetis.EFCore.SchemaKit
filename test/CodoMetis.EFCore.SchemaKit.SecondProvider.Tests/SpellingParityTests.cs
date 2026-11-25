using System.Linq.Expressions;
using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests;

/// <summary>
/// The two spellings of this project against EF Core's own queries (issue 184): a second
/// provider's package spells a lambda through the public contract, and what comes out is that
/// database's SQL, not PostgreSQL's.
/// </summary>
[TestClass]
public sealed class SpellingParityTests
{
    public sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Tag { get; set; }
        public bool Active { get; set; }
        public int Rank { get; set; }
        public int? Score { get; set; }
    }

    private sealed class Rows(DbContextOptions<Rows> options) : DbContext(options)
    {
        public DbSet<Row> Set => Set<Row>();
    }

    private static readonly Expression<Func<Row, bool>>[] Conditions =
    [
        x => x.Active,
        x => !x.Active,
        x => x.Active == true,
        x => x.Name == "a",
        x => x.Tag != "blocked",
        x => x.Name.ToLower() == "a",
        x => x.Name.ToUpper() == "A",
        x => x.Name.Trim() == "a",
        x => x.Name.Replace("a", "b") == "b",
        x => x.Name.Substring(1, 2) == "b",
        x => x.Name.Length > 3,
        x => x.Name + x.Tag == "ab",
        x => (x.Tag ?? "none") == "none",
        x => !(x.Score < 5),
        x => x.Rank > 1 && x.Active,
        x => x.Rank > 1 || !x.Active,
    ];

    private static readonly Expression<Func<Row, object?>>[] Keys =
    [
        x => x.Name.ToLower(),
        x => x.Name.ToUpper(),
        x => x.Name.Trim(),
        x => x.Name.Length,
        x => x.Name + x.Tag,
        x => x.Tag ?? "none",
    ];

    /// <summary>
    /// Each condition as the SQL Server spelling of this project writes it is what EF Core's query
    /// writes for the same lambda, but for the parentheses the kernel puts around a group and the
    /// line breaks EF Core puts into a CASE. That is the reason a spelling is a provider's: an
    /// index serves a query whose expression matches it.
    /// </summary>
    [TestMethod]
    public void A_condition_is_spelled_as_SQL_Servers_queries_spell_it()
    {
        using var context = new Rows(new DbContextOptionsBuilder<Rows>().UseSqlServer(Contexts.OfflineSqlServer).Options);

        foreach (var condition in Conditions)
        {
            var query = context.Set.Where(condition).ToQueryString();
            var theirs = Unqualified(query[(query.IndexOf("WHERE ", StringComparison.Ordinal) + 6)..], @"\[\w+\]\.");
            var ours = Regex.Replace(Bound.Where(condition), @"\{(\w+)\}", "[$1]");

            Assert.AreEqual(theirs, WithoutOuterParentheses(ours), condition.ToString());
        }
    }

    /// <summary>The same on SQLite, for an expression: what a query selects is what an index's key is.</summary>
    [TestMethod]
    public void An_expression_is_spelled_as_SQLites_queries_spell_it()
    {
        using var context = new Rows(new DbContextOptionsBuilder<Rows>().UseSqlite(Contexts.OfflineSqlite).Options);

        foreach (var key in Keys)
        {
            // The lambda as the declaration takes it: without the boxing a common element type needs.
            var typed = Expression.Lambda(key.Body is UnaryExpression { NodeType: ExpressionType.Convert } boxed ? boxed.Operand : key.Body, key.Parameters);

            var query = context.Set.Select(key).ToQueryString();
            var theirs = Unqualified(Regex.Match(query, @"SELECT (.*?)\s+FROM ", RegexOptions.Singleline).Groups[1].Value, "\"\\w+\"\\.");
            var ours = Regex.Replace(((KeyTarget.Template)KeyOf(typed)).Sql.ToString(), @"\{(\w+)\}", "\"$1\"");

            Assert.AreEqual(theirs, ours, key.ToString());
        }
    }

    private static KeyTarget KeyOf(LambdaExpression key)
        => (KeyTarget)typeof(Bound).GetMethod(nameof(Bound.Key))!.MakeGenericMethod(typeof(Row), key.ReturnType).Invoke(null, [key])!;

    private static string Unqualified(string sql, string alias)
        => Regex.Replace(Regex.Replace(sql, alias, ""), @"\s+", " ").Trim();

    // The kernel writes a group of AND or OR, and a compensated comparison, in parentheses.
    private static string WithoutOuterParentheses(string sql)
    {
        if (!sql.StartsWith('(') || !sql.EndsWith(')'))
            return sql;

        var depth = 0;
        for (var i = 0; i < sql.Length - 1; i++)
        {
            depth += sql[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0) return sql;
        }

        return sql[1..^1];
    }
}
