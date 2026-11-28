# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is, and where it stands

`CodoMetis.EFCore.SchemaKit` 11.x is the successor of `EFCore.ComplexIndexes` 5.x for EF Core 11:
a new package with a new API and an entirely new core, in its own repository. The EF Core 10 line
stays in CaffeinatedCoder/EFCore.ComplexIndexes and shares no code and no branch with this one.

**The kernel has its host, its SQL vocabulary, the contributor contract, the declaration store, the
resolver, the planner and the lambda translator, and the PostgreSQL package has the first real
contributor: an exclusion constraint declared with `DeclarePostgres` reaches the database through
every path, on PostgreSQL 16 and 18. An index is the second object: all five slices of
`PgIndex` are in (#27 has the slices and what was decided for them), every access method,
`concurrently:`, members of a JSON document, and the readers for 5.x's indexes. A snapshot
scaffolded by 5.x with exclusion constraints and indexes moves to the same model declared here
with no operations, and a migration 5.x scaffolded with an index on an expression is replayed
as the statement 5.x wrote (#28, the PostgreSQL slice). `preview.2` is published and
`preview.3` is what is being prepared** (what is left of
the resolver is #43, of the planner #24, of the translator #26, of exclusion constraints #25:
a factory for SP-GiST, the compile probes). The repository holds:

| Path | What |
|---|---|
| `src/CodoMetis.EFCore.SchemaKit` | The kernel. So far the host (`UseSchemaKit()`, the differ and generator decorators, the wiring guards), the SQL vocabulary statements are built from, the contributor contract and the declaration store. Public are the registration methods and the contract, which is marked experimental (`SK9001`) until it freezes at rc.1; the rest is internal |
| `src/CodoMetis.EFCore.SchemaKit.PostgreSQL` | The PostgreSQL package: `UseSchemaKit()` on Npgsql's builder, the dialect that counts bytes, `PgExclusion` and `PgIndex` with their factory `PgSchema<T>` and `DeclarePostgres`, and a contributor for each. Written against the kernel's public contract alone: the kernel grants it no `InternalsVisibleTo` |
| `test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests` | Its tests: the stored form, the wiring, the statements of each diff, and the same diffs applied on a live server (`Integration/PgExclusionLiveTests.cs`) |
| `test/Fixtures/ComplexIndexes5x` | What the **published** 5.6.0 scaffolded on `net10.0`: `Generator/` is the consumer, `generate.sh` builds it outside the repository, `Scaffolded/` is the snapshot and migration of a model with exclusion constraints, `ScaffoldedTpc/` of two TPC hierarchies with a constraint on a root, `ScaffoldedOtherKinds/` of one with an index on a property, one on an entity type and a temporal constraint, which nothing reads yet, `ScaffoldedIndexes/` of one with 32 indexes of every kind 5.x stored, beside the script 5.x's own generator wrote for them (`Initial.sql`), `ScaffoldedConvention/` of one whose unique indexes and exclusion constraints got one more condition from a loop over the entity types, beside a condition of their own, with its script, `ScaffoldedReplay/` of two migrations whose indexes carry `CustomIndex:IndexParts` with every option 5.x's generator wrote beside the parts, and the scripts it wrote for them from nothing to the second and back (`Up.sql`, `Down.sql`). Checked in and never edited: the tests compile those files as they are, and a second run of the script changes nothing |
| `test/CodoMetis.EFCore.SchemaKit.Tests` | The kernel's tests, on Npgsql, SQL Server and SQLite. `Harness/TestKit.cs` is the stand-in planner the host's own tests still use; `Harness/SampleContract.cs` is a contributor written against the public contract alone. `Conventions/` holds the repository itself: the workflows, the changelog, the packages, SECURITY.md |
| `test/CodoMetis.EFCore.SchemaKit.SecondProvider.Tests` | The second provider (#119): two contributors nobody ships, written against the kernel's public contract alone, since the kernel grants the assembly no `InternalsVisibleTo`. A filtered nonclustered index with included columns on SQL Server and an index on an expression on SQLite, each with a dialect, and their tests: the SQL Server one on a live SQL Server 2022 (`Integration/`), the SQLite one on a database file. A comment that begins `Audit:` marks what the contract could not say |
| `test/CodoMetis.EFCore.SchemaKit.ValueRanges.Tests` | The cross-tests with CodoMetis.ValueRanges (#4): the only project that references both libraries, ValueRanges at `11.0.0-preview.1`, written against the PostgreSQL package's public surface alone. Exclusion constraints and a GiST index over a `DateRange`, a `RangeSet`, a `TimeRange` and a `LocalDateRange`, each beside its twin over Npgsql's own range, without a server and on a live one (`Integration/`) |
| `spikes/` | The two spikes the host was built on (A: decorators and ownership, B: design time from a packed consumer), D, which applies `SqlOperation`s through scripts and bundles on live servers and says which statements a script does not carry (`CREATE INDEX CONCURRENTLY` in an idempotent script on PostgreSQL, a bare `CREATE TRIGGER` in any script on SQL Server) and what EF Core does with an annotation on a `SqlOperation`, E, the impact table of the plan's section 3.7 measured on live servers, and F, which binds a declaration when the model is finalized and says what a convention sees there (no type mapping on a property yet: the mapping source is asked), for #114. B is the consumer smoke test until the real one replaces it |
| `design/schemakit-api.md` | The public surface and the type model, written against rc.2. Its open questions are answered in the plan |
| `design/schemakit-core-plan.md` | The plan for the core after EF Core 11 GA, with the maintainer's decisions (section 7). **Where the two disagree, the plan holds** |
| `test/ConsumerSmokeTest` | The consumer smoke test (#34): `run.sh` packs both packages, restores them into consumers outside the repository and drives every path a consumer has (`dotnet ef`, the runtime, a compiled model, a factory with a migrations assembly of its own, a project that comes from 5.x, and one that comes from 5.x with indexes on expressions, whose migration the packed package has to replay) on a live server, judged by `Judge/` from the catalog and a row that must be refused. `SMOKE_BREAK` takes a registration away to show it failing. `Mixed/` references the kernel at another version than the PostgreSQL package's, which NuGet builds with NU1608, and must be refused (#113) |
| `docs/moving-from-complexindexes-5x.md` | The guide for a project coming from 5.x, a first cut that covers `preview.1` (#73) |
| `test/SchemaKit.ApiProbes` | A prototype of the surface and 30 compile probes: each forbidden declaration is compiled with Roslyn and its diagnostic asserted, beside an allowed twin |

The prototype is not the implementation. It has defects the plan lists on purpose (sections 3.4,
3.8 and 3.9): an identity rule that loses a declaration, an unqualified `DROP INDEX`, and a
predicate translator without null compensation. Do not copy from it without reading those.

## Commands

```bash
# Build everything that ships, and its tests
dotnet build CodoMetis.EFCore.SchemaKit.slnx

# The kernel's tests, and the PostgreSQL package's (the filters below work for both)
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj
dotnet test test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.csproj

# The pack checks alone (they pack both projects into a temporary folder)
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "ClassName~PackagingConventionTests"

# Without the tests that need a server, and those alone (SCHEMAKIT_TEST_PG is an Npgsql connection string)
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "TestCategory!=Integration"
SCHEMAKIT_TEST_PG="Host=localhost;Username=postgres;Password=…;Database=postgres" dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "TestCategory=Integration"

# One class, one method
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "ClassName~WiringConditionTests"
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "FullyQualifiedName~C05_replace_service_before"

# The second provider's contributors: SQLite always, SQL Server when SCHEMAKIT_TEST_MSSQL is a SqlClient connection string
dotnet test test/CodoMetis.EFCore.SchemaKit.SecondProvider.Tests/CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.csproj
SCHEMAKIT_TEST_MSSQL="Server=localhost,1433;User Id=sa;Password=…;TrustServerCertificate=True" dotnet test test/CodoMetis.EFCore.SchemaKit.SecondProvider.Tests/CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.csproj --filter "TestCategory=Integration"

# The cross-tests with CodoMetis.ValueRanges, whose packages come from nuget.org; the live ones when SCHEMAKIT_TEST_PG is set
dotnet test test/CodoMetis.EFCore.SchemaKit.ValueRanges.Tests/CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.csproj

# The probes and the prototype's behaviour tests (outside the solution: they pin their own compiler)
dotnet test test/SchemaKit.ApiProbes/SchemaKit.ApiProbes.csproj

# The consumer smoke test, on the server SCHEMAKIT_TEST_PG names; a folder of .nupkg as the argument tests those
SCHEMAKIT_TEST_PG="Host=localhost;Username=postgres;Password=…;Database=postgres" test/ConsumerSmokeTest/run.sh

# The same with the consumer on a later EF Core than the packages' floor
SMOKE_EF_VERSION=<version> SCHEMAKIT_TEST_PG="…" test/ConsumerSmokeTest/run.sh

# Spike B, the first version of the consumer smoke test; SPIKE_PG turns on the PostgreSQL paths
spikes/B.DesignTime/run.sh

# Spike D, SqlOperations in scripts and bundles; SPIKE_D_PG16, SPIKE_D_PG18 and SPIKE_D_MSSQL are connection
# strings, and spike E's are read where one is not set. A server without one is reported as not measured
spikes/D.ScriptsAndBundles/run.sh

# Spike E, the impact table; SPIKE_E_PG16, SPIKE_E_PG18 and SPIKE_E_MSSQL are connection strings (its README)
dotnet test spikes/E.ImpactTable/E.ImpactTable.csproj

# Spike F, binding at model finalization; no server
dotnet test spikes/F.Binder/F.Binder.csproj
```

`global.json` pins SDK `11.0.100` with `rollForward: latestPatch`. A machine with only a prerelease
SDK cannot build here; do not loosen the pin to make it.

## The decided core, in ten lines

Read `design/schemakit-core-plan.md` before writing code. What must not be re-decided by accident:

1. **Decorators, never subclasses.** SchemaKit wraps `IMigrationsModelDiffer` and
   `IMigrationsSqlGenerator`. No EF-internal type is derived from; `EF1001` is an error with an
   allowlist.
2. **One wiring point, `UseSchemaKit()`. No `.targets`, no `DesignTimeServicesReferenceAttribute`.**
   EF hands the context's own differ to design time.
3. **A missing wiring fails at the declaration:** a model-initialized convention stamps the model,
   and `DeclarePostgres` throws without the stamp.
4. **Only `SqlOperation`s, rendered at design time,** for every new object, indexes included.
5. **Nothing is lowered into EF's native index metadata.** EF builds a native JSON-member index at
   apply time from the model present then.
6. **A declaration never replaces another silently.** `Declare` throws on a repeated key;
   `Redeclare` is the only replacing path.
7. **Checked at declaration, bound at finalization, epochs for resolution** (#114, decided
   2026-11-24). A lambda is checked where it is written and becomes provider-specific SQL when
   its model is finalized, from the model's facts, stored as segments and never rendered again
   from a tree. Changing how a path resolves needs a new epoch, and the golden corpus fails the
   build without one.
8. **Contributors answer `Plan` and `ImpactOf`,** both mandatory, both closed unions.
9. **No `IF EXISTS` on a drop, no `DROP … IF EXISTS` before an `ADD`.** A collision fails.
10. **A declaration for a provider that is not the active one is an error,** unless the options
    ignore that provider explicitly.

## The host

`UseSchemaKit()` is the whole registration. What it sets up, and why each part is there
(`design/schemakit-core-plan.md`, section 3.2; every part has tests that fail without it):

- **Three parts keep SchemaKit outermost.** `UseSchemaKit()` takes over the `ReplaceService` entry of
  `IMigrationsModelDiffer` and `IMigrationsSqlGenerator`, keeping an earlier replacement as the
  inner; `SchemaKitOptionsExtension.ApplyServices` adds registrations of its own, so a replacement
  keyed on EF's type cannot remove it; and the ownership checks refuse what is left.
- **`InnerServices` finds what to wrap** by replaying every other extension's `ApplyServices` into a
  scratch collection. That is why no EF-internal type is named anywhere under `src/`.
- **`SchemaKitWiringConvention`** runs when a model is created: it resolves both services, throws
  unless the outermost is SchemaKit's, refuses an assembly that still carries the 5.x design-time
  reference, and stamps the model (`CodoMetis:SchemaKit`). A declaration API calls
  `SchemaKitModelStamp.Require` first. The stamp lands in snapshots, so its name and value are
  permanent from 11.0.0.
- **The stamp says the model is wired; which owners can be declared is kept beside the model**
  (#113). The kernel's `UseSchemaKit()` alone stamps a model and renders nothing, so `Declare` and
  `Redeclare` also ask for a contributor of the declaration's owner and name the package to
  register. `AddSchemaContributor` adds a `SchemaContributorRegistration`, a plain value, since no
  contributor may be resolved while a model is built; the convention puts the owners into a
  `ConditionalWeakTable` keyed by the model. Not an annotation: it would land in the snapshot and
  move with the packages a context registers. A snapshot's own declarations are still the
  planner's to refuse, at diff. A contributor put into the collection without
  `AddSchemaContributor` cannot be declared for.
- **`SchemaKitPackages.RequireKernel(assembly)` is the version check** (#113): a package of this
  repository calls it first in each registration, from a method that calls nothing else of the
  kernel (the rest is in a `NoInlining` method), since the runtime binds a method's calls when it
  compiles it. It compares informational versions without the commit; the assembly version is
  `11.0.0.0` for every preview. Its signature never changes: a version that changed it would fail
  as the `MissingMethodException` it is there to replace.
- **A second `AddEntityFrameworkSchemaKit()` changes nothing** (#234): it would capture
  SchemaKit's own decorators as the inner services, and the differ built itself until the
  process ended with a stack overflow. The guard is the decorator's registration, so a differ
  another package adds after the first call is still refused at model build
  (`C22_internal_service_provider_with_schemakit_added_twice`).
- **`SchemaKitModelDiffer`** asks an internal `ISchemaOperationPlanner` for operations before and
  after the inner differ's and splices them; `HasDifferences` runs the same plan. The host knows
  nothing of declarations or contributors: they arrive behind that planner.
- **`SchemaKitSqlGenerator`** runs `IMigrationOperationRewriter`s (for operations 5.x scaffolded) and
  hands the inner generator the whole list in one call. That interface is the host's own and
  takes the whole list, which a rewriter that moves an operation needs (SQLite's, still owed).
  A package's rewriter is a `LegacyOperationRewriter`, below, and reaches the host through
  `LegacyOperationRewriters`, which is one such rewriter.
- **After the rewriters, an operation that still carries an annotation only a 5.x generator
  rendered is refused** (`LegacyFormats.RequireRewritten`, #110). The provider's generator writes
  such an operation as if the annotation were not there: an `AddUniqueConstraint` stamped
  `CustomTemporal:WithoutOverlaps` becomes a plain unique constraint under the temporal one's name.
  The list is 5.x's and is finished, taken from its two generators over every published version:
  that annotation and the temporal foreign key's two (to 5.0.1), `CustomIndex:IndexParts` and
  `CustomIndex:AfterTableRebuild` on a `CreateIndex`. It is the generator's side of the snapshot's
  guard (#93) and the same on every provider. A rewriter leaves it by taking the annotation off the
  operation or replacing the operation (#28); the check reads the operations alone, never the model.
  The PostgreSQL package replays `CustomIndex:IndexParts`. The other four are still refused
  everywhere, and that one on a context without the package.

- **A statement of SchemaKit's that runs outside a transaction is refused where the way the
  migration is applied does not carry one** (`StatementStamp.RequireCarried`, #155, #206). The
  generator is the one place that is told the way (`MigrationsSqlGenerationOptions`): the same
  migration is applied by a bundle and written into a script, so nothing can be refused at
  scaffold. `SchemaSql` puts the annotation `CodoMetis:SchemaKit:Object` on the operation of a
  statement written with `suppressTransaction: true`, with what the planner says the statement
  is for; `migrations add` writes it into the migration, so its name is permanent from 11.0.0.
  Its value is text for a person, and nothing parses it. The generator asks
  `ISchemaDialect.WhyNotOutsideTransaction` for such an operation alone. PostgreSQL's dialect
  says an idempotent script does not carry it. The kernel's own dialect says the same of a
  provider without a package (#214): what nobody measured is not written, as an operation
  nobody describes is refused. SQL Server carries the statement (spike D), and its dialect
  will say so (#29). A consumer's own `migrationBuilder.Sql(…)` carries no annotation and is not judged.
  A statement in a transaction carries none either: what every statement says of itself is
  #115's to decide. `OutsideTransactionTests` holds it on the three providers.

A test suite that varies the wiring creates many internal service providers; EF Core raises the
twenty-first as an error, which `Harness/Setup.cs` switches off.

**Nothing the differ or the generator is built from may need the model.** The ownership check
resolves both while a model is being built. That is why the planner and the rewriters are resolved
on first use and not injected, and why `ISchemaDialect`, which reads the design-time model's
identifier limit, must never be a constructor parameter of either decorator.

## The SQL vocabulary

`src/CodoMetis.EFCore.SchemaKit/Sql`. A statement is `sql.Statement($"…")`: the literal parts are the
contributor's code, and a hole takes only `Identifier`, `TableRef`, `QualifiedName`, `SqlName`,
`SqlOperator`, `SqlFragment`, `int` or `long`.

- **There is no hole for a string, an object or a format, and none may be added.** That absence is
  what makes "nothing reaches the DDL unquoted" a property of the types. `KernelProbeTests` compiles
  each forbidden form against the shipping assembly; `PublicSurfaceTests` holds the handler's
  overloads to the list.
- **The guarantee is the holes', not the handler's** (#109). `SqlHandler` is public because the
  compiler builds it in the contributor's assembly, so `AppendLiteral` can be called by hand with
  any string, and nothing can stop that call. Its parameter is `[ConstantExpected]`: a value that
  is not a constant is CA1857, a warning in a contributor's build and an error here. The kernel
  probes run the SDK's .NET analyzers for it (`E11`).
- **Values come from the dialect:** `ISchemaDialect.Identifier` checks the length, `Operator` the
  operator grammar. PostgreSQL's dialect holds `Name` to the server's 63 bytes as well (#210):
  the server shortens a collation of 64 bytes without an error and takes the collation of the
  shortened name where there is one
  (`A_collation_longer_than_PostgreSQL_keeps_would_be_another_one`). The limit is the
  server's and not the model's, which may state a shorter one for its own names. None of the value types has a public constructor taking text.
- **A store type becomes SQL through `ISchemaDialect.TypeName`, which checks its form** (#208):
  words, one list of numbers or `max` in parentheses, brackets for an array. It is how a cast
  to a member's type is written, and it is no hole for a string: a type that needs delimiters
  or a schema is refused, with the expression written by hand as the way out.
- **`SqlFragment.IsColumn` says whether a fragment is one column by itself.** The resolver sets
  it, `As` keeps it, and anything composed is not one. A contributor asks it, and never what
  the declaration held, where the database tells a column from an expression: a key in
  parentheses of its own, a column and nothing else in `INCLUDE`.
- **`SchemaSql` appends the provider's statement terminator** and refuses a statement that brings
  one, an empty one, and a `DROP … IF EXISTS`. The drop is looked for in the statement without
  its string literals and delimited names (#211): a value that holds the words is no drop.
- **A schema-scoped object is dropped by `table.Qualify(name)`.** Whether a kind takes a qualified
  name is the database's rule (a PostgreSQL index does; a constraint and a trigger are named within
  their table), so the contributor calls it. `PostgresStatementTests` holds the reason as a fact
  about the server: the unqualified form removes another schema's index.
- **`SchemaDialect`** is the dialect of a provider without a SchemaKit package and the base of the
  others. It delimits and terminates through the provider's `ISqlGenerationHelper` and writes
  literals through its type mapping. It counts characters; PostgreSQL counts bytes, so its package
  overrides `Measure`.

## Declarations and the contributor contract

`src/CodoMetis.EFCore.SchemaKit/Declarations` and `Contributors`
(`design/schemakit-core-plan.md`, sections 3.4, 3.6, 3.7 and 3.10).

- **A declaration is one annotation, `CodoMetis:{Owner}:{hash}`, and its key and value are a file
  format.** They sit in consumers' snapshots. `DeclarationStoreTests` holds one key and one value
  verbatim; a change that moves either is a format change, not a refactoring.
- **`Declare` never replaces.** A key that is taken, on the entity type or earlier in the same call,
  throws before anything is stored. `Redeclare` is the only replacing path. Do not add an
  "or update" overload: the draft's identity lost a unique index that way without an error.
- **The kernel computes the key.** A declaration writes its properties through `SchemaJsonWriter`
  and marks what does not change the object with `writer.Operational`; the identity is the same
  call without that part. A named declaration's key is over its kind and its name.
- **A string is written by `CanonicalJson`, never by a runtime encoder.** The encoders of
  `System.Text.Encodings.Web` escape what their runtime's Unicode tables do not know, so their text,
  and a key hashed over it, moves with the runtime (22 characters between .NET 9 and 10). The rule
  escapes `"`, `\`, everything below U+0020, and U+0085, U+2028 and U+2029, which EF Core writes
  unescaped into the snapshot's string literal, where they end the line. Property names are ASCII
  letters, digits and `_`, since a name cannot be written raw.
- **Nothing is omitted but a null, and the reader has no default.** There is no writer method that
  drops a `false` or a zero and none may be added; a reader method either requires its property or
  returns null. `DeclarationCodecTests` holds it for every writer method by reflection, so it stays
  true of one added later.
- **A property nothing read, a property stored twice, an unknown `v`, a reader that loses the name
  or the epoch: each is refused.** They are all the same failure, a declaration that comes out of a snapshot as another one.
- **Features ship without the experimental mark** (decided 2026-11-12). What a consumer calls to
  declare, read and amend carries no `SK9001`: a provider's entry points, `GetDeclarations`,
  `FindDeclaration`, `Redeclare`. The contract other packages implement keeps it until `rc.1`,
  and so does `Declare`, which only a contributor's package calls. `ConsumerCompileTests` compiles
  a consumer's code against the shipped assemblies with nothing suppressed; every other test
  project opts in and would not notice. **A mark on a static class does not reach its extension
  methods called as extensions:** the compiler reports a marked type where it is written, and
  `entity.Declare(…)` does not write the class. Mark the method.
- **`SchemaDiagnostic` and `SchemaValidationException` carry no mark either:** a consumer catches
  and reads them. A diagnostic's id is permanent once it ships; `SK1001` is a name collision. The
  union holds text only, so it exposes nothing of the marked contract.
- **`KeyTarget`, `Predicate` and `ValueList<T>` are what a declaration is made of, and carry no
  experimental mark:** a consumer writes them and reads them back. `KeyTarget.Of` and
  `Predicate.Of` are the translator's only public entry points, **and they are the contract's,
  marked on the method** (#132): a lambda becomes one provider's SQL, so they take that provider's
  `SqlSpelling`, which its package passes. A consumer writes a lambda into the provider's factory
  (`pg.Equal(…)`, `where:`, `PgSchema.Where<T>(…)` for a condition several types share) and never
  names a spelling. The kernel holds no spelling (#184): `SqlSpelling` is abstract, and a
  package derives its own. `PublicSurfaceTests` holds that nothing public in the kernel takes a
  lambda without a spelling, that the kernel has no spelling of its own, and that no public
  name of it names a provider. A lambda that is one property is a
  `Column`, anything else a `Template`; the two are rendered and named differently.
  `Predicate.And` takes for a conjunct only what it wrote itself, `(…) AND (other)` with the
  first parenthesis closing right before the `AND`: `{A} OR {B} AND ({C})` ends the same way and
  is no conjunction (#83). SQL written by hand is never taken for it, being another kind of segment.
- **SQL in a declaration is a `SqlTemplate`: segments, stored as they are** (#178, the first slice
  of #114). A string in the stored list is text a package wrote, which names no column;
  `{"col":…}` is a column by its property path; `{"raw":…}` is SQL written by hand, which makes
  the object read the whole table, also beside a column. A literal is package text.
  `DeclarationCodecTests` holds the form verbatim, and every other shape is refused. The template
  is unmarked and shows no SQL string; what makes one (`SqlTemplate.FromPlaceholders`, `Raw`, `Of`)
  and its `Segments` are the contract's, marked on the member. `{Path}` is looked for once, where
  a package turns its own text or a 5.x filter into segments, never in what is stored.
- **`v` is 3.** A `v` of 2 is `11.0.0-preview.1`'s, SQL as one string, and is refused with a
  message that says to scaffold again: what a string leaves out cannot be read back (#9).
- **A declaration with a lambda is held as it was written until its model is finalized** (#179,
  the second slice of #114; measured first in spike F). `SqlTemplate.IsBound` says which state a
  template is in; unbound it compares by its lambda's tree, is added to with `And`, shows the
  lambda in `ToString()` and refuses `Segments`. `UnboundLambda.Of` makes every value the lambda
  reads from outside a constant, at the call: left in the tree, a local would be read when the
  model is finalized. The entity type holds the declaration itself as the annotation's value:
  under its final key when it has a name, and otherwise under `CodoMetis:{Owner}:~{number}`,
  numbered in the order written, which no stored key can be.
- **`SchemaKitBindingConvention` binds, last among the finalizing conventions.** It writes each
  held declaration with a writer that makes segments of its lambdas, and stores key and value as
  for any other. The contributor's `Write` is all it needs, so the contract has no member for
  binding. A finalized model, its runtime model, a snapshot and a compiled model hold text
  alone, and a snapshot's model is finalized without the convention.
- **The refusal of a repeated declaration has two moments.** At the call: a name that is taken,
  bound or not, and a declaration without a name that equals one already held, lambda trees
  included. When the model is finalized: two that were written differently and are one key once
  they are SQL, named in the order written, against each other or against one stored already.
- **`GetDeclarations`, `FindDeclaration` and `Redeclare` work in both states.** Before the model
  is finalized they hand out the declarations as written; `Redeclare` keeps a held declaration's
  place. `DeclarationCodec.Key` and `Value` take what binds a lambda, and without it refuse a
  declaration that is not bound.
- **Nothing in the contract binds a lambda without a model.** A package's test of what a lambda
  is spelled as declares it on a model and reads it from the finalized one, as
  `Harness/Bound.cs` of the second provider's tests does.
- **`Plan` and `ImpactOf` are abstract on the base class.** No optional hook, no default answer.
  The base class refuses an `InPlace` with no statement and statements beside another answer.
  A table of measured answers is a provider package's own, read by its contributors' `ImpactOf`
  (decided 2026-11-25 with #117); the kernel has none and no default for a cell.
- **A column dependency says how the column is read, and an altered column what was altered**
  (#117). `Dependency.Column` has a `ColumnRole`: the resolver says `Key` for a column by itself
  and `Expression` for one in a template, and `SqlFragment.As` says `Condition` and `Included`
  where a contributor puts a fragment. Read in two ways, a column is two dependencies.
  `BaseChange.ColumnAltered` has a `ColumnAlteration`, read from the operation's two sides: the
  type (a length is part of it), the nullability, the default, the collation, or `Other`, which
  is never nothing. A fragment still compares by its text alone.
- **A contributor derives from `SchemaContributor<,>`.** The interface is what the kernel calls, not
  what a package implements: `AddSchemaContributor` and the registry both refuse a class that
  implements it directly, since the base class is where an answer is checked.
- **The unions are closed for the major.** `ContractValueTests` lists every case; adding one is a
  decision to take with the maintainer, since it breaks every exhaustive switch of a contributor.
- **A `LegacyDeclarationReader<TDeclaration>` reads an earlier package's annotation into
  declarations** (`AddLegacyDeclarationReader`, marked like the rest of the contract). It
  translates and decides nothing: what the old format could say and the declaration cannot is
  thrown, never left out. It takes no dialect, since it runs before anything is resolved.
- **A `LegacyPropertyDeclarationReader<TDeclaration>` reads an annotation that sits on a
  property** (#218), as 5.x's marker of an index on a property does. It is asked for every
  property that is declared on the entity type and carries the annotation, the properties of
  its complex types included, and is handed the property's path. Declared on it: a property a
  derived type inherits is read at its base type, or one object would be two
  (`A_property_is_read_where_it_is_declared`). An annotation of a property and one of an
  entity type are two annotations, whatever their names.
- **A `LegacyOperationRewriter` replays an operation of a migration an earlier package
  scaffolded** (`AddLegacyOperationRewriter`, marked like the rest of the contract; #28). It is
  asked for an operation that carries its `Annotation`, and what it returns is generated in
  that operation's place. The operation itself is not changed: a migration's operations are
  compiled code, generated from again each time. The base class refuses an answer that is
  empty or still carries the annotation, since either is an object the database does not get,
  and a rewriter that took the annotation off the operation it was asked for: generated a
  second time, that operation went to the provider alone.
  Two rewriter classes for one annotation are refused when the first migration is generated,
  and of two annotations on one operation the first the operation holds decides, not the
  order of two registrations. A statement it writes with the transaction suppressed is
  stamped as SchemaKit's own are, so `StatementStamp.RequireCarried` asks the dialect about
  it. It takes no model and no dialect. `LegacyOperationRewriterTests` holds each rule on the
  three providers.
- **One annotation has one reader, as one owner has one contributor** (#108). Two reader classes
  for one annotation each read the same objects out of a snapshot: one copy pairs with the model's
  declaration and the other is dropped by the migration, while the model still declares it. The
  planner refuses the pair at the first diff, naming both. Not the registration: a reader says
  its annotation only once it is made.
- **`SchemaContributorRegistry` is a diff-time service.** A contributor may take the dialect, which
  reads the design-time model, so the registry must never be a constructor parameter of the differ
  or the generator.

## The planner

`src/CodoMetis.EFCore.SchemaKit/Migrations/Internal/SchemaOperationPlanner.cs`
(`design/schemakit-core-plan.md`, sections 3.6 and 3.7; #24 lists what is still missing).

- **Both models are read the same way,** each declaration through its contributor and the
  kernel's resolver. Equal descriptors of one contributor are one object and produce nothing; that
  is what makes a model against its own snapshot an empty diff. Nothing compares SQL text.
- **Matching is exact, then by kind, scope and name, then by equality but for the name.** A pair
  is the contributor's to plan; what is left is drops and creates.
- **Order:** drops, EF Core's operations untouched, in-place statements, creates; contributors by
  placement, and a `BeforeTables` contributor the other way round (created before, dropped after,
  never rebuilt).
- **Within a phase, what an object needs decides** (#195, slice B of #115): a create comes after
  the creates of what its `Dependency.SchemaObject`s name, and a drop before their drops, whatever
  the placement and the model's order say. The model's order is the order of the stored keys, not
  the one written. A circle is refused with its members named, and so is an object created
  before the tables that needs one created after them. Which object is needed is its owner, its
  scope and its name together, as its descriptor says them.
- **A dependency on an object the model does not declare is refused, in the context's own model
  only.** A snapshot that holds one is read: it describes a database that exists.
- **What this does not do:** it orders SchemaKit's own statements among themselves. A place
  among EF Core's operations, such as a create behind the `EnsureSchema` of its schema, is the
  one ordered list of #115's slice C, at the gate of `rc.1`. An object that stays while what it
  needs is rebuilt is not looked at either.
- **A rename in place whose name is held when it runs is a rebuild** (`KeepInPlace`, #99). A
  change in place runs after EF Core's operations: an index EF Core creates under the name a
  constraint gives up fails with 42P07. Both names are compared, through `NameClaims`, with what
  holds a name at that moment: EF Core's objects as the target has them, the declared objects as
  EF Core's operations leave them but for those dropped before or lost, and the earlier changes in
  place under their new names. A name EF Core gives up is free by then, and the server agrees. The
  contributor's statements go into a builder of their own and reach the migration only if the
  change stays in place. It is a point fix; the ledger over ordered actions is #115.
- **A declaration whose owner has no contributor is refused,** on either side. #22 adds the
  explicit way out.
- **Which model is the consumer's is told by reference, never by where it stands in a diff**
  (#98). It is the context's own design-time model (`IsOwn`). EF Core's scaffolder asks for Down
  as `GetDifferences(model, snapshot)` whenever Up is not empty, and `migrations remove` asks
  `HasDifferences` of two snapshots, so "the second argument" is a snapshot as often as not. A
  rule for the consumer's model is asked of that one model on either side; every other model, a
  snapshot or a migration's target model, is only read. A test of such a rule diffs both ways: a
  handover that diffs to nothing never asks for Down, which is how this was missed.
- **An earlier package's annotation is read in every model but the context's own, and refused in
  that one.** Every legacy reader is asked for the entity type's annotation, and what it returns
  is resolved like any declaration; that is how a 5.x snapshot and the model that declares the
  same objects differ in nothing, whichever of the two the diff starts from. In the context's
  own model the annotation is an error, with a reader for it or without one: nothing renders it
  from a model, and beside a declaration of the same object it would be a second one. In a
  snapshot, an annotation of 5.x that declares an object and has no reader (its temporal
  constraints, and its indexes and exclusion constraints on a context without the PostgreSQL
  package) is refused (`LegacyFormats`, #93): passed over, its objects would be created a second
  time or never dropped. This applies to a context that declares nothing as well, since the
  snapshot alone says the objects exist. A reader leaves the guard by being registered.
- **An extension a created object depends on is ensured once and first.** The planner collects
  `Dependency.Extension` over what it creates and asks `ISchemaDialect.EnsureExtension` for each,
  with the target model, ahead of every other statement. The kernel's own dialect refuses: an
  object must not be created as if a dependency nothing can satisfy were met. An object that
  stays counts too, for what its target descriptor depends on and its source did not: a column's
  type change makes the database rebuild an index under the new type (#81).
- **A declaration on a derived type that shares its table with a base type is refused, in the
  context's own model only** (#82). An object is the table's: declared on a derived type it would
  hold among the sibling types' rows too. A snapshot is read as it is, since one that holds such a
  declaration describes an object that exists; moved to the base type it is the same object and
  the migration is empty, Up and Down. The cost for now: a column only the derived type has cannot be
  named from the base type, so nothing can be declared on it while the two share a table.
- **A declaration lands on every table `Landings.Of` names** (#118, #187; it replaced the refusal
  of #101). A type has one table, its own, unless it is mapped with TPC: then the declaration is
  one object in its own table and in the table of every type derived from it that has one, as
  an index of EF Core's declared there is. A root without a table has the derived types' alone.
  The function is asked for both sides of a diff and nothing of the copies is stored, so a
  derived type that gets a table gets its copy by the next migration.
- **A copy's name is made by the resolver's `Name`.** A default name has its table in it. A
  stated name stays as it is in the declaring type's own table; in a derived type's table it
  gets `_{table}` behind it when the object's claims hold a `NameClaim.Relation`, a name taken
  in the whole schema, and stays as it is otherwise. That is 5.x's rule for a copy. The planner
  resolves a copy once to read its claims and a second time for the name. A contributor that
  names an object without `Name` gives every copy one name, which is refused as `SK1001`. A
  name that is too long is refused by the dialect; 5.x's shortening is not ported.
- **A declaration on a split entity lands on the one table that has its columns** (#191). The
  planner resolves it in each of `Landings.Fragments` and takes the table where no path lacks a
  column. The resolver's refusal of such a path carries the path (`MissingColumn`), and only
  that refusal is caught, so a path that names no property still fails as itself. No table: refused, the columns
  are in more than one. More than one table: the declaration reads the key's columns alone, and
  is in the first; with SQL written by hand it is refused, since nothing says which table the
  SQL means.
- **A member of a JSON document is in the tables its document is mapped to** (#243,
  `GetMappedStoreObjects`): asked of the container column alone, a member had a column in
  every table of a split entity, and an index that also read the second table landed there
  (`A_member_on_a_split_entity_is_in_the_table_of_its_document`).
- **A derived type with a table of its own that reads an inherited column is refused with the
  base type named** (#191). The column is in the base type's table; the refusal was there
  before, as a path without a column.
- **A copy in a derived type's table is that table's alone:** a row of one table and a row of
  another are not compared, on any database. `PgExclusionLiveTests` holds each table's refusal.
- **On EF Core 11 a complex property's column is in a TPC derived table too** (measured;
  5.x found the opposite on EF Core 10). A path with no column in a landing's table is still
  refused, by the resolver.
- **A name the model takes twice is refused, every collision at once** (`NameClaims`, #96). Each
  contributor says which namespaces its object's name is in (`Claims`), the dialect says it for
  EF Core's own tables, indexes, keys, foreign keys, check constraints and sequences
  (`ISchemaDialect.Claims`), and two equal claims are a `SchemaDiagnostic.NameCollision`
  (`SK1001`) when at least one party is SchemaKit's. All of them arrive in one
  `SchemaValidationException`. Only the context's own model is held to it, wherever it stands:
  a snapshot that holds a collision stays diffable, both ways. Names compare ordinally unless either declaration's `NameComparison` says
  otherwise. A table without a schema of its own carries the model's default schema in the
  relational model already; where the model sets none, the claims are compared under the
  dialect's `DefaultSchema`, the schema the database puts it in (#100). The kernel's dialect says
  null and does not guess: a table without a schema then shares a namespace only with another. The kernel's default
  scopes (an index within its table, a constraint within its table) are a guess for a provider
  without a package; SQL Server's, where a constraint is an object of the schema, come with #29.
- **Nothing is resolved until a declaration is met.** A context that declares nothing never builds
  the dialect.
- **EF Core's operations are read, never changed, and what each does is the dialect's to say**
  (#116, #198). `ISchemaDialect.EffectOf` answers an operation with an `OperationEffect`: the
  `BaseChange`s it stands for (none for one that touches nothing an object depends on), that the
  provider writes nothing for it, or that nobody knows it. The kernel's `SchemaDialect` answers
  EF Core Relational's operations, and for now SQL Server's and SQLite's, which have no dialect
  to move to (#29); `SchemaImpactTests` fails when one of those assemblies has a type the table
  lacks. A provider's dialect overrides it for what its generator does otherwise, and holds each
  such rule against the generator in a test. An operation nobody describes is refused when a
  model declares an object.
- **An operation the provider writes nothing for is refused under an object of its table**
  (`OperationEffect.NotWritten`, #105). The database would stay as it was while the model and
  the migration say otherwise. The effect carries the way out as a sentence of the provider's
  package, since only that package knows it. A context that declares nothing on the table is
  not touched.
- **`ImpactOf` is asked for each change that touches what an object depends on,** tables and
  columns carried forward through EF Core's renames, and the answer that asks most decides:
  `RebuildAround` drops before and creates after even when both sides are equal, `Lost` creates
  again without a drop. Either decides before `Plan` is asked.
- **An object that follows EF Core's renames is compared under the new names.** `ImpactOf` is
  asked with the object as it is. When every rename that touches it is answered `Follows`, its
  declaration is resolved again with the table and the columns as the target names them, and that
  is what is matched. So a renamed table or column leaves what reads it alone. A default name is
  still built from the source's own table and columns, which makes it a name-only change: one
  `InPlace` rename. A drop names the object as it is before EF Core's operations (`Before`).
- **An `AlterColumnOperation` the provider writes as drop and add is a `ColumnDropped`** (#80).
  Npgsql and SQL Server both do that when a column's computation changes. PostgreSQL drops an
  index and an exclusion constraint with the column in silence, so as an alteration the
  migration applied and the object was gone for good. `BaseChanges.DroppedAndAdded` is a copy of
  the two providers' rule, held against both generators by a test. What only Npgsql does, for
  a generated `tsvector`, is `NpgsqlSchemaDialect`'s to say. What the provider writes decides,
  not what the operation is called.
- **A `TableRebuilt` names its table as the target has it,** like every other change (#193). Its
  operation may stand before the table's rename and name it as the source has it: EF Core drops
  keys and constraints first. Taken as written, the objects on a table renamed and rebuilt in
  one migration did not hear of the rebuild, and were lost on SQLite without a word.
- **Which operations SQLite rebuilds a table for is the provider's rule,** and
  `BaseChanges.RebuiltOnSqlite` is a copy of it, held against the provider's generator by a test.
- **On SQLite a create on a table the same migration rebuilds is refused at the diff** (#145,
  decided with #115 on 2026-11-25). SQLite's generator moves the rebuild to the end of the
  migration, so the planner's create would run first: on the object that is still there, which
  fails at apply, or, under a name that is free then (an object declared in the same migration,
  a renamed one, one answered `RebuildAround`), on the old table, which the rebuild drops with
  the object, in silence. The refusal names the declaration and the table, and the way round it:
  take the declaration out, change the table, declare it again, a migration each
  (`In_three_migrations_the_table_is_changed_and_the_index_is_there`). Only creates are
  refused: an object whose contributor answers that the rebuild leaves it alone is the
  contributor's word. The refusal goes when the generator decorator places the create behind
  the rebuild (#115, slice D), and that comes with the first object SchemaKit creates on SQLite.

## The lambda translator

`src/CodoMetis.EFCore.SchemaKit/Translation` (`design/schemakit-core-plan.md`, section 3.9; #26 lists
what is still missing: `[SchemaFunction]`).

- **It is 5.x's translator, ported with its rules unchanged.** Each comment in it that names a
  failure records one that happened. Do not simplify a rule because it looks redundant: a stored
  template must come out the same, and `LambdaTranslatorTests` holds each one.
- **A lambda is checked where it is written and becomes SQL when its model is finalized** (#179).
  The factory translates it once and throws the result away, so a refusal is still on the
  consumer's line. No service is needed at either moment, which is what lets a declaration be a
  value in `OnModelCreating`.
- **The two translations differ in their facts** (`TranslationFacts`, #180). At the call they are
  the C# types': nullability from the annotations, and a value only the model can write (an enum,
  a `DateTime`, a `Guid`, a value object) as the mark `LambdaTranslator.BoundLater`, which is
  never stored. At finalization they are `ModelFacts`: a column can be null when the property
  or a complex property on its path is nullable in the model, and a value compared with a
  property is written by that property's mapping, through its converter.
- **No property holds its mapping while a model is finalized** (spike F), so the binding asks
  `IRelationalTypeMappingSource`, a singleton that needs no model. `HasConversion<string>()`
  leaves no converter on the property either: ask the mapping, never the property's converter
  alone. `PropertyPaths.Find` is the one walk from a path to a property, for the resolver and
  for the facts.
- **`ModelFactsBindingTests` is the oracle for what the model's facts write:** each condition is
  compared with EF Core's `ToQueryString()` for the same lambda on the same model. A new kind of
  value gets its row there. The sweep translates with the model's facts as well.
- **An order comparison on what a converter stores as text is refused at finalization,** and so
  is a value the mapping refuses, with the property named. A converter between numbers, or from
  an enum to its number, keeps the order (Npgsql stores an `sbyte` that way).
- **Two properties ordered by each other are asked the same,** each one that is a property
  path (#242): `x.A > x.B` over two ints stored as text compared `'10'` with `'2'`. An operand
  that is an expression over such a property is not asked.
- **A boolean stored as something else is compared with what true is stored as,** and a negation
  with what false is stored as, not wrapped in `NOT`: that is how EF Core writes both.
- **`SchemaKitBindingCheckConvention` binds a second time, first on the finalized model,** and
  refuses a different result: a convention of another package that runs after the binding can
  change a column's nullability or a property's conversion, and the stored condition would say
  what the lambda does not.
- **Spelled as the provider's queries spell it,** character for character: an index serves only a
  query whose expression matches it. The kernel walks the lambda and keeps what is C#'s and what
  is the same SQL everywhere: the conversions that keep a value, null compensation, the
  comparison and boolean operators, `NULL`. Everything else it asks of the provider's
  `SqlSpelling` (#184): the methods and members it accepts and what each is written as, the
  literals it writes without a column, concatenation and its operands, `??`, a boolean where a
  condition stands, and the negation of a comparison that can be null. PostgreSQL's is
  `NpgsqlSpelling` in its package, handed out as `PgSchema.Spelling` for a package that adds
  PostgreSQL declarations of its own; SQL Server's comes with #29.
- **A spelling is one object,** and what its methods and members write is null exactly when an
  input is: the null compensation tests the inputs' columns. Both are said on `SqlSpelling`.
- **The translator's tests and the sweep translate with the PostgreSQL package's spelling,** as
  it ships (`Harness/Shipped.cs` in the kernel's tests, which reference the package for it). A copy
  of the tables in the tests would be what they hold instead.
- **A comparison that can be NULL is written as EF Core writes it.** `x.Tag != "blocked"` keeps the
  rows whose tag is null, as C# does; a negation over such a comparison is pushed down to it.
- **The accepted constructs are data:** the spelling's `Methods`, `Members` and `LiteralTypes`,
  and the kernel's `NullableMembers`, operator sets and `SilentConversions`. Add a construct to
  a table, not only to the dispatch: the sweep and the analyzer read the tables.
- **A conversion is left out of a template only when it keeps every value on every database**
  (#103): to and from `Nullable<T>`, to a type the source already is, and the pairs of
  `SilentConversions`, which are the widenings between integer types and `char` to `int` (C#
  compares chars as ints). Every other conversion of what the lambda reads from the entity is
  refused, the ones the compiler adds unasked included (`x.Count > 2.5` converts an `int` to a
  `double`): `(int)x.Amount == 2` was stored as `{Amount} = 2`. One pass over the whole lambda
  asks, ahead of the dispatch, because the dispatch takes a conversion off in eight places. A
  conversion of a captured value is C#'s to do, and its result is written. Nothing renders a cast
  yet (#112, #114): a row added to the table is a claim about every provider, and a widening EF
  Core writes as a cast is waived in the sweep until then (#125).
- **The parity sweep is what says a template is right** (`test/…/TranslatorParity`, ported from
  5.x). It reads the tables, crosses every construct with every operand shape, compiles the rows
  with Roslyn and compares the translator and the resolver with EF Core's `ToQueryString()`, both
  parsed with PostgreSQL's precedence. A disagreement is fixed or listed in `waivers.txt` with its
  reason, and a waiver nothing needs any more fails. `TranslatorParityLiveTests` runs the same
  rows on PostgreSQL 18. Members of a JSON document are swept since the
  PostgreSQL package's dialect writes them (#208): the sweep's context is wired through that
  package, and its rows over a document fall under the blocks that waive the same row over a
  column.
- **A literal is written as it is.** 5.x doubled a brace inside a literal for a second template
  grammar it had; the resolver has one grammar and takes nothing inside quotes for a placeholder.
- **`ConvertChecked` is `Convert` wherever a conversion is taken off** (#126). A project compiled
  with `CheckForOverflowUnderflow` has every numeric conversion of a tree as that node, the ones
  the compiler adds unasked included, so `x.Small == 5` was refused there. Every place asks one
  helper (`Converted`, `Unconverted`): match a conversion through it, never by its node type.
  The sweep compiles its rows a second time with overflow checks and holds each template, and
  each refusal, to the unchecked one (`A_checked_context_changes_no_row`).
- **`Sql.Raw<T>("…")` in a lambda is SQL written by hand** (#220): a marker in the kernel, unmarked,
  that throws when called. Alone in a lambda it is the SQL as written, which is what a 5.x
  filter is read into, so the two are one object; as a part it stands in parentheses. The
  translator never looks inside: `T` is all that says whether it can be null, a value type not,
  its `Nullable<T>` and a reference type yes. To the translator it reads the entity like a
  parameter (`DependsOnParameter`), so nothing evaluates it as a value, and `UnboundLambda`'s
  visitor of captured values passes over the call and reads its argument. `SqlRawTests` holds
  each rule.
- **Its SQL travels through the translator's one string between two noncharacters** (`RawSql`,
  U+FDD0 and U+FDD1), since the spelling composes text. `RawSql.Template` is the one place that
  makes segments of a translated lambda: what stands between the marks becomes `raw` segments,
  the rest text, a `{Path}` of either a column. A mark is looked for outside single-quoted
  literals only, and SQL that holds one is refused. `KeyTarget.Of` asks the same function, so a
  placeholder alone is the column.
- **SQL with braces and a literal the scan does not read is refused at the call** (#224): a
  backslash before a quote, which in PostgreSQL's escape string does not end the literal, and a
  dollar quote. The kernel's grammar for a placeholder is single quotes, one for every
  provider; it does not learn a provider's literals, it refuses text it would misread. Only
  `Sql.Raw` refuses: a 5.x snapshot's filter is read as 5.x read it.
- **A path in SQL written by hand is asked of the model when it is finalized,** by the
  resolver's own walk (`ModelFacts.Require`): it is the one path of a lambda no compiler saw. So
  the application's own model is refused too, not only `migrations add`.
- **The class is named `Sql`, and a namespace `Sql` nearer to the caller hides it.** The kernel's
  tests have one, which is why `SqlRawTests` has an alias inside its namespace. Decided as
  drafted (plan, Q1); a rename is the maintainer's.
- **Refused where the lambda is written:** a comparison of two enum properties, a `DateTime` or
  `Guid` anywhere but in a comparison with a property, a member of a scalar, a method outside
  the tables.

## The resolver

`src/CodoMetis.EFCore.SchemaKit/Resolution` (`design/schemakit-core-plan.md`, section 3.5; #43 lists
what is still missing: a member of a collection's element, an owned type mapped to JSON).

- **It reads metadata only,** which is what a configured model and a snapshot have in common. Every
  rule is tested on both (`SchemaResolverTests`, through `Harness/Snapshots.cs`): an answer that
  differs between them is a migration nobody asked for, or one that is missing.
- **A snapshot is a property bag without converters.** `Email.Value` resolves to the column on a
  model only when the converter stores exactly that member: its `ConvertToProviderExpression` is
  `e => e.Value`. The member's type alone was the rule, 5.x's, and a second string member of the value object, its domain,
  named the whole column (#222). On a snapshot the member is taken as it is.
  Refusing it there lets the first `migrations add` pass and fails every diff after it.
- **The column is asked of the declared property** (`GetColumnName(storeObject)`), never looked up
  by name in the table.
- **A path into a JSON document is the provider's to write** (#208). `PropertyPaths.Walk` is
  the one walk: a complex property mapped to a JSON column starts a document, and what is
  below it is named as the JSON names it (`HasJsonPropertyName`). A path that ends at the
  document, or at a top-level complex collection, is the column that holds it, a plain column
  on every provider. Anything inside is what `ISchemaDialect.JsonMember` answers for a
  `JsonMember` (the container, the names, a kind, the store type, the mapping), and
  `resolver.Column` hands it on as an expression that reads the container as an `Expression`.
  The kernel's own dialect refuses: a member written otherwise than the provider's queries
  write it is an index no query uses. In a template a member stands in parentheses.
- **A member's type is its mapping's, and a column type stated for it is refused.** A snapshot
  keeps a member's type and its facets (`HasPrecision`, `HasMaxLength`) and not
  `HasColumnType`, so the model and its snapshot would cast to two types in every diff
  (measured, #208). Only a configured model can hold the annotation, so the refusal needs no
  word on whose model it is. A model that was never initialized holds no mapping on a member:
  the resolver then asks the provider's mapping source, as the binder does.
- **A path through a collection is refused,** and a path into an owned type mapped with
  `ToJson()` names no property: that one is a navigation, and stays out until its snapshot's
  paths are measured.
- **A default name takes the container and the JSON names,** `IX_people_prefsRank`, 5.x's rule,
  never the SQL. Under a rename of the container column the SQL takes the new name and the
  name does not: a rename of the index in place
  (`Renaming_the_documents_column`).
- **The resolver scans nothing.** A template arrives as segments: text is written as it is, a
  column segment is resolved by its path.
- **A fragment records what it reads,** and composition keeps it. SQL written by hand depends on
  the whole table, beside the columns it names by a path; a package's own text on nothing.
  `SqlFragment` still compares by its text alone.
- **`StoreType(path)` is the column's type as the model states it,** asked of the declared
  property in the table like its column. A snapshot records it, so both sides answer alike.
- **A default name is built from tokens, not from SQL:** a column's name, a template's letters and
  digits. Rendering a part differently must not rename the object.
- **Under EF Core's renames (`ISchemaRenames`) the SQL takes the new names and a default name does
  not.** Only the planner hands a resolver renames, for the source side of a diff.
- **What a method returns for an input it accepted is epoch 1.** Changing it is a new epoch
  (`ResolutionEpoch`), and a declaration of an epoch the version does not know is refused.

## The PostgreSQL package

`src/CodoMetis.EFCore.SchemaKit.PostgreSQL` (`design/schemakit-api.md`, sections 5 and 14; #25 lists
what is still missing).

- **`o.UseSchemaKit()` inside `UseNpgsql` is the whole registration:** the kernel's, then an options
  extension of its own that puts `NpgsqlSchemaDialect` in place of the kernel's dialect and adds the
  contributor. The dialect is registered with `Replace`, since EF Core does not promise which of
  the two extensions is applied first. `AddEntityFrameworkSchemaKitNpgsql()` is the same for a
  caller-owned provider.
- **A table without a schema is in `public`** (`NpgsqlSchemaDialect.DefaultSchema`, #100), as on a
  stock database: claims are compared under it, and `NameClaimLiveTests` holds it. A `search_path`
  that points elsewhere is a database the model does not describe.
- **The dialect counts an identifier's bytes.** PostgreSQL keeps 63 bytes and cuts the rest off
  without an error. A model may state a shorter limit and not a longer one: one above 63 is
  held to 63 (#223). It still delimits through Npgsql's helper, which writes a lower-case name bare:
  `ALTER TABLE role_grants`, not `"role_grants"`.
- **`PgExclusion`'s stored form is a file format:** owner `Pg.Exclusion`, kinds `gist` and
  `spgist`, an element's `column` or `sql` (exactly one, `sql` as segments) with its `op`,
  `where` as segments, `deferral`.
  `PgExclusionDeclarationTests` holds two keys and two values verbatim.
- **The stage types are the first guard, the `init` accessors the second.** `pg.Equal(…)` is an
  `ExclusionDraft<T>` that no factory takes; `with` and a snapshot go around the factory, so every
  record checks in `init` what its constructor checks.
- **A plain `ADD CONSTRAINT`, a drop without `IF EXISTS`.** A name that is taken fails (42P07, the
  constraint's index) and replaces nothing; 5.x's `DROP CONSTRAINT IF EXISTS` in front is gone.
- **A constraint is named within its table,** so its descriptor's name has no schema and its
  statements qualify the table. It claims the name as a constraint and as a relation.
- **The dialect's table of namespaces is held against the server** (`NameClaimLiveTests`): an
  index is a relation of its schema, a primary key and a unique constraint take a constraint name
  and a relation name, a foreign key and a check constraint a constraint name only. Each row of
  `NameClaimTests` has its row there, refused with 42P07 or 42710 or taken. A change to
  `NpgsqlSchemaDialect.Claims` needs the server's word first.
- **`NpgsqlSchemaDialect.EffectOf` holds Npgsql's own rules** (#198), each against the generator
  in `NpgsqlOperationEffectTests`: a change of a generated `tsvector` is a dropped column, its
  database operations touch nothing, and a table moved back to the default schema is not
  written. Npgsql 11.0.0 writes `SET SCHEMA` only for a schema the operation names, so
  `ToTable("x", "grants")` to `ToTable("x")` leaves the table in `grants` (#105). With an
  object on the table the migration is refused, and the way out is to name the schema,
  `ToTable("x", "public")`, which Npgsql writes
  (`Moving_the_table_back_with_the_default_schema_named`). When the provider writes the move,
  the test of the rule fails and the rule and its refusal go.
- **`Plan` is a rename or a rebuild.** PostgreSQL alters nothing else about an exclusion constraint.
- **`ImpactOf` is the package's one measured table, `PgImpact`** (#117; plan, section 3.7), keyed
  by the object's kind, the role the column is read in and the change. Renames follow, except
  under an object with SQL written by hand, which is rebuilt around a rename of a column, of
  the table or of its schema (#209): PostgreSQL carries the rename into the object and the
  model's SQL goes on saying the old name, so left to follow the migration applied and the model described what the
  database did not hold. Beyond that, an
  altered column leaves the constraint alone, a dropped column that is an element by itself
  takes the constraint with it (`Lost`), whatever else reads it; one that only an element's
  expression or only the condition reads has to have the constraint out of its way
  (`RebuildAround`, #102). The rows of a change are read in order and the first that fits
  answers, since PostgreSQL's answer for a column read in two ways is not the stronger of the
  two. A change no row answers is `RebuildAround`. The server's word for each way of reading a
  column is one test in `PgExclusionLiveTests`: a row is added with its test. The index's
  contributor reads the same table under its own kind, with its own rows and its own tests
  (`PgIndexLiveTests`), and writes no answer of its own.
- **`PgIndex` has a case per access method** (#202 and #204, slices A and B of #27): `BTree`,
  `Hash`, `Gist`, `SpGist`, `Gin`, `Brin`, each with the options PostgreSQL has for it and no
  other, and a factory method each. Its stored form is a file format: owner `Pg.Index`, the
  kind is the method, `keys` or `key` (each a `column` or `sql`, then `collation` and `ops`
  where stated; a B-tree key has `desc` always and `nulls` where it was said), `unique` with
  `nullsDistinct`, `include` always where the method has it, each storage parameter where it
  was given, `where`. `PgIndexDeclarationTests` holds two keys and ten values verbatim.
  `nullsDistinct:` is a parameter of `pg.Unique` alone, and `Uniqueness` holds it, so no index
  that is not unique can say it.
- **A key is typed by its method** (`PgKeys.cs`). `pg.Key` makes a `PgKey<TValue>`, which
  converts to each method's key; `Desc()`, `NullsFirst()` and `NullsLast()` make it a
  `BTreeKey`, and `Ops(…)` the key of the class's method. `Collate` is an extension on
  `PgKey<string>` and `PgKey<char>`. `PgIndexProbeTests` compiles the design's nine P1 probes
  and `P3.collate-on-integer` against the shipped assembly, each beside a twin that compiles,
  and eight more for the options one method has and another lacks.
- **A storage parameter's bounds are the server's,** checked in `init`, and each is held against
  the server at its ends (`A_storage_parameters_bounds_are_the_servers`). B-tree's
  `fillfactor` is 10 to 100 like every other method's: the first cut said 50, from memory, and
  the server took 49. A changed parameter is a rebuild (decided with #27).
- **`PgOperatorClasses` refuses at the diff one of PostgreSQL's own classes on a column whose
  store type does not take it,** and answers only where it knows both: a class an extension
  brings and a type that is not on its list (a domain, an enum) are the server's to judge.
  `The_table_of_operator_classes_agrees_with_the_server` asks the server about every pair of a
  known class and a known type; it found that `bpchar_pattern_ops` takes `text`. A row is
  added with the server's word, never from the documentation.
- **The catalog does not say an operator class that is the default of the key's type,** so a
  key that states one and a key that states none are one index there and two descriptors here:
  the diff between them is a rebuild that was not needed, and no more. Unlike a key's order,
  which class is a type's default is not known where the descriptor is made.
- **A GiST index's key on a scalar column needs `btree_gist`,** by the exclusion constraint's
  rule for a column, unless the key names a class. An expression's type is not known, and an
  index has no operator to judge it by: the model declares the extension for one.
- **An order PostgreSQL has anyway is not in the descriptor.** An ascending key has its nulls
  last and a descending one first, and the catalog says neither: `Desc().NullsFirst()` and
  `Desc()` are one index, and a diff between them is empty
  (`An_order_PostgreSQL_has_anyway_is_the_same_index_in_the_catalog`).
- **An index is a relation of its table's schema.** Its descriptor's name is
  `resolver.Table.Qualify(…)`: the drop and the rename name the schema, the create and the
  rename's new name cannot. It claims a `NameClaim.Relation`, so a native `HasIndex` under
  the same default name, `IX_{table}_{keys}`, is `SK1001`, and a TPC copy of a stated name gets
  its table behind it. Nothing is lowered into EF Core's index metadata.
- **`Plan` for an index is a rename or a rebuild** (decided with #27): `ALTER INDEX … RENAME TO`,
  and a drop and a create for everything else.
- **PostgreSQL drops an index with any column it reads,** a key, a column of an expression, the
  condition's, an included one, and refuses nothing: each is `Lost` in `PgImpact`, where an
  exclusion constraint has `RebuildAround` for two of them
  (`The_server_drops_an_index_with_any_column_it_reads`). A column only SQL written by hand may
  read has no row. Since the four answers are one, nothing yet tells an included column's role
  from a key's in a test: `As(ColumnRole.Included)` and `As(ColumnRole.Condition)` in the
  contributor say what is true and are held by no test until a row differs by them.
- **`NpgsqlSchemaDialect.JsonMember` writes a member as Npgsql's queries write it** (#208), case
  for case and in the order of Npgsql's own generator: `->` for what is JSON itself (a
  collection, a nested document), `->>` for a text, `decode(…, 'base64')` for `bytea`, and a
  `CAST` of the text to the member's store type for everything else; one step is `->>`, more
  are `#>>` with `'{a,b}'`, or `ARRAY[…]::text[]` when a name is not ASCII letters and digits.
  `PgJsonMemberTests` holds each kind against `ToQueryString()` on the model and on its
  snapshot, and `The_planner_takes_a_members_index_for_the_query_EF_Core_writes` asks the
  server. A new kind of member gets its row in both.
- **A name the array literal reads as NULL is refused by the dialect** (#235): `null` in any
  case, or an empty name, in a path of more than one name written `'{a,b}'`. PostgreSQL reads
  it as a NULL element and the member as NULL, so a unique index on it took two equal rows
  (measured on 18). Npgsql's queries write the same text, so another form would serve no
  query; the expression is wrong for every reader, which is why the dialect refuses and not a
  contributor (`A_name_the_path_literal_reads_as_null_is_refused`).
- **A member whose cast is not immutable is refused by the index and by the exclusion
  constraint, not by the dialect** (`PgJsonMembers`, decided with #208). A date, a time, an
  interval or money out of a document is a cast PostgreSQL takes neither as a key nor in a
  condition (42P17), and as text it serves no query EF Core writes. Each contributor checks
  every path it reads, in keys and in conditions, by `IsColumn` and the store type; a column of
  such a type is no member. The list is asked of the server type by type
  (`The_casts_an_index_refuses_are_the_servers`). A later object, a trigger, may read such a
  member, which is why the rule is not the dialect's.
- **A key stands bare when the resolver says it is a column, and in parentheses otherwise,** in
  both contributors. `include:` takes a column: the document's is one, a member is refused.
- **`concurrently:` is how an index is built, not what it is** (#206, slice C of #27). It is
  stored through `writer.Operational` and sits in the descriptor as an `Operational<bool>`, so
  a declaration that differs only in it has the same key and diffs to nothing, both ways. The
  create is `CREATE INDEX CONCURRENTLY` with the transaction suppressed; a drop and a rename
  are written as for any index (decided with #27). `NpgsqlSchemaDialect` says an idempotent
  script does not carry the statement: Npgsql puts each statement of such a script into a `DO`
  block, where PostgreSQL refuses it (25001) and psql goes on to record the migration.
- **A changed index that is built concurrently is built beside the old one** (#213): `Plan`
  answers `InPlace` with three statements, the create under `{name}_ccnew` outside the
  transaction, then the drop of the old index and the rename in one transaction. `_ccnew` is
  the suffix PostgreSQL's own `REINDEX CONCURRENTLY` uses, and a name too long for it is cut by
  bytes at a character. A drop ahead of the build was committed before the build began: no
  index while it ran, and none that was valid when it failed. The target says how the index is
  built, so a change to an index that is no longer concurrent is a rebuild. Not covered, and
  said in the README: an index that changes its name too is no pair to the planner, and a
  `RebuildAround` drops before EF Core's operations; both leave the table without the index
  during the build. `A_concurrent_rebuild_that_fails_leaves_the_index_the_table_had` and
  `A_unique_index_holds_while_its_replacement_is_built` hold it on the server.
- **A concurrent build that fails is PostgreSQL's and EF Core's to leave as they do,** and a
  test holds what that is
  (`A_concurrent_build_that_fails_leaves_an_invalid_index_and_what_stood_before_it`): what
  stood before the statement is committed, the index is there and invalid, and the statement
  then fails on its own name (42P07) until the index is dropped by hand.
- **The planner's word that an index is used is a test** (`PgIndexLiveTests`): `EXPLAIN` of the
  query EF Core writes for the key's lambda names the index, and of another expression does not.
- **`=` on a scalar column needs `btree_gist`, and the migration brings it.** The descriptor lists
  the extension as a dependency by 5.x's rule (a column by its store type, an expression by its
  operator, GiST only), in an `Operational<>` field: it says how the constraint comes to be, not
  what it is, so a column whose type changes does not rebuild the constraint for it. The dialect
  writes `CREATE EXTENSION IF NOT EXISTS btree_gist`, and nothing when the model declares the
  extension with `HasPostgresExtension` or calls `SuppressBtreeGistInjection()`, which sets 5.x's
  annotation `CustomTemporal:SuppressAutoExtension`. The test models declare no extension, so
  every live test that creates a constraint measures it.
- **`Legacy/Internal/ComplexIndexes5xExclusionReader` reads 5.x's `CustomExclusion:Constraints`**
  into `PgExclusion`, which is then resolved by the rules of a declaration, not by 5.x's. The two
  agree on the object (a path's column, a filter's placeholders, `EX_{table}_{columns}`) and
  differ in how a name is written, which the database does not see. That agreement is not
  argued, it is measured: `LegacyHandoverTests` diffs the fixture's snapshot against the same
  model declared here, and `LegacyHandoverLiveTests` applies 5.x's migration as scaffolded and
  compares the catalog with what `EnsureCreated()` builds. Offline alone proves little: both
  sides go through one resolver, so a default name that drifted from 5.x's would still diff to
  nothing. The live test and the drop statements of `NothingDeclared` are what hold the names.
- **A 5.x exclusion constraint that reads a member of a JSON document is read only where 5.x
  and a path here agree** (#225, from 5.6.0's source). 5.x pinned a filter to its first
  rendering, the member's text, unless the current rendering casts the member, which it then
  took: a string and a number are the same on both sides. Binary data (`decode` here) and a
  collection (`->` here) are not, and are refused by name. An element followed the snapshot's
  version: before 5.4.0 a member is refused, as for an index. Both sides of a handover go
  through one resolver, so nothing but the reader can tell. Held against models written in the
  test, not against a snapshot 5.x scaffolded: the fixture has no such constraint.
- **A condition 5.x's `AddComplexIndexFilter` and `AddExclusionConstraintFilter` added is
  `Redeclare` with `AndWhere` here, and the same object** (#249). 5.x stored the convention's
  condition alone, or `(its own) AND (the convention's)`, as one filter text; the reader takes
  that text as SQL written by hand, and `Predicate.And` writes the same text, for a condition
  of the declaration's own that is SQL or a lambda. The convention's condition is
  `PgSchema.Where<object>(x => Sql.Raw<bool>("{DeletedOn} IS NULL"))`: no entity type is known
  where it is written, and the path is resolved on each type it lands on, a shadow property
  included. `LegacyConventionHandoverTests` holds the empty diff both ways against
  `ScaffoldedConvention`, that the loop run twice changes nothing, and, as the control, that
  without the loop the nine objects are rebuilt; `LegacyConventionLiveTests` compares the
  catalog with what 5.6.0's script builds.
- **What `PgExclusion` cannot say is refused by name, with the way off it:** a constraint of
  equality elements only, an access method other than GiST and one-element SP-GiST, a property
  5.x never wrote, and a constraint on a TPC type with derived types in a snapshot from before
  5.5.0 (`CustomIndex:RenderingVersion` under 3), where 5.x had it in that type's table alone.
- **A constraint on a TPC type is read from 5.5.0 on** (#189): 5.x made a copy per table, and a
  declaration lands on the same tables under the same names. The fixture's `ScaffoldedTpc`
  holds a root with a table and an abstract one, with a stated name and a default one; the
  snapshot diffs to nothing against the model declared here, both ways, and 5.x's migration
  leaves the catalog `EnsureCreated()` builds (`A_TPC_hierarchy_built_by_5x_holds_what_the_model_declared_here_creates`).
  The 5.x format is finished, so the reader changes only when a 5.x snapshot is found that it
  reads wrongly. Regenerating the fixture needs the network and the .NET 10 runtime; CI does not
  do it.
- **`Legacy/Internal/ComplexIndexes5xIndexReader` and `ComplexIndexes5xPropertyIndexReader` read
  5.x's indexes into `PgIndex`** (#218, slice E of #27): `CustomIndex:CompositeIndexes` on an
  entity type, and the marker `CustomIndex:IsIndexed` on a property with `IsUnique`, `Filter`,
  `Name` and Npgsql's options beside it. Both build through `ComplexIndexes5xIndexes`. A
  `path` is a column key, an `expr` SQL written by hand, a `tmpl` the package's own text with
  the doubled braces of its literals written once; a `filter` is SQL written by hand, as an
  exclusion constraint's is, since its format does not say whether a lambda wrote it.
- **The oracle for the index readers is 5.x's own SQL.** The fixture's `Initial.sql` is the
  script the published 5.6.0 generated for its 32 indexes. `LegacyIndexHandoverLiveTests`
  applies it and compares the catalog with what `EnsureCreated()` builds for the model
  declared here (`Legacy/LegacyIndexFixture.cs`), and `LegacyIndexHandoverTests` holds the
  empty diff both ways and that every index is found under the name the script created it
  by. The script is the source, not what SchemaKit makes of 5.x's migration: an oracle that
  went through the replay would hold the replay against itself. A storage parameter is compared as a set and
  a flag by what it means: 5.x wrote `deduplicate_items=false` where this package writes
  `off`, and the catalog keeps each as written.
- **`Legacy/Internal/ComplexIndexes5xIndexPartsRewriter` replays the index 5.x scaffolded as a
  `CreateIndex` with `CustomIndex:IndexParts`** (#28): one with an expression or a null order,
  which only 5.x's generator wrote. The operation becomes a `SqlOperation` with the statement
  that generator wrote, built clause for clause as 5.6.0's `NpgsqlComplexIndexSqlGenerator`
  built it. It is 5.x's statement and not `PgIndex`'s: every expression in parentheses of its
  own, names through the provider's helper, `deduplicate_items=false`. 5.x rendered when a
  migration was applied, with the version installed then: one text from 5.1.0 to 5.6.0, and
  before 5.0.0 neither `DESC` nor a null order (read from 5.x's tags), so a database applied
  back then holds such an index without the order, and one built from the migration today has it.
  The operation's columns, with their last one `__requires_UseNpgsqlComplexIndexes__`, are not
  read, and nothing is written for an annotation 5.x's generator passed over.
- **The oracle for the replay is 5.x's own script too.** `LegacyIndexReplayTests` holds the
  index statements of `ScaffoldedReplay`'s two migrations, and of `ScaffoldedIndexes`',
  against the scripts character for character, and the migrator's whole script against
  `Up.sql` and `Down.sql` from the first transaction on, which holds the order and which
  statement runs outside a transaction. `LegacyIndexReplayLiveTests` applies the migrations
  through EF Core's migrator, as an application and a bundle do, and compares the catalog and
  the history with what 5.x's scripts build, up, back to the first migration and to nothing.
  5.x kept one index per list of parts, so the fixture's indexes each have parts of their own:
  a second index over `lower("Code")` replaced the first without a word.
- **Parts in a shape 5.x never wrote are refused by name,** and so is a value beside them in a
  type no migration of 5.x holds: the format is `e` and `v` from 3.0.0, `d` from 3.1.0 and `n`
  as `First` or `Last` from 5.0.0, read from 5.x's tags. Read loosely, the index would be
  built without what was not understood.
- **An index 5.x built with `CONCURRENTLY` is refused in an idempotent script,** where the
  published 5.6.0 wrote the statement into a `DO` block (measured 2026-11-28), in which
  PostgreSQL refuses it (25001): the replayed statement carries the stamp of a statement
  outside a transaction, and the dialect answers for it as for a declared index (#206).
- **What `PgIndex` cannot say of a 5.x index is refused by name:** a date or a time in a JSON
  document, which 5.x indexed as the member's text; `NULLS NOT DISTINCT` on an index that is
  not unique, which PostgreSQL keeps; an order on a key that is no B-tree's; an option or a
  storage parameter the method does not have; an included entry that is neither a property
  nor a column; from a snapshot before 5.4.0, a member of a JSON document and an expression
  without a name (decided with #27); from one before 5.5.0, an index on a TPC type with
  derived types. `ComplexIndexes5xIndexReaderTests` has a row for each.
- **SQL that 5.x took as written is `Sql.Raw<T>` in the model** (#220), and the fixture's model
  is declared that way (`MovedIndexes.Declared`): what the reader makes of 5.x's text with
  `SqlTemplate.Raw` and what the binder makes of the marker are one template. A lambda that
  becomes the same SQL is the same index too, since fragments compare by their text
  (`SQL_of_5x_declared_as_the_lambda_that_becomes_it_is_no_operations`).
- **The live tests take a database each** (`LiveDatabase`) and compare what a migration leaves with
  what `EnsureCreated()` builds for the target model, by the catalog's own text.

Locally the integration tests go inconclusive without `SCHEMAKIT_TEST_PG`. When `CI` is set they
fail instead: an unreachable server must not retire them while the build reports green.

## The second provider

`test/CodoMetis.EFCore.SchemaKit.SecondProvider.Tests` (#119; its epic is #111). Evidence for the
contract's shape, measured before the contract freezes. It is not the beginning of the SQL Server
package (#29): nothing moves from it to `src/` without its `Audit:` comments being read.

- **Nothing under `src/` was changed for it, and nothing may be.** Where the contract cannot say
  what SQL Server or SQLite needs, the contributor goes around it and says so, or a test holds
  the failure. `PublicContractTests` holds that the kernel grants the assembly no internals.
- **Each database has a spelling here** (#184): `ProbeSqlServerSpelling` and
  `ProbeSqliteSpelling`, a handful of constructs each, derived from `SqlSpelling` through the
  public contract. `SpellingParityTests` compares what they write with EF Core's
  `ToQueryString()` on that provider. Two members of the contract have their shape from it: SQL
  Server has no boolean that is a condition, so `Condition` takes the negation too
  (`[Active] = CAST(0 AS bit)`), and its queries do not fold a substring's start, so an argument
  arrives as SQL and as a value.
- **Each database's dialect writes a member of a JSON document** (#208, `JsonMemberParityTests`):
  SQL Server's `JSON_VALUE` with a path and a cast, SQLite's `->>` with a name or a path,
  compared with `ToQueryString()`. One audit point: the path is a literal, and the contract's
  only literal is the provider's string literal, `N'$.Rank'` on SQL Server where the query
  writes `'$.Rank'`.
- **A rule is held when a test fails with the rule changed, and that was measured by hand, once.**
  Each line of the two contributors, dialects and declarations was changed in turn and the
  project run on the server: 102 changes, of which 94 fail a test and 8 are no rules (#119 says
  why for each: the answer to `TableDropped`, an answer to a change the provider never makes,
  which of `Plan`'s descriptors names the table, and on SQLite `WithName` and a `Lost` beside
  the rebuild's). Nothing repeats that run: a rule added later is held once its change is shown
  to fail. `ImpactOverride` and `CreateAsAPlainStatement` change a rule inside the suite:
  `Answered_as_…` and `Written_as_…` apply the migration with one answer changed and assert the
  server's message number. `ClaimedAs` lets the index stand in for an object of the schema,
  which this contributor has none of, so that the dialect's claims for EF Core's tables,
  constraints and sequences are compared with something.
- **SQL Server's impact table is not PostgreSQL's.** A dropped column and a change of a column's
  type are refused under an index (Msg 5074, Msg 4922), so both are `RebuildAround`; `Lost` is
  for a dropped table alone. A change of nullability alone and a default leave the index where
  it is, unless its filter reads the column: the change says what was altered, and the column's
  role says whether the filter reads it (#117).
  The rename of a key or an included column follows, the rename of a column the filter reads is
  refused. The contributor finds a changed column by its name, never as
  `new Dependency.Column(change.Table, change.From)`: the change names the table as the target
  has it and the descriptor as the source has it.
- **A filtered index is created through `EXEC`, always.** EF Core writes a migration into a
  script as one batch, and SQL Server binds a filter's columns when it compiles the batch: after
  a rename of the column in the same migration the plain statement fails with Msg 207, in the
  migrations script and in the idempotent one, while the same migration applies by commands.
- **A rename is `sp_rename`,** with the index's delimited name and the new name as it is, both as
  literals. The only way from composed SQL to a literal is `SqlFragment.ToString()`.
- **The scripts are EF Core's own Migrator's.** `Harness/Scripted.cs` is a context with three
  migrations in the test assembly; each holds what the differ scaffolds for its step and the
  compiled snapshot of its target model. They are applied batch by batch on SqlClient, whose
  session has `QUOTED_IDENTIFIER` on. With it off, which is how `sqlcmd` starts without `-I`,
  SQL Server refuses to create a filtered index, EF Core's own too (Msg 1934): the script tests
  say the batches compile, not that `sqlcmd` applies them.
- **The live tests take a database each** (`SqlServerDatabase`, also with a collation of its own)
  and compare the indexes a migration leaves with what `EnsureCreated()` builds. An index that
  was renamed is told from one that was rebuilt by its storage (`hobt_id`); a change of
  nullability rebuilds the storage by itself, so that oracle is not for an altered column.
- **On SQLite every migration that rebuilds a table under an index is refused at the diff**
  (#145): the four that failed at apply and the three that applied and lost the index. One
  helper holds all seven, and that the database is not touched.
- **`SCHEMAKIT_TEST_MSSQL` is a SqlClient connection string** to a server the tests may create
  databases on. Without it the tests of category `Integration` go inconclusive, and fail when
  `CI` is set. The lane `integration-sqlserver` starts SQL Server 2022 in a step, with a password
  it makes there, since a service's environment would have to be in the file.

## The cross-tests with ValueRanges

`test/CodoMetis.EFCore.SchemaKit.ValueRanges.Tests` (#4): a consumer of SchemaKit and of
CodoMetis.ValueRanges `11.0.0-preview.1`, the same maintainer's range types, mapped to
PostgreSQL's range and multirange columns. The mirror tests on the other side are
CaffeinatedCoder/CodoMetis.ValueRanges#333.

- **Neither library references the other, and that stays so** (decided). The contract between
  them is the column's store type: `daterange`, `datemultirange`, `timerange`. SchemaKit reads
  it for the `btree_gist` rule and reads nothing else of ValueRanges. `IndependenceTests` holds
  both directions, the project files under `src/`, and that the packages grant the assembly no
  internals: what compiles there compiles for a consumer. This project is the only one that may
  reference ValueRanges.
- **A range of ValueRanges is an element through `WithUncheckedOperator(x => x.Period, "&&")`,**
  since the typed `Overlaps` takes Npgsql's `NpgsqlRange<T>` forms alone. The typed form for
  ValueRanges' types comes with its satellite (CaffeinatedCoder/CodoMetis.ValueRanges#540).
  `TypedOverlaps` in the harness is what that package can write today with nothing
  experimental, and `SatelliteSurfaceTests` holds that it makes the same declaration, and that
  nothing public starts the elements with an operator of one's own (#257): a constraint over
  the range alone, or with the range first, is written as records until then.
- **Each model has a twin over Npgsql's own range,** with the same table, columns and names.
  The first migration, the create script and the catalog `EnsureCreated()` leaves are compared
  between the two, so a difference is the property type's. None was found.
- **A snapshot holds a property of ValueRanges as the `NpgsqlRange<T>` its converter writes,**
  never as ValueRanges' type, and the column's type as text. On that side Npgsql's mapping is
  asked, on the model's ValueRanges'. To a snapshot such a column is one of Npgsql's own, so a
  5.x snapshot over one is the fixture's form: `ComingFrom5xTests` diffs one against the model
  declared here, both ways. Its snapshot is written in the test, since no fixture was
  scaffolded by 5.x beside ValueRanges 10.x.
- **The snapshot of a `TimeRange` column cannot be the target of a diff, and that is not
  SchemaKit's.** Nothing maps `NpgsqlRange<TimeOnly>` to `timerange` in a context wired as
  ValueRanges documents, and EF Core's own differ throws a `NullReferenceException`, also with
  no SchemaKit in the context
  (`The_snapshot_of_a_TimeRange_column_fails_as_the_target_of_a_diff_with_or_without_SchemaKit`).
  `MapRange<TimeOnly>("timerange")` beside `UseValueRanges()` is the way round it that was
  measured. The test fails when nothing is thrown any more, and says what to move then.
- **`timerange` is created by the model** (`HasPostgresRange`), as an `AlterDatabaseOperation`
  of EF Core's. SchemaKit's create comes after EF Core's operations, the type's among them
  (`One_migration_creates_the_range_type_the_table_and_the_constraint`). `LiveDatabase` there
  calls `ReloadTypes` after a migration, as Npgsql's migrator does: a data source keeps the
  types it read before the type existed.
- **The README's sample is a model of these tests** (`Bookings.AsInTheReadme`). The sample
  tests compile against the shipped assemblies alone and cannot see a type of ValueRanges, so
  the page's entity has Npgsql's range, and the test here holds the statements the page shows.
- **Dependabot raises ValueRanges here on purpose:** the pull request that brings its next
  preview is where the cross-tests meet it.

## Packaging and the release

`.github/workflows/release.yml`, `nightly.yml`, and the tests under `Conventions/` of the kernel's
test project that hold them, ported from 5.x (#35). The guards of 5.x whose subject does not exist
here yet are listed in #152, each with what has to exist first.

- **The PostgreSQL package depends on the kernel at exactly its own version,** `[11.0.0-preview.3]`.
  Pack writes a project reference as a lower bound; the target `ExactKernelVersion` in the
  package's project file brackets it. It hooks `_GetProjectReferenceVersions`, a name of NuGet's
  that is no contract, so the test reads the packed `.nuspec` and not the project file.
- **The pack checks open the `.nupkg`.** They pack every project under `src/` into a temporary
  folder with its own `bin` and `obj`, under a version no release has (`….packcheck`): the exact
  pin, a README of its own in each package, license, repository and commit, `net11.0` only, a
  `.snupkg` whose PDB has Source Link. With `SCHEMAKIT_PACKAGES` set they read that folder
  instead, which is how the release workflow checks the packages it is about to push.
- **`InternalsVisibleTo` for the test assemblies stays in the Release build.** What CI tests is
  what is packed; nothing internal is a promise.
- **`RepositoryUrl` is stated, `PublishRepositoryUrl` is not set:** it would put the clone's
  `origin` into the nuspec.
- **A tag `v*` publishes; `Directory.Build.props` holds the version** and the tag is checked
  against it. `ci` calls `dotnet.yml`, `verify` packs and runs the pack checks, `publish` is the
  only job with `id-token: write` and waits for the `nuget` environment's approval, `release`
  the only one with `contents: write`. `publish` names the events it runs for; do not turn its
  condition into an exclusion, or an event added to `on:` publishes.
- **The version needs its section in `CHANGELOG.md`,** `## [x.y.z] — yyyy-mm-dd`: `verify` fails
  a tag without one, and the section is the GitHub release's notes. A version with a suffix is a
  prerelease there and never Latest.
- **`— unreleased` stands for the date of the version being prepared,** and of no other section
  (`ChangelogConsistencyTests`). The commit that is tagged puts the date there; `release.yml`
  refuses a tag while it stands. A run from a branch goes on with a warning and notes that say
  it is a dry run, as for a version with no section, so a dry run reaches `publish`'s approval
  from `main` while it is headed `— unreleased` (#151). The heading is read to its line's last character, as the
  step's `grep` reads it, and the step is handed the changelog itself, with the date put in.
- **`verify` writes one CycloneDX SBOM per package** with the tool `.config/dotnet-tools.json`
  pins, before the pack checks, so that the packed folder is uploaded as it was checked, and
  `release` attaches them once the release exists. A reference with `PrivateAssets="all"` is named in the
  step's `--exclude-filter`, and nothing else is; the references are read as MSBuild evaluates
  a project, since `Directory.Build.props` can give every project one.
- **A rule about what a step does is held by running the step.** `WorkflowFile.RunStep` takes a
  step's `run:` block out of the workflow and `Harness/Bash.cs` runs it as a runner does, in a
  folder of the test's: the tag check, the release's notes, the SBOMs, `dotnet.yml`'s gate, the
  nightly report. A rule matched against the text passes with the lines rewritten into something
  that never matches. A step that writes into the checkout takes its folder from a variable.
  The script runs with `pipefail` only where the step, its job or its workflow says
  `shell: bash`; with no shell a runner runs `bash -e`.
- **`WorkflowFile` sees every job or fails.** Each line at two spaces under `jobs:` is a job's
  key, with a comment behind it or in quotes; a job written on its key's line fails the test
  that loads the file. A job the reader passed over would be a lane no rule is asked of.
- **No job says `continue-on-error`,** every action is used at a commit with its version in a
  comment, and no workflow but `release.yml` writes anything but the nightly's issue
  (`WorkflowGateConventionTests`, `CiInputConventionTests`, `ReleaseWiringConventionTests`).
- **Every test project directly under `test/` has a NuGet entry in `dependabot.yml`**
  (`DependabotConventionTests`, #236). What is deeper, the smoke test's consumers and the 5.x
  fixture, pins what it measures and has none.
- **Every test project of the solution is run by the `test` lane, and one with integration
  tests by a lane that has a server** (`TestLaneConventionTests`). `dotnet.yml` names each
  lane's projects, so that a project that ran no test fails its step; one nobody added to the
  list would be run by no lane. The steps are run with a function in `dotnet`'s place that
  records what it was called with.
- **A test that reads the repository reads what git tracks** (`RepositoryLayout.Files`), not
  the folder: a file that was never added and another worktree's copy are on the disk and in no
  commit. Add a new file before a test is asked about it.
- **`nightly.yml` calls `dotnet.yml` every night** and opens an issue, or comments on the open
  one, for every result but success (`CiInputConventionTests`). Nothing may depend on it: GitHub
  turns a schedule off after 60 days without activity.
- **SECURITY.md supports what is released** (`SecurityPolicyConsistencyTests`): no row before a
  version without a suffix has its date in the changelog, then one row for the latest minor. The
  commit that dates `11.0.0` fails until the policy has its table.
- **Never tag or publish:** a release is the maintainer's to trigger.

## The documentation

`README.md`, `docs/moving-from-complexindexes-5x.md` (#73), `CHANGELOG.md` and a README per package
under `src/*/README.md`, which is what nuget.org shows. Held by
`test/…PostgreSQL.Tests/Documentation`:

- **Every ` ```csharp ` block compiles against the shipped assemblies with nothing suppressed.** A
  block with a type declaration is a file of its own; any other is statements, compiled in a method
  that has `modelBuilder`, `options`, `services` and `connectionString`, beside the entity types the
  samples name. Both are in the test, so a sample's surroundings are visible. 5.x code is fenced
  ` ```csharp 5.x ` and is not compiled; any other marker fails.
- **Every link resolves:** a relative one to a file and a heading that exist, an absolute one to
  this repository's `blob/main` or `tree/main` the same way. A package README has absolute links
  only, since nuget.org resolves no relative one.
- **Every API a page names in its prose exists** (`DocumentationApiTests`): a `Name(` or a
  `Type.Member` in inline code is public API of this repository, or on the test's list of EF Core,
  Npgsql and BCL calls. The first column of a table headed `5.x` is 5.x's API and is not read.
  A package's README names no call only another provider's package has.
- **What CLAUDE.md and the skills cite exists** (`ClaudeMdConsistencyTests`): a path, in
  backticks or in a command of a code block; a file name; an annotation key under `CodoMetis:`
  or `Custom…:`; a member written with its type; a test class; what a command's `--filter`
  names. A name written alone, be it a type, a member or a call, has to occur in a C# or
  project file, which is less: nothing says whose member it is, and a name that lingers in a
  comment counts. Write each as it is, a placeholder of a command in angle brackets, and a
  name that is another library's and that the source does not use on the test's short list.
- **Write only what is measured.** A statement about what a migration does names the test that
  shows it, or is left out.
- **The voice of a page a consumer reads is the skill `plain-docs`.** The README and the guide
  were written before it (#153).

## What counts as correct

The failure this package exists to prevent is a migration that scaffolds and applies cleanly while
the database does not enforce what the model declared. Example tests do not catch it. The plan's
section 4 names the oracles that do: catalog equivalence on a live server, behaviour (writes that
must fail do fail), the golden corpus per renderer epoch, translator parity against EF's own query
SQL, and the removal of each registration in turn from a packed consumer.

A guard is not done until it has been shown to fail without what it guards.

## Upstream pins

Npgsql EF 11.0.0 depends on EF Core `[11.0.0, 12.0.0)`, and the two packages reference their
upstream by the same range: the kernel `Microsoft.EntityFrameworkCore.Relational`, the PostgreSQL
package `Npgsql.EntityFrameworkCore.PostgreSQL` (#174). Every test project, the probes and the
spikes reference 11.0.0, the floor: what is tested is the lowest version the packages promise.
Nothing upstream is a prerelease any more, so nothing upstream keeps SchemaKit one.

Npgsql EF 11.0.0 brings the driver Npgsql 11.0.0; rc.3 ran on 10.0.3.

The published `11.0.0-preview.1` depends on rc.3. A consumer that references Npgsql EF 11.0.0
beside it restores 11.0.0 of everything without a warning (measured 2026-11-24, restore and build
only).

The probes drive Roslyn `5.12.0-1.26504.105` from the dotnet-tools feed
(`test/SchemaKit.ApiProbes/nuget.config` says why).

## Tracking

Work is tracked as labelled GitHub issues, not as lists in docs or memory: open the issue before
the work, give the PR the issue's labels and milestone, open it with `Closes #n`. `AGENTS.md` has
the rules in short form, `CONTRIBUTING.md` the legend. The pinned Roadmap issue holds the `11.0.0`
epic; spikes and slices are its sub-issues.

## Coming from the 5.x repository

Its `CLAUDE.md` is the record of what went wrong there and why each guard exists. The convention
tests, the workflows, the translator parity sweep and the skills are ported from it as files,
each adapted to what is true here (#35). The skills are under `.claude/skills`:
`migration-safety-review` for a change that can make a migration answer wrongly,
`verify-the-guard` for showing that a test fails without its subject, `plain-docs` for the pages
a consumer reads.

`MigrationHarness` is not ported. It built a differ subclass from five EF-internal services and
rendered with or without the package's generator; here the differ is a decorator the context
hands out, and no object depends on a generator when a migration is applied. `Contexts` in the
PostgreSQL tests' `Harness/Models.cs` builds a context wired as a consumer wires it and diffs two
models through it, and `LiveDatabase` applies the diff to a server.
