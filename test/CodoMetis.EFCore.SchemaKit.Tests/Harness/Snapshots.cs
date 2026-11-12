using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>
/// What a consumer's next diff reads is not the model but the model snapshot: C# that EF Core
/// generated, compiled into the consumer's assembly. This makes one the same way: the snapshot is
/// scaffolded by EF Core's own design-time services, compiled with Roslyn and loaded.
/// </summary>
internal static class Snapshots
{
    public static (IModel Model, string Code) Of(DbContext context)
    {
        var code = new DesignTimeServicesBuilder(typeof(Snapshots).Assembly, typeof(Snapshots).Assembly, new OperationReporter(handler: null), [])
            .Build(context)
            .GetRequiredService<IMigrationsScaffolder>()
            .ScaffoldMigration("Initial", rootNamespace: "Scaffolded")
            .SnapshotCode;
        return (Compile(code), code);
    }

    private static IModel Compile(string snapshotCode)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location))
            .Distinct()
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            $"Snapshot_{Guid.NewGuid():N}",
            [CSharpSyntaxTree.ParseText(snapshotCode)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + snapshotCode);

        stream.Position = 0;
        var assembly = new AssemblyLoadContext("snapshot", isCollectible: true).LoadFromStream(stream);
        var type = assembly.GetTypes().Single(t => typeof(ModelSnapshot).IsAssignableFrom(t));
        return ((ModelSnapshot)Activator.CreateInstance(type)!).Model;
    }
}
