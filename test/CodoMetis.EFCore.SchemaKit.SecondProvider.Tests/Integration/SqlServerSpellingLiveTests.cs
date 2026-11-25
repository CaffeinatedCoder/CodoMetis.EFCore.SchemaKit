using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using Microsoft.Data.SqlClient;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Integration;

/// <summary>
/// What SQL Server says to the SQL its spelling writes for a lambda, and to what PostgreSQL's
/// wrote for the same lambda while the kernel had that one alone (issue 184).
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SqlServerSpellingLiveTests
{
    private sealed class Flagged
    {
        public bool Active { get; set; }
        public string? Tag { get; set; }
    }

    private const string Table = "CREATE TABLE Flags (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Active bit NOT NULL, Tag nvarchar(40) NULL)";

    private static string Filtered(string name, string filter) => $"CREATE INDEX {name} ON Flags (CustomerId) WHERE {filter}";

    private static string Spelled(System.Linq.Expressions.Expression<Func<Flagged, bool>> filter)
        => Bound.Where(filter).Replace("{Active}", "[Active]").Replace("{Tag}", "[Tag]");

    private static int Refused(SqlServerDatabase database, string statement)
        => Assert.ThrowsExactly<SqlException>(() => database.Execute(statement)).Number;

    /// <summary>
    /// A boolean property is a predicate on PostgreSQL and a number on SQL Server: the property
    /// alone, which is what PostgreSQL's spelling writes, is no condition here (Msg 102), and
    /// neither is a comparison with <c>TRUE</c> (Msg 10735). The spelling of SQL Server compares
    /// the bit, and its negation with what false is, and the server takes both.
    /// </summary>
    [TestMethod]
    public void The_server_takes_a_boolean_as_its_own_spelling_writes_it()
    {
        Assert.AreEqual("[Active] = CAST(1 AS bit)", Spelled(x => x.Active));
        Assert.AreEqual("[Active] = CAST(0 AS bit)", Spelled(x => !x.Active));
        Assert.AreEqual("[Active] = CAST(1 AS bit)", Spelled(x => x.Active == true));

        using var database = new SqlServerDatabase();
        database.Execute(Table);

        Assert.AreEqual(102, Refused(database, Filtered("IX_bare", "[Active]")));
        Assert.AreEqual(10735, Refused(database, Filtered("IX_true", "[Active] = TRUE")));

        database.Execute(Filtered("IX_alone", Spelled(x => x.Active)));
        database.Execute(Filtered("IX_negated", Spelled(x => !x.Active)));
    }

    /// <summary>
    /// Not a matter of spelling alone. A comparison that can be NULL is written as EF Core's query
    /// writes it, with <c>OR … IS NULL</c>, so that the condition keeps the rows C# keeps. SQL
    /// Server's filtered index takes no <c>OR</c> (Msg 156): what the translator does for a
    /// nullable column has no form here, whoever spells it.
    /// </summary>
    [TestMethod]
    public void The_server_refuses_the_null_compensation_of_an_inequality_in_a_filter()
    {
        Assert.AreEqual("([Tag] <> N'blocked' OR [Tag] IS NULL)", Spelled(x => x.Tag != "blocked"));

        using var database = new SqlServerDatabase();
        database.Execute(Table);

        Assert.AreEqual(156, Refused(database, Filtered("IX_compensated", Spelled(x => x.Tag != "blocked"))));
        database.Execute(Filtered("IX_plain", "[Tag] <> N'blocked'"));
    }
}
