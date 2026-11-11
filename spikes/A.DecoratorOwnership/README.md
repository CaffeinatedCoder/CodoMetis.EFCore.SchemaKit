# Spike A: decorate the differ and the generator, and check who owns them

For [#16](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/16). Throwaway code:
it leaves when the kernel host replaces it.

**Question.** Can SchemaKit wrap `IMigrationsModelDiffer` and `IMigrationsSqlGenerator` as decorators
instead of deriving from EF's classes, keep another package's replacement as its inner service, and
check while the model is being built that the outermost instance of both is its own?

**Answer: yes,** on Npgsql, SQL Server and SQLite, with public API only. 69 conditions pass. Each
mechanism was removed in turn in a scratch copy, and the conditions that depend on it failed.

```bash
dotnet test spikes/A.DecoratorOwnership/A.DecoratorOwnership.csproj
```

No database is contacted: models are built and create scripts generated offline, on EF Core
`11.0.0-rc.3` (Npgsql's pin).

## What it takes

| Mechanism | In `SpikeKit/` | Without it |
|---|---|---|
| Take over the `ReplaceService` entry, keeping an earlier one as the inner | `UseSpikeKit` | C05, C09, C16, C19 fail: an earlier replacement is discarded, and a later direct registration wins |
| A registration of its own in `ApplyServices` | `SpikeOptionsExtension.ApplyServices` | C07, C08, C21 fail: a replacement keyed on EF's type removes SchemaKit |
| Find the inner by replaying the other extensions' `ApplyServices` | `InnerServices` | (C05 and C19 fail when the earlier replacement is ignored) |
| The stamp, required by the declaration API | `StampConvention`, `DeclareSpike` | C03 fails: a declaration without the wiring is accepted |
| `Validate` refuses a later service-only `ReplaceService` | `SpikeOptionsExtension.Validate` | C06 fails: the refusal moves from construction to model build, with a worse message |
| The resolve-time check | `StampConvention.Outermost` | C20, C21 fail: a displaced SchemaKit goes unnoticed |

## The conditions

All on the three providers.

| | Condition | Outcome |
|---|---|---|
| C01, C02 | `UseSpikeKit()` after the provider, and before it | SchemaKit outermost, the provider's own differ and generator inside, the declared object in `GenerateCreateScript()` |
| C03 | No `UseSpikeKit()` | `InvalidOperationException` at the declaration, in `OnModelCreating` |
| C04 | `HasDifferences` | True for a model with a declaration, false for the model against itself |
| C05 | Another package's `ReplaceService<TService, X>()` before | X is the inner; its operations and SchemaKit's are both in the script |
| C06 | The same, after | Refused when the context is **constructed** (that is when EF validates the options) |
| C07, C08 | `ReplaceService<TService, MigrationsModelDiffer, X>()`, before and after | X is the inner |
| C09, C10 | A differ registered in another extension's `ApplyServices`, by type and by factory, after the kit and before the provider | It is the inner |
| C11 to C13 | `AddDbContext`, `AddDbContextPool` (the instance reused), `AddDbContextFactory` | As C01, on every scope and instance |
| C14 | `UseInternalServiceProvider` with `AddEntityFrameworkSpikeKit()` on the collection | As C01 |
| C15 | The same without it | Refused when the context is constructed |
| C16 | `UseSpikeKit()` called before `UseInternalServiceProvider()` | Refused by EF itself: `ReplaceService` beside a caller-owned provider |
| C17 | Second context of the same type and options | The check ran once; the decorators are there both times |
| C18 | A model cache shared between options with and without the kit | **Silent.** The context without the kit gets the stamped model, nothing throws, and its script lacks the object |
| C19 | `UseSpikeKit()` twice | As once |
| C20 | A registration added to a caller-owned collection after the kit's | Refused when the model is built |
| C21 | `ReplaceService<TService, SpikeDiffer, X>()`, keyed on the kit's own type | Refused when the model is built |

## What it found beyond the question

- **Resolving both services inside a model-initialized convention does not cycle** on any provider.
- **`Validate` fires at context construction,** earlier than the model. A displaced registration that
  shows in the options is refused there.
- **A later service-only `ReplaceService` cannot be composed with.** EF keeps one entry per service;
  the later call overwrites SchemaKit's. It is refused, with the order to use.
- **C18 is a fourth residual** no plugin can guard. It is in the plan beside the other three.
- **A test suite that varies the wiring trips `ManyServiceProvidersCreatedWarning`,** which EF raises
  as an error at the twenty-first internal service provider. The kernel's tests will need the same
  `ConfigureWarnings` line as `Setup.Use` here.
- **`EF1001` is an error in this project.** `SpikeKit/` compiles under it; only `OtherPackage/` and
  the one condition that plays a stranger disable it.

## What it does not cover

- Design time (`dotnet ef`), compiled models, bundles: spike B, #17.
- A live database. `GenerateCreateScript()` and `EnsureCreated()` share the differ and generator
  path, and only the first is run here.
