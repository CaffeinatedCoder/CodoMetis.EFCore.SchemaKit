---
name: migration-safety-review
description: Review a change to SchemaKit for migrations that scaffold and apply without an error while the database ends up different from the model. Use when changing the host (the differ and generator decorators, the wiring), the planner, a contributor or its ImpactOf, the declaration store or a stored form, the resolver, the lambda translator, a legacy reader, or packaging, and before a release.
---

# Migration-safety review

The failure this package exists to prevent is no crash. It is a migration that scaffolds without
a word, applies without an error, and leaves the database enforcing something else than the
model declared. A consumer has no sign of it: `dotnet ef migrations add` succeeds,
`dotnet ef database update` succeeds, and the guarantee they asked for is not there.

A general review does not look for this, and example tests do not catch it. This skill is the
lens. It is ported from `EFCore.ComplexIndexes`, where three of the eleven defects of the 5.0.1
audit were of this kind, the two most severe among them. SchemaKit's core was decided against
those defects (CLAUDE.md, "The decided core, in ten lines"), so most questions below ask whether
a change keeps one of those decisions.

## How to use it

Work through the sections the change touches. Answer each question against the code, and not
from a document, which may state the intent and not the behaviour. Where a question has no plain
answer, write the test that answers it: that is usually faster than reasoning.

Report each finding with its scenario spelled out: the model a user writes, the statements that
result, and what the database then does differently from what was declared. In a tracked
repository each finding becomes an issue; one that answers wrongly without throwing carries the
`silent` label.

## 1. The wiring: nothing may degrade without a word

`UseSchemaKit()` is the one wiring point. A model built without it throws at the declaration
(`SchemaKitModelStamp.Require`), and a declaration whose owner has no contributor registered
throws there too.

- Does a new declaration API ask for the stamp first, and for its contributor's registration?
- Does a new package call `SchemaKitPackages.RequireKernel` first in each registration, from a
  method that calls nothing else of the kernel?
- Does everything the change adds reach the migration as a `SqlOperation`, rendered when the
  migration is scaffolded? Anything that needs a service of SchemaKit when the migration is
  applied is wrong: render the operations through the provider's own generator and read the SQL.
  Valid SQL that says something else is the defect; a failure is fine.
- Is anything lowered into EF Core's native index metadata, or into a native operation with an
  annotation? Decision 5 says no. `LegacyFormats.RequireRewritten` refuses the operations 5.x
  built that way, since the provider's generator writes such an operation as if the annotation
  were not there.
- Does the differ or the generator now take a constructor parameter that needs the model
  (`ISchemaDialect`, the contributor registry)? The ownership check resolves both while a model
  is being built.

The oracle is the consumer smoke test: `test/ConsumerSmokeTest/run.sh` with `SMOKE_BREAK` takes
each registration away in turn and every path has to fail.

## 2. The declaration store: what replaces what

- `Declare` never replaces: a key that is taken throws. Is there a new path that stores a
  declaration and is neither `Declare` nor `Redeclare`? An "or update" overload is how the draft
  lost a unique index without an error.
- What is the declaration's identity? Everything that makes two declarations two different
  objects is in it; `writer.Operational` marks only what does not change the object. Write two
  declarations that differ in one field outside the identity: are they two objects, an error,
  or, wrongly, one?
- A stored form is a file format, in consumers' snapshots. Does the change move a key or a
  value that `DeclarationStoreTests` or `PgExclusionDeclarationTests` hold verbatim? Then it is
  a format change, and a snapshot written before it has to be read as the same object.
- Is anything omitted but a null? Does a reader fall back to a default? Each makes a declaration
  come out of a snapshot as another one.

## 3. Names

- Can two declarations resolve to one name, default names included? Those are built from the
  resolved table and columns, so only the planner knows them.
- Does a new kind of object say which namespaces its name is in (`Claims`), and is the dialect's
  table of namespaces held against the server (`NameClaimLiveTests`)?
- What happens when a name is taken as the migration is applied: an error, or a replacement? A
  drop has no `IF EXISTS` and no `ADD` is preceded by one (decision 9). 5.x wrote
  `DROP CONSTRAINT IF EXISTS` before each `ADD`, so a second constraint of the same name
  replaced the first, which is worse than the error.
- A collision is refused in the context's own model only. A snapshot that holds one stays
  diffable, both ways, or the model can never be put right.

## 4. Which model, which provider

- A rule for the consumer's model is asked of the context's own design-time model, told by
  reference (`IsOwn`), and never of "the second argument": EF Core asks for Down with the models
  the other way round. Test such a rule by diffing both ways.
- A declaration for a provider that is not the active one is an error (decision 10). Does the
  change add a way for one provider's object to be planned by another's contributor, or passed
  over?
- The kernel and its contract are every provider's. Judge a change to them for SQL Server and
  SQLite as well, and not for PostgreSQL alone: a default the kernel guesses is a claim about
  each of them.

## 5. EF Core's own operations

EF Core's operations are read and never changed, and each one that touches what an object depends
on is asked of the contributor (`ImpactOf`).

- Is every `MigrationOperation` type mapped in `BaseChanges`? `SchemaImpactTests` fails when an
  assembly has a type the table lacks.
- Is the answer of `ImpactOf` measured on a live server, or reasoned? PostgreSQL drops an index
  and an exclusion constraint with a dropped column, without a word: answered as "follows", the
  migration applies and the object is gone for good. Each way of reading a column has its row in
  `PgExclusionLiveTests`; a new kind of element needs its row before the contributor answers
  for it.
- What does the provider write for the operation? An `AlterColumnOperation` that the provider
  writes as drop and add is a dropped column (`BaseChanges.DroppedAndAdded`), whatever the
  operation is called.
- SQLite rebuilds the table for some operations, and its generator moves the rebuild to the end
  of the migration. Do not ship a contributor for SQLite before the generator decorator places
  a create after it.

## 6. Churn, and the snapshot

- Does a model against its own snapshot diff to nothing? Both are read the same way, through the
  contributor and the resolver, and nothing compares SQL text.
- Does every rule of the resolver answer alike on a configured model and on a snapshot
  (`SchemaResolverTests` runs each on both)? A snapshot is a property bag without converters. An
  answer that differs is a migration nobody asked for, or one that is missing.
- Does the change alter what a method returns for an input it accepted? That is a new epoch
  (`ResolutionEpoch`), and so is a change of how a lambda is spelled.
- An earlier package's annotation: is it read in every model but the context's own, and refused
  there? Is an annotation that declares an object and has no reader refused in a snapshot? Passed
  over, its objects are created a second time or never dropped.

## 7. The order of operations

Drops, then EF Core's operations untouched, then statements in place, then creates. A wrong order
usually still scaffolds.

- What does a new statement depend on, and what depends on it? Assert the positions, as
  `OperationOrderTests` does.
- A change in place runs after EF Core's operations. Is a name it takes still held then
  (`RenameInPlaceTests`)?
- Is an extension a created object needs ensured once and first?

## 8. The lambda translator

- Is the template spelled as the provider's queries spell the expression, character for
  character? An index serves only a query whose expression matches it. The parity sweep compares
  with EF Core's `ToQueryString()`; a disagreement is fixed or waived with its reason.
- Does a comparison that can be NULL keep the rows C# keeps?
- Is a conversion left out of a template that changes a value on some database? Only the pairs
  of `SilentConversions` are.
- Is a new construct in the tables the sweep reads, and not in the dispatch alone?

## What counts as shown

The plan's section 4 names the oracles, and a finding or a fix is judged by them:

- **Catalog equivalence on a live server.** What a migration leaves is what `EnsureCreated()`
  builds for the target model, by the catalog's own text (`AssertMigratesTo` in
  `PgExclusionLiveTests`).
- **Behaviour.** A row that must be refused is refused.
- **Translator parity** against EF Core's own query SQL, offline and on PostgreSQL 18.
- **The removal of each registration** from a packed consumer.

Before reporting, confirm each finding by running it. For anything you then fix, apply the
`verify-the-guard` skill.
