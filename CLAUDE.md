# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is, and where it stands

`CodoMetis.EFCore.SchemaKit` 11.x is the successor of `EFCore.ComplexIndexes` 5.x for EF Core 11:
a new package with a new API and an entirely new core, in its own repository. The EF Core 10 line
stays in CaffeinatedCoder/EFCore.ComplexIndexes and shares no code and no branch with this one.

**No shipping code exists yet.** The repository holds:

| Path | What |
|---|---|
| `design/schemakit-api.md` | The public surface and the type model, written against rc.2. Its open questions are answered in the plan |
| `design/schemakit-core-plan.md` | The plan for the core after EF Core 11 GA, with the maintainer's decisions (section 7). **Where the two disagree, the plan holds** |
| `test/SchemaKit.ApiProbes` | A prototype of the surface and 30 compile probes: each forbidden declaration is compiled with Roslyn and its diagnostic asserted, beside an allowed twin |

The prototype is not the implementation. It has defects the plan lists on purpose (sections 3.4,
3.8 and 3.9): an identity rule that loses a declaration, an unqualified `DROP INDEX`, and a
predicate translator without null compensation. Do not copy from it without reading those.

## Commands

```bash
# The probes and the prototype's behaviour tests
dotnet test test/SchemaKit.ApiProbes/SchemaKit.ApiProbes.csproj
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
