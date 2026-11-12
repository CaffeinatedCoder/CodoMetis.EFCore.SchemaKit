namespace SchemaKit.Spikes.E;

public sealed record ExpectedCell(string Server, string Object, string Operation, Outcome Outcome);

/// <summary>
/// The impact table as measured. A row is an object; its outcomes are in the order of
/// <see cref="Operations" />. PostgreSQL 16 and 18 answer alike, so they share their rows.
/// </summary>
public static class Expected
{
    private static readonly string[] Operations =
        ["RenameColumn", "AlterColumn (type)", "AlterColumn (nullability)", "DropColumn", "RenameTable", "Schema move", "DropTable"];

    private static readonly string[] PostgresServers = ["PostgreSQL 16", "PostgreSQL 18"];

    private static readonly (string Object, string Outcomes)[] Postgres =
    [
        ("index (a)",                              "Follows Unaffected Unaffected Lost   Follows Follows Lost"),
        ("index ((a + 1))",                        "Follows Unaffected Unaffected Lost   Follows Follows Lost"),
        ("index (id) INCLUDE (a)",                 "Follows Unaffected Unaffected Lost   Follows Follows Lost"),
        ("index (id) WHERE a > 0",                 "Follows Unaffected Unaffected Lost   Follows Follows Lost"),
        ("EXCLUDE (a WITH =)",                     "Follows Unaffected Unaffected Lost   Follows Follows Lost"),
        ("EXCLUDE (during WITH &&) WHERE (a > 0)", "Follows Unaffected Unaffected Blocks Follows Follows Lost"),
        ("trigger UPDATE OF a",                    "Follows Blocks     Blocks     Blocks Follows Follows Lost"),
        ("trigger WHEN (NEW.a > 0)",               "Follows Blocks     Blocks     Blocks Follows Follows Lost"),
        ("trigger, function reads NEW.a",          "Stale   Unaffected Unaffected Stale  Follows Follows Lost"),
    ];

    // Measured in CI only: the image is amd64 and does not start under emulation on an ARM machine.
    private static readonly (string Object, string Outcomes)[] SqlServer =
    [
        ("index (a)",                            "Follows Blocks     Unaffected Blocks Follows Follows Lost"),
        ("index (id) INCLUDE (a)",               "Follows Blocks     Unaffected Blocks Follows Follows Lost"),
        ("index (id) WHERE a > 0",               "Blocks  Blocks     Blocks     Blocks Follows Follows Lost"),
        ("unique index (a) WHERE a IS NOT NULL", "Blocks  Blocks     Blocks     Blocks Follows Follows Lost"),
        ("trigger, no column",                   "Unaffected Unaffected Unaffected Unaffected Follows Follows Lost"),
        ("trigger IF UPDATE(a)",                 "Stale   Unaffected Unaffected Stale  Follows Follows Lost"),
        ("trigger, body reads inserted.a",       "Stale   Unaffected Unaffected Stale  Follows Follows Lost"),
    ];

    public static IEnumerable<ExpectedCell> Cells =>
        PostgresServers.SelectMany(server => Rows(server, Postgres)).Concat(Rows("SQL Server 2022", SqlServer));

    private static IEnumerable<ExpectedCell> Rows(string server, (string Object, string Outcomes)[] rows)
        => rows.SelectMany(row => row.Outcomes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((outcome, i) => new ExpectedCell(server, row.Object, Operations[i], Enum.Parse<Outcome>(outcome))));
}
