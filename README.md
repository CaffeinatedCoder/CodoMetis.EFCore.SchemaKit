# CodoMetis.EFCore.SchemaKit

Schema objects EF Core 11 has no API for, declared in your model and carried by your migrations.
A declaration reaches the database through every path EF Core turns a model into DDL:
`migrations add`, `EnsureCreated()`, `GenerateCreateScript()` and the pending-changes check.
The successor of `EFCore.ComplexIndexes` 5.x, with a new API.

> **`11.0.0-preview.1` is a preview.** It declares one kind of object, the PostgreSQL exclusion
> constraint. Any API may change before `11.0.0`. The stored form of a declaration, which lands in
> your model snapshot, is not final either: a later preview may ask you to scaffold your migrations
> again. Scaffolding again does not change a database you already built, so try a preview on a
> database you can build again. The permanent promise for the snapshot starts at `11.0.0`.

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

## Indexes

You declare an index in the same list. `pg.BTree` is a B-tree index and `pg.Unique` a unique one.
A key is a property or an expression, and `pg.Key` gives a key an order:

```csharp
modelBuilder.Entity<RoleGrant>().ToTable("role_grants").DeclarePostgres(pg =>
[
    // One grant per grantee and role among those not revoked.
    pg.Unique(
        [pg.Key(x => x.GranteeId), pg.Key(x => x.RoleId)],
        where: x => x.RevokedOn == null,
        include: [x => x.Note],
        nullsDistinct: false,
        name: "ux_role_grant_active"),

    pg.BTree(
        [pg.Key(x => x.Note!.ToLower()), pg.Key(x => x.RevokedOn).Desc().NullsLast()],
        name: "ix_role_grant_note"),
]);
```

`migrations add` writes one statement for each:

```sql
CREATE UNIQUE INDEX ux_role_grant_active ON role_grants ("GranteeId", "RoleId") INCLUDE ("Note") NULLS NOT DISTINCT WHERE "RevokedOn" IS NULL;
CREATE INDEX ix_role_grant_note ON role_grants ((lower("Note")), "RevokedOn" DESC NULLS LAST);
```

An expression is spelled as Npgsql spells the same lambda in a query, so PostgreSQL uses the
index for `Where(x => x.Note!.ToLower() == "a")`. A test asks the server's planner for that.

Good to know:

- **`nullsDistinct:` is a parameter of `pg.Unique` only.** PostgreSQL has `NULLS NOT DISTINCT` for
  a unique index and for no other.
- **`include:` takes properties.** An expression there throws in `OnModelCreating`
  (`ArgumentException`).
- **The default name is EF Core's,** `IX_{table}_{keys}`. If you declare the same index with
  `HasIndex` as well, the model is rejected (`SK1001`): keep one of the two.
- **EF Core's own index metadata does not hold the index.** `GetIndexes()` does not list it;
  `GetDeclarations<PgIndex>()` does.

## What this preview does

- **Exclusion constraints on PostgreSQL,** declared with `DeclarePostgres`: elements compared by
  `=` (`Equal`), by `&&` on a range or a multirange (`Overlaps`), or by any other operator
  (`WithUncheckedOperator`, checked by the database when the migration is applied); a condition;
  a deferral; a name, or a default one. An element may be a property, a path through a complex
  type, or an expression the translator renders, such as `x.Code.ToLower()`.
- **B-tree and unique indexes on PostgreSQL,** declared with `DeclarePostgres`: keys that are
  properties or expressions, each ascending or descending with its nulls first or last; a
  condition; included columns; `NULLS NOT DISTINCT` for a unique index; a name, or a default one.
  A renamed index is renamed, and anything else about it is a drop and a create. A table or column
  EF Core renames is followed, and a change of a column's type leaves the index in place.
  PostgreSQL drops an index with any column it reads, so the migration writes no drop for it and
  creates what you still declare.
- **Every migration path,** on PostgreSQL 16 and 18. The live tests apply each diff to a server in CI
  and compare the catalog it leaves with what `EnsureCreated()` builds for the same model.
- **Changes in place where PostgreSQL has one:** a renamed constraint is renamed. Anything else
  about a constraint is a drop and a create. A table or column EF Core renames is followed, and
  the constraint stays. A column EF Core drops is answered as the server answers it: PostgreSQL
  drops a constraint with a column it compares, and refuses to drop a column that only an
  expression or the condition reads, so there the constraint is dropped before and created after.
- **A table per concrete type (TPC):** a constraint declared on a type of such a hierarchy is
  created in that type's table and in the table of every type derived from it. The declaring
  type's table keeps a name you give, and a derived type's table gets it with the table's name
  behind it (`ex_reservation_stays`). Each table refuses its own overlapping rows; a row of one
  table and a row of another are not compared, since no constraint holds across tables.
- **An entity split over two tables:** a constraint is created in the table that has the columns
  it reads. One that reads a column of each table is refused, since a constraint is in one table.
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
| Indexes: the other access methods (hash, GiST, SP-GiST, GIN, BRIN), operator classes, collations, storage parameters, `CONCURRENTLY`, and a member of a JSON document as a key | [#27](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/27) |
| A 5.x project whose snapshot holds indexes or temporal constraints: the snapshot is refused, so the project stays on 5.x for now | [#93](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/93), [#18](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/18) |
| SQL Server | [#29](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/29) |
| SQLite: no contributor yet. A create on a table the same migration rebuilds is refused until it is placed after the rebuild | [#145](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/145), [#115](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/115) |
| A table with a constraint that moves back to the default schema, `ToTable("x", "grants")` to `ToTable("x")`: refused, since Npgsql writes no statement for that move. Name the schema, `ToTable("x", "public")`, and it is written | [#105](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/105) |
| A declaration on a column only a derived type has, while the type shares its table: refused | [#85](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/85) |
| A cast in a lambda that changes the value, such as `(int)x.Amount == 2`: refused | [#103](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/103) |
| Raw SQL in an element, and a factory for SP-GiST | [#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25) |
| A member of a JSON document in a lambda: refused | [#43](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/43) |

Known defects of this preview, each with what it costs:

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
- EF Core 11 and `Npgsql.EntityFrameworkCore.PostgreSQL` 11, from 11.0.0 of both. The packages
  are built and tested against 11.0.0.
- `11.0.0-preview.1` was built against Npgsql's `11.0.0-rc.3`, and that is what it restores by
  itself. A project that references `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0 beside it
  restores EF Core 11.0.0 and Npgsql 11.0.0 without a warning.
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
