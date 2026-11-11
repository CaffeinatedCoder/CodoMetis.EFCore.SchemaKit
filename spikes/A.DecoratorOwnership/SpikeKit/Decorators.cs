using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
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

/// <summary>The differ decorator: the inner differ's operations, then one statement per declaration.</summary>
public sealed class SpikeDiffer(IServiceProvider services, IDbContextOptions options, InnerServices inner) : IMigrationsModelDiffer
{
    public IMigrationsModelDiffer Inner { get; } =
        inner.Create<IMigrationsModelDiffer>(services, options, options.FindExtension<SpikeOptionsExtension>()?.InnerDiffer);

    public bool HasDifferences(IRelationalModel? source, IRelationalModel? target)
        => GetDifferences(source, target).Count > 0;

    public IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = Inner.GetDifferences(source, target).ToList();
        var before     = Names(source);
        operations.AddRange(Names(target).Except(before).Select(name => new SqlOperation { Sql = $"/* spike:{name} */ SELECT 1" }));
        return operations;
    }

    private static HashSet<string> Names(IRelationalModel? model)
        => model is null
            ? []
            : [.. model.Model.GetEntityTypes().Select(e => e.FindAnnotation(SpikeKitExtensions.Declared)?.Value).OfType<string>()];
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
