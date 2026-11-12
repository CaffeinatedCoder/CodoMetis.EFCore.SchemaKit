# Spike E: what EF Core's operations do to an index, an exclusion constraint and a trigger

For [#20](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/20). Throwaway code:
it leaves when the oracles on live servers (#32) hold the same cells for the real contributors.

**Question.** For an object that depends on a column or a table, what does each operation of EF
Core's on that column or table do: `RenameColumn`, `AlterColumn`, `DropColumn`, `RenameTable`, a
schema move, `DropTable`?

**Answer.** The table in [the plan, section 3.7](../../design/schemakit-core-plan.md#37-dependencies-and-impact),
measured on PostgreSQL 16 and 18 and SQL Server 2022. The four answers of the contract are not
enough: a trigger whose body reads a column survives its rename and its drop in the catalog and
fails when it next fires. The spike calls that `Stale`.

```bash
docker run -d --name sk-e-pg16 -e POSTGRES_PASSWORD=sk -p 54316:5432 postgres:16
docker run -d --name sk-e-pg18 -e POSTGRES_PASSWORD=sk -p 54318:5432 postgres:18
export SPIKE_E_PG16="Host=localhost;Port=54316;Username=postgres;Password=sk;Database=postgres"
export SPIKE_E_PG18="Host=localhost;Port=54318;Username=postgres;Password=sk;Database=postgres"
dotnet test spikes/E.ImpactTable/E.ImpactTable.csproj
```

`SPIKE_E_MSSQL` is a SqlClient connection string for SQL Server 2022. Without a variable its cells
go inconclusive; when `CI` is set they fail. The SQL Server image is amd64 only and does not start
under emulation on an ARM machine without Rosetta, so those cells were measured in CI.

Each server gets a database `spike_e`, dropped and made anew on every run.

## How a cell is measured

1. A schema of its own, with a table `t (id, a, b, …)` and one object named `o` that depends on `a`.
2. The operation is built as EF Core's `MigrationOperation` and turned into SQL by the provider's
   own `IMigrationsSqlGenerator`, with no model. Those statements run, one at a time.
3. The catalog is read, by the table's identity and the object's name:

| Outcome | What was observed |
|---|---|
| `Blocks` | A statement failed. `Every_operation_succeeds_on_a_table_nothing_depends_on` shows the same operation succeeds without the object, so the refusal is the object's |
| `Lost` | The operation succeeded and the object is gone |
| `Stale` | The object is in the catalog, and inserting and updating a row then fails, or a trigger no longer fires |
| `Follows` | The object is there and works, and its definition names the new column, table or schema |
| `Unaffected` | The object is there and works, with the definition it had |

`Print_the_table` measures every cell again and writes the table with the server's own message for
each refusal and the statements EF Core wrote; CI puts it into the job summary. `Expected.cs` is the
table as measured, and `Cell` fails when a server answers otherwise.
