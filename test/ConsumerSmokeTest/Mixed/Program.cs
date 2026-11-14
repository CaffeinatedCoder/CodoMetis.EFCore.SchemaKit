using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;

// The registration alone: it is where the PostgreSQL package asks the kernel for its version, and
// no server is reached. With the check the process ends on SchemaKit's message; without it this
// prints REGISTERED, or dies of a MissingMethodException at the first member that differs.
new DbContextOptionsBuilder().UseNpgsql("Host=localhost", o => o.UseSchemaKit());
Console.WriteLine("REGISTERED");
