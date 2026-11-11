using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.Tests.Integration;

/// <summary>
/// The PostgreSQL server the integration tests run against: <c>SCHEMAKIT_TEST_PG</c>, an Npgsql
/// connection string to a database the tests may create schemas and tables in.
/// </summary>
/// <remarks>
/// Without it a local run goes inconclusive. When <c>CI</c> is set it fails instead: an unreachable
/// server must not quietly retire the tests that touch a database while the build reports green.
/// </remarks>
internal static class PostgresServer
{
    public const string Variable = "SCHEMAKIT_TEST_PG";

    public static NpgsqlConnection Open()
    {
        var connectionString = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                Assert.Fail($"{Variable} is not set, and this is CI: the integration tests must run here.");
            Assert.Inconclusive($"{Variable} is not set; no PostgreSQL server to test against.");
        }

        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static void Execute(this NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    public static List<string> Column(this NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }
}
