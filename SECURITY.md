# Security policy

## Reporting a vulnerability

**Please do not open a public issue for security reports.**

Report privately through GitHub:

> **[Report a vulnerability](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/security/advisories/new)**
> (Security → Advisories → Report a vulnerability)

If you cannot use GitHub advisories, email **meistercoder@mr-gross.de** with `SECURITY` in the
subject.

Helpful to include, as far as you have it:

- The package and version (`CodoMetis.EFCore.SchemaKit`, `.PostgreSQL`, or `.SqlServer`)
- The EF Core and provider versions, and the target database
- A minimal `OnModelCreating` snippet that triggers it
- The generated migration or DDL, and what you expected instead
- Why you consider it security-relevant: the impact you have in mind

## What to expect

This is a single-maintainer, non-commercial open-source project. There is no SLA, and the
following is a good-faith intention rather than a guarantee:

| | |
|---|---|
| Acknowledgement | within 7 days |
| Initial assessment | within 30 days |
| Fix or documented decision not to fix | best effort, tracked in the advisory |

You will be told either way. If a report turns out not to be a security issue, it gets handled as
an ordinary bug and you will be told that too, with reasoning.

## Disclosure

Coordinated disclosure. The intent is to publish a fix and a GitHub Security Advisory together,
and to keep the report private until then. Ninety days from acknowledgement is the default ceiling
for going public regardless of fix status. If that timing does not suit you, say so in the report
and it can be discussed.

Reporters are credited in the advisory unless they ask not to be.

## Supported versions

**No version is released yet, so none is supported.** Previews (`11.0.0-preview.N`) are not
supported versions: a fix reaches a preview in the next preview.

When 11.0.0 ships, this section gets its table: the latest 11.x minor, and nothing older.

**11.x** targets **EF Core 11** (`net11.0`, .NET 11, Npgsql 11). The intention is to maintain it for
as long as Microsoft supports EF Core 11. A new major means a new EF Core release, and nothing
else.

The EF Core 10 line is another package, `EFCore.ComplexIndexes` 5.x, with
[its own policy](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/SECURITY.md).

## In scope

- **Identifier escaping failures.** Column, table, schema, index, constraint and JSON property
  names are delimited before they are written into DDL. A value that escapes its quoting and
  injects DDL is a vulnerability, even though it originates in model code.
- **Silently incorrect DDL where a declared constraint is a security control.** A migration that
  applies cleanly while omitting or weakening a declared unique index or exclusion constraint that
  enforced something like tenant isolation or non-overlapping grants.
- **Supply-chain integrity:** anything suggesting a published package does not correspond to the
  source at its recorded commit, or a weakness in the release workflow.

## Out of scope

- **Raw SQL passed through `Sql.Raw<T>`.** It is emitted as written, by design. Building it from
  untrusted input is a defect in the calling application.
- **Vulnerabilities in EF Core, Npgsql or the SQL Server provider.** Report those to their
  projects; if one affects this package's behaviour, a report here is still welcome.
- **A migration that fails to apply.** That is a bug; please open a normal issue.
