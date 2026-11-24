using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using Microsoft.Data.SqlClient;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Integration;

/// <summary>
/// What SQL Server says to the SQL the kernel's one spelling writes for a lambda
/// (<c>PublicContractTests.There_is_one_spelling_and_a_package_cannot_make_another</c>). It is
/// why the probe's filter is a template written by hand.
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
        => Predicate.Of(filter, SqlSpelling.PostgreSql).Template.ToString().Replace("{Active}", "[Active]").Replace("{Tag}", "[Tag]");

    private static int Refused(SqlServerDatabase database, string statement)
        => Assert.ThrowsExactly<SqlException>(() => database.Execute(statement)).Number;

    /// <summary>
    /// A boolean property is a predicate on PostgreSQL and a number on SQL Server: the filter the
    /// spelling writes is no condition here (Msg 102), and its comparison with <c>TRUE</c> is none
    /// a filtered index takes (Msg 10735).
    /// </summary>
    [TestMethod]
    public void The_server_refuses_what_the_one_spelling_writes_for_a_boolean()
    {
        Assert.AreEqual("[Active]", Spelled(x => x.Active));
        Assert.AreEqual("[Active] = TRUE", Spelled(x => x.Active == true));

        using var database = new SqlServerDatabase();
        database.Execute(Table);

        Assert.AreEqual(102, Refused(database, Filtered("IX_bare", Spelled(x => x.Active))));
        Assert.AreEqual(10735, Refused(database, Filtered("IX_compared", Spelled(x => x.Active == true))));

        // As SQL Server's own queries spell it.
        database.Execute(Filtered("IX_own", "[Active] = CAST(1 AS bit)"));
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
        Assert.AreEqual("([Tag] <> 'blocked' OR [Tag] IS NULL)", Spelled(x => x.Tag != "blocked"));

        using var database = new SqlServerDatabase();
        database.Execute(Table);

        Assert.AreEqual(156, Refused(database, Filtered("IX_compensated", Spelled(x => x.Tag != "blocked"))));
        database.Execute(Filtered("IX_plain", "[Tag] <> 'blocked'"));
    }
}
