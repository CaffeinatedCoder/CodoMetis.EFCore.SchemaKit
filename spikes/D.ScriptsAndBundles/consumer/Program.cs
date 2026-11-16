using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Consumer;

// The judge. It reads the catalog and applies a generated script; it never goes through EF Core,
// so what it reports is what the server holds and says.
//
//   version                          the server SPIKE_D_SERVER names
//   connection <database>            SPIKE_D_SERVER's connection string for that database
//   create <database>                on SPIKE_D_SERVER
//   drop-prefix <prefix>             every database on SPIKE_D_SERVER whose name starts with it
//   apply <script> <stop|continue>   to SPIKE_D_CONNECTION, the way the provider's own tool does
//   check                            what SPIKE_D_CONNECTION's database holds
//   client <database>                host, port, user, password and database, tab-separated: what
//                                    psql or sqlcmd needs to reach that database on SPIKE_D_SERVER
//
// A script is applied as psql and sqlcmd apply one, not as one command: psql sends a script
// statement by statement, sqlcmd batch by batch at each GO. `continue` goes on after an error,
// which is what both tools do unless told otherwise; `stop` is psql -v ON_ERROR_STOP=1 and
// sqlcmd -b. Sent as one command a script behaves differently on both servers, and a statement
// that cannot run in a transaction is exactly where.

if (args.Length == 0) return Usage();

try
{
    switch (args[0])
    {
        case "version":
            Console.WriteLine(Judge.Version());
            return 0;

        case "connection" when args.Length == 2:
            Console.WriteLine(Judge.ConnectionTo(args[1]));
            return 0;

        case "create" when args.Length == 2:
            Judge.Create(args[1]);
            return 0;

        case "drop-prefix" when args.Length == 2:
            Judge.DropPrefix(args[1]);
            return 0;

        case "apply" when args.Length == 3 && args[2] is "stop" or "continue":
            return Judge.Apply(args[1], stop: args[2] == "stop");

        case "check":
            Judge.Check();
            return 0;

        case "client" when args.Length == 2:
            Console.WriteLine(string.Join('\t', Judge.Client(args[1])));
            return 0;

        default:
            return Usage();
    }
}
catch (DbException e)
{
    Console.WriteLine($"JUDGE-FAILED {Judge.Code(e)}: {e.Message.Split('\n')[0]}");
    return 5;
}

static int Usage()
{
    Console.Error.WriteLine("usage: version | connection <database> | client <database> | create <database> | drop-prefix <prefix> | apply <script> <stop|continue> | check");
    return 2;
}

internal static partial class Judge
{
    private static string Server => Environment.GetEnvironmentVariable("SPIKE_D_SERVER")
                                    ?? throw new InvalidOperationException("SPIKE_D_SERVER is not set.");

    private static string Database => Environment.GetEnvironmentVariable("SPIKE_D_CONNECTION")
                                      ?? throw new InvalidOperationException("SPIKE_D_CONNECTION is not set.");

    // ---- applying a script ----------------------------------------------------------------------

    public static int Apply(string file, bool stop)
    {
        var parts = Split(File.ReadAllText(file));
        using var connection = Open(Database);
        var errors = 0;

        foreach (var part in parts)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = part;
                command.CommandTimeout = 300;
                command.ExecuteNonQuery();
            }
            catch (DbException e)
            {
                errors++;
                Console.WriteLine($"ERROR {Code(e)}: {Text(e)}");
                Console.WriteLine("STATEMENT:");
                foreach (var line in part.Split('\n')) Console.WriteLine($"    {line.TrimEnd('\r')}");
                Console.WriteLine("END-STATEMENT");

                if (!stop) continue;
                Console.WriteLine($"ERRORS: {errors} of {parts.Count}, stopped at the first");
                return 4;
            }
        }

        Console.WriteLine($"ERRORS: {errors} of {parts.Count}");
        return errors == 0 ? 0 : 4;
    }

    // ---- reading the catalog --------------------------------------------------------------------

    public static void Check()
    {
        using var connection = Open(Database);

        var table = Scalar(connection, TableSql) is not null and not DBNull;
        Console.WriteLine($"TABLE: {(table ? "present" : "missing")}");

        var history = Scalar(connection, HistoryTableSql) is not null and not DBNull
            ? Convert.ToInt32(Scalar(connection, HistoryRowsSql)).ToString()
            : "missing";
        Console.WriteLine($"HISTORY: {history}");

        var (verdict, id, body) = Forms.IsTrigger ? Trigger(connection, table) : Index(connection);
        Console.WriteLine($"STATE: table={(table ? "present" : "missing")} history={history} object={verdict} id={id} body={body}");
    }

    /// <summary>The trigger by the catalog, and by what it is for: a row it must refuse.</summary>
    private static (string Verdict, string Id, string Body) Trigger(DbConnection connection, bool table)
    {
        var (found, id, body) = TriggerInCatalog(connection);
        Console.WriteLine($"TRIGGER {Forms.ObjectName}: {found}");

        if (!table) return (found, id, body);

        var good = Insert(connection, "fine");
        var bad  = Insert(connection, "refused");
        Console.WriteLine($"GOOD-ROW: {good}");
        Console.WriteLine($"REFUSED-ROW: {bad}");

        if (found != "enabled") return (found, id, body);
        return good == "accepted" && bad == RefusalCode ? ("ok", id, body) : ("not-enforcing", id, body);
    }

    /// <summary>Inserts a row and takes it back. The answer is "accepted" or the server's error code.</summary>
    private static string Insert(DbConnection connection, string name)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertSql(name);
            command.ExecuteNonQuery();
            return "accepted";
        }
        catch (DbException e)
        {
            return Code(e);
        }
        finally
        {
            // A trigger that throws has ended the transaction on SQL Server already.
            try { transaction.Rollback(); }
            catch (Exception e) when (e is DbException or InvalidOperationException) { }
        }
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<object?[]> Rows(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        List<object?[]> rows = [];
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row);
        }

        return rows;
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

#if NPGSQL

    // ---- PostgreSQL -----------------------------------------------------------------------------

    private const string TableSql        = """SELECT to_regclass('"Things"')::text""";
    private const string HistoryTableSql = """SELECT to_regclass('"__EFMigrationsHistory"')::text""";
    private const string HistoryRowsSql  = """SELECT count(*) FROM "__EFMigrationsHistory" """;
    private const string RefusalCode     = "23514";

    private static string InsertSql(string name) => $"""INSERT INTO "Things" ("Name") VALUES ('{name}')""";

    private static DbConnection Open(string connectionString)
    {
        // No pooling: a pooled connection the judge left behind would hold the database open.
        var connection = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    public static string Code(DbException e) => e is Npgsql.PostgresException postgres ? postgres.SqlState : e.GetType().Name;

    /// <summary>The server's own words. Npgsql's message puts the SQLSTATE in front of them.</summary>
    private static string Text(DbException e) => e is Npgsql.PostgresException postgres ? postgres.MessageText : e.Message.Split('\n')[0].Trim();

    public static string ConnectionTo(string database)
        => new Npgsql.NpgsqlConnectionStringBuilder(Server) { Database = database }.ConnectionString;

    public static string[] Client(string database)
    {
        var server = new Npgsql.NpgsqlConnectionStringBuilder(Server);
        return [server.Host ?? "localhost", server.Port.ToString(), server.Username ?? "postgres", server.Password ?? "", database];
    }

    public static string Version()
    {
        using var connection = Open(Server);
        return $"PostgreSQL {((string)Scalar(connection, "SHOW server_version")!).Split(' ')[0]}";
    }

    public static void Create(string database)
    {
        using var connection = Open(Server);
        Execute(connection, $"""CREATE DATABASE "{database}" """);
    }

    public static void DropPrefix(string prefix)
    {
        using var connection = Open(Server);
        foreach (var row in Rows(connection, $"SELECT datname FROM pg_database WHERE datname LIKE '{prefix}%'"))
            Execute(connection, $"""DROP DATABASE "{row[0]}" WITH (FORCE)""");
    }

    /// <summary>An index a failed CREATE INDEX CONCURRENTLY leaves behind is in the catalog and invalid; that is not "there".</summary>
    private static (string Verdict, string Id, string Body) Index(DbConnection connection)
    {
        var rows = Rows(connection,
            $"""
             SELECT c.oid::text, i.indisvalid, pg_get_indexdef(c.oid)
             FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid
             WHERE c.relname = '{Forms.ObjectName}'
             """);
        if (rows.Count == 0)
        {
            Console.WriteLine($"INDEX {Forms.ObjectName}: missing");
            return ("missing", "-", "-");
        }

        var valid = (bool)rows[0][1]!;
        Console.WriteLine($"INDEX {Forms.ObjectName}: {(valid ? "valid" : "invalid")}: {rows[0][2]}");
        return (valid ? "ok" : "invalid", (string)rows[0][0]!, "-");
    }

    /// <summary>The body is the function's source as the catalog keeps it, by its length and hash.</summary>
    private static (string Found, string Id, string Body) TriggerInCatalog(DbConnection connection)
    {
        var rows = Rows(connection,
            $"""
             SELECT t.oid::text, t.tgenabled::text, p.oid::text, p.proname::text, length(p.prosrc)::text || ':' || md5(p.prosrc)
             FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
             WHERE t.tgname = '{Forms.ObjectName}' AND NOT t.tgisinternal
             """);
        if (rows.Count == 0) return ("missing", "-", "-");

        Console.WriteLine($"FUNCTION {rows[0][3]}: present");
        return ((string)rows[0][1]! == "O" ? "enabled" : "disabled", $"{rows[0][0]}+{rows[0][2]}", (string)rows[0][4]!);
    }

    /// <summary>
    /// The script's statements, as psql finds them: ended by a semicolon that is not inside a
    /// string, a quoted name, a comment or a dollar-quoted body.
    /// </summary>
    private static List<string> Split(string script)
    {
        List<string> statements = [];
        var current = new StringBuilder();
        var i = 0;

        void Take(int end)
        {
            current.Append(script, i, end - i);
            i = end;
        }

        while (i < script.Length)
        {
            var c = script[i];
            if (c is '\'' or '"')
            {
                var end = i + 1;
                while (end < script.Length)
                {
                    if (script[end] != c) { end++; continue; }
                    if (end + 1 < script.Length && script[end + 1] == c) { end += 2; continue; }
                    break;
                }

                Take(Math.Min(end + 1, script.Length));
            }
            else if (c == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                var end = script.IndexOf('\n', i);
                Take(end < 0 ? script.Length : end);
            }
            else if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                var end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Take(end < 0 ? script.Length : end + 2);
            }
            else if (c == '$' && DollarTag().Match(script, i) is { Success: true } tag && tag.Index == i
                     && (i == 0 || !(char.IsLetterOrDigit(script[i - 1]) || script[i - 1] == '_')))
            {
                var end = script.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                Take(end < 0 ? script.Length : end + tag.Length);
            }
            else if (c == ';')
            {
                current.Append(c);
                i++;
                Flush();
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        Flush();
        return statements;

        void Flush()
        {
            var statement = current.ToString().Trim();
            current.Clear();
            if (statement.Length > 0 && statement != ";") statements.Add(statement);
        }
    }

    [GeneratedRegex(@"\G\$([A-Za-z_][A-Za-z0-9_]*)?\$")]
    private static partial Regex DollarTag();

#elif SQLSERVER

    // ---- SQL Server -----------------------------------------------------------------------------

    private const string TableSql        = "SELECT OBJECT_ID(N'[Things]', N'U')";
    private const string HistoryTableSql = "SELECT OBJECT_ID(N'[__EFMigrationsHistory]', N'U')";
    private const string HistoryRowsSql  = "SELECT COUNT(*) FROM [__EFMigrationsHistory]";
    private const string RefusalCode     = "51000";

    private static string InsertSql(string name) => $"INSERT INTO [Things] ([Name]) VALUES (N'{name}')";

    private static DbConnection Open(string connectionString)
    {
        var connection = new Microsoft.Data.SqlClient.SqlConnection(
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    public static string Code(DbException e) => e is Microsoft.Data.SqlClient.SqlException sql ? sql.Number.ToString() : e.GetType().Name;

    private static string Text(DbException e) => e.Message.Split('\n')[0].Trim();

    public static string ConnectionTo(string database)
        => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Server) { InitialCatalog = database }.ConnectionString;

    /// <summary>sqlcmd takes the server as SqlClient does, "host,port", so the port stays in it.</summary>
    public static string[] Client(string database)
    {
        var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(Server);
        return [server.DataSource, "-", server.UserID, server.Password, database];
    }

    public static string Version()
    {
        using var connection = Open(Server);
        var row = Rows(connection, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128))")[0];
        return $"SQL Server {row[0]} ({row[1]})";
    }

    public static void Create(string database)
    {
        using var connection = Open(Server);
        Execute(connection, $"CREATE DATABASE [{database}]");
    }

    public static void DropPrefix(string prefix)
    {
        using var connection = Open(Server);
        foreach (var row in Rows(connection, $"SELECT name FROM sys.databases WHERE name LIKE N'{prefix}%'"))
        {
            Execute(connection, $"ALTER DATABASE [{row[0]}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Execute(connection, $"DROP DATABASE [{row[0]}]");
        }
    }

    private static (string Verdict, string Id, string Body) Index(DbConnection connection)
    {
        var rows = Rows(connection,
            $"SELECT CONCAT(i.object_id, '.', i.index_id), i.is_disabled FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'[Things]', N'U') AND i.name = N'{Forms.ObjectName}'");
        if (rows.Count == 0)
        {
            Console.WriteLine($"INDEX {Forms.ObjectName}: missing");
            return ("missing", "-", "-");
        }

        var disabled = (bool)rows[0][1]!;
        Console.WriteLine($"INDEX {Forms.ObjectName}: {(disabled ? "disabled" : "enabled")}");
        return (disabled ? "disabled" : "ok", (string)rows[0][0]!, "-");
    }

    /// <summary>The body is the trigger's definition as the catalog keeps it, by its length and hash.</summary>
    private static (string Found, string Id, string Body) TriggerInCatalog(DbConnection connection)
    {
        var rows = Rows(connection,
            $"""
             SELECT CONCAT(t.object_id, '@', CONVERT(varchar(33), t.modify_date, 126)), t.is_disabled,
                    CONCAT(LEN(m.definition), ':', CONVERT(varchar(32), HASHBYTES('MD5', m.definition), 2))
             FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id
             WHERE t.name = N'{Forms.ObjectName}'
             """);
        return rows.Count == 0
            ? ("missing", "-", "-")
            : ((bool)rows[0][1]! ? "disabled" : "enabled", (string)rows[0][0]!, (string)rows[0][2]!);
    }

    /// <summary>The script's batches, as sqlcmd finds them: ended by a line that says GO and nothing else.</summary>
    private static List<string> Split(string script)
    {
        List<string> batches = [];
        var current = new StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (!Go().IsMatch(line))
            {
                current.Append(line).Append('\n');
                continue;
            }

            Flush();
        }

        Flush();
        return batches;

        void Flush()
        {
            var batch = current.ToString().Trim();
            current.Clear();
            if (batch.Length > 0) batches.Add(batch);
        }
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Go();

#else

    // ---- SQLite: only what the annotations are measured on --------------------------------------

    private const string TableSql        = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'Things'";
    private const string HistoryTableSql = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory'";
    private const string HistoryRowsSql  = "SELECT count(*) FROM \"__EFMigrationsHistory\"";
    private const string RefusalCode     = "-";

    private static string InsertSql(string name) => $"INSERT INTO \"Things\" (\"Name\") VALUES ('{name}')";

    private static DbConnection Open(string connectionString)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    public static string Code(DbException e) => e is Microsoft.Data.Sqlite.SqliteException sqlite ? sqlite.SqliteErrorCode.ToString() : e.GetType().Name;

    private static string Text(DbException e) => e.Message.Split('\n')[0].Trim();

    public static string ConnectionTo(string database) => throw new NotSupportedException("SQLite has no server: SPIKE_D_CONNECTION names the file.");

    public static string[] Client(string database) => throw new NotSupportedException("SQLite has no server: SPIKE_D_CONNECTION names the file.");

    public static string Version()
    {
        using var connection = Open(Database);
        return $"SQLite {Scalar(connection, "SELECT sqlite_version()")}";
    }

    public static void Create(string database) => throw new NotSupportedException("SQLite has no server: SPIKE_D_CONNECTION names the file.");

    public static void DropPrefix(string prefix) => throw new NotSupportedException("SQLite has no server: SPIKE_D_CONNECTION names the file.");

    private static (string Verdict, string Id, string Body) Index(DbConnection connection)
    {
        var found = Scalar(connection, $"SELECT name FROM sqlite_master WHERE type = 'index' AND name = '{Forms.ObjectName}'") is string;
        Console.WriteLine($"INDEX {Forms.ObjectName}: {(found ? "present" : "missing")}");
        return (found ? "ok" : "missing", found ? Forms.ObjectName : "-", "-");
    }

    private static (string Found, string Id, string Body) TriggerInCatalog(DbConnection connection) => ("missing", "-", "-");

    private static List<string> Split(string script) => [script];

#endif
}
