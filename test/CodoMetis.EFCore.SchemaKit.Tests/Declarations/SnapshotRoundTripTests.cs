using System.Reflection;
using System.Runtime.Loader;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Declarations;

public sealed class SnapshotContext(DbContextOptions<SnapshotContext> options) : DbContext(options)
{
    public static readonly TestGuard[] Declared =
    [
        new TestGuard.Unique([new GuardKey("Name", Descending: true)])
        {
            Name = "ux \"quoted\" \\ name",
            NullsDistinct = false,
            Include = ["Id"],
            // With what the runtime's own encoders escape and the stored form writes as itself: a
            // control character on each side of U+0020, a line separator, a code point no Unicode version has
            // assigned, one that .NET 10 was the first to know, and one outside the BMP.
            Where = new GuardFilter("{Name} <> 'it''s' AND {Id} > 0\n-- a second line \u0001\u007F\u2028\u0378\uA7DB\U0001F600"),
            Concurrently = true,
        },
        new TestGuard.Unique([new GuardKey("Id")]) { FillFactor = 0 },
        new TestGuard.Trigger("Name") { Epoch = 2 },
    ];

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Thing>().Metadata.Declare(Declared);
}

/// <summary>
/// What a consumer's next diff reads is not the model but the model snapshot: C# that EF Core
/// generated, compiled into the consumer's assembly. So the round trip that counts goes through
/// one: the snapshot is scaffolded by EF Core's own design-time services, compiled with Roslyn and
/// loaded, and the declarations are read from the model it builds.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SnapshotRoundTripTests
{
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Declarations_read_from_a_compiled_snapshot_are_the_values_that_were_declared(Provider provider)
    {
        using var context = new SnapshotContext(new DbContextOptionsBuilder<SnapshotContext>().UseProvider(provider).UseSchemaKit().Options);
        var declared = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Thing))!;

        var code = new DesignTimeServicesBuilder(
                typeof(SnapshotRoundTripTests).Assembly, typeof(SnapshotRoundTripTests).Assembly, new OperationReporter(handler: null), [])
            .Build(context)
            .GetRequiredService<IMigrationsScaffolder>()
            .ScaffoldMigration("Initial", rootNamespace: "Scaffolded")
            .SnapshotCode;
        var snapshot = Compile(code).FindEntityType(typeof(Thing).FullName!)!;

        CollectionAssert.AreEquivalent(SnapshotContext.Declared, declared.GetDeclarations<TestGuard>().ToList());
        CollectionAssert.AreEqual(declared.GetDeclarations<TestGuard>().ToList(), snapshot.GetDeclarations<TestGuard>().ToList(), code);

        // One HasAnnotation per declaration, under the key the model has.
        foreach (var annotation in declared.GetAnnotations().Where(a => a.Name.StartsWith("CodoMetis:Test.Guard:", StringComparison.Ordinal)))
        {
            StringAssert.Contains(code, $"HasAnnotation(\"{annotation.Name}\"", code);
            Assert.AreEqual(annotation.Value, snapshot.FindAnnotation(annotation.Name)?.Value);
        }
        Assert.AreEqual(3, snapshot.GetAnnotations().Count(a => a.Name.StartsWith("CodoMetis:Test.Guard:", StringComparison.Ordinal)));
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
