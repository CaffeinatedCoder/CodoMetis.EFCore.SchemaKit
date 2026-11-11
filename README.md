# CodoMetis.EFCore.SchemaKit

Schema objects EF Core 11 has no API for, declared in your model and carried by your migrations:
expression and JSON-member indexes, typed index filters, PostgreSQL exclusion constraints, and a
contract other packages use to add their own (row guards, policies, ltree paths).

> **Nothing is released yet.** This repository holds the design, a compiling prototype of the API,
> and the beginning of the kernel: the host that puts SchemaKit into every migration path. Nothing
> can be declared with it yet. The first package will be `11.0.0-preview.1`.

```csharp
public sealed class RoleGrantConfiguration : IEntityTypeConfiguration<RoleGrant>
{
    public void Configure(EntityTypeBuilder<RoleGrant> b) => b.DeclarePostgres(pg =>
    [
        pg.Exclusion(
            pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
            where: x => x.RevokedAt == null,
            name: "ex_role_grant_active_period"),

        pg.Unique(x => x.Code.ToLower(), where: x => x.RevokedAt == null, nullsDistinct: false),

        pg.Gin(x => x.Payload, GinOpClass.JsonbPathOps),
    ]);
}
```

The rule the API follows: **a declaration PostgreSQL or SQL Server would reject does not compile.**
A GIN index has no `unique:` parameter, an exclusion constraint of equality elements only is a type
error, and a filter is a lambda over the entity you are configuring.

## Where it stands

| | |
|---|---|
| Design of the API | [design/schemakit-api.md](design/schemakit-api.md) |
| Plan for the core, with the decisions taken | [design/schemakit-core-plan.md](design/schemakit-core-plan.md) |
| The kernel, so far the host | [src/CodoMetis.EFCore.SchemaKit](src/CodoMetis.EFCore.SchemaKit) |
| Prototype and compile probes | [test/SchemaKit.ApiProbes](test/SchemaKit.ApiProbes): 30 forbidden declarations, each with an allowed twin |
| Work | The [issues](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues), under the `11.0.0` milestone |

## Which package for which EF Core

| EF Core | Package | Repository |
|---|---|---|
| 10 (LTS, to November 2028) | `EFCore.ComplexIndexes` 5.x | [CaffeinatedCoder/EFCore.ComplexIndexes](https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes) |
| 11 | `CodoMetis.EFCore.SchemaKit` 11.x | this one |

SchemaKit is a new package with a new API, not a rename. A project moving from 5.x keeps its
migrations and its snapshot: SchemaKit reads both, and the move adds no migration or exactly the one
its guide documents. That is a release criterion, not a hope.

EF Core 11 and Npgsql 11 do some of what 5.x was built for themselves: indexes over complex-type
members, and `WITHOUT OVERLAPS` keys. SchemaKit leaves those to them.

## Build

You need the .NET SDK 11.0.100 or a later patch (`global.json`).

```bash
dotnet test test/CodoMetis.EFCore.SchemaKit.Tests/CodoMetis.EFCore.SchemaKit.Tests.csproj
dotnet test test/SchemaKit.ApiProbes/SchemaKit.ApiProbes.csproj
```

## License

MIT. See [LICENSE](LICENSE).
