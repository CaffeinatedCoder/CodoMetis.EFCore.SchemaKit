# Contributing

SchemaKit is at its beginning: the repository holds the design, a prototype with its compile probes,
and the kernel's host. Until `11.0.0-preview.1` the most useful contribution is a review of
[design/schemakit-api.md](design/schemakit-api.md) and
[design/schemakit-core-plan.md](design/schemakit-core-plan.md), as an issue.

## Build and test

You need the .NET SDK 11.0.100 or a later patch; `global.json` pins the feature band.

```bash
dotnet build CodoMetis.EFCore.SchemaKit.slnx
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj
dotnet test test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.csproj
dotnet test test/SchemaKit.ApiProbes/SchemaKit.ApiProbes.csproj
```

The tests contact no database but SQLite, unless `SCHEMAKIT_TEST_PG` holds an Npgsql connection
string to a PostgreSQL server: then the tests of the category `Integration` run against it, and
each of the PostgreSQL package's creates and drops a database of its own.

## Issues

Work is tracked as labelled issues. Open the issue before the work.

| Label | Use |
|---|---|
| `bug`, `enhancement`, `documentation`, `tests`, `chore` | The type. Exactly one |
| `package/core`, `package/postgresql`, `package/sqlserver` | Every package the change affects |
| `area/indexes`, `area/expression-indexes`, `area/constraints`, `area/migrations`, `area/release` | The concern |
| `severity/critical`, `severity/high`, `severity/medium`, `severity/low` | On a bug |
| `silent` | A bug that answers wrongly without throwing: the migration scaffolds and applies, and the database differs from the model |
| `breaking` | Changes an existing answer or signature |
| `stacked` | A PR whose base is another open PR |
| `epic`, `roadmap` | A release or theme, and the pinned issue that lists them |

A release is an `epic` with a milestone of the same version. Its work is attached as sub-issues
and carries the milestone.

A pull request carries its issue's labels and milestone, opens with `Closes #n`, and says what was
wrong, what changed and what proves it. `.github/workflows/pr-hygiene.yml` fails one with no type
label, a bug with no severity, or no closing reference.

## Branches and releases

- **One line, `main`, on EF Core 11.** A new major means a new EF Core major, and nothing else.
- **The EF Core 10 line is `EFCore.ComplexIndexes` 5.x,** another package in
  [its own repository](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes). Nothing is
  cherry-picked between the two. A defect both have is two issues, one in each.
- **Previews** are `11.0.0-preview.N`. They may change any API, the contributor contract and the
  annotation format. From `11.0.0` the usual promise holds.
- **A silent fix,** one that changes what an existing model scaffolds, ships as a minor, never as a
  patch. Its changelog entry opens with `### Changed answers`, a row per changed answer linking the
  issue and its entry in `docs/upgrading.md`. `pr-hygiene.sh` fails a `silent` PR into a minor
  without that row.

## Changelog

One section per version, newest first, headed `## [x.y.z] — yyyy-mm-dd`. The release workflow and
`pr-hygiene.sh` find a section by that heading, so a heading in another style is no section.

Inside a section: at most one introductory line, then `### Changed answers`, `### Breaking`,
`### Added`, `### Changed`, `### Fixed`, in that order, one line per change, absolute links. What
the first `migrations add` after an upgrade does goes in `docs/upgrading.md`, not in the changelog
line.

## What is not here yet

The convention tests that enforce most of the above in `EFCore.ComplexIndexes` (changelog form,
packaging, documentation links, workflow gate, release wiring) are not ported yet. Two conventions
are held by tests already: no EF-internal API in a shipping project, and the kernel's public
surface. The rest of this page is held by review until the port.
