# CodoMetis.EFCore.SchemaKit

Schema objects EF Core 11 has no API for, declared in your model and carried by your migrations.
A declaration reaches the database through every path EF Core turns a model into DDL:
`migrations add`, `EnsureCreated()`, `GenerateCreateScript()` and the pending-changes check.
The successor of `EFCore.ComplexIndexes` 5.x, with a new API.

> **`11.0.0-preview.1` is a preview.** It declares one kind of object, the PostgreSQL exclusion
> constraint. Any API may change before `11.0.0`. The stored form of a declaration, which lands in
> your model snapshot, is not final either: a later preview may ask you to scaffold your migrations
> again. The permanent promise for the snapshot starts at `11.0.0`.

```csharp
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;

public sealed class RoleGrant
{
    public int Id { get; set; }
    public int GranteeId { get; set; }
    public int RoleId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
    public DateOnly? RevokedOn { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoleGrant>().ToTable("role_grants").DeclarePostgres(pg =>
        [
            // No two grants of one role to one grantee overlap in time, among those not revoked.
            pg.Exclusion(
                pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
                where: x => x.RevokedOn == null,
                deferral: Deferral.InitiallyDeferred,
                name: "ex_role_grant_active_period"),
        ]);
}
```

The wiring is one call, inside `UseNpgsql`:

```csharp
services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString, o => o.UseSchemaKit()));
```

`migrations add` writes the constraint as SQL into the migration, and the extension that `=` on an
integer needs under GiST before it:

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE role_grants ADD CONSTRAINT ex_role_grant_active_period EXCLUDE USING gist ("GranteeId" WITH =, "RoleId" WITH =, "Period" WITH &&) WHERE ("RevokedOn" IS NULL) DEFERRABLE INITIALLY DEFERRED;
```

A constraint of equality elements only does not compile: `pg.Equal(…)` alone is a draft that
`pg.Exclusion` does not take, since such a constraint is a unique constraint. A condition is a
lambda over the entity being configured, translated where it is written; one the translator has no
SQL for fails in `OnModelCreating`, not in a migration.

## What this preview does

- **Exclusion constraints on PostgreSQL,** declared with `DeclarePostgres`: elements compared by
  `=` (`Equal`), by `&&` on a range, an array or a multirange (`Overlaps`), or by any other operator
  (`WithUncheckedOperator`, checked by the database when the migration is applied); a condition;
  a deferral; a name, or a default one. An element may be a property, a path through a complex
  type, or an expression the translator renders, such as `x.Code.ToLower()`.
- **Every migration path,** on PostgreSQL 16 and 18. The live tests apply each diff to a server in CI
  and compare the catalog it leaves with what `EnsureCreated()` builds for the same model.
- **Changes in place where PostgreSQL has one:** a renamed constraint is renamed. Anything else
  about a constraint is a drop and a create. A table or column EF Core renames is followed, and
  the constraint stays. A column EF Core drops is answered as the server answers it: PostgreSQL
  drops a constraint with a column it compares, and refuses to drop a column that only an
  expression or the condition reads, so there the constraint is dropped before and created after.
- **`btree_gist`** is installed by the migration that first needs it, unless the model declares it
  with `HasPostgresExtension("btree_gist")` or calls `SuppressBtreeGistInjection()`.
- **The move from `EFCore.ComplexIndexes` 5.x for exclusion constraints:** a snapshot 5.x scaffolded
  is read, and a model that declares the same constraints with SchemaKit scaffolds no operations.
  The [guide](docs/moving-from-complexindexes-5x.md) has each 5.x call and its new form.
- **Guards** that refuse, with a message that names the fix: a declaration on a context without
  `UseSchemaKit()`, a second declaration of the same object, a name PostgreSQL would cut at 63 bytes,
  a name another object of the model takes, and a 5.x package still referenced.

## What it does not do yet

| | Issue |
|---|---|
| Indexes: expression, JSON-member and filtered, with typed keys | [#27](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/27) |
| A 5.x project whose snapshot holds indexes or temporal constraints: the snapshot is refused, so the project stays on 5.x for now | [#93](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/93), [#18](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/18) |
| SQL Server | [#29](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/29) |
| SQLite: no contributor until a create after SQLite's table rebuild is placed after it | [plan, section 3.12](design/schemakit-core-plan.md#312-other-providers-and-sqlite) |
| A declaration on a type of a TPC hierarchy whose derived types have tables: refused | [#118](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/118) |
| A declaration on a column only a derived type has, while the type shares its table: refused | [#85](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/85) |
| A cast in a lambda that changes the value, such as `(int)x.Amount == 2`: refused | [#103](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/103) |
| Raw SQL in an element, and a factory for SP-GiST | [#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25) |
| A member of a JSON document in a lambda: refused | [#43](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/43) |

Known defects of this preview, each with what it costs:

- `Overlaps` over a plain array compiles, and the migration fails at apply: GiST has no operator
  class for arrays ([#104](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/104)).
- A table moved back to the default schema stays in its old schema on Npgsql, with its constraints
  ([#105](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/105)).

## Which package for which EF Core

| EF Core | Package | Repository |
|---|---|---|
| 10 (LTS, to November 2028) | `EFCore.ComplexIndexes` 5.x | [CaffeinatedCoder/EFCore.ComplexIndexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes) |
| 11 | `CodoMetis.EFCore.SchemaKit.PostgreSQL` 11.x | this one |

Reference `CodoMetis.EFCore.SchemaKit.PostgreSQL`; it brings the kernel, `CodoMetis.EFCore.SchemaKit`,
at exactly its own version.

SchemaKit is a new package with a new API, not a rename. EF Core 11 and Npgsql 11 do some of what
5.x was built for themselves (temporal keys with `WithoutOverlaps()`, indexes over complex-type
members), and SchemaKit leaves those to them.

## Platform

- .NET 11 (`net11.0`).
- EF Core 11 and `Npgsql.EntityFrameworkCore.PostgreSQL` `11.0.0-rc.3`. Npgsql's rc.3 depends on
  EF Core `11.0.0-rc.3.26522.148` exactly, so the packages are built against that, and SchemaKit
  ships previews until Npgsql ships 11.0.0. A project that references EF Core 11.0.0 beside it
  restores with `NU1608` warnings from Npgsql's pin, and 11.0.0 is what it resolves.
- PostgreSQL 16 and 18, the two versions CI runs on.

## Build

You need the .NET SDK 11.0.100 or a later patch (`global.json`).

```bash
dotnet build CodoMetis.EFCore.SchemaKit.slnx
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj
dotnet test test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.csproj
dotnet test test/SchemaKit.ApiProbes/SchemaKit.ApiProbes.csproj
```

The tests that need a PostgreSQL server run when `SCHEMAKIT_TEST_PG` holds an Npgsql connection
string, and are inconclusive without it. [CONTRIBUTING.md](CONTRIBUTING.md) has the rest.

## The design

| | |
|---|---|
| The public surface and the type model | [design/schemakit-api.md](design/schemakit-api.md) |
| The plan for the core, with the decisions taken | [design/schemakit-core-plan.md](design/schemakit-core-plan.md) |
| What changed in each version | [CHANGELOG.md](CHANGELOG.md) |
| Work | The [issues](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues), under the `11.0.0` milestone |

## License

MIT. See [LICENSE](LICENSE).
