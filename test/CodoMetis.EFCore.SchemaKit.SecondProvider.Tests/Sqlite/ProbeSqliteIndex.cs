using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;

// SQLite, as far as it goes (#119): an index on an expression, which EF Core has no API for, and
// what becomes of it when the provider creates its table again. Written against the public
// contract alone, like the SQL Server probe beside it, and shipped nowhere.

/// <summary>An index of SQLite on one expression: SQL in which each <c>{Path}</c> stands for that path's column.</summary>
/// <param name="Key">The expression, or a column.</param>
public sealed record ProbeSqliteIndex(KeyTarget Key) : ISchemaDeclaration<ProbeSqliteIndex>
{
    public static string OwnerId => "Probe.SqliteIndex";

    public string Owner => OwnerId;

    public string Kind => "index";

    public string? Name { get; init; }

    public int Epoch { get; init; } = 1;

    public bool IsUnique { get; init; }

    /// <summary>
    /// Audit: SQLite folds the case of ASCII letters only, so <c>IX_Ä</c> and <c>IX_ä</c> are two
    /// names and <c>IX_A</c> and <c>ix_a</c> one. Neither case of the enum is that rule
    /// (<c>SqliteExpressionIndexTests</c>).
    /// </summary>
    public SchemaNameComparison NameComparison => SchemaNameComparison.IgnoreCase;

    public void Write(SchemaJsonWriter writer)
    {
        writer.OptionalString("column", (Key as KeyTarget.Column)?.Path);
        writer.OptionalTemplate("sql", (Key as KeyTarget.Template)?.Sql);
        writer.Boolean("unique", IsUnique);
    }

    public static ProbeSqliteIndex Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
    {
        KeyTarget key = (reader.OptionalString("column"), reader.OptionalTemplate("sql")) switch
        {
            ({ } path, null)     => new KeyTarget.Column(path),
            (null, { } template) => new KeyTarget.Template(template),
            _                    => throw new InvalidOperationException("A stored index key has to hold exactly one of 'column' and 'sql'."),
        };

        return new ProbeSqliteIndex(key) { Name = header.Name, Epoch = header.Epoch, IsUnique = reader.Boolean("unique") };
    }
}

public static class ProbeSqliteIndexDeclarationExtensions
{
    public static EntityTypeBuilder<TEntity> DeclareProbe<TEntity>(this EntityTypeBuilder<TEntity> builder, params ProbeSqliteIndex[] indexes)
        where TEntity : class
    {
        builder.Metadata.Declare(indexes);
        return builder;
    }
}

/// <param name="Table">The table.</param>
/// <param name="Name">The index's name. SQLite names an index within its database, among the tables.</param>
/// <param name="IsUnique">Whether no two rows may share the key.</param>
/// <param name="Key">The key as it is written: a column, or an expression.</param>
internal sealed record ProbeSqliteIndexDescriptor(TableRef Table, QualifiedName Name, bool IsUnique, SqlFragment Key) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies => Key.Dependencies;

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

internal sealed class ProbeSqliteIndexContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<ProbeSqliteIndex, ProbeSqliteIndexDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    /// <summary>For the tests that break a rule to show its test failing.</summary>
    public Func<BaseChange, Impact?>? ImpactOverride { get; set; }

    protected override ProbeSqliteIndexDescriptor Resolve(ProbeSqliteIndex declaration, SchemaResolver resolver)
    {
        var key = declaration.Key switch
        {
            KeyTarget.Column column     => resolver.Column(column.Path),
            KeyTarget.Template template => resolver.Template(template.Sql),
        };

        return new ProbeSqliteIndexDescriptor(
            resolver.Table,
            new QualifiedName(null, resolver.Name(declaration.Name, "IX", [key])),
            declaration.IsUnique,
            key);
    }

    protected override void Create(ProbeSqliteIndexDescriptor descriptor, SchemaSql sql)
    {
        var unique = descriptor.IsUnique ? sql.Fragment($"UNIQUE ") : SqlFragment.Empty;
        sql.Statement($"CREATE {unique}INDEX {descriptor.Name.Name} ON {descriptor.Table} ({descriptor.Key})");
    }

    protected override void Drop(ProbeSqliteIndexDescriptor descriptor, SchemaSql sql)
        => sql.Statement($"DROP INDEX {descriptor.Name.Name}");

    // SQLite renames no index: ALTER has no such form.
    protected override SchemaChange Plan(ProbeSqliteIndexDescriptor from, ProbeSqliteIndexDescriptor to, SchemaSql sql)
        => new SchemaChange.Rebuild();

    protected override Impact ImpactOf(ProbeSqliteIndexDescriptor descriptor, BaseChange change)
    {
        if (ImpactOverride?.Invoke(change) is { } forced) return forced;

        return change switch
        {
            // ALTER TABLE … RENAME COLUMN and RENAME TO rewrite the index's SQL.
            BaseChange.ColumnRenamed => new Impact.Follows(),
            BaseChange.TableRenamed  => new Impact.Follows(),

            // The provider writes both as a rebuild of the table, which is its own change below.
            BaseChange.ColumnAltered => new Impact.Unaffected(),
            BaseChange.ColumnDropped => new Impact.Unaffected(),

            // SQLite has no schemas, and EF Core's provider moves no table.
            BaseChange.TableMoved   => new Impact.Unaffected(),
            BaseChange.TableDropped => new Impact.Lost(),

            // The new table has none of the old one's indexes but those EF Core's model holds.
            BaseChange.TableRebuilt => new Impact.Lost(),
        };
    }

    // One namespace per database for tables and indexes alike. Audit: written as Relation with
    // no schema, PostgreSQL's word again.
    protected override IEnumerable<NameClaim> Claims(ProbeSqliteIndexDescriptor descriptor)
        => [new NameClaim.Relation(null, descriptor.Name.Name)];
}

/// <summary>SQLite's dialect: one rule of its own, where a name is taken.</summary>
internal sealed class ProbeSqliteDialect(SchemaDialectDependencies dependencies, int maxIdentifierLength)
    : SchemaDialect(dependencies, maxIdentifierLength)
{
    // A table and an index share the database's one namespace, whatever table the index is on.
    // A constraint's name is taken nowhere: SQLite keeps it in the table's SQL and compares it
    // with nothing. EF Core's provider writes no schema, so none is compared.
    public override IEnumerable<NameClaim> Claims(SchemaModelObject kind, TableRef table, Identifier name) => kind switch
    {
        SchemaModelObject.Table or SchemaModelObject.Index => [new NameClaim.Relation(null, name)],
        _                                                  => [],
    };
}

internal sealed class ProbeSqliteOptionsExtension(bool dialect) : IDbContextOptionsExtension
{
    public bool Dialect => dialect;

    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        if (dialect)
        {
            services.Replace(ServiceDescriptor.Scoped<ISchemaDialect>(provider => new ProbeSqliteDialect(
                new SchemaDialectDependencies(
                    provider.GetRequiredService<ISqlGenerationHelper>(),
                    provider.GetRequiredService<IRelationalTypeMappingSource>()),
                provider.GetRequiredService<IDesignTimeModel>().Model.GetMaxIdentifierLength())));
        }

        services.AddSchemaContributor<ProbeSqliteIndexContributor>();
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(ProbeSqliteOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using SchemaKit.Probe.Sqlite ";

        public override int GetServiceProviderHashCode() => extension.Dialect ? 1 : 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo info && ((ProbeSqliteOptionsExtension)info.Extension).Dialect == extension.Dialect;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["CodoMetis:SchemaKit:Probe:Sqlite"] = extension.Dialect ? "dialect" : "kernel's dialect";
    }
}

internal static class ProbeSqliteRegistration
{
    public static SqliteDbContextOptionsBuilder UseSchemaKitProbe(this SqliteDbContextOptionsBuilder optionsBuilder, bool dialect = true)
    {
        var core = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;
        core.UseSchemaKit();
        ((IDbContextOptionsBuilderInfrastructure)core).AddOrUpdateExtension(new ProbeSqliteOptionsExtension(dialect));
        return optionsBuilder;
    }
}
