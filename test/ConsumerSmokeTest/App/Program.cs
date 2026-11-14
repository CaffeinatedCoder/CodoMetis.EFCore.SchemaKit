using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shop;
using Smoke;

// A host, as an application has one. `dotnet ef` runs this far, takes the services of the host it
// sees built and stops the program, so every design-time command goes through the registration
// SMOKE_REGISTRATION names: AddDbContext (the default), AddDbContextPool or AddDbContextFactory.
var builder = Host.CreateApplicationBuilder(args);
var registration = Environment.GetEnvironmentVariable("SMOKE_REGISTRATION") ?? "context";
switch (registration)
{
    case "context":
        builder.Services.AddDbContext<ShopContext>(o => o.UsePostgres());
        break;
    case "pool":
        builder.Services.AddDbContextPool<ShopContext>(o => o.UsePostgres());
        break;
    case "factory":
        builder.Services.AddDbContextFactory<ShopContext>(o => o.UsePostgres());
        break;
    default:
        throw new InvalidOperationException($"Unknown registration '{registration}'.");
}

using var host = builder.Build();
using var scope = host.Services.CreateScope();

// What the database holds is judged by the Judge project, which knows nothing of SchemaKit. These
// commands only drive the consumer's own runtime paths.
using var context = registration == "factory"
    ? scope.ServiceProvider.GetRequiredService<IDbContextFactory<ShopContext>>().CreateDbContext()
    : scope.ServiceProvider.GetRequiredService<ShopContext>();

switch (args.FirstOrDefault())
{
    case "migrate":
        context.Database.Migrate();
        break;

    case "ensure-created":
        context.Database.EnsureCreated();
        break;

    // Which model the context runs on: the compiled one after `dbcontext optimize`.
    case "model":
        Console.WriteLine($"MODEL: {context.Model.GetType().FullName}");
        break;

    default:
        Console.Error.WriteLine("usage: migrate | ensure-created | model");
        return 2;
}

Console.WriteLine($"DONE: {args[0]} through {registration}");
return 0;
