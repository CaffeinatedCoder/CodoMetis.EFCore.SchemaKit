# Spike D: SqlOperations in idempotent scripts and bundles

For [#19](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/19), and for point 4
of [#115](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/115). Throwaway
code: it leaves when the consumer smoke test applies the real contributors' scripts and bundles
the same way.

**Question 1.** Every object SchemaKit creates reaches a migration as a `SqlOperation` rendered at
design time. Does one that suppresses the transaction (`CREATE INDEX CONCURRENTLY` on PostgreSQL,
`CREATE INDEX … WITH (ONLINE = ON)` on SQL Server) and a trigger (with its function on PostgreSQL;
on SQL Server, where `CREATE TRIGGER` must start its batch) survive
`dotnet ef migrations script --idempotent`, the script applied twice, and a migrations bundle, run
twice?

**Answer.** A bundle carries the statements of the question, on PostgreSQL 16 and 18, which
answer alike, and on SQL Server 2022. A script does not carry two of them, and after one of the
two the migration counts as applied while its object is missing:

| Statement | Does not survive | What happens |
|---|---|---|
| PostgreSQL: `CREATE INDEX CONCURRENTLY` | `migrations script --idempotent` | The script puts each statement into a `DO` block, and PostgreSQL refuses this one there (`25001`). **psql goes on and exits 0:** the migration is recorded as applied, the index is missing, and no later run creates it |
| SQL Server: `CREATE TRIGGER` | `migrations script`, with `--idempotent` and without | EF Core writes a migration's statements into one batch. `CREATE TRIGGER` must start its batch (`111`) and cannot stand inside the idempotent script's `IF … BEGIN … END` (`156`). The batch does not compile, so nothing of the migration is applied, on the second run as on the first. sqlcmd prints the error and exits 0 unless it is run with `-b` |

PostgreSQL's trigger with its function survives every path. So does a statement with the
transaction suppressed on SQL Server, and there the suppression is what carries it:
`ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON`, which the server refuses inside a
transaction, applies on every path with the transaction suppressed and fails on every path
without. `CREATE INDEX … WITH (ONLINE = ON)` survives too, and says nothing about suppression:
SQL Server builds such an index inside a transaction as well.

On SQL Server the trigger survives every path inside `EXEC(N'CREATE TRIGGER …')`, and the contract
can write that for a statement on one line only. With line breaks, what the contract writes is not
valid T-SQL and fails on every path, the bundle included:
[the trigger's forms](#sql-servers-trigger-three-forms-of-exec).

**Question 2.** What does EF Core do with an annotation on a `SqlOperation`: does `migrations add`
write it into the migration, is it there when the SQL generator sees the operation, and does it
survive a script and a bundle?

**Answer: it writes it, and the migration carries it everywhere.** `migrations add` writes each
annotation as `.Annotation(name, value)` on the `migrationBuilder.Sql(…)` call, in `Up` and in
`Down`. The generator SchemaKit's decorator wraps then receives the operation with every annotation
with its type and value, at `migrations script`, at `migrations script --idempotent` and when a bundle applies
the migration, on Npgsql, SQL Server and SQLite. A value EF Core has no C# literal for fails
`migrations add`, which writes nothing. [The table](#question-2-an-annotation-on-a-sqloperation).

```bash
SPIKE_D_PG16="Host=localhost;Port=…;Username=postgres;Password=…;Database=postgres" \
SPIKE_D_PG18="Host=localhost;Port=…;Username=postgres;Password=…;Database=postgres" \
SPIKE_D_MSSQL="Server=localhost,1433;User Id=sa;Password=…;TrustServerCertificate=True" \
spikes/D.ScriptsAndBundles/run.sh
```

Each variable is a connection string to a server the script may create databases on. Where one is
not set, spike E's variable for the same server is read (`SPIKE_E_PG16`, `SPIKE_E_PG18`,
`SPIKE_E_MSSQL`), which is how CI's spikes lane runs this spike without a line of its own. A
server neither names is reported as not measured, and fails the run when `CI` is set. Every run of
a path gets a database of its own, `spike_d_<time>_<pid>_<n>`, and the script drops them on exit
(`SPIKE_KEEP=1` keeps them). `SPIKE_ONLY="npgsql-trigger sqlserver-online"` runs those consumers
only. A run that measured only part says so in its last lines: `NOT MEASURED: …`.

`SPIKE_D_PG16_CONTAINER`, `SPIKE_D_PG18_CONTAINER` and `SPIKE_D_MSSQL_CONTAINER` name the docker
container a server runs in, where it is one of the official images. Its own psql or sqlcmd is then
what [the judge is held against](#how-a-cell-is-measured).

Measured on 2026-11-16 with EF Core `11.0.0-rc.3.26522.148` and Npgsql EF `11.0.0-rc.3`, twice:
on a developer's machine against PostgreSQL 16.15 and 18.6 and SQL Server 2022 (16.0.4305.5,
Developer Edition), and in CI against PostgreSQL 16.16 and 18.7 and SQL Server 2022 (16.0.4325.4,
Developer Edition). Every cell answered the same in both places.

## Question 1: the table

The script's `expected` holds this table, cell by cell, and a cell that answers otherwise fails
the run. "Applies" is: no error, the object is in the catalog (an index valid, a trigger refusing
the row it must refuse, a database option on), and the migration is recorded. "Nothing changed"
is: no error, and the catalog holds the same objects under the same identities as after the first
run. [Which identities](#how-a-cell-is-measured), and that each is one a recreation changes, is
below.

### PostgreSQL 16 and 18

| Statement | Path | First run | Second run | Error |
|---|---|---|---|---|
| `CREATE INDEX CONCURRENTLY`, transaction suppressed | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | **Error, and the script goes on:** the table is there, the index is missing, the migration is recorded as applied | No error, nothing changed: the index stays missing | `25001: CREATE INDEX CONCURRENTLY cannot be executed from a function` |
| | the same, stopping at the first error (`psql -v ON_ERROR_STOP=1`) | Error: the table is there, the index is missing, no migration is recorded | Error, nothing changed | `25001`, as above; second run: `42P07: relation "Things" already exists` |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| `CREATE FUNCTION` with a body, and `CREATE TRIGGER` | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| `CREATE INDEX`, the control | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |

The transaction-bound case of an exclusion constraint was measured before, on the same two
versions (the table on #34).

### SQL Server 2022

| Statement | Path | First run | Second run | Error |
|---|---|---|---|---|
| `ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON`, transaction suppressed | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| The same, the transaction not suppressed: the control | `migrations script`, applied once | **Error, and the batch goes on:** the table is there, the option is off, the migration is recorded as applied | not applicable | `226: ALTER DATABASE statement not allowed within multi-statement transaction.` |
| | `migrations script --idempotent`, applied twice | **Error, and the batch goes on:** the same | No error, nothing changed: the option stays off | `226`, as above |
| | the same, stopping at the first error (`sqlcmd -b`) | The same; sqlcmd exits 1 after the batch has committed | No error, nothing changed; sqlcmd exits 0 | `226`, as above |
| | `migrations bundle`, run twice | **Error** (the bundle exits 1): no table, the option is off, no migration recorded | The same error, nothing changed | `226`, as above |
| `CREATE INDEX … WITH (ONLINE = ON)`, transaction suppressed | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| `CREATE TRIGGER` | `migrations script`, applied once | **Error:** no table, no trigger, no migration recorded | not applicable | `111: 'CREATE TRIGGER' must be the first statement in a query batch.` |
| | `migrations script --idempotent`, applied twice | **Error:** no table, no trigger, no migration recorded | The same error, nothing changed | `156: Incorrect syntax near the keyword 'TRIGGER'.` |
| | the same, stopping at the first error (`sqlcmd -b`) | The same | The same | `156`, as above |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| `EXEC(N'CREATE TRIGGER …')`, on one line | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| `EXEC(…)` of the trigger with its line breaks, the constant written by the dialect | `migrations script`, applied once | **Error:** no table, no trigger, no migration recorded | not applicable | `102: Incorrect syntax near 'CONCAT'.` |
| | `migrations script --idempotent`, applied twice | **Error:** the same | The same error, nothing changed | `102`, as above |
| | `migrations bundle`, run twice | **Error:** the same; the bundle exits 1 | The same error, nothing changed | `102`, as above |
| `EXEC(N'CREATE TRIGGER …')`, the line breaks inside the constant | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |
| `CREATE INDEX`, the control | `migrations script`, applied once | Applies | not applicable | |
| | `migrations script --idempotent`, applied twice | Applies | Nothing changed | |
| | `migrations bundle`, run twice | Applies | Nothing changed | |

The first two statements are the ones that say whether the suppression arrives: the server
refuses `ALTER DATABASE` inside a transaction, so it applies only where EF Core ends the
transaction before it. The `ONLINE` index does not say it. By hand on the same server,
`BEGIN TRANSACTION; CREATE INDEX … WITH (ONLINE = ON); COMMIT;` creates the index. `ONLINE = ON`
needs an edition that has it; the image's is Developer Edition.

## Each failure, with the SQL that caused it

**PostgreSQL, `CREATE INDEX CONCURRENTLY` in the idempotent script.** EF Core ends the transaction
before a statement that suppresses it, and then wraps the statement like every other:

```sql
COMMIT;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116125314_Initial') THEN
    CREATE INDEX CONCURRENTLY ix_things_name ON "Things" ("Name");
    END IF;
END $EF$;
```

```
ERROR:  CREATE INDEX CONCURRENTLY cannot be executed from a function
CONTEXT:  SQL statement "CREATE INDEX CONCURRENTLY ix_things_name ON "Things" ("Name")"
PL/pgSQL function inline_code_block line 4 at SQL statement
```

The block after it inserts the migration's row into `__EFMigrationsHistory`, outside any
transaction, and succeeds. psql reports the error, runs the rest and exits 0, so the second run
finds the migration recorded and does nothing. With `ON_ERROR_STOP=1` psql exits 3 at the error;
the table is committed by then and the migration is not recorded, so the second run fails on
`CREATE TABLE "Things"` (`42P07`). The script cannot be applied either way.

**SQL Server, `CREATE TRIGGER` in a script.** EF Core 11 ends a batch where a transaction ends,
not after each statement. The script for an empty database is one batch for the whole migration:

```sql
BEGIN TRANSACTION;
CREATE TABLE [Things] (
    [Id] int NOT NULL IDENTITY,
    [Name] nvarchar(100) NOT NULL,
    CONSTRAINT [PK_Things] PRIMARY KEY ([Id])
);

CREATE TRIGGER [tr_things_guard] ON [Things] AFTER INSERT, UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM inserted WHERE [Name] = N'refused')
        THROW 51000, N'spike D: the row is refused', 1;
END;

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261116125513_Initial', N'11.0.0-rc.3.26522.148');

COMMIT;
GO
```

```
Msg 111, Level 15, State 1, Line 9
'CREATE TRIGGER' must be the first statement in a query batch.
```

The idempotent script is the same batch with each statement inside
`IF NOT EXISTS (…) BEGIN … END;`, and fails with
`Msg 156, Level 15, State 1, Line 20: Incorrect syntax near the keyword 'TRIGGER'.` Neither batch
compiles, so nothing in it runs: the database holds the history table and nothing else, and the
second run is the first again. sqlcmd exits 0 after either; with `-b` it exits 1. EF Core 11.0.0
writes the same two scripts, character for character but for the migration's id and the product
version (compared by hand for this consumer; not applied).

A bundle sends each statement as a command of its own, where `CREATE TRIGGER` is first in its
batch.

## SQL Server's trigger: three forms of `EXEC`

The trigger inside a string the server executes is a statement that may stand anywhere, so it
needs no batch of its own. It is not part of the issue's question; it is what decides whether
"refused at scaffold" is the only answer for a trigger on SQL Server.

| Form | Outcome |
|---|---|
| `EXEC(N'CREATE TRIGGER … END')`, the statement on one line, the constant written by `ISchemaDialect.Literal` | Applies on every path |
| The same with the trigger's line breaks, through `Literal` | **Fails on every path, the bundle too** (`102: Incorrect syntax near 'CONCAT'.`): EF Core's literal for a string with line breaks is `CONCAT(CAST(N'…' AS nvarchar(max)), nchar(10), N'…')`, and `EXEC` takes constants and variables, no function call |
| One constant with the line breaks inside it, the quotes doubled by hand | Applies on every path |

The third form is what a trigger with a body of several lines needs, and the contract cannot
write it: `Literal` is the only way a text becomes a string constant, and a name written into a
constant by hand could end it. The spike writes it by hand to show what the server takes, not how
a contributor may write it.

```csharp
// What the contract can write today: one line, the constant from the dialect.
var trigger = sql.Fragment($"CREATE TRIGGER {name} ON {table} AFTER INSERT, UPDATE AS BEGIN … END");
sql.Statement($"EXEC({sql.Literal(trigger.ToString())})");
```

## Question 2: an annotation on a `SqlOperation`

The statement is the control's `CREATE INDEX`. Its annotations are put on the operation the
kernel's planner built, the instance `SchemaKitModelDiffer` hands to EF Core. The kernel has no way
to say that yet, so the spike's contributor reaches `SchemaSql`'s operations by reflection. What is
read back is written by a generator that stands where the provider's stands, inside SchemaKit's
decorator: it sees the list the decorator hands on.

| | Npgsql | SQL Server | SQLite |
|---|---|---|---|
| `migrations add` | `.Annotation(…)` on `migrationBuilder.Sql(…)`, in `Up` and in `Down` | the same | the same |
| The snapshot and the designer file | neither holds it | neither | neither |
| `migrations script`: the generator receives the operation | with all five, each with its type and value, under `Script` | the same | the same |
| `migrations script --idempotent` | with all five, under `Script, Idempotent` | the same | the provider has no idempotent scripts |
| `migrations bundle`, applying the migration | with all five, under `Default`, on PostgreSQL 18 | the same, on SQL Server 2022 | the same |
| A value with no C# literal (a record) | `migrations add` fails and writes nothing | the same | the same |

What `migrations add` wrote for the five annotations (two strings, a `string[]`, an `int` and a
`bool`), sorted by name:

```csharp
migrationBuilder.Sql("CREATE INDEX ix_things_name ON \"Things\" (\"Name\");")
    .Annotation("Spike:AfterRebuild", true)
    .Annotation("Spike:Claims", new[] { "relation:ix_things_name", "index:Things.ix_things_name" })
    .Annotation("Spike:Kind", "create index")
    .Annotation("Spike:Object", "ix_things_name")
    .Annotation("Spike:Order", 3);
```

What the generator received when the idempotent script was written:

```
SEEN [Script, Idempotent] SqlOperation "CREATE INDEX ix_things_name ON "Things" ("Name");" | Spike:AfterRebuild=Boolean True | Spike:Claims=string[] ["relation:ix_things_name", "index:Things.ix_things_name"] | Spike:Kind=string "create index" | Spike:Object=string "ix_things_name" | Spike:Order=Int32 3
```

The refusal of a value with no literal, which is EF Core's:

```
Cannot scaffold C# literals of type 'Consumer.SpikeClaim'. The provider should implement CoreTypeMapping.GenerateCodeLiteral to support using it at design time.
```

Three things follow for #115:

- **The migration's code is the only carrier.** Nothing else holds the annotation, so it is as
  permanent as the statement beside it, and what is written there is a file format in consumers'
  repositories. A migration scaffolded before the kernel wrote annotations has none.
- **An annotation is what tells SchemaKit's statement from another.** The generator receives
  `SqlOperation`s that are neither SchemaKit's nor the consumer's: EF Core's own, for the history
  table (`DO $EFHistory$ …` on Npgsql, `CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"` on
  SQLite).
- **The generator is told how the migration is applied.** `MigrationsSqlGenerationOptions` arrives
  with the operations: `Script, Idempotent` for an idempotent script, `Script` for a plain one,
  `Default` when a bundle applies.

## Where a refusal could stand

**Built since (#155, #206).** The kernel puts the annotation `CodoMetis:SchemaKit:Object` on a
statement written with `suppressTransaction: true`, and the generator decorator refuses such a
statement where the dialect says the way a migration is applied does not carry it: on Npgsql, an
idempotent script. The spike holds that as its own row, `migrations script --idempotent, as
scaffolded`. It then takes the annotation out of the migration and measures the cells above on
the statement as a consumer writes one by hand, which SchemaKit does not judge: they are what the
refusal is there for, and they stay measured on every run.

The kernel's own dialect, which SQL Server has until its package exists, refuses the idempotent
script as well, since it does not know that SQL Server carries the statement (#214). The spike's
two SQL Server consumers with a suppressed statement hold that refusal and are then measured the
same way: their cells are what a dialect for SQL Server will be able to say.

What follows is the spike's finding as it was written, before the refusal existed.

The issue's rule is that a combination the provider cannot wrap is refused at scaffold, with the
declaration named. The spike builds no refusal. It measured two things that bear on where one can
stand:

- **Scaffold does not know the path.** The migration that fails in an idempotent script on
  PostgreSQL is the one a bundle, and the script for an empty database, apply. Refused at scaffold,
  `CREATE INDEX CONCURRENTLY` is refused for those paths as well.
- **The generator knows it.** SchemaKit's generator decorator is handed `Idempotent` together with
  the operation, and the operation says `SuppressTransaction`. With an annotation that names the
  object (question 2), a refusal there can name the declaration.

For a trigger on SQL Server it is the statement's form that fails, in both scripts, and the form
is the contributor's to choose: inside `EXEC` it needs no refusal, once the contract can write a
statement with line breaks into a string constant.

The two are #155 (PostgreSQL) and #156 (SQL Server).

## How a cell is measured

1. `run.sh` packs the kernel and the PostgreSQL package from the checkout and copies `consumer/`
   to a directory outside the repository, once for each provider and statement. The statement is a
   compile-time constant: a bundle builds the model again when it runs.
2. The consumer writes a contributor against the kernel's public contract (`Kit.cs`), registered
   with `AddSchemaContributor`, beside `UseSchemaKit()`. Its statements reach the migration through
   the kernel's planner and `SchemaKitModelDiffer`, as a package's do: `dotnet ef migrations add`
   scaffolds them, and nothing is written into a migration by hand.
3. `migrations script`, `migrations script --idempotent` and `migrations bundle` are run once for
   each consumer. None of them needs a server.
4. For each server and path, in a new database: the script is applied, or the bundle run, and the
   consumer's judge (`Program.cs`) reads the catalog. It goes through no EF Core. An index has to
   be there and valid (`pg_index.indisvalid`: a failed `CREATE INDEX CONCURRENTLY` can leave one
   that is not). A trigger has to be there and enabled, a row named `fine` has to be accepted, and
   a row named `refused` has to be refused with the trigger's own error.
5. Then the same again in the same database, and the catalog is compared with what the first run
   left, the object's identity included: on PostgreSQL the index's OID, or the trigger's and its
   function's; on SQL Server the trigger's `object_id` with its `modify_date`, and for an index
   the id of its rowset (`sys.partitions.hobt_id`), since `object_id` and `index_id` are the same
   after a drop and create. A database option has no identity; there the comparison is its state.
6. Each identity is shown to be one a recreation changes: after the bundle's second run the judge
   drops the object and creates it again, and has to report another identity.

**A script is applied as the provider's own client applies one,** not as one command: psql sends a
file statement by statement, sqlcmd batch by batch at each `GO`, and both go on after an error
unless told otherwise. The judge does the same. Where the first run met an error, the path is run
again in a new database, stopping at the first error, which is `psql -v ON_ERROR_STOP=1` and
`sqlcmd -b`. Spike B sent a script as one command; whether a statement runs in a transaction is
what this spike is about, so it does not.

**The judge is held against the tools.** `run.sh` applies every script through psql or sqlcmd as
well, into a database of its own, and the object, the table and the history it leaves must be
what the judge's way left. The tool is the one on PATH (`psql -f script.sql`,
`sqlcmd -i script.sql`), or the one in the server's container where the variables above name it.
In a container the script goes in on standard input, and for sqlcmd without the byte order mark
EF Core writes: sqlcmd reads one off a file given with `-i`, and on standard input takes it for
a syntax error at line 1 (Msg 102) and loses the batch that creates the history table.

CI's runner has psql (18.6) and no sqlcmd. On the developer's machine the tools were the
containers': psql 16.15 and 18.6, and sqlcmd 18.7. In both places, for every consumer, with the
tools' defaults and with the stop switch, they left what the judge's way left. The exit codes
named above are theirs.

**One thing the tool's session decides is not in a cell.** SQL Server keeps with a trigger whether
`QUOTED_IDENTIFIER` was on when it was created (`sys.sql_modules.uses_quoted_identifier`). It is 0
for a trigger a script created through sqlcmd's defaults, and 1 through sqlcmd `-I`, through the
judge's connection and from a bundle. `run.sh` holds the 0 and the 1 beside each such cell. What
follows from it, for a trigger that later writes to a table with a filtered index or an indexed
view, is not measured.

**The control.** Every database is empty before its first run, and the failing cells are the proof
that the judge reads the catalog: the same check that says "applies" elsewhere sees the index and
the trigger missing there. A database that cannot be created is a failing row, so a cell cannot
go missing from a run that reports the table as measured: with `Create` made to throw for every
database after the first, the run fails with one row for each path.

## What it found beyond the question

- **The idempotent script changes a body.** It indents every line of a statement to stand inside
  its `IF`, and the lines of a body are lines of the statement. Both servers keep the body as it
  was sent. PostgreSQL's function is 153 characters after the script for an empty database and
  after the bundle, and 181 after the idempotent script, on 16 and 18. SQL Server's trigger with
  its line breaks inside the constant is 219 and 239; on one line it is 203 on every path. The
  trigger behaves the same. A comparison of catalog text between two databases built on
  different paths does not come out equal. `run.sh` holds which paths agree.
- **On SQL Server an error at run time does not end the batch, and a migration is one batch.**
  The control shows it: after Msg 226 the script goes on, records the migration and commits, so
  the migration counts as applied with one of its statements never run, and the second run does
  nothing. `sqlcmd -b` exits 1 and changes none of that, since the batch has committed by then. A
  bundle stops at the error and rolls back. Only an error that keeps the batch from compiling,
  as the bare trigger's, leaves nothing behind.
- **A statement that suppresses the transaction splits the migration in three.** What stands
  before it is committed first, and what stands after it, the history row included, runs outside
  a transaction in both scripts. That is where the half-applied state of the failing cell comes
  from.
- **sqlcmd leaves `QUOTED_IDENTIFIER` off, and a filtered index needs it on.** By hand, with sqlcmd
  18.7 on SQL Server 16.0.4305.5: `CREATE INDEX … WHERE …` through sqlcmd fails with Msg 1934 and
  creates nothing, and sqlcmd exits 0; with `-I` the index is created. The judge connects through
  SqlClient, which sets the option on, so for a statement of that kind the judge is no stand-in for
  sqlcmd. None of this spike's statements is one; a filtered index in a script is not measured
  here beyond this.
- **`SchemaSql.Statement` already takes `suppressTransaction`,** and EF Core scaffolds it as
  `migrationBuilder.Sql("…", suppressTransaction: true)`.

## What it does not cover

- `Down`. The drops are scaffolded and never applied.
- A migration with a suppressed statement that fails halfway on a bundle. The bundles that hold
  one all succeed here.
- `DROP INDEX CONCURRENTLY`, `REINDEX CONCURRENTLY` and the other statements PostgreSQL refuses in
  a transaction. They suppress it the same way and were not run.
- Several migrations in one script, and `--no-transactions`.
- SQLite, for question 1: its provider has no idempotent scripts (spike B).
- Any edition of SQL Server without `ONLINE = ON`, and Azure SQL.
- EF Core 11.0.0, applied. Its two scripts for the bare trigger on SQL Server were compared with
  rc.3's as text; nothing else was run on it. The consumer builds on it only without its
  reference to `System.Security.Cryptography.Pkcs` 9.0.23, which the 11.0.0 provider has
  outgrown (NU1605).
