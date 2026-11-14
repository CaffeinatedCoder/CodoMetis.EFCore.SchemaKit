# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is, and where it stands

`CodoMetis.EFCore.SchemaKit` 11.x is the successor of `EFCore.ComplexIndexes` 5.x for EF Core 11:
a new package with a new API and an entirely new core, in its own repository. The EF Core 10 line
stays in CaffeinatedCoder/EFCore.ComplexIndexes and shares no code and no branch with this one.

**The kernel has its host, its SQL vocabulary, the contributor contract, the declaration store, the
resolver, the planner and the lambda translator, and the PostgreSQL package has the first real
contributor: an exclusion constraint declared with `DeclarePostgres` reaches the database through
every path, on PostgreSQL 16 and 18, and a snapshot scaffolded by 5.x with exclusion constraints
moves to the same model declared here with no operations. Nothing is published** (what is left of
the resolver is #43, of the planner #24, of the translator #26, of exclusion constraints #25:
`Sql.Raw<T>` in an element, a factory for SP-GiST, the compile probes). The repository holds:

| Path | What |
|---|---|
| `src/CodoMetis.EFCore.SchemaKit` | The kernel. So far the host (`UseSchemaKit()`, the differ and generator decorators, the wiring guards), the SQL vocabulary statements are built from, the contributor contract and the declaration store. Public are the registration methods and the contract, which is marked experimental (`SK9001`) until it freezes at rc.1; the rest is internal |
| `src/CodoMetis.EFCore.SchemaKit.PostgreSQL` | The PostgreSQL package: `UseSchemaKit()` on Npgsql's builder, the dialect that counts bytes, `PgExclusion` with its factory `PgSchema<T>` and `DeclarePostgres`, and its contributor. Written against the kernel's public contract alone: the kernel grants it no `InternalsVisibleTo` |
| `test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests` | Its tests: the stored form, the wiring, the statements of each diff, and the same diffs applied on a live server (`Integration/PgExclusionLiveTests.cs`) |
| `test/Fixtures/ComplexIndexes5x` | What the **published** 5.6.0 scaffolded on `net10.0`: `Generator/` is the consumer, `generate.sh` builds it outside the repository, `Scaffolded/` is the snapshot and migration of a model with exclusion constraints, `ScaffoldedOtherKinds/` of one with the kinds nothing reads yet (indexes, a temporal constraint). Checked in and never edited: the tests compile those files as they are, and a second run of the script changes nothing |
| `test/CodoMetis.EFCore.SchemaKit.Tests` | The kernel's tests, on Npgsql, SQL Server and SQLite. `Harness/TestKit.cs` is the stand-in planner the host's own tests still use; `Harness/SampleContract.cs` is a contributor written against the public contract alone |
| `spikes/` | The two spikes the host was built on (A: decorators and ownership, B: design time from a packed consumer) and E, the impact table of the plan's section 3.7 measured on live servers. B is the consumer smoke test until the real one replaces it |
| `design/schemakit-api.md` | The public surface and the type model, written against rc.2. Its open questions are answered in the plan |
| `design/schemakit-core-plan.md` | The plan for the core after EF Core 11 GA, with the maintainer's decisions (section 7). **Where the two disagree, the plan holds** |
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

# The probes and the prototype's behaviour tests (outside the solution: they pin their own compiler)
dotnet test test/SchemaKit.ApiProbes/SchemaKit.ApiProbes.csproj

# Spike B, the stand-in for the consumer smoke test; SPIKE_PG turns on the PostgreSQL paths
spikes/B.DesignTime/run.sh

# Spike E, the impact table; SPIKE_E_PG16, SPIKE_E_PG18 and SPIKE_E_MSSQL are connection strings (its README)
dotnet test spikes/E.ImpactTable/E.ImpactTable.csproj
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
7. **Templates at declaration, epochs for resolution.** A lambda becomes provider-specific
   placeholder text where it is written. Changing how a path resolves needs a new epoch, and the
   golden corpus fails the build without one.
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
- **`SchemaKitModelDiffer`** asks an internal `ISchemaOperationPlanner` for operations before and
  after the inner differ's and splices them; `HasDifferences` runs the same plan. The host knows
  nothing of declarations or contributors: they arrive behind that planner.
- **`SchemaKitSqlGenerator`** runs `IMigrationOperationRewriter`s (for operations 5.x scaffolded) and
  hands the inner generator the whole list in one call.

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
- **Values come from the dialect:** `ISchemaDialect.Identifier` checks the length, `Operator` the
  operator grammar. None of the value types has a public constructor taking text.
- **`SchemaSql` appends the provider's statement terminator** and refuses a statement that brings
  one, an empty one, and a `DROP … IF EXISTS`.
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
  `Predicate.Of` are the translator's only public entry points. A lambda that is one property is a
  `Column`, anything else a `Template`; the two are rendered and named differently.
  `Predicate.And` takes for a conjunct only what it wrote itself, `(…) AND (other)` with the
  first parenthesis closing right before the `AND`: a hand-written `{A} OR {B} AND ({C})` ends the
  same way and is no conjunction (#83).
- **`Plan` and `ImpactOf` are abstract on the base class.** No optional hook, no default answer.
  The base class refuses an `InPlace` with no statement and statements beside another answer.
- **A contributor derives from `SchemaContributor<,>`.** The interface is what the kernel calls, not
  what a package implements: `AddSchemaContributor` and the registry both refuse a class that
  implements it directly, since the base class is where an answer is checked.
- **The unions are closed for the major.** `ContractValueTests` lists every case; adding one is a
  decision to take with the maintainer, since it breaks every exhaustive switch of a contributor.
- **A `LegacyDeclarationReader<TDeclaration>` reads an earlier package's annotation into
  declarations** (`AddLegacyDeclarationReader`, marked like the rest of the contract). It
  translates and decides nothing: what the old format could say and the declaration cannot is
  thrown, never left out. It takes no dialect, since it runs before anything is resolved.
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
  snapshot, an annotation of 5.x that declares an object and has no reader (its indexes, its
  temporal constraints, and its exclusion constraints on a context without the PostgreSQL
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
- **A name the model takes twice is refused, every collision at once** (`NameClaims`, #96). Each
  contributor says which namespaces its object's name is in (`Claims`), the dialect says it for
  EF Core's own tables, indexes, keys, foreign keys, check constraints and sequences
  (`ISchemaDialect.Claims`), and two equal claims are a `SchemaDiagnostic.NameCollision`
  (`SK1001`) when at least one party is SchemaKit's. All of them arrive in one
  `SchemaValidationException`. Only the context's own model is held to it, wherever it stands:
  a snapshot that holds a collision stays diffable, both ways. Names compare ordinally unless either declaration's `NameComparison` says
  otherwise. A table without a schema of its own carries the model's default schema in the
  relational model already, so nothing here adds it. The kernel's default
  scopes (an index within its table, a constraint within its table) are a guess for a provider
  without a package; SQL Server's, where a constraint is an object of the schema, come with #29.
- **Nothing is resolved until a declaration is met.** A context that declares nothing never builds
  the dialect.
- **EF Core's operations are read, never changed.** `BaseChanges` maps every `MigrationOperation`
  type of EF Core Relational and the three providers to a `BaseChange` or marks it harmless, and
  `SchemaImpactTests` fails when an assembly has a type the table lacks. An operation of a type
  outside it is refused when a model declares an object.
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
  Npgsql and SQL Server both do that when a column's computation changes (and Npgsql for a
  generated `tsvector`). PostgreSQL drops an index and an exclusion constraint with the column in
  silence, so as an alteration the migration applied and the object was gone for good.
  `BaseChanges.DroppedAndAdded` is a copy of the providers' rule, held against both generators by
  a test. What the provider writes decides, not what the operation is called.
- **Which operations SQLite rebuilds a table for is the provider's rule,** and
  `BaseChanges.RebuiltOnSqlite` is a copy of it, held against the provider's generator by a test.
- **On SQLite a create after a rebuild does not work yet.** SQLite's generator moves the rebuild
  to the end of the migration, so the planner's create runs first and fails on the object that is
  still there: the migration fails to apply, where before it applied and lost the object. The
  generator decorator has to place it (plan, section 3.12). Do not ship a contributor for SQLite
  before that.

## The lambda translator

`src/CodoMetis.EFCore.SchemaKit/Translation` (`design/schemakit-core-plan.md`, section 3.9; #26 lists
what is still missing: `[SchemaFunction]`, and JSON members in the sweep).

- **It is 5.x's translator, ported with its rules unchanged.** Each comment in it that names a
  failure records one that happened. Do not simplify a rule because it looks redundant: a stored
  template must come out the same, and `LambdaTranslatorTests` holds each one.
- **A lambda becomes a template where it is written,** with `{Path}` placeholders the resolver
  fills in. No service is needed, which is what lets a declaration be a value in `OnModelCreating`.
- **Spelled as the provider's queries spell it,** character for character: an index serves only a
  query whose expression matches it. The spellings are PostgreSQL's; SQL Server's come with #29.
- **A comparison that can be NULL is written as EF Core writes it.** `x.Tag != "blocked"` keeps the
  rows whose tag is null, as C# does; a negation over such a comparison is pushed down to it.
- **The accepted constructs are data** (`StringMethods`, `ValueMembers`, the operator sets,
  `LiteralTypes`). Add a construct to a table, not only to the dispatch: the sweep and the analyzer
  read the tables.
- **The parity sweep is what says a template is right** (`test/…/TranslatorParity`, ported from
  5.x). It reads the tables, crosses every construct with every operand shape, compiles the rows
  with Roslyn and compares the translator and the resolver with EF Core's `ToQueryString()`, both
  parsed with PostgreSQL's precedence. A disagreement is fixed or listed in `waivers.txt` with its
  reason, and a waiver nothing needs any more fails. `TranslatorParityLiveTests` runs the same
  rows on PostgreSQL 18. Members of a JSON document are left out behind
  `ParityCatalogue.JsonMembers` until a provider renders them.
- **A literal is written as it is.** 5.x doubled a brace inside a literal for a second template
  grammar it had; the resolver has one grammar and takes nothing inside quotes for a placeholder.
- **Refused where the lambda is written:** an enum comparison, a `DateTime` or `Guid` literal, a
  member of a scalar, a method outside the tables.

## The resolver

`src/CodoMetis.EFCore.SchemaKit/Resolution` (`design/schemakit-core-plan.md`, section 3.5; #43 lists
what is still missing: JSON members, TPC).

- **It reads metadata only,** which is what a configured model and a snapshot have in common. Every
  rule is tested on both (`SchemaResolverTests`, through `Harness/Snapshots.cs`): an answer that
  differs between them is a migration nobody asked for, or one that is missing.
- **A snapshot is a property bag without converters.** `Email.Value` resolves to the column on a
  model only when a converter stores the member's type; on a snapshot the member is taken as it is.
  Refusing it there lets the first `migrations add` pass and fails every diff after it.
- **The column is asked of the declared property** (`GetColumnName(storeObject)`), never looked up
  by name in the table.
- **A placeholder is `{Path}` outside single-quoted literals, and only when the content is a
  property path.** Everything else stays as written, array literals included.
- **A fragment records what it reads,** and composition keeps it. SQL without a placeholder depends
  on the whole table. `SqlFragment` still compares by its text alone.
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
- **The dialect counts an identifier's bytes.** PostgreSQL keeps 63 bytes and cuts the rest off
  without an error. It still delimits through Npgsql's helper, which writes a lower-case name bare:
  `ALTER TABLE role_grants`, not `"role_grants"`.
- **`PgExclusion`'s stored form is a file format:** owner `Pg.Exclusion`, kinds `gist` and
  `spgist`, an element's `column` or `sql` (exactly one) with its `op`, `where`, `deferral`.
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
- **`Plan` is a rename or a rebuild.** PostgreSQL alters nothing else about an exclusion constraint.
- **`ImpactOf` is the measured table** (plan, section 3.7): renames follow, an altered column leaves
  it alone, a dropped column that is an element by itself takes the constraint with it (`Lost`),
  whatever else reads it; one that only an element's expression or only the condition reads has
  to have the constraint out of its way (`RebuildAround`, #102). The descriptor keeps the columns
  that are elements by themselves (`KeyColumns`) for that, outside its comparison. The server's
  word for each way of reading a column is one test in `PgExclusionLiveTests`: a new kind of
  element needs its row there before `GoesWith` answers for it.
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
- **What `PgExclusion` cannot say is refused by name, with the way off it:** a constraint of
  equality elements only, an access method other than GiST and one-element SP-GiST, a constraint
  on the root of a TPC hierarchy (5.x made a copy per table; #43), a property 5.x never wrote.
  The 5.x format is finished, so the reader changes only when a 5.x snapshot is found that it
  reads wrongly. Regenerating the fixture needs the network and the .NET 10 runtime; CI does not
  do it.
- **The live tests take a database each** (`LiveDatabase`) and compare what a migration leaves with
  what `EnsureCreated()` builds for the target model, by the catalog's own text.

Locally the integration tests go inconclusive without `SCHEMAKIT_TEST_PG`. When `CI` is set they
fail instead: an unreachable server must not retire them while the build reports green.

## Packaging and the release

`.github/workflows/release.yml`, `Conventions/PackagingConventionTests.cs` (#35 lists what of the
5.x port is still missing: the SBOMs, `nightly.yml`, the other convention tests).

- **The PostgreSQL package depends on the kernel at exactly its own version,** `[11.0.0-preview.1]`.
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
- **Never tag or publish:** a release is the maintainer's to trigger.

## What counts as correct

The failure this package exists to prevent is a migration that scaffolds and applies cleanly while
the database does not enforce what the model declared. Example tests do not catch it. The plan's
section 4 names the oracles that do: catalog equivalence on a live server, behaviour (writes that
must fail do fail), the golden corpus per renderer epoch, translator parity against EF's own query
SQL, and the removal of each registration in turn from a packed consumer.

A guard is not done until it has been shown to fail without what it guards.

## Upstream pins

Npgsql EF `11.0.0-rc.3` depends on EF Core `[11.0.0-rc.3.26522.148]`, exactly. Every EF reference
in the repository stays on rc.3 until Npgsql ships 11.0.0, although EF Core 11.0.0 is out. A stable
package cannot depend on a prerelease, so SchemaKit ships previews until then.

The probes drive Roslyn `5.12.0-1.26504.105` from the dotnet-tools feed
(`test/SchemaKit.ApiProbes/nuget.config` says why).

## Tracking

Work is tracked as labelled GitHub issues, not as lists in docs or memory: open the issue before
the work, give the PR the issue's labels and milestone, open it with `Closes #n`. `AGENTS.md` has
the rules in short form, `CONTRIBUTING.md` the legend. The pinned Roadmap issue holds the `11.0.0`
epic; spikes and slices are its sub-issues.

## Coming from the 5.x repository

Its `CLAUDE.md` is the record of what went wrong there and why each guard exists. The convention
tests, `MigrationHarness`, the translator parity sweep and the skills are ported from it as files
when the first `src/` project needs them, not before.
