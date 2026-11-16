using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Integration;

/// <summary>
/// Where SQL Server keeps a name once, and how it compares two, as the server says it. The rows
/// are what the probe's dialect and contributor claim (<c>SqlServerIndexMigrationTests</c>): what
/// is refused here is what the check refuses, and what is taken here the check lets through. Where
/// the two disagree the test says so, and that is a finding of #119.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SqlServerNameLiveTests
{
    private const string Tables =
        """
        CREATE TABLE Invoices (Id int NOT NULL CONSTRAINT PK_Invoices PRIMARY KEY, CustomerId int NOT NULL CONSTRAINT CK_other CHECK (CustomerId > 0));
        CREATE TABLE Orders (
            Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NOT NULL CONSTRAINT CK_own CHECK (CustomerId > 0) CONSTRAINT FK_own REFERENCES Invoices (Id),
            Number nvarchar(40) NOT NULL CONSTRAINT AK_own UNIQUE,
            Total decimal(18,2) NOT NULL CONSTRAINT DF_own DEFAULT 0,
            ShippedAt datetime2 NULL);
        CREATE INDEX IX_own ON Orders (Number);
        CREATE INDEX IX_other ON Invoices (CustomerId);
        CREATE SEQUENCE Numbers;
        """;

    private static string Add(string name) => $"CREATE NONCLUSTERED INDEX [{name}] ON Orders (CustomerId) INCLUDE (Total) WHERE ShippedAt IS NULL";

    /// <summary>Msg 1913: an index or statistics of that name is on the table already. A key is one: it is built on an index of its name.</summary>
    [TestMethod]
    [DataRow("PK_Orders", DisplayName = "its table's primary key")]
    [DataRow("AK_own", DisplayName = "a unique constraint of its table")]
    [DataRow("IX_own", DisplayName = "an index of its table")]
    [DataRow("pk_orders", DisplayName = "its primary key in another case")]
    public void The_server_refuses_an_index_named_like(string name)
    {
        using var database = new SqlServerDatabase();
        database.Execute(Tables);

        var refused = Assert.ThrowsExactly<SqlException>(() => database.Execute(Add(name)));

        Assert.AreEqual(1913, refused.Number, refused.Message);
    }

    /// <summary>An index is named within its table, and among indexes alone: no object of the schema holds its name.</summary>
    [TestMethod]
    [DataRow("IX_other", DisplayName = "an index of another table")]
    [DataRow("PK_Invoices", DisplayName = "the primary key of another table")]
    [DataRow("CK_own", DisplayName = "a check constraint of its table")]
    [DataRow("FK_own", DisplayName = "a foreign key of its table")]
    [DataRow("DF_own", DisplayName = "a default of its table")]
    [DataRow("Orders", DisplayName = "its table")]
    [DataRow("Numbers", DisplayName = "a sequence")]
    public void The_server_takes_an_index_named_like(string name)
    {
        using var database = new SqlServerDatabase();
        database.Execute(Tables);

        database.Execute(Add(name));

        Assert.AreEqual(1, database.Column($"SELECT COUNT(*) FROM sys.indexes WHERE name = N'{name}' AND object_id = OBJECT_ID(N'Orders') AND has_filter = 1").Select(int.Parse).Single());
    }

    /// <summary>
    /// What the dialect says of EF Core's own objects, and why <c>NameClaim.Constraint</c> and
    /// <c>NameClaim.Trigger</c> have no reading on SQL Server: a constraint and a trigger are
    /// objects of the schema, as a table is. One name, one object, whatever table it is on
    /// (Msg 2714).
    /// </summary>
    [TestMethod]
    [DataRow("ALTER TABLE Orders ADD CONSTRAINT CK_other CHECK (Total >= 0)", DisplayName = "a check constraint named like one of another table")]
    [DataRow("ALTER TABLE Orders ADD CONSTRAINT PK_Invoices UNIQUE (Total)", DisplayName = "a unique constraint named like the key of another table")]
    [DataRow("ALTER TABLE Orders ADD CONSTRAINT Numbers CHECK (Total >= 0)", DisplayName = "a check constraint named like a sequence")]
    [DataRow("CREATE TABLE CK_own (Id int)", DisplayName = "a table named like a check constraint")]
    [DataRow("CREATE TRIGGER TR_other ON Orders AFTER INSERT AS RETURN", DisplayName = "a trigger named like one of another table")]
    [DataRow("CREATE TRIGGER Invoices ON Orders AFTER INSERT AS RETURN", DisplayName = "a trigger named like a table")]
    public void The_server_refuses_an_object_of_the_schema_named_like_another(string statement)
    {
        using var database = new SqlServerDatabase();
        database.Execute(Tables);
        database.Execute("CREATE TRIGGER TR_other ON Invoices AFTER INSERT AS RETURN");

        var refused = Assert.ThrowsExactly<SqlException>(() => database.Execute(statement));

        Assert.AreEqual(2714, refused.Number, refused.Message);
    }

    /// <summary>
    /// The kernel's own dialect knows of no index behind a key, so the name passes the diff
    /// (<c>The_kernels_own_scopes_let_a_key_named_index_through</c>) and the server refuses the
    /// migration.
    /// </summary>
    [TestMethod]
    public void Under_the_kernels_own_scopes_a_key_named_index_fails_at_the_server()
    {
        using var database = new SqlServerDatabase();

        var refused = Assert.ThrowsExactly<SqlException>(() => database.Create<Sales.NamedLikeThePrimaryKey>(dialect: false));

        Assert.AreEqual(1913, refused.Number, refused.Message);
    }

    // ── How two names compare: by the database's collation ──

    /// <summary>
    /// Under the server's default collation <c>ß</c> and <c>ss</c> are one, which neither an
    /// ordinal comparison nor one that ignores case says. The model passes the diff, and the
    /// server refuses the second index.
    /// </summary>
    [TestMethod]
    public void Two_names_the_collation_takes_for_one_pass_the_diff_and_fail_at_the_server()
    {
        using (var offline = Contexts.SqlServer<Sales.TwoIndexesTheCollationTakesForOne>())
            Assert.IsNull(offline.Collisions());

        using var database = new SqlServerDatabase();
        var refused = Assert.ThrowsExactly<SqlException>(() => database.Create<Sales.TwoIndexesTheCollationTakesForOne>());

        Assert.AreEqual(1913, refused.Number, refused.Message);
    }

    /// <summary>
    /// And the other way: in a database whose collation tells the cases apart, the two names are
    /// two indexes. The declaration says its names compare without regard to case, for every
    /// database, and the kernel refuses the model where it is written.
    /// </summary>
    [TestMethod]
    public void Two_names_that_differ_in_case_are_two_in_a_case_sensitive_database_and_one_to_the_kernel()
    {
        using var database = new SqlServerDatabase(collation: "Latin1_General_100_CS_AS");
        database.Execute(Tables);
        database.Execute(Add("IX_orders"));
        database.Execute(Add("IX_ORDERS"));
        database.Execute(Add("pk_orders"));

        using var context = Contexts.SqlServer<Sales.TwoIndexesThatDifferInCase>();
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => _ = context.Model);
        StringAssert.Contains(refused.Message, "declares the same schema object twice");

        using var named = Contexts.SqlServer<Sales.NamedLikeThePrimaryKeyInAnotherCase>();
        Assert.AreEqual("SK1001", named.Collisions()?.Diagnostics.Single().Id);
    }

    // ── A table without a schema ──

    /// <summary>
    /// The premise of <c>DefaultSchema</c>: for the owner of a database, an object created without
    /// a schema is in <c>dbo</c>, and the name of a table there is taken whether the statement
    /// says the schema or not.
    /// </summary>
    [TestMethod]
    public void A_table_created_without_a_schema_is_in_dbo()
    {
        using var database = new SqlServerDatabase();
        database.Execute(Tables);

        CollectionAssert.AreEqual(new[] { "dbo" }, database.Column("SELECT SCHEMA_NAME(schema_id) FROM sys.tables WHERE name = N'Orders'"));
        var refused = Assert.ThrowsExactly<SqlException>(() => database.Execute("CREATE TABLE dbo.Orders (Id int)"));
        Assert.AreEqual(2714, refused.Number, refused.Message);
    }

    // ── Names the database defines ──

    /// <summary>
    /// The kernel's rule for a name the database defines is PostgreSQL's: bare when it is lower
    /// case, delimited otherwise. SQL Server needs it the other way round, twice. A filegroup
    /// called <c>primary</c> is a reserved word in any case, and written bare it is refused
    /// (Msg 156). A collation is no identifier, and delimited it is refused (Msg 102).
    /// </summary>
    /// <remarks>
    /// A dialect can override <c>Delimit(SqlName)</c>, and is then asked without being told which
    /// of the two the name is: no one rule serves both.
    /// </remarks>
    [TestMethod]
    public void A_name_the_database_defines_is_delimited_by_PostgreSQLs_rule()
    {
        using var context = Contexts.SqlServer<Sales.Bare>();
        var dialect = context.GetService<ISchemaDialect>();
        var (bare, delimited) = (dialect.Delimit(dialect.Name("primary")), dialect.Delimit(dialect.Name("PRIMARY")));
        var collation = dialect.Delimit(dialect.Name("Latin1_General_100_CI_AS"));
        Assert.AreEqual("primary", bare);
        Assert.AreEqual("[PRIMARY]", delimited);
        Assert.AreEqual("[Latin1_General_100_CI_AS]", collation);

        using var database = new SqlServerDatabase();
        database.Execute(Tables);

        Assert.AreEqual(156, Assert.ThrowsExactly<SqlException>(() => database.Execute($"CREATE INDEX IX_bare ON Orders (CustomerId) ON {bare}")).Number);
        database.Execute($"CREATE INDEX IX_delimited ON Orders (CustomerId) ON {delimited}");

        Assert.AreEqual(102, Assert.ThrowsExactly<SqlException>(() => database.Execute($"SELECT Id FROM Orders WHERE Number = N'a' COLLATE {collation}")).Number);
        database.Execute("SELECT Id FROM Orders WHERE Number = N'a' COLLATE Latin1_General_100_CI_AS");
    }
}
