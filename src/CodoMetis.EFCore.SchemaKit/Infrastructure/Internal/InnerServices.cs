using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

/// <summary>
/// Finds and builds the service a decorator wraps, without naming an EF-internal type.
/// </summary>
/// <remarks>
/// By the time a decorator is constructed, EF Core has rewritten every registration of its service
/// to the decorator's type, so the collection no longer says what was there. This replays every
/// other extension's <c>ApplyServices</c> into a scratch collection and takes the last registration:
/// what the context would have had without SchemaKit. It is one replay per service and internal
/// service provider.
/// </remarks>
internal sealed class InnerServices
{
    private readonly ConcurrentDictionary<Type, ServiceDescriptor> _descriptors = new();

    /// <summary>A caller-owned collection: the registration that is last now is the inner service.</summary>
    public void Capture<TService>(IServiceCollection services)
        => _descriptors[typeof(TService)] = services.LastOrDefault(d => d.ServiceType == typeof(TService))
           ?? throw new InvalidOperationException(SchemaKitStrings.NoInnerRegistration(typeof(TService)));

    public TService Create<TService>(IServiceProvider services, IDbContextOptions options, Type? earlierReplacement)
        where TService : class
    {
        var descriptor = _descriptors.GetOrAdd(typeof(TService), static (_, o) => Replay<TService>(o), options);
        var type       = descriptor.ImplementationType;

        // EF Core's own order: a replacement keyed on the registered type first, else the one keyed
        // on the service alone, which is the entry UseSchemaKit() took over.
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
            if (extension is not SchemaKitOptionsExtension)
                extension.ApplyServices(scratch);

        return scratch.LastOrDefault(d => d.ServiceType == typeof(TService))
               ?? throw new InvalidOperationException(SchemaKitStrings.NoInnerRegistration(typeof(TService)));
    }
}
