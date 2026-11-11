# Spike B: design time without `.targets`, from a packed consumer

For [#17](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/17). Throwaway code:
the consumer smoke test of the real packages replaces it.

**Question.** A package with no `build/` folder, no `.targets` and no
`DesignTimeServicesReferenceAttribute`: does every design-time and runtime path still see a declared
object, when the only wiring is `UseSpikeKit()` on the context's options?

**Answer: yes.** 62 paths behave as expected, on SQLite, Npgsql and SQL Server, in the three ways
`dotnet ef` builds a context. EF Core 11 seeds the design-time services from the context, so the
context's own differ scaffolds the migration.

```bash
# The PostgreSQL paths that write to a database need a server this script may create and drop in:
SPIKE_PG="Host=localhost;Username=postgres;Password=…;Database=spike_b" spikes/B.DesignTime/run.sh
```

The script packs spike A's kit (`SpikeKit.Package/`), copies `consumer/` to a directory **outside the
repository**, restores it through a private package cache and package source mapping, and drives the
real `dotnet ef` (11.0.0-rc.3). Each row is judged by scaffolded content or by what the database
holds. The kit's statement is a real `CREATE INDEX`, so "the database holds both indexes" is read
from the catalog.

## The paths

| Path | SQLite | Npgsql | SQL Server |
|---|---|---|---|
| `migrations add`: the migration creates the declared index | ok, in all three variants | ok | ok |
| `has-pending-model-changes` when nothing changed | no changes | no changes | no changes |
| `has-pending-model-changes` after a change only the kit can see | reports it | reports it | reports it |
| `migrations add` for that change: `Up` creates the one index, `Down` drops it | ok | ok | ok |
| `migrations script`, and applied to an empty database | both indexes | both indexes | text only |
| `migrations script --idempotent`, applied twice | refused by the provider | both indexes | text only |
| The context's differ and generator at run time | the kit's, around the provider's | same | same |
| `GenerateCreateScript()` | both indexes | both indexes | both indexes |
| `EnsureCreated()` | both indexes | both indexes | not run |
| `Migrate()` | both indexes | both indexes | not run |
| `Migrate()` with a pending kit-only change | refused: `PendingModelChangesWarning` | refused | not run |
| `database update` | both indexes | both indexes | not run |
| `migrations bundle`, executed | both indexes | both indexes | not run |
| `dbcontext optimize`, then `EnsureCreated()` on the compiled model | both indexes | both indexes | not run |

The variants, on SQLite: a parameterless constructor with `OnConfiguring`, an
`IDesignTimeDbContextFactory`, and a host whose services `dotnet ef` takes (`AddDbContext`). The
design-time rows hold in all three.

## Leaving the kit out

| Condition | Outcome |
|---|---|
| No `UseSpikeKit()` at all, `migrations add` (constructor and host variants) | Refused at the declaration; no migration is written |
| A design-time factory that forgets the kit while the application has it | The same |
| A compiled model used without the kit, `EnsureCreated()` | **Loud.** EF builds the design-time model from `OnModelCreating` even with a compiled model, and the declaration throws |
| The same, `Migrate()` | **Loud,** for the same reason: the pending-changes check needs the design-time model |
| The user's own `IDesignTimeServices` registers a differ | **Silent.** EF adds user services last. The migration scaffolds the tables without the index, the snapshot records the declaration, and no later diff notices |

The last row is the control as well: the same content check that passes everywhere else sees the
index missing there.

## What it found beyond the question

- **A statement without a terminator passes every path but the scripts.** The first version of the
  kit emitted `CREATE INDEX … ` with no `;`. `migrations add`, `database update`, the bundle and
  `Migrate()` accepted it; both generated scripts failed to apply (`42601: syntax error at or near
  "INSERT"`). The row that searched the script's text for the index was green throughout. The rows
  that apply the script were added because of it, and failed before the fix.
- **The compiled model is not a gap** for `EnsureCreated()` and `Migrate()`. The plan had listed it
  as one.
- **The stamp lands in the snapshot** (`.HasAnnotation("Spike:Wired", "1")` on the model), like
  `Relational:MaxIdentifierLength` does. Its value has to stay constant, or every package upgrade
  would rewrite every snapshot.
- **`dotnet new tool-manifest` moved its file** between SDKs (`.config/dotnet-tools.json`, then the
  folder itself). A script that reruns into the same directory has to remove both.

## What it does not cover

- SQL Server with a live database: its consumer runs the paths that need none.
- Statements that cannot run in a transaction, and triggers, in idempotent scripts: spike D, #19.
- `ForProvider`, startup projects other than the context's, and a migrations assembly of its own.
  The mechanism (EF seeds design time from the context) does not depend on them, but they were not
  run.
