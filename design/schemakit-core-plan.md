# SchemaKit core: the plan after EF Core 11 GA

Written 2026-11-11 and **decided the same day**: the maintainer's answers are in
[section 7](#7-decisions). It revises `design/schemakit-api.md` (written against rc.2) and does not
replace it: the public surface there stands, the core underneath changes in the places listed in
[section 5](#5-the-draft-section-by-section). Where the two disagree, this document holds.

Inputs:

- **Upstream**, read at dotnet/efcore tag `v11.0.0` and npgsql/efcore.pg tag `v11.0.0-rc.3`.
- **A second design.** GPT-6.1 designed the core cold, without seeing the draft, then attacked the
  draft and this plan's positions over two more rounds. Every claim of its that this plan adopts
  was checked in the code first.
- **Two decisions of the maintainer:** SchemaKit has an entirely new core, and it lives in its own
  repository. This repository is not renamed.

## 1. The verdict

1. **Decorate, do not subclass.** SchemaKit wraps `IMigrationsModelDiffer` and
   `IMigrationsSqlGenerator` instead of deriving from EF's and Npgsql's classes. No EF-internal
   type is in the inheritance chain, EF 12's sixth constructor parameter is nobody's problem, and
   a package that replaced the differ before SchemaKit keeps working inside it.
2. **One wiring point, no `.targets`.** `UseSchemaKit()` is the only registration. EF 11 hands the
   context's own differ to design time (measured from a packed consumer, spike B), so the injected
   `DesignTimeServicesReferenceAttribute`, its `build/` and `buildTransitive/` copies and the
   order-independence machinery of 5.x go.
3. **Missing wiring fails where the declaration is written.** A convention stamps the model when it
   is created; `DeclarePostgres` throws in `OnModelCreating` without the stamp.
4. **No lowering into EF's native index metadata.** A native JSON-member index is rendered at apply
   time from the model present then. That is the failure this package exists to prevent.
5. **Contributors answer two more questions, and both are mandatory:** what to do with a changed
   pair (`None`, `InPlace`, `Rebuild`), and what an EF operation on a column or table they depend
   on does to their object (`Unaffected`, `Follows`, `Lost`, `Blocks`). The second one replaces
   five separate 5.x bug fixes with one mechanism.
6. **A declaration never replaces another one silently.** The draft's identity rule loses an index
   without a word (confirmed in the prototype).
7. **Three oracles decide correctness, not example tests:** catalog equivalence, behaviour on a
   live server, and a golden corpus per renderer epoch.
8. **A catalog verifier ships as a feature.** It is the one guard that does not depend on which
   differ ran.

The draft's surface (declarations as immutable values, provider-typed factories, closed unions,
`Sql.Raw<T>`, one annotation per declaration, the compile probes) is unchanged. Both designs
arrived at it or kept it.

## 2. Upstream on 2026-11-11

| Fact | Where | Consequence |
|---|---|---|
| EF Core 11.0.0 is on nuget.org. Npgsql EF is at 11.0.0-rc.3 and depends on EF Core `[11.0.0-rc.3.26522.148]`, exactly | nuspec of rc.3 | The whole solution stays on EF rc.3 until Npgsql ships 11.0.0. A stable package cannot depend on a prerelease (NU5104), so SchemaKit ships previews until then |
| Design-time services: the context's services are seeded first (`TryAdd(_ => context.GetService<IMigrationsModelDiffer>())`), then assembly attributes, provider, EF's own, the user's `IDesignTimeServices` last | `DesignTimeServicesBuilder.cs:59-63` | The context's differ is the design-time differ. No `.targets` needed. Anything registered later still wins, which is the residual in [3.2](#32-wiring-one-registration-three-guards) |
| `ReplaceService` entries are applied after every extension's `ApplyServices`, and a service-only entry replaces any registration, factories included | `ServiceProviderCache.cs:93-104` | A decorator has to take over the `ReplacedServices` entry, not only the descriptor |
| The `Model` constructor dispatches `IModelInitializedConvention` before `OnModelCreating`; `RelationalMaxIdentifierLengthConvention` stamps the model there | `Model.cs:77` | The wiring stamp has a public, precedented hook |
| `HasDifferences` calls `Diff` directly. `Sort` buckets by exact type; a `SqlOperation` lands in the leftovers. `SqlOperation` has `Sql` and `SuppressTransaction` only, on `main` too. No differ plugin point, on 11 or on `main` | `MigrationsModelDiffer.cs` | Splice around EF's sorted list, as the draft says. Nothing upstream replaces this package's seam before EF 13 at the earliest |
| `CommandBatchPreparerDependencies` takes no `IModel` | its constructor | Resolving the differ while the model is being built should not cycle. Spike A proves it |
| Native JSON-member indexes: all three upstream bugs are fixed (dotnet/efcore#39064, #39065, npgsql/efcore.pg#3918). The DDL is built **at apply time** from `Relational:JsonIndex`, which the annotation provider yields at design time only and the C# operation generator skips on purpose. The generator falls back to the index in the model it is handed. SQL Server rejects unique, descending, filter and `INCLUDE` on a JSON index | `RelationalAnnotationProvider.cs`, `CSharpMigrationOperationGenerator.cs`, `SqlServerMigrationsSqlGenerator.cs:964`, Npgsql PR #3974 | A migration's JSON index depends on the model at apply, not at scaffold. SchemaKit's own JSON-member indexes stay, rendered at design time |
| Native `HasIndex` over complex paths: `HasFilter` is string-only; `INCLUDE` cannot name a non-JSON complex path on SQL Server | `RelationalIndexBuilderExtensions.cs`, `SqlServerModelValidator.cs:247` | Typed filters and `INCLUDE` on complex members stay SchemaKit's |
| Npgsql rc.3: no `EXCLUDE` (efcore.pg#1975 open), no general expression index (efcore.pg#119 open). `WithoutOverlaps()` on keys only, PostgreSQL 18 only. `INpgsqlRangeTypeMapping` is in `Storage.Internal.Mapping` | rc.3 source | Exclusion constraints and expression indexes have no native rival. The range test stays behind one internal helper |
| C# 15 `closed` hierarchies and `union` are in Roslyn's shipped C# 15.0 table. System.Text.Json in .NET 11 infers closed-hierarchy polymorphism | Roslyn `Language Feature Status.md`, Learn | The draft's type model holds. Not yet run here on the GA compiler: only SDK 11.0.100-rc.1 is installed on this machine (gate 0) |
| EF 11 writes the latest migration id into the snapshot | Learn, What's New | Snapshots conflict across branches more often. One annotation per declaration matters more, not less |

## 3. The core

### 3.1 Repository and packages

This repository, `CaffeinatedCoder/CodoMetis.EFCore.SchemaKit`. `net11.0` only.

| Package | Holds | References |
|---|---|---|
| `CodoMetis.EFCore.SchemaKit` | Kernel: declarations and the read model, the host (differ and generator decorators), resolver, change planner, impact analysis, SQL vocabulary, diagnostics, wiring guards, the provider-agnostic 5.x readers | `Microsoft.EntityFrameworkCore.Relational` |
| `CodoMetis.EFCore.SchemaKit.PostgreSQL` | `PgIndex`, `PgExclusion`, their contributors, the dialect, the PostgreSQL 5.x readers and replay, the catalog verifier | kernel `[x.y.z]`, Npgsql EF |
| `CodoMetis.EFCore.SchemaKit.SqlServer` | `SqlServerIndex`, its contributor and dialect | kernel `[x.y.z]`, EF SQL Server |

The analyzer of [3.9](#39-expression-translation) ships inside the kernel package
(`analyzers/dotnet/cs`), so whoever can write a declaration has it.

What this repository takes from `EFCore.ComplexIndexes`, as files, not as history: the convention tests
(changelog, packaging, documentation links and API names, workflow gate, CI inputs, release
wiring, security policy), `release.yml` and `dotnet.yml`, `MigrationHarness`, the translator
parity sweep, the three skills, and `design/` with the probes project from PR CaffeinatedCoder/EFCore.ComplexIndexes#63.

What it does not take: `.targets` files, `ComplexIndexDesignTimeRegistration`, the consumer smoke
test's reason for neutering `.targets` (the test stays, with a different break: remove
`UseSchemaKit()` and it must fail).

5.x compatibility is tested against the **published** 5.x packages: a fixture project on
`net10.0` restores `EFCore.ComplexIndexes` 5.6.x from nuget.org, scaffolds real snapshots and
migrations, and the results are checked in as golden files. No shared source tree is needed, and
the fixtures are what a consumer actually has.

### 3.2 Wiring: one registration, three guards

```csharp
options.UseNpgsql(cs, o => o.UseSchemaKit());
options.UseSqlServer(cs, o => o.UseSchemaKit());
options.UseSqlite(cs).UseSchemaKit(k => k.IgnoreDeclarationsOf(SchemaProvider.PostgreSql));   // tests on SQLite
```

| Guard | Catches | How |
|---|---|---|
| **Stamp** | `UseSchemaKit()` forgotten, at run time or in an `IDesignTimeDbContextFactory` | The options extension adds an `IConventionSetPlugin` whose `IModelInitializedConvention` stamps the model. `DeclarePostgres` and `DeclareSqlServer` throw without the stamp, naming the call to add |
| **Ownership** | Another package in front of SchemaKit's differ or generator | Two checks. `Validate`, when the context is constructed, refuses a service-only `ReplaceService` made after `UseSchemaKit()`. When the model is built, the plugin resolves both services and throws unless the outermost instance is SchemaKit's: that one catches what the options do not show, a registration added to a caller-owned provider after SchemaKit's, and a replacement keyed on SchemaKit's own type |
| **5.x still referenced** | The old packages left in the project, whose `.targets` would put the 5.x differ in front at design time | The context's assembly or the entry assembly carries a `DesignTimeServicesReferenceAttribute` into an `EFCore.ComplexIndexes` assembly: throw, naming the packages to remove. The attribute is what does the damage, so it is what the guard looks for; a loaded assembly would be a guess |

**The decorator's registration,** as spike A measured it on Npgsql, SQL Server and SQLite
(`spikes/A.DecoratorOwnership`, 69 conditions, each mechanism shown to fail its conditions when
removed). It takes three parts, and none is redundant:

- `UseSchemaKit()` reads the `ReplacedServices` entry of the service, keeps its type as the inner, and
  takes the entry over. EF applies that entry after every extension's `ApplyServices`, so it also
  beats a differ another extension registers directly, in either order.
- `ApplyServices` adds SchemaKit's own registration as well. Without it, a replacement keyed on EF's
  type (`ReplaceService<IMigrationsModelDiffer, MigrationsModelDiffer, X>()`) rewrites the provider's
  only registration and SchemaKit is gone.
- The inner service is found by replaying every other extension's `ApplyServices` into a scratch
  collection and taking the last registration, then applying the replacements EF would. No
  EF-internal type is named: the spike compiles with `EF1001` as an error.

What another package's registration becomes:

| The other package | Before `UseSchemaKit()` | After it |
|---|---|---|
| `ReplaceService<TService, X>()` | SchemaKit's inner | Refused when the context is constructed: EF keeps one entry per service, so the two cannot both hold it |
| `ReplaceService<TService, TCurrent, X>()` keyed on EF's type | Inner | Inner |
| A registration in its extension's `ApplyServices`, by type or factory | Inner | Inner |
| A registration added to a caller-owned provider (`UseInternalServiceProvider`) | Inner | Refused when the model is built |

With `UseInternalServiceProvider`, the caller adds `AddEntityFrameworkSchemaKit()` to the collection
after the provider's `AddEntityFramework…()`, and calls `UseSchemaKit()` after
`UseInternalServiceProvider()`. The other order records `ReplaceService` entries EF then refuses, with
its own message.

Resolving both services while the model is being built does not cycle on any of the three providers.
The check runs once per model build; a model-cache hit skips it, and the decorators are still there,
since they are resolved per context.

**Design time needs nothing more.** Spike B measured it from a packed consumer outside the
repository (`spikes/B.DesignTime`): a package with no `build/` folder, on SQLite, Npgsql and SQL
Server, with a parameterless constructor, an `IDesignTimeDbContextFactory` and a host with
`AddDbContext`. `migrations add`, `has-pending-model-changes`, `migrations script` with and without
`--idempotent`, `database update`, a bundle and `dbcontext optimize` all use the context's differ. A
change that only SchemaKit can see is reported by `has-pending-model-changes` and refused by
`Migrate()`. A factory that forgets `UseSchemaKit()` fails at the declaration and writes no
migration.

**A compiled model is not the gap it was taken for.** `EnsureCreated()` and `Migrate()` build the
design-time model from `OnModelCreating` even when a compiled model is in use, so a compiled model
without `UseSchemaKit()` throws at the declaration there (measured, both providers). A context that
only queries runs no SchemaKit code and needs none.

**What no plugin can guard**, stated in the docs instead of promised away:

- a model cache shared through `UseMemoryCache` between options with and without `UseSchemaKit()`: the
  context without it gets the stamped model from the cache, so nothing throws, and its create script
  lacks every declaration (measured in spike A, condition C18);
- a user's own `IDesignTimeServices` that registers a differ: EF adds it last, and a simulation of
  EF's design-time composition cannot prove what the real collection holds. Measured in spike B:
  the migration scaffolds the tables without the index, the snapshot records the declaration, and
  no later diff ever notices;
- a host with no SchemaKit at all applying 5.x migrations with pre-5.0.2 temporal operations, which
  carry no failure marker.

The catalog verifier ([3.11](#311-the-catalog-verifier)) is the answer to all three, since it reads
the database and not the pipeline.

### 3.3 The seam

- **`SchemaKitModelDiffer : IMigrationsModelDiffer`**, scoped, a decorator. `GetDifferences` calls the
  inner differ, then plans and splices SchemaKit's operations around the sorted list.
  `HasDifferences` runs the same plan and returns whether it is empty, so the pending-changes check,
  `has-pending-model-changes` and `migrations remove` see SchemaKit's changes without the 5.x
  override. What 5.x took from protected members (renamed tables, renamed columns) is read from the
  inner differ's output operations.
- **The host knows nothing of declarations.** It asks an internal `ISchemaOperationPlanner` for the
  operations before and after the inner differ's, and an `IMigrationOperationRewriter` list for the
  generator. Declarations, descriptors and contributors arrive behind the planner, so the host could
  be built and tested first (#21).
- **Only `SqlOperation`, rendered at design time**, for every new object, indexes included. Unchanged
  from the draft, and both designs chose it.
- **`SchemaKitSqlGenerator : IMigrationsSqlGenerator`**, a decorator with one job: rewrite operations
  that carry 5.x annotations into `SqlOperation`s and hand the whole list to the inner generator, so
  order, transaction suppression and script options are the provider's. It never derives from
  `NpgsqlMigrationsSqlGenerator`. An owned annotation in a shape it does not know is an error, not a
  pass-through.
- **No EF-internal API in the kernel.** `EF1001` is an error with an allowlist file, checked by a
  convention test. Today's list would be one entry, the range-mapping interface in the PostgreSQL
  package.

### 3.4 Declarations, identity, storage

The draft's values, factories and one-annotation-per-declaration stand. The identity rule changes.

**The defect.** The prototype's identity is `Method|Targets|Where`, and `Declare` calls
`SetAnnotation`. `pg.Unique(x => x.Code)` followed by `pg.BTree(x => x.Code)` in one list writes the
same key twice: the unique index is gone, no error, and the migration applies clean
(`PgDeclarations.cs:35`, `Seam.cs:62`). 5.x has the same rule, where a fluent call re-declaring an
index made sense. A list of values has no such reading.

**The rule:**

- Annotation key: `CodoMetis:{Owner}:{hash}`. The hash is over `(kind, explicit name)` for a named
  declaration, and over `(kind, canonical content with every non-operational option)` for an
  unnamed one.
- `Declare` never replaces. A second declaration with the same key throws at the call, naming both.
- `Redeclare` is the only replacing path. It computes every resulting key first and throws if two
  declarations map onto one, before it changes anything.
- Two unnamed declarations that differ only in options get different keys and the same default
  name, which the name-claim check reports at diff.
- Names compare by the provider's rule (ordinal on PostgreSQL, case-insensitive on SQL Server).

**Built in #7 and #9,** in the kernel, public and experimental (`SK9001`):

- **The kind in the key is the case of the union** (`$kind`), not the owner, which the key carries
  already. So an index and a trigger of one name under one owner are two declarations. Whether the
  name is taken twice depends on the namespaces of the two kinds, which is the database's rule and
  the name claims' to answer, not the store's.
- **The kernel computes the key, not the contributor.** A declaration writes its properties through
  `SchemaJsonWriter`, and what does not change the object through `writer.Operational`. The value is
  everything written; the identity of an unnamed declaration is the same call with the operational
  part left out. A contributor cannot hash one thing and store another.
- **The value** is one JSON object in a fixed order: `$kind`, `v`, `r`, `name` when there is one,
  then the declaration's properties as it writes them. `v` is the format version and is 2; a reader
  that meets another refuses it. The wiring stamp `CodoMetis:SchemaKit` = `1` stays a mark of
  presence and is not the format version: a per-declaration `v` lets one snapshot hold declarations
  of two formats while it is being rewritten, and a model-wide number could not.
- **Nothing is omitted but a null, and the reader has no default.** The writer has no way to leave
  out a `false`, a zero or an empty list; the reader's methods either require the property or
  return null. A property a declaration does not read is refused, since it is an option a newer
  version wrote and this one would drop.
- **`Declare` is all or nothing,** and so is `Redeclare`, which compares results only: two names
  can be swapped in one call.

### 3.5 Resolution and renderer epochs

- **Templates at declaration, not a tree at diff.** A lambda is translated where it is written into
  provider-specific placeholder text (`lower({Email.Value})`), and that text is what the annotation
  stores. When a later version spells a function differently, the target's text differs from the
  snapshot's and the differ sees it. A tree rendered on both sides would hide exactly that, which is
  the 5.x `RenderingVersion` trap. GPT proposed the tree in round 1 and conceded this in round 2.
- **Epochs for the resolution layer only:** path to column or JSON extraction, casts, converter
  unwrapping. Each declaration carries its epoch `r`. An unknown epoch is an error.
- **A golden corpus enforces the epoch.** Checked-in triples of declaration, model fixture and
  resolved descriptor, per epoch, for configured models **and** for compiled snapshots (the
  converter-less property-bag case). A renderer change without a new epoch fails the corpus. This
  turns "remember to bump the version" into a build failure.
- **The mapping is the declared property's**, found through the property and the store object.
  The draft's `IColumn.PropertyMappings[0]` picks another property's converter on a shared column
  and renders a valid, wrong literal.
- **The snapshot lock is deferred, not rejected.** Recording the resolved descriptor in the snapshot
  would end old-renderer retention for new snapshots. It needs a decorated
  `ICSharpSnapshotGenerator`, so a design-time registration again, and a second permanent format.
  It is additive (a reader that prefers the lock when present), so it can come in an 11.x minor.

### 3.6 Change planning

The draft's `Rename(from, to) → bool` becomes a plan for any changed pair:

```csharp
public closed record SchemaChange
{
    public sealed record None : SchemaChange;                       // operational difference only
    public sealed record InPlace(ValueList<Statement> Statements) : SchemaChange;
    public sealed record Rebuild : SchemaChange;                    // drop before EF's operations, create after
}
```

**As built (#7):** `InPlace` carries no statements. `Plan` is handed the `SchemaSql` the statements
are written into, so the answer is `None`, `InPlace` or `Rebuild` alone, and the base class refuses
an answer that disagrees with what was written: an `InPlace` with no statement, or statements beside
a `None` or a `Rebuild`.

A rename is one `InPlace`. So are `ALTER INDEX … SET (fillfactor = …)`, `ALTER POLICY` and, where
PostgreSQL allows it for the constraint kind, a deferrability change. A statement carries its
transaction requirement. Matching: exact descriptor equality first; the rest are paired on
`(kind, scope, resolved name)`, then on equality modulo name; what is left is drops and creates.

Order, generalising 5.x's load-bearing one:

```
drops and rebuild-drops (source names, dependents first)
  → the inner differ's operations, untouched
  → in-place statements
  → creates and rebuild-creates (dependencies first)
```

### 3.7 Dependencies and impact

5.x fixed the interaction with EF's own operations one bug at a time: a renamed table, a renamed
column (CaffeinatedCoder/EFCore.ComplexIndexes#182), `ALTER COLUMN` on SQL Server (CaffeinatedCoder/EFCore.ComplexIndexes#84, CaffeinatedCoder/EFCore.ComplexIndexes#141, CaffeinatedCoder/EFCore.ComplexIndexes#186), a dropped column taking an index in
silence (CaffeinatedCoder/EFCore.ComplexIndexes#142), a schema move (CaffeinatedCoder/EFCore.ComplexIndexes#185), a generated column (CaffeinatedCoder/EFCore.ComplexIndexes#184), SQLite's table rebuild (CaffeinatedCoder/EFCore.ComplexIndexes#95). The
new core has one mechanism.

- **Every descriptor lists what it depends on**, as a closed union: `Column(table, name)`,
  `WholeTable(table)`, `SchemaObject(owner, name)`, `Extension(name)`. Placeholders give the columns
  of an expression exactly.
- **The kernel normalises EF's operations into a closed `BaseChange`:** `ColumnRenamed`,
  `ColumnAltered`, `ColumnDropped`, `TableRenamed`, `TableMoved`, `TableDropped`, `TableRebuilt`.
- **Every contributor answers `ImpactOf(descriptor, change)`**, a mandatory member:

  | Answer | Meaning | The kernel then |
  |---|---|---|
  | `Unaffected` | The object survives as it is | does nothing |
  | `Follows` | The database renames or moves it along | compares the source under its new identity, emits nothing |
  | `Lost` | The database drops it without saying so | requires the target not to declare it, or re-creates it after |
  | `Blocks` | The operation fails while the object exists | drops it before and creates it after, even if unchanged |

- **Measured (spike E, #20),** on PostgreSQL 16 and 18, which answer alike, and on SQL Server
  2022. Each cell ran the statements EF Core's own generator writes for the operation and then read
  the catalog; `spikes/E.ImpactTable` holds a live test per cell.

  | PostgreSQL 16 and 18 | Rename column | Alter column, type | Alter column, nullability | Drop column | Rename table | Schema move | Drop table |
  |---|---|---|---|---|---|---|---|
  | Index on the column, on an expression of it, with it in `INCLUDE`, or in its predicate | Follows | Unaffected | Unaffected | **Lost** | Follows | Follows | Lost |
  | Exclusion constraint with the column as an element | Follows | Unaffected | Unaffected | **Lost** | Follows | Follows | Lost |
  | Exclusion constraint with the column in its predicate only | Follows | Unaffected | Unaffected | Blocks | Follows | Follows | Lost |
  | Trigger `UPDATE OF` the column, or with it in `WHEN` | Follows | Blocks | Blocks | Blocks | Follows | Follows | Lost |
  | Trigger whose function body reads the column | **Stale** | Unaffected | Unaffected | **Stale** | Follows | Follows | Lost |

  | SQL Server 2022 | Rename column | Alter column, type | Alter column, nullability | Drop column | Rename table | Schema move | Drop table |
  |---|---|---|---|---|---|---|---|
  | Index on the column, or with it in `INCLUDE` | Follows | Blocks | Unaffected | Blocks | Follows | Follows | Lost |
  | Filtered index whose predicate reads the column | Blocks | Blocks | Blocks | Blocks | Follows | Follows | Lost |
  | Trigger whose body does not name the column | Unaffected | Unaffected | Unaffected | Unaffected | Follows | Follows | Lost |
  | Trigger whose body reads the column, `IF UPDATE` included | **Stale** | Unaffected | Unaffected | **Stale** | Follows | Follows | Lost |

  What the table says beyond its cells:

  - **Stale is a fifth outcome, and the union has four.** The operation succeeds, the trigger stays
    in the catalog, and it fails when it next fires (`record "new" has no field "a"`, Msg 207):
    every write to the table fails from then on. Neither server tracks a column named in a
    function or trigger body. `Lost` would leave the broken trigger in place, so the only one of
    the four answers that is safe is `Blocks`, whose action (drop before, create after) is the
    right one although the operation does not fail. Either `Blocks` is documented as "must be
    rebuilt around the operation" or it is renamed before the contract freezes; to decide with
    the maintainer (#24).
  - **PostgreSQL drops an index, and an exclusion constraint, with a column in silence,** also
    when the column is only in the index's predicate. An exclusion constraint with the column in
    its predicate only blocks the drop instead (2BP01). The same predicate, two answers.
  - **On PostgreSQL an `AlterColumn` that changes only nullability blocks under a trigger,**
    because Npgsql's generator writes `ALTER COLUMN … TYPE` for every `AlterColumnOperation`, and
    PostgreSQL refuses to alter the type of a column a trigger definition uses (0A000). The answer
    is for EF Core's statement, not for `DROP NOT NULL` alone.
  - **PostgreSQL rebuilds an index and an exclusion constraint under a new OID on a type change.**
    The object is the same by name and definition; a catalog check must not compare OIDs.
  - **SQL Server refuses a type change under any index that reads the column (Msg 5074), but not a
    change of nullability,** unless the index is filtered on the column. 5.x recorded the refusal
    for `ALTER COLUMN` as a whole (its issues 84 and 141).
  - **SQL Server refuses `sp_rename` of a column under a filtered index** (Msg 5074), as 5.x found
    (its issue 182); a plain index follows the rename.
  - Not measured: a change from nullable to not null, a clustered index, a computed column, and
    SQLite, whose table rebuild is `TableRebuilt` and has no statement to run here.

- **Fail closed.** A convention test holds a table of every `MigrationOperation` type in EF Core
  Relational and the two providers, each mapped to a `BaseChange` or marked harmless. An operation
  type outside the table that touches a table with SchemaKit objects is an error naming the type,
  and the newest-patch CI lane finds a new type before a consumer does.
- **`Sql.Raw` and columns.** A column named outside a `{placeholder}` is invisible to all of this.
  A token scan cannot prove its absence (GPT's objection, accepted), so the rule is the conservative
  one: raw text without placeholders depends on every column of its table.

The draft's optional `ISchemaRebuildAround` and `ISchemaDependent` go: an optional hook lets a
contributor compiled against an older contract miss a rebuild without anyone noticing.

### 3.8 SQL vocabulary

- **`QualifiedName`** joins `Identifier`. The prototype drops an index as `DROP INDEX IF EXISTS "ix"`
  while creating it on a schema-qualified table: with `tenant.ix` and `public.ix`, the drop resolves
  through `search_path` and can remove the wrong one (`PgContributors.cs:99`). Held on a live server
  since #23: the unqualified form removes the public schema's index and leaves the one meant. The
  types cannot enforce it for every kind, because a constraint and a trigger are legitimately named
  within their table; a contributor writes `table.Qualify(name)`, and its own live test holds it.
- **Built in #23,** as the first part of the contributor contract, public and marked experimental
  (`SK9001`): `Identifier`, `TableRef`, `QualifiedName`, `SqlName`, `SqlOperator`, `SqlFragment`,
  `SchemaScope`, `SchemaSql` with its handler, `ISchemaDialect` and `SchemaDialect`. The handler has
  no `bool` hole, unlike the prototype's: how a boolean is spelled is the dialect's (`true`, `ON`,
  `1`), so a contributor composes it. The builder itself refuses a `DROP … IF EXISTS`.
- **`ISchemaDescriptor.Scope`** is a closed `Table | Schema | Database`, replacing the mandatory
  `TableRef`. Domains and extensions are not table objects, and the contract is permanent.
- **Every statement ends with the provider's terminator** (`ISqlGenerationHelper.StatementTerminator`).
  EF runs a `SqlOperation` as it is. Spike B's first statement had none: `migrations add`,
  `database update`, a bundle and `Migrate()` all accepted it, and both generated scripts failed to
  apply with a syntax error, while a check of the script's text found the index in it. `SchemaSql`
  appends the terminator itself, so a contributor cannot leave it out.
- **No `IF EXISTS` on a drop, and no `DROP CONSTRAINT IF EXISTS` before an `ADD`.** 5.x's adoption
  shortcut is how a same-named exclusion constraint replaced a temporal one in silence. A collision
  fails. Adopting an existing object becomes an explicit operation, if it is wanted at all.

### 3.9 Expression translation

The draft's choice stands: a dedicated translator, EF's query pipeline as the oracle. Two release
criteria are added, because the prototype's miniature shows what a shortcut costs:

- **The full 5.x translator is ported, null compensation included.** The prototype renders
  `x.Tag != "blocked"` as `"Tag" <> 'blocked'`, which leaves out rows whose tag is null where C#
  keeps them. Under a partial unique index that admits duplicates.
- **The parity sweep and its answers-alike test come with it**, read from the translator's own
  tables as in 5.x.

**An analyzer ships in 11.0.0** (decided; the draft's Q4). A lambda body is the one place the type
system cannot reach, so the translator's refusals (an enum comparison, a literal with no portable
spelling, a method outside the subset, an expression key on SQL Server) become build errors. Two
rules keep it honest:

- It reads the translator's own tables, the ones the sweep reads. A test holds that every construct
  the translator refuses at declaration is reported by the analyzer and every one it accepts is not.
- The run-time refusal stays. The analyzer is absent under an older compiler, a disabled
  diagnostic, or a declaration built outside a lambda.

### 3.10 The contributor contract

```csharp
public interface ISchemaContributor
{
    string Owner { get; }
    SchemaPlacement Placement { get; }
    ISchemaDescriptor Resolve(ISchemaObject declaration, SchemaResolver resolver);
    void Create(ISchemaDescriptor descriptor, SchemaSql sql);
    void Drop(ISchemaDescriptor descriptor, SchemaSql sql);
    SchemaChange Plan(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql);
    Impact ImpactOf(ISchemaDescriptor descriptor, BaseChange change);
    IEnumerable<NameClaim> Claims(ISchemaDescriptor descriptor);
}

public interface ISchemaDescriptor
{
    SchemaScope Scope { get; }
    QualifiedName Name { get; }
    ValueList<Dependency> Dependencies { get; }
    ISchemaDescriptor WithName(QualifiedName name);
}
```

- The typed base class `SchemaContributor<TDeclaration, TDescriptor>` and its dependencies record
  stay as drafted. An optional `ICatalogProbe` serves the verifier.
- **Built in #7:** the interface, the base class, `ISchemaDescriptor`, the closed unions
  (`SchemaChange`, `Impact`, `BaseChange`, `Dependency`, `NameClaim`, `SchemaPlacement`),
  `ValueList<T>`, `Operational<T>`, and `AddSchemaContributor<T>()`, which adds a contributor to a
  context's services without touching the two EF services. Two contributors for one owner are
  refused. A descriptor type that is not a record is refused when its contributor is built.
  `SchemaResolver` exists as what `Resolve` is handed (the table, the dialect, a fragment builder)
  and cannot be derived outside the kernel; turning paths into SQL is #43. Nothing calls a
  contributor yet: that is the planner (#24).
- **The contract freezes only after a contributor outside the repository compiles against it.**
  ValueRanges' vocabulary and the ltree contributor are the candidates. Until then it is marked
  experimental with a diagnostic id, so previews can change it.
- **A public closed union is closed for the major.** Adding a case breaks every exhaustive switch
  of a consumer who builds with warnings as errors. That is the design's intent, so a new case
  waits for the next major.

### 3.11 The catalog verifier

```csharp
var report = await context.Database.VerifySchemaKitAsync(ct);
// per declared object: Present | Missing | Different(facet) | Unverifiable(reason)
```

- It resolves the model's declarations and asks the database's catalog for each: existence, kind,
  keys, uniqueness, predicate, and for a constraint its operators and deferrability, for a
  PostgreSQL index its validity.
- **`Unverifiable` is a result.** A contributor without a probe, or a role that cannot read the
  catalog, never reports clean.
- It shares its catalog readers with the test oracle, so the feature and the tests cannot drift.
- Use: a startup check, a health check, a CI step after `database update`. 11.0.0 covers the
  built-in PostgreSQL kinds.

### 3.12 Other providers and SQLite

- A declaration whose owner has no contributor in the active provider is an **error** at diff.
- The exception is explicit: `IgnoreDeclarationsOf(SchemaProvider.PostgreSql)` on the options of the
  other provider, logged once. Since the stamp already requires `UseSchemaKit()` on those options,
  a consumer testing on SQLite writes one argument. A warning by default, my first position, would
  let a suite pass on SQLite while saying nothing about the invariant; GPT held, and was right.
- **11.0.0 creates no objects on SQLite.** SQLite enters with the row guard (CaffeinatedCoder/EFCore.ComplexIndexes#64), behind a
  generator decorator with a fail-closed marker, as 5.x does it.
- **SQLite replay is still owed.** 5.x migrations hold `CustomIndex:AfterTableRebuild` operations
  and a deliberate failing marker; the kernel's generator decorator has to replay them on a fresh
  database.

### 3.13 Coming from 5.x

- **Readers, forever:** every 5.x annotation and historical JSON form, resolved by the 5.x rules
  frozen in a `Legacy` namespace that is never edited again (rendering versions 1 to 3).
- **Handover matcher, one way:** a 5.x descriptor and an operation of the inner differ that are
  equal in full physical terms (name, keys, order, filter, provider options) cancel out. That is how
  a `HasComplexIndex` moved to native `HasIndex`, or a temporal constraint moved to
  `WithoutOverlaps()`, costs no DDL.
- **A mismatch never discards.** A 5.x temporal constraint the native key cannot represent (a
  mutable period, a multirange) is a refusal with the documented path, not a silent drop. The
  draft reads temporal annotations "only for handover" and does not say what happens otherwise.
- **Replay:** the generator decorators of [3.3](#33-the-seam) and [3.12](#312-other-providers-and-sqlite).

## 4. Verification

| Oracle | Statement | Catches |
|---|---|---|
| **Catalog equivalence** | For generated model pairs (A, B) on PostgreSQL 16 and 18 and SQL Server 2022: the catalog after migrating A to B equals the catalog of `EnsureCreated(B)`; a second diff is empty after a compiled snapshot round trip; `Down` returns to A's catalog for reversible changes | Ordering, impact, rename and rebuild mistakes; snapshot churn |
| **Behaviour** | Per kind, writes that must fail do fail and writes that must pass do pass: duplicates under a partial unique index with null keys, overlapping ranges, a deferred constraint at commit | A renderer that is wrong the same way on both paths, which catalog equality cannot see |
| **Golden corpus** | [3.5](#35-resolution-and-renderer-epochs) | A rendering change that reaches no database |
| **Translator parity** | The 5.x sweep and answers-alike test | An index no query uses; a predicate that answers differently |
| **Compile probes** | The draft's 30, moved onto the real assemblies and the GA compiler | A forbidden declaration that compiles |
| **Wiring removal** | Each registration removed in turn, from a packed consumer outside the repository: `migrations add`, `has-pending-model-changes`, `EnsureCreated`, script, idempotent script, bundle, `dbcontext optimize` must each fail or agree. A generated script is **applied** to an empty database, the idempotent one twice; its text proves nothing (spike B) | Silent degradation of delivery; a script that does not run |
| **Guard mutation** | Each guard is shown to fail without what it guards (`verify-the-guard`): a dependency edge, an epoch bump, a contributor registration | A test that asserts nothing |
| **Newest EF patch lane** | As in 5.x, plus the operation-type table of [3.7](#37-dependencies-and-impact) | A patch that adds or changes an operation |

GPT's condition for shipping 11.0.0 was the behaviour oracle. It is the second row, and it is an
exit criterion.

## 5. The draft, section by section

| Draft | Verdict | Change |
|---|---|---|
| 1 to 8, surface and principles | **Keep** | P8's "values" gains the no-replace rule of [3.4](#34-declarations-identity-storage) |
| 9.1 Services | **Change** | Decorators, no `.targets`, the stamp, the resolve-time check |
| 9.2 Type mapping | **Change one line** | The declared property's mapping, not `PropertyMappings[0]` |
| 9.3 Translation | **Keep**, criteria added | Full null compensation and the sweep before any preview with typed predicates |
| 9.4 Resolution | **Change** | Impact and dependencies replace the optional rebuild hook |
| 9.5 Diffing | **Change** | A decorator; `SchemaChange` planning; the order of [3.6](#36-change-planning) |
| 9.6 SQL generation | **Keep**, plus | `QualifiedName`; no `IF EXISTS`; the legacy generator is a rewriting decorator |
| 9.7 Annotations | **Change the key rule** | Identity and no-replace; epochs with a corpus; snapshot fixtures |
| 9.8 Diagnostics | **Keep** | |
| 9.9 Ownership check | **Replace** | Three guards and the stated residuals; the design-time half is dropped as unprovable |
| 9.10 Contract | **Change** | `Plan`, `Impact`, `Scope`, `Dependencies`; frozen after an outside contributor |
| 10 to 11 Extension, TypeKit | **Keep** | The ltree walkthrough is rewritten against the new contract |
| 12 to 13 Left out, the mapping table | **Keep**, plus | The temporal handover's refusal path |
| 14 First slice | **Keep** | Both designs put exclusion constraints first |

## 6. Sequence

**Gate 0, before anything.**

- The new repository exists, with CI, the convention tests, the release workflow and a nuget.org
  trusted-publishing policy for its name.
- SDK 11.0.100 is installed and pinned in `global.json`; the 30 probes pass on the GA compiler.
- Every EF reference is on rc.3 (Npgsql's pin).

**Spikes.** A and B decide whether the host and its delivery work at all; both designs put them
first. No public API is written before they pass. **Both passed on 2026-11-11.**

| | Spike | It fails when | Then |
|---|---|---|---|
| **A** | Decorator plus resolve-time ownership check on Npgsql, SQL Server and SQLite; pooled contexts, `AddDbContextFactory`, `UseInternalServiceProvider`, a model-cache hit | Resolving the differ during model building cycles or the check is skipped on a cache hit | **Passed 2026-11-11** (#16): no cycle, and the outcome is in [3.2](#32-wiring-one-registration-three-guards). Runtime paths only; design time is spike B |
| **B** | No `.targets`: a packed consumer outside the repository runs every path of the wiring-removal row | Any design-time path does not get the context's differ | **Passed 2026-11-11** (#17): every path gets it, in all three ways `dotnet ef` builds a context. No `.targets`, no fallback attribute. It also found the terminator rule of [3.8](#38-sql-vocabulary) and corrected the compiled-model residual in [3.2](#32-wiring-one-registration-three-guards) |
| **C** | A real 5.x snapshot with exclusion constraints, and one temporal constraint moved to `WithoutOverlaps()`, diff to zero operations | The handover needs facts the snapshot does not hold | The guide documents one migration for that case (#10 allows exactly that) |
| **D** | `CREATE INDEX CONCURRENTLY` and a trigger as `SqlOperation`s in an idempotent script, applied twice, and in a bundle, on PostgreSQL and SQL Server | The provider cannot wrap the statement | That combination is refused with the declaration named |
| **E** | The impact answers for index, exclusion constraint and trigger against column rename, alter, drop, table rename and schema move, on live servers | A measured answer differs from the assumed one | The table is corrected; this spike is how it gets written |

**Slices.**

| Release | Contents | Exit |
|---|---|---|
| `11.0.0-preview.1` | Kernel host, the three guards, exclusion constraints on PostgreSQL, their 5.x reader, spike C's exclusion half | Catalog and behaviour oracles green for exclusion constraints on 16 and 18 |
| `preview.2` | `PgIndex` with expression and JSON-member keys and typed filters; the translator and its sweep; the 5.x index reader; legacy replay on PostgreSQL and SQLite; the handover matcher | A 5.x project's migrations replay on a fresh database; its snapshot diffs to nothing or the documented migration |
| `preview.3` | SQL Server package; the catalog verifier for PostgreSQL; the analyzer; the first contributor outside the repository (`CodoMetis.ValueRanges.EFCore.PostgreSQL.SchemaKit`) | The contract is compiled against from outside; analyzer and translator agree on every construct |
| `rc.1` | When Npgsql ships 11.0.0: floors move to `[11.0.0, 12.0.0)`, the contract freezes, the migration guide is complete | Every row of section 4 green |
| `11.0.0` | | Package validation gets its first baseline (#6) |

## 7. Decisions

Taken by the maintainer on 2026-11-11. "As recommended" means the plan above already says it.

**Repository**

| Question | Decision |
|---|---|
| Where SchemaKit lives | Its own repository, `CaffeinatedCoder/CodoMetis.EFCore.SchemaKit`. `EFCore.ComplexIndexes` is not renamed |
| The 5.x line in `EFCore.ComplexIndexes` | `main` carries it again: `release/5.x` is merged into `main` and retired |
| The SchemaKit issues filed there | Transferred here, with their history |
| The design and the probes of its draft PR CaffeinatedCoder/EFCore.ComplexIndexes#63 | Moved here as the first content; that PR is closed unmerged |

**Core**

| Question | Decision |
|---|---|
| Failure policy: the three guards and their stated residuals ([3.2](#32-wiring-one-registration-three-guards)) | As recommended |
| A declaration for a provider that is not the active one ([3.12](#312-other-providers-and-sqlite)) | An error, unless the options ignore that provider explicitly |
| How a rendering change reaches existing databases ([3.5](#35-resolution-and-renderer-epochs)) | Epochs with a golden corpus. The snapshot lock stays possible for an 11.x minor |
| Column-only indexes (#3) | Kept in SchemaKit's typed index. Nothing is lowered into EF's metadata; the guide points simple cases at native `HasIndex` |
| The catalog verifier ([3.11](#311-the-catalog-verifier)) | In 11.0.0, for the PostgreSQL kinds |
| Identity: no silent replacement ([3.4](#34-declarations-identity-storage)) | Adopted. It repairs a confirmed defect, so it was not put as a choice |
| The contract: `Plan` and `ImpactOf` mandatory, frozen after an outside contributor ([3.10](#310-the-contributor-contract)) | Adopted, with the freeze at `rc.1` |

**Surface** (the open questions of `design/schemakit-api.md`, section 16)

| Draft question | Decision |
|---|---|
| Q1 Names | As drafted: `DeclarePostgres` / `DeclareSqlServer`, `PgSchema<T>` / `SqlServerSchema<T>`, `Sql.Raw<T>`, owners `Pg.Index`, `Pg.Exclusion`, `SqlServer.Index`. Previews may still rename; the owners are permanent from 11.0.0 |
| Q2 Column-only indexes | Kept, as above |
| Q3 Exclusion element syntax | The staged chain stays |
| Q4 Analyzer | **In 11.0.0**, against the recommendation to wait for a minor ([3.9](#39-expression-translation)) |
| Q5 Indexes rendered at design time | Yes: both designs chose it ([3.3](#33-the-seam)) |
| Q6 Expression translation | A dedicated translator with EF's pipeline as the oracle, stored as templates ([3.5](#35-resolution-and-renderer-epochs)) |
| Q9 Design-time ownership check | Dropped as unprovable; the verifier answers it ([3.2](#32-wiring-one-registration-three-guards)) |
| Q12 ValueRanges' vocabulary | A small satellite, `CodoMetis.ValueRanges.EFCore.PostgreSQL.SchemaKit`, in the ValueRanges repository. It is the outside contributor of `preview.3` |
| Q14 First slice | Exclusion constraints |

**Still open,** none of them blocking `preview.1`: Q7 (every diagnostic of a diff at once; the plan
assumes yes), Q8 (server-version gates as errors once a version is set; the failure policy suggests
yes), Q10 and Q11 (TypeKit on .NET 11), Q13 (operator classes typed by CLR type).

## 8. What stays uncertain

- Spikes A and B passed, on EF Core `11.0.0-rc.3`. They have to be run again when Npgsql ships
  11.0.0 and the floors move, since both rest on EF's composition order, which no API promises.
- Spike B's SQL Server consumer ran the paths that need no database only.
- Nothing here was compiled against the GA SDK.
- Npgsql's rc.3 facts come from its source and tests, not from a run on this machine.
- The impact table is a hypothesis until spike E measures it.
- GPT held one objection to the end: no simulation proves who owns the design-time differ. This
  plan agrees and answers with the verifier, not with a stronger claim.
