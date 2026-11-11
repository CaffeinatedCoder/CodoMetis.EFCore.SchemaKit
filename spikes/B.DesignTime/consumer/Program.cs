using Consumer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using SchemaKit.Spikes.A.SpikeKit;
#if HOST
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// `dotnet ef` runs this far, takes the services of the host it sees built, and stops the program.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDbContext<SpikeContext>(options => Config.Apply(options));
using var host = builder.Build();
using var scope = host.Services.CreateScope();
var context = scope.ServiceProvider.GetRequiredService<SpikeContext>();
#else
var options = new DbContextOptionsBuilder<SpikeContext>();
Config.Apply(options);
using var context = new SpikeContext(options.Options);
#endif

switch (args.FirstOrDefault())
{
    case "differ":
        var differ    = context.GetService<IMigrationsModelDiffer>();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        Console.WriteLine($"DIFFER: {differ.GetType().Name}>{(differ as SpikeDiffer)?.Inner.GetType().Name}");
        Console.WriteLine($"GENERATOR: {generator.GetType().Name}>{(generator as SpikeGenerator)?.Inner.GetType().Name}");
        return 0;

    case "create-script":
        Console.WriteLine(context.Database.GenerateCreateScript());
        return 0;

    case "ensure-created":
        context.Database.EnsureDeleted();
        context.Database.EnsureCreated();
        return Indexes();

    case "migrate":
        context.Database.EnsureDeleted();
        try
        {
            context.Database.Migrate();
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"MIGRATE-REFUSED: {e.Message.Split('\n')[0]}");
            return 3;
        }

        return Indexes();

    case "check":
        return Indexes();

    // A generated script, run against an empty database as a DBA would run it: args are the file
    // and how many times. Text that "contains the index" proves nothing; this does.
    case "apply":
        context.Database.EnsureDeleted();
        context.GetService<IRelationalDatabaseCreator>().Create();
        var script = File.ReadAllText(args[1]);
        var target = context.Database.GetDbConnection();
        target.Open();
        for (var i = 0; i < int.Parse(args[2]); i++)
        {
            using var batch = target.CreateCommand();
            batch.CommandText = script;
            try
            {
                batch.ExecuteNonQuery();
            }
            catch (System.Data.Common.DbException e)
            {
                Console.WriteLine($"APPLY-FAILED: {e.Message.Split('\n')[0]}");
                return 4;
            }
        }

        return Indexes();

    case "drop":
        context.Database.EnsureDeleted();
        return 0;

    default:
        Console.Error.WriteLine("usage: differ | create-script | ensure-created | migrate | check | drop | apply <file> <times>");
        return 2;
}

// What the database holds, which is the only thing that counts.
int Indexes()
{
    var connection = context.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText =
#if SQLITE
        "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'spike!_%' ESCAPE '!' ORDER BY name";
#elif NPGSQL
        "SELECT indexname FROM pg_indexes WHERE indexname LIKE 'spike!_%' ESCAPE '!' ORDER BY indexname";
#else
        "SELECT name FROM sys.indexes WHERE name LIKE 'spike!_%' ESCAPE '!' ORDER BY name";
#endif
    List<string> names = [];
    using (var reader = command.ExecuteReader())
        while (reader.Read()) names.Add(reader.GetString(0));
    Console.WriteLine($"INDEXES: {string.Join(",", names)}");
    return 0;
}
