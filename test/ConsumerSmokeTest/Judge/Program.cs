using Npgsql;

// The judge of every path of the smoke test. SCHEMAKIT_TEST_PG is the server, as the integration
// tests take it: a connection string whose database this program never writes to, and from which
// it creates and drops the databases the script names.
//
//   version                       the server's own word for its version
//   connection <db>               the connection string of that database
//   create <db> | drop <db>       an empty database, or none
//   drop-prefix <prefix>          every database whose name starts with it (the script's clean-up)
//   apply <db> <file> <times>     a generated script, run as a DBA runs it, into a new database
//   check <db> shop|legacy        what the database holds, and what it refuses
var server = Environment.GetEnvironmentVariable("SCHEMAKIT_TEST_PG")
    ?? throw new InvalidOperationException("SCHEMAKIT_TEST_PG is not set.");

string Of(string database) => new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;

string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

void Admin(string sql)
{
    using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Pooling = false }.ConnectionString);
    connection.Open();
    using var command = new NpgsqlCommand(sql, connection);
    command.ExecuteNonQuery();
}

void Drop(string database) => Admin($"DROP DATABASE IF EXISTS {Quote(database)} WITH (FORCE)");

void Create(string database)
{
    Drop(database);
    Admin($"CREATE DATABASE {Quote(database)}");
}

switch (args[0])
{
    case "version":
    {
        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Pooling = false }.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SHOW server_version", connection);
        Console.WriteLine(command.ExecuteScalar());
        return 0;
    }

    case "connection":
        Console.WriteLine(Of(args[1]));
        return 0;

    case "create":
        Create(args[1]);
        return 0;

    case "drop":
        Drop(args[1]);
        return 0;

    case "drop-prefix":
    {
        List<string> names = [];
        using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = new NpgsqlCommand("SELECT datname FROM pg_database WHERE starts_with(datname, $1)", connection);
            command.Parameters.AddWithValue(args[1]);
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }

        foreach (var name in names) Drop(name);
        Console.WriteLine($"DROPPED: {names.Count}");
        return 0;
    }

    // The text of a script that "contains the constraint" proves nothing: a script without a
    // statement terminator passed that check in spike B and did not run. This runs it.
    case "apply":
    {
        Create(args[1]);
        var script = File.ReadAllText(args[2]);
        using var connection = new NpgsqlConnection(Of(args[1]));
        connection.Open();
        for (var i = 1; i <= int.Parse(args[3]); i++)
        {
            using var command = new NpgsqlCommand(script, connection);
            try
            {
                command.ExecuteNonQuery();
            }
            catch (PostgresException e)
            {
                Console.WriteLine($"APPLY-FAILED: run {i}: {e.SqlState}: {e.MessageText}");
                return 1;
            }
        }

        Console.WriteLine($"APPLIED: {args[3]} time(s)");
        return 0;
    }

    case "check":
    {
        using var connection = new NpgsqlConnection(Of(args[1]));
        connection.Open();
        Constraints(connection);
        return args[2] switch
        {
            "shop" => Behaviour(connection, "bookings", "\"RoomId\", \"Period\", \"CancelledOn\"", cancelled: true),
            "legacy" => Behaviour(connection, "\"Booking\"", "\"RoomId\", \"During\"", cancelled: false),
            _ => throw new InvalidOperationException($"Unknown model '{args[2]}'."),
        };
    }

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        return 2;
}

// Every exclusion constraint of the database, as the catalog states it, and btree_gist.
static void Constraints(NpgsqlConnection connection)
{
    using (var command = new NpgsqlCommand(
        """
        SELECT n.nspname || '.' || t.relname, c.conname, pg_get_constraintdef(c.oid)
        FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE c.contype = 'x'
        ORDER BY 1, 2
        """, connection))
    using (var reader = command.ExecuteReader())
    {
        var any = false;
        while (reader.Read())
        {
            any = true;
            Console.WriteLine($"CONSTRAINT {reader.GetString(0)} {reader.GetString(1)}: {reader.GetString(2)}");
        }

        if (!any) Console.WriteLine("CONSTRAINTS: none");
    }

    using var extension = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'", connection);
    Console.WriteLine((long)extension.ExecuteScalar()! == 1 ? "EXTENSION: btree_gist" : "EXTENSION: none");
}

// What the constraint is for: a second, overlapping booking of the room is refused (23P01). In the
// shop's model a cancelled one is not, which is the lambda condition at work. Rolled back, so that
// a path checked twice is checked on the same rows.
static int Behaviour(NpgsqlConnection connection, string table, string columns, bool cancelled)
{
    using (var exists = new NpgsqlCommand($"SELECT to_regclass('{table.Replace("'", "''")}') IS NOT NULL", connection))
        if (!(bool)exists.ExecuteScalar()!)
        {
            Console.WriteLine($"TABLE: {table} missing");
            return 0;
        }

    using var transaction = connection.BeginTransaction();
    string Row(string period, string? cancelledOn)
        => cancelled ? $"(1, '{period}', {(cancelledOn is null ? "NULL" : $"'{cancelledOn}'")})" : $"(1, '{period}')";

    string Try(string row)
    {
        transaction.Save("probe");
        try
        {
            using var insert = new NpgsqlCommand($"INSERT INTO {table} ({columns}) VALUES {row}", connection, transaction);
            insert.ExecuteNonQuery();
            transaction.Release("probe");
            return "accepted";
        }
        catch (PostgresException e)
        {
            transaction.Rollback("probe");
            return e.SqlState;
        }
    }

    Console.WriteLine($"FIRST: {Try(Row("[2026-01-01,2026-01-10)", null))}");
    Console.WriteLine($"OVERLAP: {Try(Row("[2026-01-05,2026-01-15)", null))}");
    if (cancelled) Console.WriteLine($"CANCELLED-OVERLAP: {Try(Row("[2026-01-05,2026-01-15)", "2026-01-02"))}");
    transaction.Rollback();
    return 0;
}
