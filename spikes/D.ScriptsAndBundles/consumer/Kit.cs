using CodoMetis.EFCore.SchemaKit;
#if ANNOTATED
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
#endif

namespace Consumer;

// A contributor as a package outside SchemaKit writes it, against the kernel's public contract and
// nothing else. Its statements reach a migration the way a real contributor's do: through the
// kernel's planner, as the SqlOperations SchemaKitModelDiffer splices around EF Core's own.

/// <summary>The statements the spike asks about. One is compiled into each consumer.</summary>
public static class Forms
{
    /// <summary>PostgreSQL: <c>CREATE INDEX CONCURRENTLY</c>, which cannot run in a transaction.</summary>
    public const string ConcurrentIndex = "concurrent-index";

    /// <summary>SQL Server: <c>CREATE INDEX … WITH (ONLINE = ON)</c>, with the transaction suppressed as well.</summary>
    public const string OnlineIndex = "online-index";

    /// <summary>A trigger as it is written: with its function on PostgreSQL, a bare <c>CREATE TRIGGER</c> on SQL Server.</summary>
    public const string Trigger = "trigger";

    /// <summary>SQL Server: the same trigger inside <c>EXEC(N'…')</c>, which is a statement that may stand anywhere in a batch.</summary>
    public const string TriggerExec = "trigger-exec";

    /// <summary>SQL Server: the same, with the trigger's body on several lines, as a real one is written.</summary>
    public const string TriggerExecLines = "trigger-exec-lines";

    /// <summary>A plain <c>CREATE INDEX</c>: the control, and what the annotations are put on.</summary>
    public const string PlainIndex = "plain-index";

#if CONCURRENT_INDEX
    public const string Current = ConcurrentIndex;
#elif ONLINE_INDEX
    public const string Current = OnlineIndex;
#elif TRIGGER
    public const string Current = Trigger;
#elif TRIGGER_EXEC
    public const string Current = TriggerExec;
#elif TRIGGER_EXEC_LINES
    public const string Current = TriggerExecLines;
#else
    public const string Current = PlainIndex;
#endif

    public static bool IsTrigger { get; } = Current.StartsWith(Trigger, StringComparison.Ordinal);

    public static string ObjectName => IsTrigger ? "tr_things_guard" : "ix_things_name";

    /// <summary>PostgreSQL's trigger function.</summary>
    public const string FunctionName = "things_guard";
}

public sealed record SpikeObject(string Form, string Column) : ISchemaDeclaration<SpikeObject>
{
    public static string OwnerId => "Spike.D";

    public string Owner => OwnerId;

    public string Kind => Form;

    public string? Name { get; init; }

    public int Epoch { get; init; } = 1;

    public void Write(SchemaJsonWriter writer) => writer.String("column", Column);

    public static SpikeObject Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
        => new(header.Kind, reader.String("column")) { Name = header.Name, Epoch = header.Epoch };
}

public sealed record SpikeDescriptor(TableRef Table, QualifiedName Name, Identifier Column, string Form, QualifiedName? Function) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies => [new Dependency.Column(Table, Column)];

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

public sealed class SpikeContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<SpikeObject, SpikeDescriptor>(dependencies)
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    protected override SpikeDescriptor Resolve(SpikeObject declaration, SchemaResolver resolver)
    {
        var column = ((Dependency.Column)resolver.Column(declaration.Column).Dependencies.Single()).Name;
        var name   = resolver.Table.Qualify(resolver.Dialect.Identifier(declaration.Name ?? Forms.ObjectName));

#if NPGSQL
        var function = declaration.Form == Forms.Trigger ? resolver.Table.Qualify(resolver.Dialect.Identifier(Forms.FunctionName)) : null;
#else
        QualifiedName? function = null;
#endif
        return new SpikeDescriptor(resolver.Table, name, column, declaration.Form, function);
    }

    protected override void Create(SpikeDescriptor descriptor, SchemaSql sql)
    {
#if ANNOTATED
        var first = Annotations.Of(sql).Count;
#endif
        var name = descriptor.Name.Name;
        switch (descriptor.Form)
        {
            case Forms.PlainIndex:
                sql.Statement($"CREATE INDEX {name} ON {descriptor.Table} ({descriptor.Column})");
                break;

#if NPGSQL
            case Forms.ConcurrentIndex:
                sql.Statement($"CREATE INDEX CONCURRENTLY {name} ON {descriptor.Table} ({descriptor.Column})", suppressTransaction: true);
                break;

            // Two statements, the first with a body: semicolons and line breaks inside a
            // dollar-quoted string, which is what a real trigger function looks like.
            case Forms.Trigger:
                sql.Statement($"""
                    CREATE FUNCTION {descriptor.Function!}() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW.{descriptor.Column} = 'refused' THEN
                            RAISE EXCEPTION 'spike D: the row is refused' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END
                    $$
                    """);
                sql.Statement($"CREATE TRIGGER {name} BEFORE INSERT OR UPDATE ON {descriptor.Table} FOR EACH ROW EXECUTE FUNCTION {descriptor.Function!}()");
                break;
#elif SQLSERVER
            case Forms.OnlineIndex:
                sql.Statement($"CREATE INDEX {name} ON {descriptor.Table} ({descriptor.Column}) WITH (ONLINE = ON)", suppressTransaction: true);
                break;

            case Forms.Trigger:
                sql.Statement($"""
                    CREATE TRIGGER {descriptor.Name} ON {descriptor.Table} AFTER INSERT, UPDATE AS
                    BEGIN
                        SET NOCOUNT ON;
                        IF EXISTS (SELECT 1 FROM inserted WHERE {descriptor.Column} = N'refused')
                            THROW 51000, N'spike D: the row is refused', 1;
                    END
                    """);
                break;

            // The statement as a string the server executes: the form that needs no batch of its
            // own. On one line, since EXEC takes string constants and their concatenation only.
            case Forms.TriggerExec:
                var trigger = sql.Fragment(
                    $"CREATE TRIGGER {descriptor.Name} ON {descriptor.Table} AFTER INSERT, UPDATE AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM inserted WHERE {descriptor.Column} = N'refused') THROW 51000, N'spike D: the row is refused', 1; END");
                sql.Statement($"EXEC({sql.Literal(trigger.ToString())})");
                break;

            // What the line above avoids. The dialect writes a string with line breaks as
            // CONCAT(N'…', nchar(10), N'…'), EF Core's literal for it, and EXEC takes no function call.
            case Forms.TriggerExecLines:
                var lines = sql.Fragment($"""
                    CREATE TRIGGER {descriptor.Name} ON {descriptor.Table} AFTER INSERT, UPDATE AS
                    BEGIN
                        SET NOCOUNT ON;
                        IF EXISTS (SELECT 1 FROM inserted WHERE {descriptor.Column} = N'refused')
                            THROW 51000, N'spike D: the row is refused', 1;
                    END
                    """);
                sql.Statement($"EXEC({sql.Literal(lines.ToString())})");
                break;
#endif

            default:
                throw new InvalidOperationException($"Spike D has no statement '{descriptor.Form}' for this provider.");
        }

#if ANNOTATED
        Annotations.Stamp(sql, first, "create", descriptor);
#endif
    }

    protected override void Drop(SpikeDescriptor descriptor, SchemaSql sql)
    {
#if ANNOTATED
        var first = Annotations.Of(sql).Count;
#endif
        var name = descriptor.Name.Name;
        if (descriptor.Form.StartsWith(Forms.Trigger, StringComparison.Ordinal))
        {
#if NPGSQL
            sql.Statement($"DROP TRIGGER {name} ON {descriptor.Table}");
            sql.Statement($"DROP FUNCTION {descriptor.Function!}()");
#else
            sql.Statement($"DROP TRIGGER {descriptor.Name}");
#endif
        }
        else
        {
#if SQLSERVER
            sql.Statement($"DROP INDEX {name} ON {descriptor.Table}");
#else
            sql.Statement($"DROP INDEX {descriptor.Name}");
#endif
        }

#if ANNOTATED
        Annotations.Stamp(sql, first, "drop", descriptor);
#endif
    }

    // The spike scaffolds one migration from nothing, so neither is asked. Each answers what is
    // safe for any object: a change is a rebuild, and an operation on what the object depends on
    // has it rebuilt around it.
    protected override SchemaChange Plan(SpikeDescriptor from, SpikeDescriptor to, SchemaSql sql) => new SchemaChange.Rebuild();

    protected override Impact ImpactOf(SpikeDescriptor descriptor, BaseChange change) => new Impact.RebuildAround();

    protected override IEnumerable<NameClaim> Claims(SpikeDescriptor descriptor)
    {
        if (descriptor.Form.StartsWith(Forms.Trigger, StringComparison.Ordinal))
            return [new NameClaim.Trigger(descriptor.Table, descriptor.Name.Name)];
#if SQLSERVER
        return [new NameClaim.TableIndex(descriptor.Table, descriptor.Name.Name)];
#else
        return [new NameClaim.Relation(descriptor.Name.Schema, descriptor.Name.Name)];
#endif
    }
}

#if ANNOTATED

/// <summary>
/// Puts annotations on the operations a contributor's statements became: what point 4 of #115
/// would have the kernel do. The kernel has no way to say it yet, so the spike reaches the
/// operations by reflection. They are the very instances the planner hands the differ.
/// </summary>
internal static class Annotations
{
    private static readonly PropertyInfo Operations =
        typeof(SchemaSql).GetProperty("Operations", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SchemaSql.Operations is gone, and this spike reads it by reflection.");

    public static IReadOnlyList<SqlOperation> Of(SchemaSql sql) => (IReadOnlyList<SqlOperation>)Operations.GetValue(sql)!;

    /// <summary>One annotation of each kind of value #115 names: what the statement is, of which object, with which claims.</summary>
    public static void Stamp(SchemaSql sql, int first, string action, SpikeDescriptor descriptor)
    {
        foreach (var operation in Of(sql).Skip(first))
        {
            operation.AddAnnotation("Spike:Kind", $"{action} index");
            operation.AddAnnotation("Spike:Object", descriptor.Name.ToString());
            operation.AddAnnotation("Spike:Claims", new[] { $"relation:{descriptor.Name}", $"index:{descriptor.Table}.{descriptor.Name.Name}" });
            operation.AddAnnotation("Spike:Order", 3);
            operation.AddAnnotation("Spike:AfterRebuild", true);

            // A value EF Core's C# generator has no literal for, to see how that is told.
            if (Environment.GetEnvironmentVariable("SPIKE_D_ANNOTATION") == "unknown")
                operation.AddAnnotation("Spike:Unknown", new SpikeClaim("relation", descriptor.Name.ToString()));
        }
    }
}

internal sealed record SpikeClaim(string Namespace, string Name);

/// <summary>
/// The provider's generator, writing down each operation that reaches it. It is the generator
/// SchemaKit's decorator wraps, so it sees what the decorator hands on: the list a migration's
/// <c>Up</c> built, at <c>migrations script</c>, at <c>database update</c> and in a bundle.
/// </summary>
public sealed class RecordingGenerator :
#if NPGSQL
    Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.NpgsqlMigrationsSqlGenerator
#elif SQLSERVER
    SqlServerMigrationsSqlGenerator
#else
    SqliteMigrationsSqlGenerator
#endif
{
#if NPGSQL
    public RecordingGenerator(
        MigrationsSqlGeneratorDependencies dependencies,
        Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.INpgsqlSingletonOptions options,
        Microsoft.EntityFrameworkCore.Query.IQuerySqlGeneratorFactory queries)
        : base(dependencies, options, queries)
    {
    }
#elif SQLSERVER
    public RecordingGenerator(MigrationsSqlGeneratorDependencies dependencies, Microsoft.EntityFrameworkCore.Update.ICommandBatchPreparer preparer)
        : base(dependencies, preparer)
    {
    }
#else
    public RecordingGenerator(MigrationsSqlGeneratorDependencies dependencies, IRelationalAnnotationProvider annotations)
        : base(dependencies, annotations)
    {
    }
#endif

    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        if (Environment.GetEnvironmentVariable("SPIKE_D_SEEN") is { Length: > 0 } file)
            File.AppendAllLines(file, operations.Select(operation => Describe(operation, options)));

        return base.Generate(operations, model, options);
    }

    private static string Describe(MigrationOperation operation, MigrationsSqlGenerationOptions options)
    {
        var annotations = operation.GetAnnotations().OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => $"{a.Name}={Value(a.Value)}");
        var sql = operation is SqlOperation statement ? $" \"{statement.Sql.Split('\n')[0].Trim()}\"" : "";
        return $"SEEN [{options}] {operation.GetType().Name}{sql} | {string.Join(" | ", annotations)}";
    }

    private static string Value(object? value) => value switch
    {
        null             => "null",
        string text      => $"string \"{text}\"",
        string[] strings => $"string[] [{string.Join(", ", strings.Select(s => $"\"{s}\""))}]",
        _                => $"{value.GetType().Name} {value}",
    };
}

#endif
