using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace SchemaKit.Spikes.A.SpikeKit;

/// <summary>
/// Finds and builds the service a decorator wraps, without naming an EF-internal type: it replays
/// every other extension's <c>ApplyServices</c> into a scratch collection and takes the last
/// registration, which is what the context would have had without SpikeKit.
/// </summary>
public sealed class InnerServices
{
    private readonly ConcurrentDictionary<Type, ServiceDescriptor> _descriptors = new();

    /// <summary>The caller-owned collection: the last registration is the inner service.</summary>
    public void Capture<TService>(IServiceCollection services)
        => _descriptors[typeof(TService)] = services.LastOrDefault(d => d.ServiceType == typeof(TService))
           ?? throw new InvalidOperationException(
               $"No {typeof(TService).Name} is registered yet. Call AddEntityFrameworkSpikeKit() after the provider's AddEntityFramework…().");

    public TService Create<TService>(IServiceProvider services, IDbContextOptions options, Type? earlierReplacement)
        where TService : class
    {
        var descriptor = _descriptors.GetOrAdd(typeof(TService), _ => Replay<TService>(options));
        var type       = descriptor.ImplementationType;

        // EF's own order: a replacement keyed on the registered type first, else the one keyed on
        // the service alone, which is the entry SpikeKit took over.
        var replaced = options.FindExtension<CoreOptionsExtension>()?.ReplacedServices;
        if (type is not null && replaced is not null && replaced.TryGetValue((typeof(TService), type), out var specific))
            type = specific;
        else if (earlierReplacement is not null)
            type = earlierReplacement;

        if (type is not null)
            return (TService)ActivatorUtilities.CreateInstance(services, type);
        if (descriptor.ImplementationFactory is { } factory)
            return (TService)factory(services);
        return (TService)descriptor.ImplementationInstance!;
    }

    private static ServiceDescriptor Replay<TService>(IDbContextOptions options)
    {
        var scratch = new ServiceCollection();
        foreach (var extension in options.Extensions)
            if (extension is not SpikeOptionsExtension)
                extension.ApplyServices(scratch);

        return scratch.LastOrDefault(d => d.ServiceType == typeof(TService))
               ?? throw new InvalidOperationException($"No extension on the options registers {typeof(TService).Name}. Is a relational provider configured?");
    }
}

/// <summary>
/// The differ decorator: the inner differ's operations, with a real statement per declaration
/// spliced around them, so a path can be judged by what the database ends up holding.
/// </summary>
public sealed class SpikeDiffer(IServiceProvider services, IDbContextOptions options, InnerServices inner) : IMigrationsModelDiffer
{
    private readonly ISqlGenerationHelper _sql = services.GetRequiredService<ISqlGenerationHelper>();
    private readonly bool _dropNeedsTable = services.GetRequiredService<IDatabaseProvider>().Name.Contains("SqlServer");

    public IMigrationsModelDiffer Inner { get; } =
        inner.Create<IMigrationsModelDiffer>(services, options, options.FindExtension<SpikeOptionsExtension>()?.InnerDiffer);

    public bool HasDifferences(IRelationalModel? source, IRelationalModel? target)
        => GetDifferences(source, target).Count > 0;

    public IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var before = Declarations(source);
        var after  = Declarations(target);

        // Drops before the inner differ's operations, creates after them: 5.x's order. Each statement
        // ends with the provider's terminator: EF runs a SqlOperation as it is, so `database update`,
        // a bundle and Migrate() accept one without, and a generated script does not apply (spike B).
        List<MigrationOperation> operations = [];
        foreach (var (name, table) in before.Where(d => !after.ContainsKey(d.Key)))
            operations.Add(new SqlOperation
            {
                Sql = _dropNeedsTable
                    ? $"DROP INDEX {table.Index} ON {table.Table} /* spike:{name} */{_sql.StatementTerminator}"
                    : $"DROP INDEX {table.Index} /* spike:{name} */{_sql.StatementTerminator}",
            });
        operations.AddRange(Inner.GetDifferences(source, target));
        foreach (var (name, table) in after.Where(d => !before.ContainsKey(d.Key)))
            operations.Add(new SqlOperation { Sql = $"CREATE INDEX {table.Index} ON {table.Table} ({table.Column}) /* spike:{name} */{_sql.StatementTerminator}" });
        return operations;
    }

    /// <summary>Declared name to its index (<c>spike_</c> plus the name), table and first non-key column, all delimited.</summary>
    private Dictionary<string, (string Index, string Table, string Column)> Declarations(IRelationalModel? model)
    {
        Dictionary<string, (string, string, string)> found = [];
        if (model is null) return found;

        foreach (var entity in model.Model.GetEntityTypes())
        {
            if (entity.FindAnnotation(SpikeKitExtensions.Declared)?.Value is not string name) continue;
            var table  = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
            var column = entity.GetProperties().First(p => !p.IsPrimaryKey()).GetColumnName(table)!;
            found[name] = (_sql.DelimitIdentifier($"spike_{name}"), _sql.DelimitIdentifier(table.Name, table.Schema), _sql.DelimitIdentifier(column));
        }

        return found;
    }
}

/// <summary>The generator decorator: hands the whole list to the inner generator.</summary>
public sealed class SpikeGenerator(IServiceProvider services, IDbContextOptions options, InnerServices inner) : IMigrationsSqlGenerator
{
    public IMigrationsSqlGenerator Inner { get; } =
        inner.Create<IMigrationsSqlGenerator>(services, options, options.FindExtension<SpikeOptionsExtension>()?.InnerGenerator);

    public int Calls { get; private set; }

    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        Calls++;
        return Inner.Generate(operations, model, options);
    }
}
