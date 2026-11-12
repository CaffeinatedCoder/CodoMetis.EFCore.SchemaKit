using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SchemaKit.Spikes.E;

public sealed class Postgres : Server
{
    private Postgres(string name, NpgsqlConnection connection, DbContext context) : base(name, connection, context)
    {
    }

    /// <summary>Connects to the server and gives the spike a database of its own, made anew.</summary>
    public static Postgres Open(string name, string variable)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString(variable));
        using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            admin.Open();
            using var command = admin.CreateCommand();
            command.CommandText = "DROP DATABASE IF EXISTS spike_e WITH (FORCE)";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE DATABASE spike_e";
            command.ExecuteNonQuery();
        }

        builder.Database = "spike_e";
        builder.Pooling = false;
        var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        var server = new Postgres(name, connection, new DbContext(new DbContextOptionsBuilder().UseNpgsql(builder.ConnectionString).Options));
        server.Execute("CREATE EXTENSION btree_gist");
        return server;
    }

    public override IReadOnlyList<ObjectKind> Objects =>
    [
        new("index (a)", "CREATE INDEX o ON {s}.t (a)"),
        new("index ((a + 1))", "CREATE INDEX o ON {s}.t ((a + 1))"),
        new("index (id) INCLUDE (a)", "CREATE INDEX o ON {s}.t (id) INCLUDE (a)"),
        new("index (id) WHERE a > 0", "CREATE INDEX o ON {s}.t (id) WHERE a > 0"),
        new("EXCLUDE (a WITH =)", "ALTER TABLE {s}.t ADD CONSTRAINT o EXCLUDE USING gist (a WITH =)"),
        new("EXCLUDE (during WITH &&) WHERE (a > 0)", "ALTER TABLE {s}.t ADD CONSTRAINT o EXCLUDE USING gist (during WITH &&) WHERE (a > 0)"),
        new("trigger UPDATE OF a", "CREATE TRIGGER o BEFORE UPDATE OF a ON {s}.t FOR EACH ROW EXECUTE FUNCTION {s}.note()", IsTrigger: true),
        new("trigger WHEN (NEW.a > 0)", "CREATE TRIGGER o BEFORE INSERT ON {s}.t FOR EACH ROW WHEN (NEW.a > 0) EXECUTE FUNCTION {s}.note()", IsTrigger: true),
        new("trigger, function reads NEW.a", "CREATE TRIGGER o BEFORE INSERT ON {s}.t FOR EACH ROW EXECUTE FUNCTION {s}.guard()", IsTrigger: true),
    ];

    protected override string Int => "integer";

    protected override string BigInt => "bigint";

    protected override void CreateTable(string schema)
    {
        Execute($"CREATE SCHEMA {schema}");
        Execute($"CREATE TABLE {schema}.t (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, a int NOT NULL, b text NOT NULL, during tsrange NOT NULL, other int)");
        Execute($"CREATE TABLE {schema}.log (at timestamptz DEFAULT now())");
        Execute($"CREATE FUNCTION {schema}.note() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO {schema}.log DEFAULT VALUES; RETURN NEW; END $$");
        Execute($"CREATE FUNCTION {schema}.guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.a < 0 THEN RAISE EXCEPTION 'negative'; END IF; INSERT INTO {schema}.log DEFAULT VALUES; RETURN NEW; END $$");
    }

    protected override object Find(string schema) => Scalar<long>($"SELECT '{schema}.t'::regclass::oid");

    protected override (string Definition, string Identity)? Definition(object handle, ObjectKind kind)
    {
        var table = (long)handle;
        var row = Rows(kind.Create.StartsWith("CREATE INDEX", StringComparison.Ordinal)
            ? $"SELECT pg_get_indexdef(i.indexrelid), i.indexrelid::text FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = {table} AND c.relname = 'o'"
            : kind.IsTrigger
                ? $"SELECT pg_get_triggerdef(oid), oid::text FROM pg_trigger WHERE tgrelid = {table} AND tgname = 'o'"
                : $"SELECT conrelid::regclass::text || ' ' || pg_get_constraintdef(oid), oid::text FROM pg_constraint WHERE conrelid = {table} AND conname = 'o'");
        return row.Count == 0 ? null : row[0];
    }

    protected override string? Probe(string schema)
    {
        // The table by what it was created as and by where an operation may have put it.
        var table = Scalar<string>(
            $"SELECT c.oid::regclass::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            $"WHERE c.relkind = 'r' AND c.relname IN ('t', 't2') AND n.nspname IN ('{schema}', '{schema}_moved')");
        if (table is null) return null;

        var columns = Rows(
            $"SELECT quote_ident(attname), atttypid::regtype::text FROM pg_attribute " +
            $"WHERE attrelid = '{table}'::regclass AND attnum > 0 AND NOT attisdropped AND attidentity = '' ORDER BY attnum");
        var values = columns.Select(c => c.Type switch
        {
            "integer" or "bigint" => "1",
            "text"                => "'x'",
            "tsrange"             => "'[2020-01-01,2020-01-02)'",
            var other             => throw new InvalidOperationException($"No value for a {other} column."),
        });

        Execute($"INSERT INTO {table} ({string.Join(", ", columns.Select(c => c.Name))}) VALUES ({string.Join(", ", values)})");
        Execute($"UPDATE {table} SET {string.Join(", ", columns.Select(c => $"{c.Name} = {c.Name}"))}");
        return null;
    }

    protected override long LogCount(string schema) => Scalar<long>($"SELECT count(*) FROM {schema}.log");

    protected override string Short(DbException exception)
        => exception is PostgresException pg ? $"{pg.SqlState}: {pg.MessageText}" : exception.Message;
}
