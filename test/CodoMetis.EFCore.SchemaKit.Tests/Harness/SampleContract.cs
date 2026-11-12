using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

// A contributor as a package outside SchemaKit would write it, against the public contract alone:
// its own closed declaration union with its codec, its descriptor, its contributor. The tests of
// the contract and of the declaration store are about the kernel, and this is what they hand it.

public sealed record GuardKey(string Column, bool Descending = false);

public sealed record GuardFilter(string Template);

/// <summary>
/// The declaration union. It has one of each thing the stored form has to carry: a list of
/// objects, a boolean whose default is not <c>false</c>, a list of strings, an optional number, an
/// optional nested object, and an operational flag.
/// </summary>
public closed record TestGuard : ISchemaDeclaration<TestGuard>
{
    public static string OwnerId => "Test.Guard";

    public string Owner => OwnerId;

    public string? Name { get; init; }

    public int Epoch { get; init; } = 1;

    public string Kind => this switch
    {
        Unique  => "unique",
        Trigger => "trigger",
    };

    public void Write(SchemaJsonWriter writer)
    {
        _ = this switch
        {
            Unique u  => WriteUnique(writer, u),
            Trigger t => WriteTrigger(writer, t),
        };
    }

    public static TestGuard Read(SchemaDeclarationHeader header, SchemaJsonReader reader) => header.Kind switch
    {
        "unique" => new Unique(reader.Array("keys", key => new GuardKey(key.String("column"), key.Boolean("descending"))))
        {
            Name = header.Name,
            Epoch = header.Epoch,
            NullsDistinct = reader.Boolean("nullsDistinct"),
            Include = reader.Strings("include"),
            FillFactor = reader.OptionalInt32("fillFactor"),
            Where = reader.OptionalObject("where", filter => new GuardFilter(filter.String("template"))),
            Concurrently = reader.Boolean("concurrently"),
        },
        "trigger" => new Trigger(reader.String("column")) { Name = header.Name, Epoch = header.Epoch },
        var kind  => throw new InvalidOperationException($"Test.Guard has no kind '{kind}'."),
    };

    private static bool WriteUnique(SchemaJsonWriter writer, Unique unique)
    {
        writer.Array("keys", unique.Keys, (key, value) =>
        {
            key.String("column", value.Column);
            key.Boolean("descending", value.Descending);
        });
        writer.Boolean("nullsDistinct", unique.NullsDistinct);
        writer.Strings("include", unique.Include);
        writer.OptionalInt32("fillFactor", unique.FillFactor);
        writer.OptionalObject("where", unique.Where, (filter, value) => filter.String("template", value.Template));
        writer.Operational.Boolean("concurrently", unique.Concurrently);
        return true;
    }

    private static bool WriteTrigger(SchemaJsonWriter writer, Trigger trigger)
    {
        writer.String("column", trigger.Column);
        return true;
    }

    public sealed record Unique(ValueList<GuardKey> Keys) : TestGuard
    {
        /// <summary>True by default, as in PostgreSQL: the value a writer that omitted defaults would lose.</summary>
        public bool NullsDistinct { get; init; } = true;

        public ValueList<string> Include { get; init; } = [];

        public int? FillFactor { get; init; }

        public GuardFilter? Where { get; init; }

        /// <summary>Operational: how the index is built, not what it is.</summary>
        public bool Concurrently { get; init; }
    }

    public sealed record Trigger(string Column) : TestGuard;
}

/// <summary>A declaration of a kind whose names the database compares without regard to case.</summary>
public sealed record LooseGuard(string Column) : ISchemaDeclaration<LooseGuard>
{
    public static string OwnerId => "Test.Loose";

    public string Owner => OwnerId;

    public string Kind => "loose";

    public string? Name { get; init; }

    public int Epoch { get; init; } = 1;

    public SchemaNameComparison NameComparison => SchemaNameComparison.IgnoreCase;

    public void Write(SchemaJsonWriter writer) => writer.String("column", Column);

    public static LooseGuard Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
        => new(reader.String("column")) { Name = header.Name, Epoch = header.Epoch };
}

public sealed record TestGuardDescriptor(
    TableRef Table,
    QualifiedName Name,
    ValueList<Identifier> Columns,
    bool IsTrigger,
    Operational<bool> Concurrently) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies => [.. Columns.Select(Dependency (column) => new Dependency.Column(Table, column))];

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

public sealed class TestGuardContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<TestGuard, TestGuardDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    /// <summary>What the next <see cref="Plan" /> answers in place of its own judgement, for the tests of the base class.</summary>
    public Func<SchemaSql, SchemaChange>? PlanOverride { get; set; }

    /// <summary>The same for <see cref="ImpactOf" />.</summary>
    public Func<BaseChange, Impact>? ImpactOverride { get; set; }

    /// <summary>Every change <see cref="ImpactOf" /> was asked about, in order.</summary>
    public List<BaseChange> Asked { get; } = [];

    protected override TestGuardDescriptor Resolve(TestGuard declaration, SchemaResolver resolver) => declaration switch
    {
        TestGuard.Unique unique => new(
            resolver.Table,
            resolver.Table.Qualify(resolver.Dialect.Identifier(unique.Name ?? $"ux_{string.Join("_", unique.Keys.Select(k => k.Column))}")),
            [.. unique.Keys.Select(k => SampleColumns.Of(resolver, k.Column))],
            IsTrigger: false,
            new Operational<bool>(unique.Concurrently)),
        TestGuard.Trigger trigger => new(
            resolver.Table,
            new QualifiedName(null, resolver.Dialect.Identifier(trigger.Name ?? $"tr_{trigger.Column}")),
            [SampleColumns.Of(resolver, trigger.Column)],
            IsTrigger: true,
            new Operational<bool>(false)),
    };

    protected override void Create(TestGuardDescriptor descriptor, SchemaSql sql)
    {
        if (descriptor.IsTrigger)
            sql.Statement($"CREATE TRIGGER {descriptor.Name.Name} ON {descriptor.Table} ({sql.List(descriptor.Columns)})");
        else
            sql.Statement($"CREATE UNIQUE INDEX {descriptor.Name.Name} ON {descriptor.Table} ({sql.List(descriptor.Columns)})");
    }

    protected override void Drop(TestGuardDescriptor descriptor, SchemaSql sql)
    {
        if (descriptor.IsTrigger)
            sql.Statement($"DROP TRIGGER {descriptor.Name.Name} ON {descriptor.Table}");
        else
            sql.Statement($"DROP INDEX {descriptor.Name}");
    }

    protected override SchemaChange Plan(TestGuardDescriptor from, TestGuardDescriptor to, SchemaSql sql)
    {
        if (PlanOverride is not null) return PlanOverride(sql);
        if (!from.WithName(to.Name).Equals(to)) return new SchemaChange.Rebuild();

        sql.Statement($"ALTER INDEX {from.Name} RENAME TO {to.Name.Name}");
        return new SchemaChange.InPlace();
    }

    protected override Impact ImpactOf(TestGuardDescriptor descriptor, BaseChange change)
    {
        Asked.Add(change);
        return ImpactOverride is not null ? ImpactOverride(change) : Default(change);
    }

    private static Impact Default(BaseChange change) => change switch
    {
        BaseChange.ColumnRenamed => new Impact.Follows(),
        BaseChange.ColumnAltered => new Impact.Unaffected(),
        BaseChange.ColumnDropped => new Impact.Lost(),
        BaseChange.TableRenamed  => new Impact.Follows(),
        BaseChange.TableMoved    => new Impact.Follows(),
        BaseChange.TableDropped  => new Impact.Lost(),
        BaseChange.TableRebuilt  => new Impact.Lost(),
    };

    protected override IEnumerable<NameClaim> Claims(TestGuardDescriptor descriptor)
        => descriptor.IsTrigger
            ? [new NameClaim.Trigger(descriptor.Table, descriptor.Name.Name)]
            : [new NameClaim.Relation(descriptor.Name.Schema, descriptor.Name.Name)];
}

internal static class SampleColumns
{
    /// <summary>The column a path resolves to, as the samples' descriptors hold it.</summary>
    public static Identifier Of(SchemaResolver resolver, string path)
        => ((Dependency.Column)resolver.Column(path).Dependencies.Single()).Name;
}

/// <summary>A contributor placed before <see cref="TestGuardContributor" /> among those that come after the tables.</summary>
public sealed class LooseGuardContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<LooseGuard, TestGuardDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 5);

    protected override TestGuardDescriptor Resolve(LooseGuard declaration, SchemaResolver resolver) => new(
        resolver.Table,
        resolver.Table.Qualify(resolver.Dialect.Identifier(declaration.Name ?? $"lx_{declaration.Column}")),
        [SampleColumns.Of(resolver, declaration.Column)],
        IsTrigger: false,
        new Operational<bool>(false));

    protected override void Create(TestGuardDescriptor descriptor, SchemaSql sql)
        => sql.Statement($"CREATE INDEX {descriptor.Name.Name} ON {descriptor.Table} ({sql.List(descriptor.Columns)})");

    protected override void Drop(TestGuardDescriptor descriptor, SchemaSql sql) => sql.Statement($"DROP INDEX {descriptor.Name}");

    protected override SchemaChange Plan(TestGuardDescriptor from, TestGuardDescriptor to, SchemaSql sql) => new SchemaChange.Rebuild();

    protected override Impact ImpactOf(TestGuardDescriptor descriptor, BaseChange change) => new Impact.Unaffected();

    protected override IEnumerable<NameClaim> Claims(TestGuardDescriptor descriptor) => [new NameClaim.Relation(descriptor.Name.Schema, descriptor.Name.Name)];
}

/// <summary>A declaration of something tables are made of: it belongs to a schema, not to a table.</summary>
public sealed record TestDomain(string Type) : ISchemaDeclaration<TestDomain>
{
    public static string OwnerId => "Test.Domain";

    public string Owner => OwnerId;

    public string Kind => "domain";

    public string? Name { get; init; }

    public int Epoch { get; init; } = 1;

    public void Write(SchemaJsonWriter writer) => writer.String("type", Type);

    public static TestDomain Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
        => new(reader.String("type")) { Name = header.Name, Epoch = header.Epoch };
}

public sealed record TestDomainDescriptor(QualifiedName Name, SqlName Type, TableRef DeclaredOn) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Schema(Name.Schema);

    /// <summary>The table it was declared on, so that the planner has a change to ask this contributor about.</summary>
    public ValueList<Dependency> Dependencies => [new Dependency.WholeTable(DeclaredOn)];

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

/// <summary>A contributor whose objects are created before the tables and dropped after them.</summary>
public sealed class TestDomainContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<TestDomain, TestDomainDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.BeforeTables;

    protected override TestDomainDescriptor Resolve(TestDomain declaration, SchemaResolver resolver)
        => new(resolver.Table.Qualify(resolver.Dialect.Identifier(declaration.Name ?? "dom")), resolver.Dialect.Name(declaration.Type), resolver.Table);

    public Func<BaseChange, Impact>? ImpactOverride { get; set; }

    protected override void Create(TestDomainDescriptor descriptor, SchemaSql sql) => sql.Statement($"CREATE DOMAIN {descriptor.Name} AS {descriptor.Type}");

    protected override void Drop(TestDomainDescriptor descriptor, SchemaSql sql) => sql.Statement($"DROP DOMAIN {descriptor.Name}");

    protected override SchemaChange Plan(TestDomainDescriptor from, TestDomainDescriptor to, SchemaSql sql)
    {
        if (!from.WithName(to.Name).Equals(to)) return new SchemaChange.Rebuild();

        sql.Statement($"ALTER DOMAIN {from.Name} RENAME TO {to.Name.Name}");
        return new SchemaChange.InPlace();
    }

    protected override Impact ImpactOf(TestDomainDescriptor descriptor, BaseChange change) => ImpactOverride?.Invoke(change) ?? new Impact.Unaffected();

    protected override IEnumerable<NameClaim> Claims(TestDomainDescriptor descriptor) => [];
}

/// <summary>A second contributor for the owner <see cref="TestGuardContributor" /> has.</summary>
public sealed class RivalGuardContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<TestGuard, TestGuardDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.BeforeTables;
    protected override TestGuardDescriptor Resolve(TestGuard declaration, SchemaResolver resolver) => throw new NotSupportedException();
    protected override void Create(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
    protected override void Drop(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
    protected override SchemaChange Plan(TestGuardDescriptor from, TestGuardDescriptor to, SchemaSql sql) => throw new NotSupportedException();
    protected override Impact ImpactOf(TestGuardDescriptor descriptor, BaseChange change) => throw new NotSupportedException();
    protected override IEnumerable<NameClaim> Claims(TestGuardDescriptor descriptor) => throw new NotSupportedException();
}

/// <summary>A contributor that does not say where its operations go.</summary>
public sealed class UnplacedContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<LooseGuard, TestGuardDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => null!;
    protected override TestGuardDescriptor Resolve(LooseGuard declaration, SchemaResolver resolver) => throw new NotSupportedException();
    protected override void Create(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
    protected override void Drop(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
    protected override SchemaChange Plan(TestGuardDescriptor from, TestGuardDescriptor to, SchemaSql sql) => throw new NotSupportedException();
    protected override Impact ImpactOf(TestGuardDescriptor descriptor, BaseChange change) => throw new NotSupportedException();
    protected override IEnumerable<NameClaim> Claims(TestGuardDescriptor descriptor) => throw new NotSupportedException();
}

/// <summary>A descriptor that is a class: it compares by reference.</summary>
public sealed class ReferenceDescriptor : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Database();
    public QualifiedName Name => throw new NotSupportedException();
    public ValueList<Dependency> Dependencies => [];
    public ISchemaDescriptor WithName(QualifiedName name) => this;
}

public sealed class ReferenceDescriptorContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<TestGuard, ReferenceDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.BeforeTables;
    protected override ReferenceDescriptor Resolve(TestGuard declaration, SchemaResolver resolver) => new();
    protected override void Create(ReferenceDescriptor descriptor, SchemaSql sql) { }
    protected override void Drop(ReferenceDescriptor descriptor, SchemaSql sql) { }
    protected override SchemaChange Plan(ReferenceDescriptor from, ReferenceDescriptor to, SchemaSql sql) => new SchemaChange.None();
    protected override Impact ImpactOf(ReferenceDescriptor descriptor, BaseChange change) => new Impact.Unaffected();
    protected override IEnumerable<NameClaim> Claims(ReferenceDescriptor descriptor) => [];
}

/// <summary>
/// A contributor that implements the interface itself, so none of the base class's checks stands
/// between its answers and the kernel: an InPlace with no statement, and nulls.
/// </summary>
public sealed class DirectContributor : ISchemaContributor
{
    public string Owner => "Test.Direct";
    public SchemaPlacement Placement => SchemaPlacement.BeforeTables;
    public ISchemaDescriptor Resolve(ISchemaObject declaration, SchemaResolver resolver) => null!;
    public void Create(ISchemaDescriptor descriptor, SchemaSql sql) { }
    public void Drop(ISchemaDescriptor descriptor, SchemaSql sql) { }
    public SchemaChange Plan(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql) => new SchemaChange.InPlace();
    public Impact ImpactOf(ISchemaDescriptor descriptor, BaseChange change) => null!;
    public IEnumerable<NameClaim> Claims(ISchemaDescriptor descriptor) => null!;
}

/// <summary>A base of a package's own between its contributors and the kernel's base class.</summary>
public abstract class PackageContributor<TDeclaration>(SchemaContributorDependencies dependencies)
    : SchemaContributor<TDeclaration, TestGuardDescriptor>(dependencies)
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
{
    public override SchemaPlacement Placement => SchemaPlacement.BeforeTables;
}

/// <summary>A contributor two classes away from the kernel's base class, which answers null where the others throw.</summary>
public sealed class DerivedTwiceContributor(SchemaContributorDependencies dependencies) : PackageContributor<LooseGuard>(dependencies)
{
    protected override TestGuardDescriptor Resolve(LooseGuard declaration, SchemaResolver resolver) => null!;
    protected override void Create(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
    protected override void Drop(TestGuardDescriptor descriptor, SchemaSql sql) => throw new NotSupportedException();
    protected override SchemaChange Plan(TestGuardDescriptor from, TestGuardDescriptor to, SchemaSql sql) => throw new NotSupportedException();
    protected override Impact ImpactOf(TestGuardDescriptor descriptor, BaseChange change) => throw new NotSupportedException();
    protected override IEnumerable<NameClaim> Claims(TestGuardDescriptor descriptor) => null!;
}

/// <summary>The resolver a test hands a contributor; the kernel's own arrives with issue 43.</summary>
internal sealed class TestResolver(TableRef table, ISchemaDialect dialect) : SchemaResolver
{
    public override TableRef Table => table;

    public override ISchemaDialect Dialect => dialect;

    /// <summary>The path is the column: enough for the tests of the contract, which are not about resolution.</summary>
    public override SqlFragment Column(string path)
    {
        var column = dialect.Identifier(path);
        return new SqlFragment(dialect.Delimit(column), [new Dependency.Column(table, column)], path);
    }

    public override SqlFragment Template(string template) => throw new NotSupportedException();
}

/// <summary>A package's options extension that adds services, which is how a contributor is registered.</summary>
public sealed class ServicesExtension(string id, Action<IServiceCollection> add) : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this, id);

    public void ApplyServices(IServiceCollection services) => add(services);

    public void Validate(IDbContextOptions options) { }

    public static DbContextOptionsBuilder<TContext> Add<TContext>(DbContextOptionsBuilder<TContext> builder, string id, Action<IServiceCollection> add)
        where TContext : DbContext
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new ServicesExtension(id, add));
        return builder;
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension, string id) : DbContextOptionsExtensionInfo(extension)
    {
        public string Id => id;
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"using {id} ";
        public override int GetServiceProviderHashCode() => id.GetHashCode(StringComparison.Ordinal);
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo o && o.Id == id;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Test:Services"] = id;
    }
}
