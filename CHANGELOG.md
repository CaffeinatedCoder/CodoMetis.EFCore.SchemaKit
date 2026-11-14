# Changelog

One section per version, newest first, in the form
[CONTRIBUTING.md](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/blob/main/CONTRIBUTING.md#changelog) describes.

<!-- The date replaces "unreleased" in the commit that is tagged. release.yml refuses a tag whose
     heading is not `## [x.y.z] — yyyy-mm-dd`, so this heading stops a tag until then. -->

## [11.0.0-preview.1] — unreleased

The first preview, for `net11.0`, EF Core 11 with `Npgsql.EntityFrameworkCore.PostgreSQL` `11.0.0-rc.3`, and PostgreSQL 16 and 18. It declares exclusion constraints only: indexes, SQL Server, SQLite and the rest are [not in it yet](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/blob/main/README.md#what-it-does-not-do-yet). The stored form of a declaration in a model snapshot is not final in a preview, and a later preview may ask for the migrations to be scaffolded again; the permanent promise starts at `11.0.0`.

### Added

- `CodoMetis.EFCore.SchemaKit`, the kernel: `UseSchemaKit()` puts SchemaKit's model differ and SQL generator around EF Core's in every path that turns a model into DDL, and refuses a context where something else displaced them ([#40](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/pull/40)).
- `CodoMetis.EFCore.SchemaKit.PostgreSQL`: `UseNpgsql(…, o => o.UseSchemaKit())`, and exclusion constraints declared with `DeclarePostgres(pg => [pg.Exclusion(…)])`, with lambda elements and conditions, a deferral and a name ([#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25)).
- `btree_gist` is installed by the migration that first needs it, unless the model declares it or calls `SuppressBtreeGistInjection()` ([#25](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/25)).
- A model snapshot of `EFCore.ComplexIndexes` 5.x with exclusion constraints is read, and the same model declared with SchemaKit scaffolds no operations; a snapshot with 5.x's indexes or temporal constraints is refused ([#93](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/93), [guide](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/blob/main/docs/moving-from-complexindexes-5x.md)).
- An operation of a migration 5.x scaffolded that only 5.x's SQL generator rendered is refused, not applied as another object ([#110](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/110)).
- `GetDeclarations<T>()`, `FindDeclaration<T>(name)` and `Redeclare<T>(…)` read and amend what a model declares ([#45](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/45)).
- A name two objects of one model take is refused as `SK1001`, every collision at once ([#96](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/96)).
- A declaration is refused where it is written when the context's options do not register the package that renders it, as with `UseNpgsql(…).UseSchemaKit()` in place of `UseNpgsql(…, o => o.UseSchemaKit())`; and the PostgreSQL package beside a kernel of another version is refused by `UseSchemaKit()` with both versions ([#113](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/113)).
- The contributor contract other packages declare objects through, experimental (`SK9001`) until `rc.1` ([#7](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/7)).
