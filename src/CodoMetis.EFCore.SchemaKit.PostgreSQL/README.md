# CodoMetis.EFCore.SchemaKit.PostgreSQL

Schema objects of PostgreSQL that EF Core 11 and Npgsql 11 have no API for, declared in the model
and carried by every migration path: `migrations add`, `EnsureCreated()`, `GenerateCreateScript()`
and the pending-changes check. In this preview: exclusion constraints, on PostgreSQL 16 and 18.

```csharp
services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString, o => o.UseSchemaKit()));
```

```csharp
modelBuilder.Entity<RoleGrant>().DeclarePostgres(pg =>
[
    pg.Exclusion(
        pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period),
        where: x => x.RevokedOn == null,
        deferral: Deferral.InitiallyDeferred,
        name: "ex_role_grant_active_period"),
]);
```

The migration adds `btree_gist` when an element needs it, then the constraint, as SQL.

The package depends on
[CodoMetis.EFCore.SchemaKit](https://www.nuget.org/packages/CodoMetis.EFCore.SchemaKit) at exactly
its own version; update the two together. It is built against EF Core 11 and
`Npgsql.EntityFrameworkCore.PostgreSQL` `11.0.0-rc.3`.

This is a preview. Any API may change between previews, and so may the stored form of a
declaration in the model snapshot: a later preview may ask for the migrations to be scaffolded
again. The permanent promise starts at `11.0.0`. What the preview does not do yet is listed in the
[README](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/blob/main/README.md#what-it-does-not-do-yet).

Coming from `EFCore.ComplexIndexes` 5.x:
[the guide](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/blob/main/docs/moving-from-complexindexes-5x.md).
Documentation, the design and the issues are in the
[repository](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit).
