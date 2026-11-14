# Moving from EFCore.ComplexIndexes 5.x

For a project on `EFCore.ComplexIndexes` 5.x and EF Core 10 that moves to EF Core 11. SchemaKit is
a new package with a new API: nothing of 5.x is kept as `[Obsolete]`, so the compiler names every
call that has to change, and this page says what each one becomes.

**This is the first cut, for `11.0.0-preview.1`.** That preview declares exclusion constraints on
PostgreSQL and nothing else. A project can move now when its 5.x model declares exclusion
constraints and no indexes or temporal constraints. A project with those keeps 5.x for now:
SchemaKit refuses its snapshot ([below](#what-cannot-move-yet)). Each later preview fills in the
entries of what it adds ([#73](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/73)).

A preview's snapshot carries no promise yet: the stored form of a declaration may change in a later
preview, which may then ask you to scaffold your migrations again. The permanent promise starts at
`11.0.0`.

## Before you start

- **Move from 5.6.x.** 5.5 began to refuse a default name over PostgreSQL's 63 bytes. A snapshot
  from an earlier version can hold a declaration whose default name PostgreSQL cut short, and
  SchemaKit refuses a name over the limit in the snapshot too, so that diff fails. Measured with a
  long name written into a snapshot; not run against a snapshot an earlier 5.x scaffolded.
- **Apply every migration 5.x scaffolded,** with 5.x, to every database you keep. See
  [Migrations 5.x scaffolded](#migrations-5x-scaffolded) for what SchemaKit does with them later.

## The packages

Remove the 5.x package references and add SchemaKit's PostgreSQL package. It brings the kernel,
`CodoMetis.EFCore.SchemaKit`, at exactly its own version.

| Remove | Add |
|---|---|
| `EFCore.ComplexIndexes`, `EFCore.ComplexIndexes.PostgreSQL` | `CodoMetis.EFCore.SchemaKit.PostgreSQL` `11.0.0-preview.1` |

5.x put a `DesignTimeServicesReference` for its own differ into your assembly through a `.targets`
file. SchemaKit has none: EF Core hands the context's own services to design time. When the
reference is still there, because a 5.x package is still referenced, the first model built is
refused:

> '…' still references the EFCore.ComplexIndexes packages: it carries a design-time services
> reference to '…'. At design time that would put the 5.x differ in front of SchemaKit's, and
> SchemaKit declarations would be left out of migrations without an error. …

## The wiring

5.x was registered after `UseNpgsql`. SchemaKit is registered inside it:

```csharp 5.x
options.UseNpgsql(connectionString).UseNpgsqlComplexIndexes();
```

```csharp
options.UseNpgsql(connectionString, o => o.UseSchemaKit());
```

That is the whole registration, at runtime and at design time. It goes wherever the context's
options are built, an `IDesignTimeDbContextFactory` included. With
`UseInternalServiceProvider`, add `AddEntityFrameworkSchemaKitNpgsql()` after
`AddEntityFrameworkNpgsql()` to that provider's collection, in place of 5.x's
`AddNpgsqlComplexIndexes()`.

What happens when a step is missed, as this preview has it:

| Missed | What you get |
|---|---|
| `UseSchemaKit()` | `DeclarePostgres` throws in `OnModelCreating`: "… declares a SchemaKit object, but UseSchemaKit() is not on this context's options." |
| `UseSchemaKit()`, with a 5.x snapshot that holds exclusion constraints and a model that declares none | Nothing refuses it: no SchemaKit service reads the snapshot, so the next `migrations add` does not see the constraints. Wire it before the first `migrations add` |
| `UseSchemaKit()` written after `UseNpgsql(…)` instead of inside it | It compiles: that is the kernel's own `UseSchemaKit()`, without the PostgreSQL package's services. `DeclarePostgres` throws in `OnModelCreating`: "… declares a 'Pg.Exclusion' object (…), and this context's options do not register what renders it: UseSchemaKit() is there, the registration of CodoMetis.EFCore.SchemaKit.PostgreSQL is not." A model that declares nothing over a 5.x snapshot is refused at the first diff, with "call UseSchemaKit() on the options builder of UseNpgsql" |
| A direct reference to `CodoMetis.EFCore.SchemaKit` at another version than the PostgreSQL package's | NuGet builds it with the warning NU1608. `UseSchemaKit()` throws: "CodoMetis.EFCore.SchemaKit.PostgreSQL … is loaded beside CodoMetis.EFCore.SchemaKit …" Reference the PostgreSQL package alone; it brings the kernel |
| `ReplaceService` of the differ or the SQL generator after `UseSchemaKit()` | The first model built is refused, and the message says to call `UseSchemaKit()` last |

Each row but the second is a row of the consumer smoke test (`test/ConsumerSmokeTest/run.sh`), on
the packed packages.

## Exclusion constraints

### The model, before and after

The 5.x model below holds every way 5.x stored an exclusion constraint. It is the model of the
fixture the tests read: `test/Fixtures/ComplexIndexes5x` holds what the published 5.6.0 scaffolded
for it.

```csharp 5.x
modelBuilder.Entity<Booking>().HasExclusionConstraint(x => x.RoomId, x => x.During);

modelBuilder.Entity<Shift>(shift =>
{
    shift.ToTable("shifts", "ops");

    shift.HasExclusionConstraint(c => c
        .WithEquality(x => x.WorkerId)
        .WithOverlaps(x => x.During)
        .HasFilter(x => x.CancelledOn == null)
        .HasName("ex_shift_active")
        .IsDeferrable(initiallyDeferred: true));

    shift.HasExclusionConstraint(
        x => new { x.WorkerId, x.SiteId },
        x => x.During,
        filter: "{Kind} <> 'draft'",
        name: "ex_shift_site");
});

modelBuilder.Entity<Lease>(lease =>
{
    lease.ComplexProperty(x => x.Term);
    lease.HasExclusionConstraint(c => c
        .WithExpression("lower(\"Code\")", "=")
        .WithOverlaps(x => x.Term.Span)
        .IsDeferrable());
});

modelBuilder.Entity<Slot>().HasExclusionConstraint(c => c
    .WithOverlaps(x => x.During)
    .UseMethod("spgist"));
```

The same model declared with SchemaKit:

```csharp
modelBuilder.Entity<Booking>().DeclarePostgres(pg =>
[
    pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.During)),
]);

modelBuilder.Entity<Shift>(shift =>
{
    shift.ToTable("shifts", "ops");

    shift.DeclarePostgres(pg =>
    [
        pg.Exclusion(
            pg.Equal(x => x.WorkerId).Overlaps(x => x.During),
            where: x => x.CancelledOn == null,
            deferral: Deferral.InitiallyDeferred,
            name: "ex_shift_active"),

        pg.Exclusion(
            pg.Equal(x => x.WorkerId).Equal(x => x.SiteId).Overlaps(x => x.During),
            where: x => x.Kind != "draft",
            name: "ex_shift_site"),
    ]);
});

modelBuilder.Entity<Lease>(lease =>
{
    lease.ComplexProperty(x => x.Term);
    lease.DeclarePostgres(pg =>
    [
        pg.Exclusion(pg.Equal(x => x.Code.ToLower()).Overlaps(x => x.Term.Span), deferral: Deferral.InitiallyImmediate),
    ]);
});

// SP-GiST has no factory method yet (#25): the constraint is written as the value it is.
modelBuilder.Entity<Slot>().DeclarePostgres(_ =>
[
    new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Column("During"), ExclusionOperator.Overlaps)),
]);
```

**What the next `migrations add` produces: a migration with no operations.** The snapshot 5.x wrote
is read, and its constraints are the ones the model declares, under the same names, default names
included. `LegacyHandoverTests` diffs the fixture's snapshot against this model and gets nothing,
and the snapshot against itself, which is what `migrations remove` asks of the last 5.x migration,
gets nothing too. `LegacyHandoverLiveTests` applies 5.x's migration as it was scaffolded, on
PostgreSQL 16 and 18, and compares the catalog with what `EnsureCreated()` builds for this model.

The first migration after the move may hold changes of its own; it scaffolds both ways, Up and Down
([#98](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/98), fixed).

A constraint you leave undeclared is dropped by that migration, under the name 5.x gave it. One you
give another name is renamed in place. One whose condition or elements you change is dropped and
created again.

### Call by call

| 5.x | SchemaKit | Measured |
|---|---|---|
| `HasExclusionConstraint(x => x.A, x => x.Period)` | `pg.Exclusion(pg.Equal(x => x.A).Overlaps(x => x.Period))` | No operations |
| `HasExclusionConstraint(x => new { x.A, x.B }, x => x.Period, filter, name)` | `pg.Exclusion(pg.Equal(x => x.A).Equal(x => x.B).Overlaps(x => x.Period), where: x => …, name: "…")` | No operations |
| `filter: "{Kind} <> 'draft'"`, a string with placeholders | `where: x => x.Kind != "draft"`, when the lambda renders the same text. See [Conditions](#conditions) | No operations, for a column that is not nullable |
| `HasExclusionConstraint(c => c.WithEquality(…).WithOverlaps(…))` | `pg.Equal(…).Overlaps(…)`, in the order written | No operations |
| `HasFilter(x => …)` on the builder | `where: x => …` | No operations |
| `HasName("…")` | `name: "…"` | No operations |
| No name | No `name:`: the default is `EX_{table}_{elements}`, as 5.x built it | No operations |
| `IsDeferrable(initiallyDeferred: true)` | `deferral: Deferral.InitiallyDeferred` | No operations |
| `IsDeferrable()` | `deferral: Deferral.InitiallyImmediate` | No operations |
| Not deferrable | No `deferral:`, which is `Deferral.NotDeferrable` | No operations |
| `WithExpression("lower(\"Code\")", "=")` | `pg.Equal(x => x.Code.ToLower())`. See [Expressions](#expressions) | No operations when the text is the same |
| `WithColumn("Column", "-\|-")`, any other operator | `.WithUncheckedOperator(x => x.Column, "-|-")`, after the first element. The database checks the operator when the migration is applied | Not in the fixture |
| `UseMethod("spgist")`, one element | `new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Column("…"), ExclusionOperator.Overlaps))`; a factory method comes with [#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25) | No operations |
| `UseMethod("gist")` | `pg.Exclusion(…)`, which is GiST | No operations |
| `UseMethod("btree")`, `"hash"`, `"gin"`, `"brin"`, or SP-GiST with more than one element | Refused: the snapshot is not read. PostgreSQL cannot back an exclusion constraint with these, or it is a unique constraint. Remove it in the 5.x project first, apply that migration, and keep it by hand-written SQL afterwards | Refused |
| Every element compared by `=` | Refused, in the snapshot and in the model: that is a unique constraint. Replace it with a unique index in the 5.x project first | Refused |
| On the root of a TPC hierarchy | Refused: 5.x created a copy on every table of the hierarchy ([#43](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/43), [#118](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/118)) | Refused |
| On a derived type that shares its table with its base type | Declare it on the base type: the same constraint, and no operations. A constraint that reads a column only the derived type has cannot move until [#85](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/85): move the property to the base type, or map the derived type to a table of its own | Refused on the derived type |
| `UseBtreeGist()` | `modelBuilder.HasPostgresExtension("btree_gist")`, EF Core's own. Without it, the migration that first needs `btree_gist` writes `CREATE EXTENSION IF NOT EXISTS btree_gist` | |
| `SuppressTemporalExtensionAutoInjection()` | `modelBuilder.SuppressBtreeGistInjection()`, which sets the same annotation | |
| `GetDeclaredExclusionConstraints()`, `GetExclusionConstraints()` | `entityType.GetDeclarations<PgExclusion>()`, on the declaring type | |
| `FindExclusionConstraint(name)` | `entityType.FindDeclaration<PgExclusion>(name)` | |
| `AddExclusionConstraintFilter(…)` | `entityType.Redeclare<PgExclusion>(ex => ex.AndWhere(…))`. How a condition is written outside `DeclarePostgres` is not settled ([#112](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/112)) | |
| `ExclusionConstraintDeclaration`, `ExclusionPartDefinition`, `ExclusionConstraintDefinition` | `PgExclusion`, `ExclusionElement` | |
| `NpgsqlExclusionAnnotations.Constraints` | Not public. The snapshot's `CustomExclusion:Constraints` is read; in the context's own model it is refused | |

### Expressions

5.x had no lambda for an element: an expression was SQL text, written by hand. SchemaKit writes an
expression from a lambda, as Npgsql's queries spell it. The two are the same constraint, but the
diff compares what is stored, so they diff to nothing only when the text is the same:

- `WithExpression("lower(\"Code\")", "=")` against `pg.Equal(x => x.Code.ToLower())`: the column
  `Code` has a capital, Npgsql quotes it, the text is `lower("Code")` on both sides, and the
  migration has no operations. That is the fixture's case.
- `WithExpression("lower(\"code\")", "=")` against `pg.Equal(x => x.Code.ToLower())` on a column
  named `code`: Npgsql writes a lower-case name bare, so the lambda renders `lower(code)`, and the
  migration drops the constraint and creates it again. Written as `lower(code)` in 5.x, it moves
  with no operations.

A raw SQL element that keeps 5.x's text as it was comes with
[#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25).

## Conditions

A condition is a lambda over the entity, translated where it is written: a lambda with no
translation throws in `OnModelCreating`. The translator is 5.x's, ported with its rules.

Translated: comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=`), `== null` and `!= null` as `IS NULL` and
`IS NOT NULL`, `&&`, `||`, `!`, boolean properties, `HasValue` and `Value` of a nullable, and on the
operands property paths, `ToLower`, `ToUpper`, `Trim`, `TrimStart`, `TrimEnd`, `Substring`,
`Replace`, `Length`, `+` on strings, `??`, and constants of strings, booleans, characters and
numbers.

A comparison that can be null is written as EF Core writes it, so that the constraint holds for the
rows C# says: `x.Tag != "blocked"` on a nullable column is
`("Tag" <> 'blocked' OR "Tag" IS NULL)`. A 5.x string filter `{Tag} <> 'blocked'` leaves those rows
out. The two are different conditions, and the first migration after the move drops the
constraint and creates it again. `x.Tag != null && x.Tag != "blocked"` covers the rows 5.x's
condition covered, and is rebuilt too, since its text is not 5.x's.

Refused where the lambda is written, each with a message that says why:

- a comparison of an enum: how it is stored depends on its value conversion;
- a `DateTime` or `Guid` literal, and any value whose SQL spelling depends on the database's
  settings;
- a member of a scalar that the model does not map, such as `x.Start.Year`;
- a member of a JSON document ([#43](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/43));
- a conversion that changes the value, such as `(int)x.Amount == 2`, or `x.Count > 2.5` on an
  integer, where the compiler converts the property
  ([#103](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/103)). Compare the
  property with a value of its own type, `x.Amount == 2m`. 5.x left such a conversion out and stored
  `{Amount} = 2`;
- a method outside the list above.

## Migrations 5.x scaffolded

They stay, and SchemaKit's SQL generator applies them to a new database:

- **Exclusion constraints** are SQL operations in a migration 5.x scaffolded, and apply as they are.
  `LegacyHandoverLiveTests` applies the fixture's migration, as 5.6.0 scaffolded it, on both
  servers.
- **An operation that only 5.x's own SQL generator rendered is refused, not replayed** as something
  else ([#110](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/110)): a
  temporal unique constraint as 5.0.1 and earlier scaffolded it, a temporal foreign key with its
  period annotations, and an index with an expression or `NULLS FIRST`/`LAST`, whichever 5.x
  version scaffolded it. Npgsql's generator would write a plain
  unique constraint under the temporal one's name, and the migration would apply. The message
  names the operation and the statement 5.x wrote; apply such a migration with 5.x, or replace the
  operation in the migration with `migrationBuilder.Sql(…)` of that statement. Replaying them is
  [#28](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/28).

## What cannot move yet

A model snapshot that holds a 5.x annotation nothing in `preview.1` reads is refused at the first
diff, whatever the model declares
([#93](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/93)). Passed over, its
objects would be created a second time, or never dropped. Such a project stays on 5.x until the
preview that reads it:

| The snapshot holds | Comes with |
|---|---|
| Indexes: `CustomIndex:CompositeIndexes`, or `CustomIndex:IsIndexed` on a property | `preview.2`, [#27](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/27) |
| Temporal constraints and temporal foreign keys: `CustomTemporal:Constraints`, `CustomTemporal:ForeignKeys` | [#18](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/18) |
| Exclusion constraints, with a context wired without the PostgreSQL package | Wire it as [above](#the-wiring) |

## Every other 5.x entry point

Where each goes: **native**, the EF Core 11 or Npgsql 11 API named; **later**, a SchemaKit form in
a later preview, with its issue; **dropped**, no replacement on purpose. Whether a native form gives
the same DDL and the same default name as 5.x is not measured yet
([#3](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/3)), so the first
migration after such a move is not documented here.

### `EFCore.ComplexIndexes`

| 5.x | Goes to |
|---|---|
| `HasComplexIndex(…)`, every overload, on a complex property or on the entity type | Later, [#27](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/27). Native for a column-only index: `HasIndex(…)`, with `IsUnique()`, `HasFilter(sql)`, `HasDatabaseName(…)` |
| `HasComplexCompositeIndex(…)`, both overloads | Later, #27. Native: `HasIndex(x => new { … })` |
| `ComplexIndexBuilder.IsUnique()`, `HasFilter(string)`, `HasName(string)` | Later, #27 |
| `DbOrder.Asc`, `DbOrder.Desc` | Later, #27. Native: `IsDescending(…)` |
| `DbOrder.NullsFirst`, `DbOrder.NullsLast`, `DbNullSort` | Later, #27. Native on Npgsql, for a column: `HasNullSortOrder(…)` |
| `IIndexExpression`, `SqlIndexExpression` | Later, [#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25) and #27 |
| `GetDeclaredComplexIndexes()`, `GetComplexIndexes()`, `FindComplexIndex(name)` | Later, #27: `GetDeclarations<…>()` and `FindDeclaration<…>(name)` over the index type |
| `IMutableEntityType.AddComplexIndex(…)`, `AddComplexIndexFilter(…)` | Later, #27 |
| `UseComplexIndexes()`, `UseComplexIndexes<TContext>()`, `AddComplexIndexes()` | `UseSchemaKit()` on the options builder, `AddEntityFrameworkSchemaKit()`: the kernel alone, for a provider without a SchemaKit package. It declares nothing by itself |
| `IndexNameScope` | Dropped: a property of the provider's dialect |
| `CompositeIndexDefinition`, `IndexPartDefinition`, `ResolvedIndexPart`, `CompositeIndexSerializer`, `IndexPartsSerializer`, `ComplexIndexDeclaration` | Dropped: declarations are values, and their stored form is SchemaKit's |
| `CustomDesignTimeServices` | Dropped: there is no design-time reference |
| `CustomMigrationsModelDiffer` and its virtual methods | Dropped: a package adds objects through SchemaKit's contributor contract, which is experimental until `rc.1` (`SK9001`) |
| `CustomMigrationsModelDiffer.RuntimeWiringSentinel` | Dropped |
| `ComplexIndexAnnotations.*` | Dropped as public API |

### `EFCore.ComplexIndexes.PostgreSQL`

| 5.x | Goes to |
|---|---|
| `HasExclusionConstraint(…)` and its builder | SchemaKit, [above](#exclusion-constraints) |
| `UseNpgsqlComplexIndexes()`, `UseNpgsqlComplexIndexes<TContext>()`, `AddNpgsqlComplexIndexes()` | `UseNpgsql(…, o => o.UseSchemaKit())`, `AddEntityFrameworkSchemaKitNpgsql()`, [above](#the-wiring) |
| `UseGin()`, `UseGist()`, `UseBrin()`, `UseHash()`, `UseSpGist()` | Later, #27. Native: `HasMethod("gin")` |
| `HasOperators(…)` | Later, #27. Native: `HasOperators(…)` |
| `IncludeProperties(…)` | Later, #27. Native: `IncludeProperties(…)` |
| `IsCreatedConcurrently()` | Later, #27. Native: `IsCreatedConcurrently()` |
| `AreNullsDistinct(false)` | Later, #27. Native: `AreNullsDistinct(false)` |
| `UseCollation(…)` | Later, #27. Native: `UseCollation(…)` |
| `HasStorageParameter(name, value)` | Later, #27. Native: `HasStorageParameter(…)` |
| `HasExpressionIndex(…)`, every overload, and `ExpressionIndexBuilder` | Later, #27 |
| `HasFilter<TEntity>(predicate)` on an index builder, `AddComplexIndexFilter<TEntity>(…)` | Later, #27 |
| `HasTemporalConstraint(…)` | Native: `HasAlternateKey(…).WithoutOverlaps()` or `HasKey(…).WithoutOverlaps()`. Its snapshot is refused until [#18](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/18) |
| `HasTemporalForeignKey(…)` | Native: `HasForeignKey(…).WithPeriod()`. Its snapshot is refused until #18 |
| `UseBtreeGist()`, `SuppressTemporalExtensionAutoInjection()` | [Above](#call-by-call) |
| `NpgsqlComplexIndexSqlGenerator`, `NpgsqlComplexIndexMigrationsModelDiffer`, `NpgsqlComplexIndexDesignTimeServices` | Dropped: SchemaKit's services are internal |

### `EFCore.ComplexIndexes.SqlServer`

All of it comes with SQL Server support, [#29](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/29).

| 5.x | Native |
|---|---|
| `IsClustered()` | `IsClustered()` |
| `IncludeProperties(…)` | `IncludeProperties(…)` |
| `IsCreatedOnline()` | `IsCreatedOnline()` |
| `HasFillFactor(int)` | `HasFillFactor(…)` |
| `SortInTempDb()` | `SortInTempDb()` |
| `UseDataCompression(…)` | `UseDataCompression(…)` |
| `UseSqlServerComplexIndexes()`, `<TContext>`, `AddSqlServerComplexIndexes()` | None yet: SchemaKit has no SQL Server package |
| `SqlServerComplexIndexMigrationsModelDiffer`, `SqlServerComplexIndexDesignTimeServices` | Dropped |

## Known limits of this preview

Measured in the review of 2026-11-14 and checked again since.

- **A table moved back to the default schema stays where it was on Npgsql**
  ([#105](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/105)).
  `ToTable("role_grants", "grants")` becoming `ToTable("role_grants")` applies, and the table stays
  in `grants` with its constraints: Npgsql writes `SET SCHEMA` only when the new schema is not
  null. A constraint rebuilt in the same migration fails with `42P01`. This is the Down of every
  move into a schema. It is Npgsql's to fix; #105 tracks the report upstream.
- **EF Core 11.0.0 beside Npgsql's rc.3.** `Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0-rc.3
  depends on EF Core `11.0.0-rc.3.26522.148` exactly. A project that references EF Core 11.0.0
  restores with `NU1608` warnings, one per EF Core package Npgsql pins that the project lifts, and
  11.0.0 is what resolves. SchemaKit is built against rc.3.
