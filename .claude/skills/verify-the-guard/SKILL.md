---
name: verify-the-guard
description: Show that a guard fails without what it guards. Revert the fix, or, where the subject is delivery (the wiring, a package, a workflow, a document) and there is no fix to revert, break the subject in a scratch copy. Use whenever a test is added beside a fix or a new refusal in this repository, whenever a check's subject is not code, and before reporting any of it as done.
---

# Verify the guard

A test that passes with the fix and without it is no regression test. It is a test that happens
to pass, and it goes on passing while the defect comes back. CLAUDE.md says it in one line: a
guard is not done until it has been shown to fail without what it guards.

The discipline is one more step. **After the test is green, take away what it guards and see it
turn red.** Then put it back and run again.

Ported from `EFCore.ComplexIndexes`, where the step paid for itself in the 5.0.1 audit: a test
declared two exclusion constraints that were meant to collide on their default names, but they had
the same elements and the same filter, so the store made one of them and no exception was
possible. The test passed against the broken code. Only the run without the fix showed it.

## The loop

1. Write the test. Run it: it **fails**, for the reason you expect. Read the message. A failure
   for another reason than the defect means the test tests another thing.
2. Apply the fix. Run it: it passes.
3. **Take back the source change only**, keeping the test. Run again: it has to fail.
4. Put the fix back. Run the whole suite.

Step 1 is left out when the fix was written first. Step 3 gives the same assurance afterwards, so
do step 3 always.

## Taking it back without losing work

Copy the file aside, take the change out, run, copy back:

```bash
SCRATCH=$(mktemp -d)
cp "<the file>" "$SCRATCH/"
# take out the new guard alone, then:
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "TestCategory!=Integration"
cp "$SCRATCH/<its name>" "<the file>"
```

Take back the **source**, never the test. `git checkout --` on a file that holds other work loses
it; copy aside. Run the suite again afterwards and compare the count with the one you started
from. Never leave the working tree with half a fix.

## Failing for the right reason

- **Right:** `Assert.ThrowsExactly` says nothing was thrown; a count is 1 where 2 was asserted; the
  statement asserted is missing from the migration; the catalog of the migrated database differs
  from the created one's.
- **Wrong:** a `NullReferenceException`, a compile error, a failure in another test. A compile
  error usually means the test needs API the fix brought. Then it is a test of the feature and no
  regression test: say so.

## When the subject is delivery and not code

The wiring, a packed package, a workflow and a document have no line of source to take back. Break
the mechanism, and where the mechanism is the repository itself, break it **in a copy outside the
checkout**:

```bash
SCRATCH=$(mktemp -d)
git archive HEAD | tar -x -C "$SCRATCH"
cd "$SCRATCH" && git init -q && git add -A && git commit -qm pristine
dotnet build test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj
# one break at a time: edit a workflow, the changelog, a README, a project file
git add -A
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --no-build --filter "ClassName~ReleaseWiringConventionTests"
git reset -q --hard HEAD && git clean -fdq
```

`git archive` copies the commit and nothing else. A copy of the folder would bring the build
outputs and, from a checkout that has worktrees inside it, each of those as well, which is a
whole repository apiece; for work that is not committed yet, commit it to a branch first.

The convention tests find the repository from their own output folder, so in the copy they read
the copy, and they read the files git tracks there: a break that adds a file is seen once it is
added, which is what the `git add -A` is for. A break of a file that is not compiled needs no
build. `git reset --hard`, and not `git checkout -- .`: a break made with `git mv` or `git rm` is
in the index, and a checkout leaves it there for every break that follows.

A copy has no `origin`, so the one test that reads Source Link from a packed PDB fails in it
(`PackagingConventionTests`); that is the copy and not the commit.

For the packages, `test/ConsumerSmokeTest/run.sh` has the break built in: `SMOKE_BREAK` takes one
registration away from every path that expects SchemaKit (`no-schemakit`, `kernel-only`,
`user-differ`), and each has to make the run fail.

Three traps, each of which has produced a green run against a build that was broken on purpose:

**The mechanism is redundant.** Three parts keep SchemaKit's differ and generator outermost
(CLAUDE.md, The host), and `dotnet.yml`'s gate refuses an empty `needs` twice, in a check of its
own and again in the loop over the results. Take one away and the other still answers, which is
the design and no failure of the guard. Break each **alone**, expect the single break to pass
where the design says so, and then break them together.

**The assertion does not tell the two cases apart.** A 5.x snapshot and the model that declares
the same objects diff to nothing, and that alone proves little: both sides go through one
resolver, so a default name that had drifted from 5.x's would diff to nothing too. What holds the
names is the live test, which applies 5.x's migration as scaffolded and compares the catalog
(`LegacyHandoverLiveTests`). In the same way a value read from `Directory.Build.props` and
compared with the package is compared with itself; `PackagingConventionTests` states the license
and the repository for that reason. Assert on something only the right path produces.

**The conditions carry the defect.** Running the same command again proves nothing; change what
the check depends on without saying so. A cold checkout and not a warm one. A folder of packages
packed beforehand and not one the script packs itself, which is why the smoke test runs both ways
in CI. A package cache of the test's own and not the global one, which serves a package of the
same version from an earlier build. A diff in both directions: a handover that diffs to nothing
never asks for Down, which is how the planner's rule for the consumer's own model was first
missed.

## How a guard passes on nothing

Check these before believing a green run:

- It asserts something the right path and the wrong one both produce.
- It reads a package that was cached or published before, and not the one just built.
- It looks for a phrase that is no longer there, because the phrase was reworded. Assert that the
  match succeeded, so that a reworded sentence fails and is not passed over.
- Its filter matches no test. `dotnet test --filter` passes on nothing; the workflows read the
  count back for that reason.
- It was inconclusive. Without `SCHEMAKIT_TEST_PG` the tests that need a server do not run; with
  `CI` set they fail.
- Its setup collapsed: two declarations that were meant to collide became one, so the exception
  under test could never be thrown.
- Its subject is not there. A test over "every row" passes while there is no row. Leave such a
  guard out until its subject exists, and say where it is recorded.

## Tests that pass either way, and should

Some tests are no regression guards and are not forced through the loop:

- A test that pins behaviour that was right already, added as a record.
- A test against a correction that goes too far later: that a snapshot which holds a collision
  stays diffable, for one, which keeps a validation from being made too strict.

Both are worth having. Do not count them as evidence that a fix works, and say which they are.

## Reporting

State the result of the run without the subject, with the failure's message: one line per guard,
what was broken and what the test said. "Added a test" says nothing about whether the defect is
caught. When several fixes land together, take them all back at once and compare the number of
failures with the number of guards: a fix whose test still passes stands out.
