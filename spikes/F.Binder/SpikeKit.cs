using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace SchemaKit.Spikes.F;

// What the kernel would do, in miniature. A declaration is a condition and nothing else: held on
// the entity type as an object from the call on, and replaced by its text when the model is
// finalized.

/// <summary>A declaration as it is between the call and finalization: a name and a lambda.</summary>
public sealed record Pending(string? Name, LambdaExpression Condition);

public static class SpikeKit
{
    public const string Prefix = "Spike:Declared:";
    private const string Unbound = Prefix + "~";

    public static DbContextOptionsBuilder UseSpikeKit(this DbContextOptionsBuilder options)
    {
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new SpikeOptionsExtension());
        return options;
    }

    /// <summary>
    /// Holds the condition on the entity type, unbound. One that is already there, by its name or
    /// by what it says, is refused here, at the call.
    /// </summary>
    /// <param name="snapshotCaptures">False leaves a captured variable in the tree, to be read at finalization: the control of question 6.</param>
    public static EntityTypeBuilder<T> Declare<T>(this EntityTypeBuilder<T> entity, Expression<Func<T, bool>> condition, string? name = null, bool snapshotCaptures = true)
        where T : class
    {
        LambdaExpression lambda = snapshotCaptures ? (LambdaExpression)new CapturedValues().Visit(condition) : condition;
        var declared = new Pending(name, lambda);

        foreach (var (_, earlier) in PendingOf(entity.Metadata))
        {
            if (Same(earlier, declared))
                throw new InvalidOperationException($"Declared twice on {entity.Metadata.DisplayName()}: {Describe(declared)}");
        }

        // In the order they were written, so that a message names them in that order.
        entity.Metadata.SetAnnotation(Unbound + PendingOf(entity.Metadata).Count().ToString("D8", System.Globalization.CultureInfo.InvariantCulture), declared);
        return entity;
    }

    /// <summary>The one replacing path, before finalization: each unbound declaration becomes what <paramref name="amend" /> returns.</summary>
    public static int Redeclare(this IMutableEntityType entityType, Func<Pending, Pending> amend)
    {
        var changed = 0;
        foreach (var (key, pending) in PendingOf(entityType).ToList())
        {
            var amended = amend(pending);
            if (ReferenceEquals(amended, pending)) continue;
            entityType.SetAnnotation(key, amended);
            changed++;
        }

        return changed;
    }

    /// <summary>What is declared on the entity type: unbound declarations before finalization, texts after.</summary>
    public static IReadOnlyList<object> Declared(this IReadOnlyEntityType entityType)
        => [.. entityType.GetAnnotations().Where(a => a.Name.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => a.Value!)];

    public static Expression<Func<T, bool>> And<T>(LambdaExpression left, Expression<Func<T, bool>> right)
    {
        var body = new Reparent(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(left.Body, body), left.Parameters[0]);
    }

    internal static IEnumerable<(string Key, Pending Declaration)> PendingOf(IReadOnlyEntityType entityType)
        => entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith(Unbound, StringComparison.Ordinal) && a.Value is Pending)
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => (a.Name, (Pending)a.Value!));

    internal static string Describe(Pending pending) => pending.Name is { } name ? $"'{name}'" : pending.Condition.ToString();

    internal static string FinalKey(Pending pending, string text)
        => Prefix + (pending.Name ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]);

    // The logical declaration: a name, or the tree itself. EF Core's comparer compares constants by
    // value, which is why the captured values have to be constants by now.
    private static bool Same(Pending left, Pending right)
        => left.Name is not null || right.Name is not null
            ? left.Name == right.Name
            : ExpressionEqualityComparer.Instance.Equals(left.Condition, right.Condition);

    /// <summary>A member read off a closure becomes the value it has now.</summary>
    private sealed class CapturedValues : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
            => node.Expression is ConstantExpression
                ? Expression.Constant(Expression.Lambda(node).Compile().DynamicInvoke(), node.Type)
                : base.VisitMember(node);
    }

    private sealed class Reparent(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>What the finalizing convention saw of one property.</summary>
public sealed record Seen(string Property, bool Nullable, string? Converter, bool MappingOnProperty, string? StoreType, string? Column);

/// <summary>What a test reads back: keyed by the scenario the context was built for.</summary>
public sealed class Scenario(Action<ModelBuilder> build)
{
    /// <summary>What a convention of another package does to the model after the binding, or null.</summary>
    public Action<IConventionModelBuilder>? Late { get; init; }

    public Action<ModelBuilder> Build { get; } = build;
    public List<Seen> Seen { get; } = [];
    public int Finalized { get; set; }
}

internal sealed class SpikeOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) => services.AddScoped<IConventionSetPlugin, SpikePlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
        }
    }
}

internal sealed class SpikePlugin(IServiceProvider services) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        // Last: after every convention of EF Core's and the provider's that runs at finalization.
        conventionSet.ModelFinalizingConventions.Add(new BindingConvention(services));

        // Another package's plugin, registered after this one, would add its conventions here.
        conventionSet.ModelFinalizingConventions.Add(new LateConvention(services));

        // First: the model as it was finalized, before anything is made from it.
        conventionSet.ModelFinalizedConventions.Insert(0, new VerifyingConvention());
        return conventionSet;
    }
}

/// <summary>Binds every unbound declaration and stores its text in the declaration's place.</summary>
internal sealed class BindingConvention(IServiceProvider services) : IModelFinalizingConvention
{
    /// <summary>What was bound, by model, so that the finalized model can be asked again.</summary>
    internal static readonly ConditionalWeakTable<object, List<(string EntityType, string Key, Pending Declaration)>> Bound = new();

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        var kept = Bound.GetOrCreateValue(modelBuilder.Metadata);
        var scenario = (services.GetRequiredService<ICurrentDbContext>().Context as SpikeContext)?.Scenario;
        var mappings = services.GetRequiredService<IRelationalTypeMappingSource>();
        if (scenario is not null) scenario.Finalized++;

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            var mutable = (IMutableEntityType)entityType;

            foreach (var property in entityType.GetProperties())
            {
                scenario?.Seen.Add(new Seen(
                    property.Name,
                    property.IsNullable,
                    (property.FindTypeMapping()?.Converter ?? property.GetValueConverter())?.GetType().Name,
                    property.FindTypeMapping() is not null,
                    property.GetColumnType(),
                    property.GetColumnName()));
            }

            Dictionary<string, (Pending Declaration, string Text)> bound = new(StringComparer.Ordinal);
            foreach (var (key, pending) in SpikeKit.PendingOf(mutable).ToList())
            {
                var text = new Binder(entityType, mappings).Bind(pending.Condition.Body);
                var final = SpikeKit.FinalKey(pending, text);

                // Two declarations that said different things at the call and say one thing now.
                if (bound.TryGetValue(final, out var earlier))
                    throw new InvalidOperationException(
                        $"Two declarations on {entityType.DisplayName()} are one object: {SpikeKit.Describe(earlier.Declaration)} and {SpikeKit.Describe(pending)} are both {text}");

                bound.Add(final, (pending, text));
                kept.Add((entityType.Name, final, pending));
                mutable.RemoveAnnotation(key);
            }

            foreach (var (final, (_, text)) in bound) mutable.SetAnnotation(final, text);
        }
    }
}

/// <summary>Stands for a convention that runs after the binding and changes what it read.</summary>
internal sealed class LateConvention(IServiceProvider services) : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        => (services.GetRequiredService<ICurrentDbContext>().Context as SpikeContext)?.Scenario.Late?.Invoke(modelBuilder);
}

/// <summary>
/// Binds every declaration a second time, against the finalized model, whose properties hold their
/// mappings. A text that comes out differently was bound from a fact that changed afterwards.
/// </summary>
internal sealed class VerifyingConvention : IModelFinalizedConvention
{
    public IModel ProcessModelFinalized(IModel model)
    {
        if (!BindingConvention.Bound.TryGetValue(model, out var kept)) return model;

        foreach (var (entityTypeName, key, pending) in kept)
        {
            var entityType = model.FindEntityType(entityTypeName)!;
            var again = new Binder(entityType, mappings: null).Bind(pending.Condition.Body);
            var stored = (string?)entityType.FindAnnotation(key)?.Value;
            if (again != stored)
                throw new InvalidOperationException(
                    $"The model changed after {SpikeKit.Describe(pending)} on {entityType.DisplayName()} was bound: stored {stored}, now {again}");
        }

        return model;
    }
}

/// <summary>
/// A translator for as much as the questions need: a comparison of a property with a value, a
/// boolean property, its negation, and both of two conditions. What it shows is where each fact
/// comes from, not how a lambda is spelled.
/// </summary>
internal sealed class Binder(IReadOnlyEntityType entityType, IRelationalTypeMappingSource? mappings)
{
    public string Bind(Expression expression) => expression switch
    {
        BinaryExpression { NodeType: ExpressionType.AndAlso } both
            => $"({Bind(both.Left)}) AND ({Bind(both.Right)})",
        BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } comparison
            => Compare(Property(comparison.Left), Value(comparison.Right), comparison.NodeType == ExpressionType.Equal),
        UnaryExpression { NodeType: ExpressionType.Not } not
            => Compare(Property(not.Operand), false, equal: true),
        MemberExpression member
            => Compare(Property(member), true, equal: true),
        _ => throw new NotSupportedException(expression.ToString()),
    };

    private string Compare(IReadOnlyProperty property, object? value, bool equal)
    {
        // The mapping is the declared property's. The model is not finalized yet, so the mapping is
        // asked of the source when the property does not hold one.
        var mapping = (RelationalTypeMapping)(property.FindTypeMapping() ?? mappings?.FindMapping((IProperty)property)
            ?? throw new InvalidOperationException($"No mapping for {property.Name}"));

        // The literal is the stored value: the mapping converts it. A compiler hands an enum over
        // as its number, which the mapping takes back to the enum.
        if (value is not null && property.ClrType.IsEnum && value.GetType() != property.ClrType)
            value = Enum.ToObject(property.ClrType, value);
        var literal = mapping.GenerateSqlLiteral(value);

        // Nullability is the column's, as the finalized model has it, not the C# annotation's.
        return (equal, property.IsNullable) switch
        {
            (true, _)      => $"{{{property.Name}}} = {literal}",
            (false, false) => $"{{{property.Name}}} <> {literal}",
            (false, true)  => $"{{{property.Name}}} <> {literal} OR {{{property.Name}}} IS NULL",
        };
    }

    private IReadOnlyProperty Property(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } converted)
            expression = converted.Operand;

        return expression is MemberExpression { Expression: ParameterExpression } member
               && entityType.FindProperty(member.Member.Name) is { } property
            ? property
            : throw new NotSupportedException(expression.ToString());
    }

    private static object? Value(Expression expression) => Expression.Lambda(expression).Compile().DynamicInvoke();
}

/// <summary>A context per scenario and provider. The model is not cached across scenarios.</summary>
public sealed class SpikeContext(string provider, Scenario scenario) : DbContext
{
    public Scenario Scenario { get; } = scenario;
    public string Provider { get; } = provider;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        _ = Provider switch
        {
            "npgsql"    => options.UseNpgsql("Host=unused"),
            "sqlserver" => options.UseSqlServer("Server=unused"),
            "sqlite"    => options.UseSqlite("Data Source=unused"),
            _           => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        options.UseSpikeKit().ReplaceService<IModelCacheKeyFactory, PerScenario>();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) => configurationBuilder.Properties<Email>().HaveConversion<EmailConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => Scenario.Build(modelBuilder);

    private sealed class PerScenario : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (((SpikeContext)context).Scenario, ((SpikeContext)context).Provider, designTime);
    }
}
