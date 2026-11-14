using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// Where PostgreSQL keeps a name once, as the server says it. Each row is the database of a model
/// of <c>NameClaimTests</c> with the exclusion constraint added last: what is refused there is
/// what the check refuses, and what is taken there the check lets through. Were the dialect's
/// table of namespaces wrong, one side or the other would disagree with the server.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class NameClaimLiveTests
{
    private const string Tables =
        """
        CREATE TABLE role_grants (id int CONSTRAINT "PK_role_grants" PRIMARY KEY, role_id int CONSTRAINT ck_own CHECK (role_id > 0), period daterange);
        CREATE TABLE bookings (id int PRIMARY KEY, room_id int CONSTRAINT ck_other CHECK (room_id > 0) CONSTRAINT ak_other UNIQUE, period daterange);
        CREATE INDEX ix_other ON bookings (room_id);
        CREATE SEQUENCE numbers;
        ALTER TABLE bookings ADD CONSTRAINT ex_other EXCLUDE USING gist (period WITH &&);
        ALTER TABLE role_grants ADD CONSTRAINT fk_own FOREIGN KEY (role_id) REFERENCES bookings (id);
        ALTER TABLE bookings ADD CONSTRAINT fk_other FOREIGN KEY (room_id) REFERENCES role_grants (id);
        """;

    private static string Add(string name) => $"""ALTER TABLE role_grants ADD CONSTRAINT "{name}" EXCLUDE USING gist (period WITH &&)""";

    [TestMethod]
    [DataRow("PK_role_grants", PostgresErrorCodes.DuplicateTable, DisplayName = "its table's primary key")]
    [DataRow("ix_other", PostgresErrorCodes.DuplicateTable, DisplayName = "an index of another table")]
    [DataRow("bookings", PostgresErrorCodes.DuplicateTable, DisplayName = "a table")]
    [DataRow("numbers", PostgresErrorCodes.DuplicateTable, DisplayName = "a sequence")]
    [DataRow("ak_other", PostgresErrorCodes.DuplicateTable, DisplayName = "a unique constraint of another table")]
    [DataRow("ex_other", PostgresErrorCodes.DuplicateTable, DisplayName = "an exclusion constraint of another table")]
    [DataRow("ck_own", PostgresErrorCodes.DuplicateObject, DisplayName = "a check constraint of its table")]
    [DataRow("fk_own", PostgresErrorCodes.DuplicateObject, DisplayName = "a foreign key of its table")]
    public void The_server_refuses_an_exclusion_constraint_named_like(string name, string state)
    {
        using var database = new LiveDatabase();
        database.Execute(Tables);

        var refused = Assert.ThrowsExactly<PostgresException>(() => database.Execute(Add(name)));

        Assert.AreEqual(state, refused.SqlState);
    }

    [TestMethod]
    [DataRow("ck_other", DisplayName = "a check constraint of another table")]
    [DataRow("fk_other", DisplayName = "a foreign key of another table")]
    [DataRow("pk_role_grants", DisplayName = "its primary key in another case")]
    public void The_server_takes_an_exclusion_constraint_named_like(string name)
    {
        using var database = new LiveDatabase();
        database.Execute(Tables);

        database.Execute(Add(name));

        Assert.HasCount(2, database.Exclusions());
    }

    /// <summary>
    /// A table created without a schema lands in public on a stock database, so its constraint's
    /// index and an index on a table named public.x are relations of one schema (#100). The
    /// constraint's table is the unqualified one, as a model without a default schema writes it.
    /// </summary>
    [TestMethod]
    public void The_server_refuses_a_name_taken_in_public_for_a_table_created_without_a_schema()
    {
        using var database = new LiveDatabase();
        database.Execute(Tables);
        database.Execute("CREATE TABLE public.desks (id int); CREATE INDEX ix_public ON public.desks (id)");

        var refused = Assert.ThrowsExactly<PostgresException>(() => database.Execute(Add("ix_public")));

        Assert.AreEqual(PostgresErrorCodes.DuplicateTable, refused.SqlState);
    }

    /// <summary>And a name taken in another schema leaves it free there: the reverse of the row above.</summary>
    [TestMethod]
    public void The_server_takes_a_name_taken_in_another_schema_for_a_table_created_without_one()
    {
        using var database = new LiveDatabase();
        database.Execute(Tables);
        database.Execute("CREATE SCHEMA ops; CREATE TABLE ops.desks (id int); CREATE INDEX ix_ops ON ops.desks (id)");

        database.Execute(Add("ix_ops"));

        Assert.HasCount(2, database.Exclusions());
    }

    [TestMethod]
    public void The_server_takes_one_name_in_two_schemas()
    {
        using var database = new LiveDatabase();
        database.Execute(Tables);
        database.Execute("CREATE SCHEMA other; CREATE TABLE other.role_grants (period daterange)");

        database.Execute("ALTER TABLE other.role_grants ADD CONSTRAINT ex_other EXCLUDE USING gist (period WITH &&)");

        Assert.HasCount(2, database.Exclusions());
    }
}
