# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is, and where it stands

`CodoMetis.EFCore.SchemaKit` 11.x is the successor of `EFCore.ComplexIndexes` 5.x for EF Core 11:
a new package with a new API and an entirely new core, in its own repository. The EF Core 10 line
stays in CaffeinatedCoder/EFCore.ComplexIndexes and shares no code and no branch with this one.

**The kernel has its host, its SQL vocabulary, the contributor contract and the declaration store.
A declaration can be stored and read back; nothing turns one into DDL yet** (the resolver is #43,
the planner #24). The repository holds:

| Path | What |
|---|---|
| `src/CodoMetis.EFCore.SchemaKit` | The kernel. So far the host (`UseSchemaKit()`, the differ and generator decorators, the wiring guards), the SQL vocabulary statements are built from, the contributor contract and the declaration store. Public are the registration methods and the contract, which is marked experimental (`SK9001`) until it freezes at rc.1; the rest is internal |
| `test/CodoMetis.EFCore.SchemaKit.Tests` | Its tests, on Npgsql, SQL Server and SQLite. `Harness/TestKit.cs` stands in for the planning layer that does not exist yet; `Harness/SampleContract.cs` is a contributor written against the public contract alone |
| `spikes/` | The two spikes the host was built on (A: decorators and ownership, B: design time from a packed consumer). B is the consumer smoke test until the real one replaces it |
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

# The kernel's tests
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj

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
- **A property nothing read, an unknown `v`, a reader that loses the name or the epoch: each is
  refused.** They are all the same failure, a declaration that comes out of a snapshot as another one.
- **`Plan` and `ImpactOf` are abstract on the base class.** No optional hook, no default answer.
  The base class refuses an `InPlace` with no statement and statements beside another answer.
- **The unions are closed for the major.** `ContractValueTests` lists every case; adding one is a
  decision to take with the maintainer, since it breaks every exhaustive switch of a contributor.
- **`SchemaContributorRegistry` is a diff-time service.** A contributor may take the dialect, which
  reads the design-time model, so the registry must never be a constructor parameter of the differ
  or the generator.

Locally the integration tests go inconclusive without `SCHEMAKIT_TEST_PG`. When `CI` is set they
fail instead: an unreachable server must not retire them while the build reports green.

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
