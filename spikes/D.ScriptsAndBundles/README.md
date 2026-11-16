# Spike D: SqlOperations in idempotent scripts and bundles

For [#19](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/19), and for point 4
of [#115](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/115). Throwaway
code: it leaves when the consumer smoke test holds the same cells for the real contributors.

**Question 1.** Every object SchemaKit creates reaches a migration as a `SqlOperation` rendered at
design time. Does one that suppresses the transaction (`CREATE INDEX CONCURRENTLY` on PostgreSQL,
`CREATE INDEX … WITH (ONLINE = ON)` on SQL Server) and a trigger (with its function on PostgreSQL;
on SQL Server, where `CREATE TRIGGER` must start its batch) survive
`dotnet ef migrations script --idempotent`, the script applied twice, and a migrations bundle, run
twice?

**Question 2.** What does EF Core do with an annotation on a `SqlOperation`: does `migrations add`
write it into the migration, is it there when the SQL generator sees the operation, and does it
survive a script and a bundle?

The table is being measured; this file is not finished.

```bash
SPIKE_D_PG16="Host=localhost;Port=54316;Username=postgres;Password=…;Database=postgres" \
SPIKE_D_PG18="Host=localhost;Port=54318;Username=postgres;Password=…;Database=postgres" \
SPIKE_D_MSSQL="Server=localhost,14333;User Id=sa;Password=…;TrustServerCertificate=True" \
spikes/D.ScriptsAndBundles/run.sh
```
