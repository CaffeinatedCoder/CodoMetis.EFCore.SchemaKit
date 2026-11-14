using CodoMetis.EFCore.SchemaKit;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Smoke;

/// <summary>
/// How every consumer of the smoke test registers its database, and the three ways the script takes
/// a registration away. Shared by the consumers so that a removal reaches every path of each.
/// </summary>
/// <remarks>
/// <c>SMOKE_WIRING</c> is set by one row of the script: <c>package</c> (the default) is
/// <c>UseNpgsql(cs, o => o.UseSchemaKit())</c>; <c>none</c> leaves SchemaKit out; <c>kernel</c> is
/// the kernel's <c>UseSchemaKit()</c> on the outer builder, which compiles as soon as both
/// namespaces are imported. <c>SMOKE_BREAK</c> applies the same removal to the whole run, which is
/// how the script is shown to fail without each registration (<c>no-schemakit</c>,
/// <c>kernel-only</c>, <c>user-differ</c>).
/// </remarks>
public static class Wiring
{
    // A row that sets SMOKE_WIRING asks for a refusal and keeps it under SMOKE_BREAK: the break is
    // for the paths that expect SchemaKit to be there.
    private static string? Explicit => Environment.GetEnvironmentVariable("SMOKE_WIRING");

    private static string? Break => Explicit is null ? Environment.GetEnvironmentVariable("SMOKE_BREAK") : null;

    public static string Kind => Explicit ?? Break switch
    {
        "no-schemakit" => "none",
        "kernel-only" => "kernel",
        _ => "package",
    };

    public static bool UserDiffer
        => Break == "user-differ" || Environment.GetEnvironmentVariable("SMOKE_USER_DIFFER") == "1";

    public static string Connection => Environment.GetEnvironmentVariable("SMOKE_DB")
        ?? throw new InvalidOperationException("SMOKE_DB is not set.");

    public static TBuilder UsePostgres<TBuilder>(this TBuilder builder, Action<NpgsqlDbContextOptionsBuilder>? npgsql = null)
        where TBuilder : DbContextOptionsBuilder
    {
        switch (Kind)
        {
            case "package":
                builder.UseNpgsql(Connection, o =>
                {
                    o.UseSchemaKit();
                    npgsql?.Invoke(o);
                });
                break;
            case "none":
                builder.UseNpgsql(Connection, o => npgsql?.Invoke(o));
                break;
            case "kernel":
                builder.UseNpgsql(Connection, o => npgsql?.Invoke(o)).UseSchemaKit();
                break;
            default:
                throw new InvalidOperationException($"Unknown wiring '{Kind}'.");
        }

        return builder;
    }
}

#pragma warning disable EF1001
/// <summary>
/// A consumer's own design-time services that register a differ (SMOKE_USER_DIFFER=1). EF Core adds
/// them last, so this differ, not the context's, scaffolds the migration. SchemaKit cannot guard
/// that (design/schemakit-core-plan.md, section 3.2): it is the control that shows the checks of the
/// script look at the database, since they must see the constraint missing here.
/// </summary>
public sealed class UserDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
    {
        if (Wiring.UserDiffer)
            services.AddScoped<IMigrationsModelDiffer, Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer>();
    }
}
#pragma warning restore EF1001
