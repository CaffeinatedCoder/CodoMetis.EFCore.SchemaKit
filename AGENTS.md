# AGENTS.md

`CLAUDE.md` is the canonical source of repository instructions.

Before working in this repository, every coding agent that does not load `CLAUDE.md` automatically must read it in full and follow all of its guidance.

## Issues
Work is tracked as GitHub issues, not as lists in docs or memory. Open the issue before starting
the work, and close it from the PR (`Closes #n`).

- **Labels, on issues and PRs alike** (the scheme of EFCore.ComplexIndexes and
  CodoMetis.ValueRanges): exactly one type (`bug`, `enhancement`, `documentation`, `tests`,
  `chore`); every `package/*` the change affects (`core`, `postgresql`, `sqlserver`); the `area/*`
  concern (`indexes`, `expression-indexes`, `constraints`, `migrations`, `release`); and
  `severity/*` on a bug. Add `silent` to a bug where the migration scaffolded and applied cleanly
  and the database differs from the model. Add `breaking` to anything that changes an existing
  answer or signature, and `stacked` to a PR whose base is another open PR
- **PRs** carry their issue's labels and milestone, open with `Closes #n`, and describe the
  problem, the change and how it was verified (`.github/PULL_REQUEST_TEMPLATE.md`).
  `pr-hygiene.yml` fails a PR with no type label, a bug with no severity, or no closing reference
- **Epics:** a release is an `epic` issue with a milestone of the same version (`11.0.0`). Its work
  is attached as native sub-issues and carries the same milestone. Previews are slices of that
  epic, not epics of their own
- **Roadmap:** the pinned `roadmap` issue holds every epic as a sub-issue; `design/` keeps the
  long-form design
- **Branches:** one line, `main`, on EF Core 11. The EF Core 10 line is another package in another
  repository (CaffeinatedCoder/EFCore.ComplexIndexes); nothing is cherry-picked between the two.
  See CONTRIBUTING.md#branches-and-releases
