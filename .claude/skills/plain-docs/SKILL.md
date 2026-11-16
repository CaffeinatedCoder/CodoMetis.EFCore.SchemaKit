---
name: plain-docs
description: The voice for every page a consumer of SchemaKit reads: the root README, the package READMEs and the guides in docs/. Plain, friendly, example-first and exact. Use when writing or rewriting any of them, including a new section, a new page under docs/, or a paragraph added beside a feature.
---

# Plain docs

The reader is a .NET developer deciding whether to try SchemaKit, or using it right now. They
should understand each paragraph on the first read, and finish a section knowing what to type. A
page that is correct and hard to read loses them before the details matter.

This voice is friendly, not chatty. Every claim stays as exact as before. Plain words make a page
readable; they never make it vaguer. Ported from `EFCore.ComplexIndexes`.

## Which pages

The root `README.md`, the package READMEs under `src/*/README.md`, which are what nuget.org shows,
and the guides under `docs/`: today `docs/moving-from-complexindexes-5x.md`.

`CONTRIBUTING.md`, `AGENTS.md`, `CLAUDE.md`, `SECURITY.md`, the pages under `design/` and the
changelog keep their own style. The changelog follows CONTRIBUTING.md#changelog, which
`ChangelogConsistencyTests` holds.

## The voice

- **Write to "you".** "You declare the constraint in `OnModelCreating`" rather than "constraints
  are declared in `OnModelCreating`".
- **Say what the reader gets first, then how it works.** "`migrations add` writes the constraint
  into the migration as SQL" comes before "SchemaKit wraps EF Core's `IMigrationsModelDiffer`".
- **One idea per sentence.** Split a sentence at a semicolon, at a colon that is followed by a
  list, at a dash that starts a second thought, or at a second "and". About 20 words is a good
  ceiling.
- **Active voice, a person or a thing doing something.** "`migrations add` fails and names the
  constraint" rather than "a refusal that names the constraint".
- **Natural word order.** Not "What the planner resolves, the migration carries".
- **Don't personify the code.** A planner doesn't "refuse" or "see", a condition doesn't "say", a
  guard doesn't "watch". Write what happens: "`OnModelCreating` throws", "the migration fails
  when you apply it".
- **State things positively when you can.** "Give both constraints a name" instead of "a
  constraint without a name is not allowed here".
- **No hype and no filler.** Avoid "simply", "just", "easy", "seamless", "powerful", "zero
  ceremony", "out of the box", "under the hood", "first-class", exclamation marks and emoji.
  Friendly comes from clarity, not from adjectives.

## Plain words

Use the left column in prose. Keep the API's own names in code (`UseSchemaKit`, `DeclarePostgres`,
`PgExclusion`, `Deferral.InitiallyDeferred`), and introduce a term once where the API or EF Core
forces it on the reader.

| Write | Instead of |
|---|---|
| the PostgreSQL package, the SQL Server package | the provider package unexplained, the contributor's package |
| the kernel, once you have said it is the package the others depend on | the core, the host |
| fail, throw, reject (a person or a database rejects) | refuse, refusal |
| `UseSchemaKit()` (name the call) | the wiring, the registration, the stamp |
| what you declare, your declaration | the declaration, used bare as a subject |
| when you run `dotnet ef migrations add`; in the migration | at design time, at scaffolding |
| when the migration runs, when you apply the migration | at apply time, at run time |
| `migrations add` writes, the migration contains | scaffolds, renders, bakes in |
| EF Core's own SQL generator, the provider's SQL generator | the inner generator, the stock generator |
| the model snapshot, once: the file `migrations add` keeps beside your migrations | the snapshot, unexplained |
| your code, your application, your model | the consumer, the caller, the context's own model |
| PostgreSQL uses the index for a query | the index serves the query |
| works with any relational provider | provider-neutral |
| the migration runs without an error | applies cleanly |
| keeps, uses | honours, rides on, flows as |
| on purpose | deliberately, by design |
| what makes two declarations the same object | identity, the key |
| a condition (`where:`) | a predicate, a filter (which is 5.x's word; say it once in the guide) |

Name an error after its plain description, in parentheses: "the migration fails when it runs
(42P07)", "`OnModelCreating` throws (`InvalidOperationException`)", "the build warns (NU1608)",
"the model is rejected (`SK1001`)". The code helps someone who searches a log; the words help
everyone else.

## The shape of a page

1. **Open with one or two sentences** on what the page lets the reader do. Then show the smallest
   example that works.
2. **Headings are tasks or questions** in a guide: "Keep overlapping bookings out", "Move a
   constraint from 5.x". Keep every heading something links to (see below).
3. **Example first, explanation after.** A section leads with code where there is code, then says
   what it does in a few short sentences.
4. **Edge cases go last**, in a short "Good to know" list at the end of the section, not in the
   middle of the explanation. Each one is a bullet with a bold lead phrase.
5. **Lists for three or more parallel items.** A bullet starts with a bold phrase a reader can
   skim, followed by one or two sentences.
6. **Link instead of repeating.** A guide explains and a package README is the short version for
   nuget.org; a detail is in one place and the other links to it.
7. **What does not work yet is said, with its issue.** A preview's README lists what it does not
   do and its known defects, each with a link.

## What must not change

Rewriting the voice must not break what the tests hold, or what nothing holds. Run both test
projects after every page:

```bash
dotnet test test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.csproj --filter "TestCategory!=Integration"
```

```bash
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj --filter "TestCategory!=Integration"
```

- **Every `csharp` block is compiled.** `DocumentationSampleTests` compiles each one against the
  shipped assemblies with nothing suppressed. A block with a type declaration is a file of its
  own; any other is statements in a method that has `modelBuilder`, `options`, `services` and
  `connectionString`, beside the entity types the test declares. A sample that needs another
  entity type needs it added there. 5.x code is fenced `csharp 5.x` and is not compiled; any
  other marker fails.
- **What is not compiled is yours to check.** A `sql` block, a comment that states generated SQL,
  an error message in quotes: each is the source's text or the database's. Check it against the
  code or the test that shows it, not against the old page.
- **Write only what is measured.** A sentence about what a migration does names the test that
  shows it, or is left out (CLAUDE.md, The documentation). The guide's tables have a column for
  it.
- **API names in a sentence are checked.** `DocumentationApiTests` reads every `Name(` and every
  `Type.Member` in inline code outside the code blocks. A call that is not public API of this
  repository has to be on the test's list of EF Core, Npgsql and BCL calls, and a name on that
  list that no page uses any more fails too. In a table whose first column is headed `5.x`, that
  column is 5.x's API and is not read; a 5.x call in a sentence goes on the test's second list.
- **A package's README is about that package.** The same test fails when the kernel's README
  names a call only the PostgreSQL package has. A page under `docs/` with a provider's name in
  its file name belongs to that provider's package.
- **Links and anchors are checked.** `DocumentationLinkTests` resolves a relative link and its
  `#anchor`, and an absolute link to this repository's `blob/main` or `tree/main` the same way.
  A renamed heading changes its anchor: find every link to it with
  `git grep -n 'page.md#anchor'` and change them in the same commit. A heading that is repeated
  on a page gets a numbered anchor the test does not model, so a link to one fails.
- **Keep the headings that published text links to.** The changelog links
  `blob/main/…#anchor` into `README.md` and `docs/`, and a section of the changelog becomes a
  GitHub release's notes, which stay as published. Check with
  `git grep -n -E '\.md#' -- '*.md' src` before renaming a heading.
- **A package README links with absolute URLs only.** nuget.org resolves no relative link
  (`DocumentationLinkTests`). No README holds a version section (`ChangelogConsistencyTests`).
- **Strings a user searches for stay exact:** an error message in quotes, `SK1001`, `SK9001`,
  `NU1608`, a SQLSTATE such as 42P07 or 23P01, an annotation key such as
  `CustomExclusion:Constraints`.
- **Facts stay exact.** Where the old sentence had a condition, an exception, a provider, a
  version or an issue, the new one has it too, perhaps moved to "Good to know". When you are not
  sure what a sentence means, read the code or the test it describes before you rewrite it. A
  sentence about what PostgreSQL does is backed by a test on a live server; keep its meaning
  when its wording changes.
