# CodoMetis.EFCore.SchemaKit

Schema objects EF Core 11 has no API for, declared in your model and carried by your migrations.
A declaration reaches the database through every path EF Core turns a model into DDL:
`migrations add`, `EnsureCreated()`, `GenerateCreateScript()` and the pending-changes check.
The successor of `EFCore.ComplexIndexes` 5.x, with a new API.

> **`11.0.0-preview.2` is a preview.** It declares two kinds of object on PostgreSQL: exclusion
> constraints and indexes. Any API may change before `11.0.0`. The stored form of a declaration, which lands in
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
    public string? Note { get; set; }
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

An expression is spelled as Npgsql spells the same lambda in a query, so PostgreSQL can use the
index for `Where(x => x.Note!.ToLower() == "a")`. A test asks the server's planner for that, with
sequential scans switched off: on a small table the planner may still prefer one.

The other access methods have a method each, with the options PostgreSQL has for them:

```csharp
modelBuilder.Entity<Document>().ToTable("documents").DeclarePostgres(pg =>
[
    pg.Gin(x => x.Tags, name: "ix_doc_tags"),
    pg.Gist([pg.Key(x => x.Rank), pg.Key(x => x.Period)], include: [x => x.Title], name: "ix_doc_rank_period"),
    pg.Hash(x => x.Title, fillFactor: 80, name: "ix_doc_title_hash"),
    pg.Brin(x => x.Rank, pagesPerRange: 16, name: "ix_doc_rank_brin"),
    pg.BTree([pg.Key(x => x.Title).Collate("C").Ops(BTreeOpClass.TextPatternOps)], name: "ix_doc_title"),
]);
```

```sql
CREATE INDEX ix_doc_tags ON documents USING gin ("Tags");
CREATE INDEX ix_doc_rank_period ON documents USING gist ("Rank", "Period") INCLUDE ("Title");
CREATE INDEX ix_doc_title_hash ON documents USING hash ("Title") WITH (fillfactor = 80);
CREATE INDEX ix_doc_rank_brin ON documents USING brin ("Rank") WITH (pages_per_range = 16);
CREATE INDEX ix_doc_title ON documents ("Title" COLLATE "C" text_pattern_ops);
```

What PostgreSQL has no statement for does not compile. `pg.Gin(…)` has no `include:`, a key with
`Desc()` does not go into `pg.Gist([…])`, and a GIN operator class such as
`GinOpClass.JsonbPathOps` does not go onto a B-tree key. `Collate` exists on a key of text only.

`concurrently: true` builds an index without locking the table against writes. Every method
takes it:

```csharp
modelBuilder.Entity<RoleGrant>().ToTable("role_grants").DeclarePostgres(pg =>
[
    pg.BTree(x => x.RoleId, concurrently: true, name: "ix_role_grant_role"),
]);
```

```sql
CREATE INDEX CONCURRENTLY ix_role_grant_role ON role_grants ("RoleId");
```

PostgreSQL does not run this statement in a transaction, so the migration runs it outside its own
(`suppressTransaction: true`). Beside the statement, the migration notes which index it is for, in
an annotation named `CodoMetis:SchemaKit:Object`.

- **`migrations script --idempotent` fails for such a migration** (`InvalidOperationException`)
  and names the index. An idempotent script holds every statement in a `DO` block, where
  PostgreSQL rejects this one (25001). `migrations script`, a bundle, `dotnet ef database update`
  and `Migrate()` all run it.
- **The migration is not applied as one.** What stands before the statement is committed when the
  statement starts. If the build then fails, for example a unique index on rows that are not
  unique (23505), PostgreSQL keeps an invalid index under the name. Applied again, the migration
  starts from its first statement, and one that cannot run twice fails there, such as an added
  column (42701).
- **Give such an index a migration of its own.** Then a failed build is mended by dropping the
  invalid index, fixing the rows and applying the migration again.
- **A changed index keeps the old one until the new one is built.** The migration builds the new
  index beside the old one as `{name}_ccnew`, the name PostgreSQL itself uses in
  `REINDEX CONCURRENTLY`. Then it drops the old index and renames the new one, in one
  transaction. A unique index holds the whole time. If the build fails, the old index is still
  there and valid, and an invalid `{name}_ccnew` is left: fix the rows, drop that index and apply
  the migration again. A name too long for the suffix is cut to the 63 bytes PostgreSQL keeps.
- **A new name and a change at once are two indexes.** The migration drops the old index and
  then builds the new one, so the table is without either during the build. Change the index in
  one migration and rename it in another to avoid that. The same holds where a renamed column or
  table rebuilds an index with SQL of your own.
- **A drop and a rename are written as for any index.**
  Adding `concurrently:` to an index you already declared, or taking it away, is no migration.

A key or a condition can read a member of a JSON document, which is a complex property mapped
with `ToJson()`. The document itself can be a key too:

```csharp
modelBuilder.Entity<Customer>(customer =>
{
    customer.ToTable("customers");
    customer.ComplexProperty(x => x.Prefs, prefs => prefs.ToJson("prefs"));
    customer.DeclarePostgres(pg =>
    [
        pg.BTree(x => x.Prefs.Rank, name: "ix_customer_rank"),
        pg.BTree(x => x.Name, where: x => x.Prefs.Active, name: "ix_customer_active"),
        pg.Gin(x => x.Prefs, GinOpClass.JsonbPathOps, name: "ix_customer_prefs"),
    ]);
});
```

```sql
CREATE INDEX ix_customer_rank ON customers ((CAST(prefs ->> 'Rank' AS integer)));
CREATE INDEX ix_customer_active ON customers ("Name") WHERE (CAST(prefs ->> 'Active' AS boolean));
CREATE INDEX ix_customer_prefs ON customers USING gin (prefs jsonb_path_ops);
```

A member is written as Npgsql writes it in a query, so PostgreSQL uses `ix_customer_rank` for
`Where(x => x.Prefs.Rank == 5)`. A test asks the server's planner for that, for each kind of
member.

- **A date or a time in the document cannot be a key or stand in a condition.** `migrations add`
  fails and names the member (`InvalidOperationException`). A query reads such a member through
  a cast that PostgreSQL rejects in an index (42P17). Keep the value in a column of its own.
- **`HasColumnType` on a member you index fails in `migrations add`.** The model snapshot does
  not keep it for a member, so every later migration would rebuild the index. `HasPrecision`,
  `HasMaxLength` and a value converter are kept.
- **`include:` takes columns.** The document's column is one; a member of it is not.
- **Not yet:** a member of an element of a collection in the document, and an owned type mapped
  with `ToJson()`. Both fail in `migrations add`.

Good to know:

- **A storage parameter out of PostgreSQL's range throws where you write it**
  (`ArgumentOutOfRangeException`): `fillFactor:` is 10 to 100, `pagesPerRange:` 1 to 131072,
  `pendingListLimit:` 64 or more.
- **A changed storage parameter is a drop and a create.** PostgreSQL can set one in place, which
  leaves the pages that exist as they were.
- **One of PostgreSQL's own operator classes on a column whose type does not take it fails in
  `migrations add`,** such as `GinOpClass.JsonbPathOps` on a `text[]` column. A class an extension
  brings, `new GinOpClass("gin_trgm_ops")`, is written as you give it.
- **A GiST key on a scalar column needs `btree_gist`,** and the migration installs it, as for an
  exclusion constraint. A key that names an operator class, `.Ops(new GistOpClass("gist_int4_ops"))`,
  gets no extension from the migration: declare it with `HasPostgresExtension("btree_gist")`.
- **`nullsDistinct:` is a parameter of `pg.Unique` only.** PostgreSQL has `NULLS NOT DISTINCT` for
  a unique index and for no other.
- **`include:` takes properties.** An expression there throws in `OnModelCreating`
  (`ArgumentException`).
- **The default name is EF Core's,** `IX_{table}_{keys}`. If you declare the same index with
  `HasIndex` as well, the model is rejected (`SK1001`): keep one of the two.
- **EF Core's own index metadata does not hold the index.** `GetIndexes()` does not list it;
  `GetDeclarations<PgIndex>()` does.

## SQL you write yourself

Where no lambda says what you need, write the SQL into the lambda with `Sql.Raw<T>`. It goes
wherever a lambda goes: a key, an element of an exclusion constraint, a condition. `{Note}` stands
for the column of the property `Note`:

```csharp
modelBuilder.Entity<RoleGrant>().ToTable("role_grants").DeclarePostgres(pg =>
[
    pg.BTree(
        [pg.Key(x => Sql.Raw<string>("upper({Note})")).Desc(), pg.Key(x => x.RoleId)],
        where: x => x.GranteeId > 0 && Sql.Raw<bool>("{RoleId} % 2 = 0 OR {Note} ~ '^x'"),
        name: "ix_role_grant_mixed"),
]);
```

```sql
CREATE INDEX ix_role_grant_mixed ON role_grants ((upper("Note")) DESC, "RoleId") WHERE ("GranteeId" > 0 AND ("RoleId" % 2 = 0 OR "Note" ~ '^x'));
```

`T` is what the SQL evaluates to. It decides which options the key has, as a property's type
does: `Collate` for a `string`, `Overlaps` for a range.

Good to know:

- **Your SQL reaches the database as you wrote it.** SchemaKit does not check it and does not
  quote a name in it. Alone in a lambda it is written as it is, and as a part of a larger
  expression it gets parentheses around it.
- **Name a column with `{Property}` where you can.** The placeholder becomes the column's name,
  quoted where PostgreSQL needs it, and keeps up with a renamed column. A path through a complex
  property works too: `{Address.City}`. A placeholder that names no property makes the model
  fail when it is built (`InvalidOperationException`), not only in `migrations add`. The one
  exception is SQL that is a placeholder and nothing else, `Sql.Raw<int>("{Rank}")` as a key: that
  is the column, and a wrong name fails in `migrations add`.
- **Keep a `{…}` out of an `E'…'` or dollar-quoted literal.** A placeholder is left alone inside
  a plain `'…'` literal. SchemaKit does not know PostgreSQL's other two kinds, so a property's
  name in braces inside one is replaced by the column
  ([#224](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/224)).
- **A renamed column or table rebuilds the object.** SchemaKit cannot see which columns your SQL
  reads outside the placeholders, so the migration drops the index or constraint before the
  rename and creates it again after it.
- **`T` says whether the SQL can be NULL.** `Sql.Raw<int>` never is, `Sql.Raw<int?>` can be, and
  so can a reference type such as `string`. A comparison with SQL that can be NULL gets the same
  `OR … IS NULL` a nullable property gets, so `Sql.Raw<string>("…") != "a"` keeps the rows C#
  keeps.
- **Text from a variable is read once.** `Sql.Raw<bool>(sql)` takes what `sql` holds when
  `OnModelCreating` reaches the line.
- **A namespace named `Sql` in your project hides the class.** Add
  `using Sql = CodoMetis.EFCore.SchemaKit.Sql;` inside your namespace.

## What this preview does

- **Exclusion constraints on PostgreSQL,** declared with `DeclarePostgres`: elements compared by
  `=` (`Equal`), by `&&` on a range or a multirange (`Overlaps`), or by any other operator
  (`WithUncheckedOperator`, checked by the database when the migration is applied); a condition;
  a deferral; a name, or a default one. An element may be a property, a path through a complex
  type, or an expression the translator renders, such as `x.Code.ToLower()`.
- **SQL of your own** in a key, an element or a condition, with `Sql.Raw<T>("…")` inside the
  lambda ([above](#sql-you-write-yourself)).
- **Indexes on PostgreSQL,** declared with `DeclarePostgres`: B-tree and unique ones, hash, GiST,
  SP-GiST, GIN and BRIN. Keys are properties or expressions, with an operator class and, for
  text, a collation; a B-tree key is ascending or descending with its nulls first or last. A
  condition; included columns where the method has them; `NULLS NOT DISTINCT` for a unique index;
  each method's storage parameters; a name, or a default one.
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
| A 5.x project whose snapshot holds temporal constraints, or indexes on SQL Server or SQLite: the snapshot is refused, so the project stays on 5.x for now | [#18](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/18), [#29](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/29) |
| SQL Server | [#29](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/29) |
| SQLite: no contributor yet. A create on a table the same migration rebuilds is refused until it is placed after the rebuild | [#145](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/145), [#115](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/115) |
| A table with a constraint that moves back to the default schema, `ToTable("x", "grants")` to `ToTable("x")`: refused, since Npgsql writes no statement for that move. Name the schema, `ToTable("x", "public")`, and it is written | [#105](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/105) |
| A declaration on a column only a derived type has, while the type shares its table: refused | [#85](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/85) |
| A cast in a lambda that changes the value, such as `(int)x.Amount == 2`: refused | [#103](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/103) |
| A factory method for an SP-GiST exclusion constraint | [#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25) |
| A member of an element of a JSON collection, and an owned type mapped with `ToJson()`: refused | [#43](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/43) |

Known limits of this preview, each with what it costs:

- **A lambda is written as EF Core's query writes it, and EF Core compares what is stored.**
  That is what lets PostgreSQL use the index for the query. It differs from C# where a value
  converter changes the order of values: with `HasConversion(v => -v, v => -v)`, the condition
  `x.Score > 5` is `"Score" > -5`, in the index and in EF Core's own query alike. `Trim()` trims
  the four characters Npgsql's query trims, not every character C# calls white space.
- **`DROP INDEX` and `ALTER INDEX` name the index without a schema when its table has none,** as
  Npgsql does for EF Core's own indexes. On a connection whose `search_path` has another schema
  in front, with an index of the same name in it, PostgreSQL takes that one
  ([#226](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/226)). Give the table its
  schema, `ToTable("x", "public")`, and the statements name it.
- **An `IDesignTimeServices` of your own that registers a differ replaces SchemaKit's.** EF Core
  adds it last, and nothing can guard that. `migrations add` then writes the tables without your
  declarations and reports no error. Do not register `IMigrationsModelDiffer` there.
- **A table without a declaration that moves back to the default schema stays where it was.**
  That is Npgsql's ([#105](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/105)); with
  a declaration on the table SchemaKit rejects the migration, as the table above says.

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
