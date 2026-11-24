# Spike F: bind a declaration when the model is finalized

For [#177](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/177), which asks
the questions [#114](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/114)
needs answered before it is decided. Throwaway code: it leaves when the kernel's binder replaces it.

**Question.** A lambda is translated today where it is written, with no model at hand. Can it be
held until the model is finalized and bound there, with the column's nullability and the property's
converter, so that a snapshot holds text and is never translated again?

**Answer: yes,** on Npgsql, SQL Server and SQLite, with public API only. 45 tests pass (15 on each
provider). Each mechanism was removed in turn in a scratch copy, and the tests that depend on it
failed.

```bash
dotnet test spikes/F.Binder/F.Binder.csproj
```

No database is contacted: models are built, and snapshots and compiled models scaffolded, offline
on EF Core 11.0.0 (2026-11-24). `SpikeKit.cs` is the kernel in miniature. Its `Binder` translates
a comparison of a property with a value and nothing more: what the spike shows is where each fact
comes from, not how a lambda is spelled.

## What was measured

| | Question | Answer |
|---|---|---|
| Q1 | What a convention sees when the model is finalized, added last by an `IConventionSetPlugin` | A property's final nullability and its column name. **No type mapping and no store type:** `FindTypeMapping()` and `GetColumnType()` are null for every property. A converter is on the property only when it was set as one (`HasConversion(converter)`, `HaveConversion<T>()` in `ConfigureConventions`); after `HasConversion<string>()` the property holds none |
| Q1 | Where the literal comes from, then | `IRelationalTypeMappingSource.FindMapping(property)`, asked of the declared property. Its `GenerateSqlLiteral` writes the stored value: an enum as its number or its name, a value object through its converter, a `bool` stored as text as `'Y'`, a `DateTime` and a `Guid` as the provider writes them |
| Q1 | A convention that runs later and changes what was read | Not seen by the binding. Seen by binding a second time on the finalized model, whose properties hold their mappings: the two texts differ, and the model is refused |
| Q2 | What holds the declaration after finalization | Text alone: in the design-time model, in the runtime model (another instance, from the same one model build), in a scaffolded snapshot and in a scaffolded compiled model |
| Q2 | What a snapshot does not hold | The converter: `Active` is a `string` property there. A literal can be converted only before it is stored |
| Q3 | Is a snapshot bound again? | No. The convention does not run when a snapshot's model is finalized for a diff, and the diff against the model it was scaffolded from is empty both ways |
| Q4 | The same declaration twice | Refused at the call, in `OnModelCreating`: by its name, or by its tree. EF Core's `ExpressionEqualityComparer` compares constants by value, so `x.Tag != "blocked"` and `x.Tag != tag` are one declaration once the captured local is a constant |
| Q4 | Two lambdas that differ at the call and bind to one text (`x.Active`, `x.Active == true`) | Accepted at the call, refused at finalization with a message that names both, in the order they were written. That order has to be kept: a key made from a random value named them in either order |
| Q5 | Amending before finalization | The read model hands back the declarations as they were written, and an amended one is bound as amended. After finalization there is nothing unbound to amend, and the model takes no annotation |
| Q6 | A captured local that changes after the call | Read at the call when the tree is rewritten there. Left in the tree, the declaration holds the value the local has when the model is finalized |

## What it takes

| Mechanism | In `SpikeKit.cs` | Without it |
|---|---|---|
| The mapping is asked of the mapping source | `Binder.Compare` | Every test that builds a model fails: a property holds no mapping at finalization |
| Nullability is the property's in the model | `Binder.Compare` | With the C# annotation in its place, the literals of Q1 fail: `Tag` loses its `OR … IS NULL` |
| The declaration is bound again on the finalized model | `VerifyingConvention` | The late change of Q1 is accepted |
| Captured values become constants at the call | `SpikeKit.CapturedValues` | Q6 fails, and so does Q4 at the call: two trees that read different closures are not equal |
| The logical declaration is compared at the call | `SpikeKit.Same` | Both refusals of Q4 at the call fail |
| The bound texts are compared at finalization | `BindingConvention` | Q4's two lambdas become one annotation, and the second replaces the first without a word |

## What it does not show

- The real translator, split into what is checked at the call and what is bound at finalization.
- A path through a complex type, into a JSON document, or to a column two types share.
- `dotnet ef` out of process and a packed consumer: the design-time services are built in the test
  process.
- A provider's package handing its spelling to the binder.
