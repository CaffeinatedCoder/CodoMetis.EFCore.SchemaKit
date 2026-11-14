using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Documentation;

/// <summary>
/// Every C# sample of the READMEs and of <c>docs/</c>, compiled against the shipped assemblies with
/// nothing suppressed, as <see cref="ConsumerCompileTests" /> compiles a consumer's code. A sample
/// that names a member that does not exist reads as code that works until someone types it.
/// </summary>
/// <remarks>
/// <para>
/// A block fenced <c>```csharp</c> is compiled. One fenced <c>```csharp 5.x</c> is the code of
/// <c>EFCore.ComplexIndexes</c> 5.x, shown as it was before a move, and is not; GitHub highlights
/// both alike. Any other info string that starts with <c>csharp</c> fails, so a typo in the marker
/// does not take a sample out of the test.
/// </para>
/// <para>
/// A sample with a type declaration is a file of its own and is compiled as it is. Any other
/// sample is statements: it is compiled in <see cref="Fragment" />, beside the entity types of
/// <see cref="ModelOfTheSamples" />. Both are below, so what a fragment is given is visible.
/// </para>
/// </remarks>
[TestClass]
public sealed partial class DocumentationSampleTests
{
    /// <summary>What <c>&lt;ImplicitUsings&gt;enable&lt;/ImplicitUsings&gt;</c> gives a project: a sample may rely on it.</summary>
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    /// <summary>
    /// Where a fragment stands: in a method that has what the samples call on. A fragment names
    /// <c>modelBuilder</c> inside <c>OnModelCreating</c>, <c>options</c> where a context's options
    /// are built, and <c>services</c> with a <c>connectionString</c> at the application's start.
    /// </summary>
    private const string Fragment = """
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;
        using NpgsqlTypes;
        using CodoMetis.EFCore.SchemaKit;
        using CodoMetis.EFCore.SchemaKit.PostgreSQL;

        public static class Sample
        {
            public static void Run(ModelBuilder modelBuilder, DbContextOptionsBuilder options, IServiceCollection services, string connectionString)
            {
        #line 1
        {0}
            }
        }
        """;

    /// <summary>The entity types and the context the fragments name, as the samples that declare them write them.</summary>
    private const string ModelOfTheSamples = """
        using Microsoft.EntityFrameworkCore;
        using NpgsqlTypes;

        public sealed class RoleGrant
        {
            public int Id { get; set; }
            public int GranteeId { get; set; }
            public int RoleId { get; set; }
            public NpgsqlRange<DateOnly> Period { get; set; }
            public DateOnly? RevokedOn { get; set; }
        }

        public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);

        public sealed class Booking
        {
            public int Id { get; set; }
            public int RoomId { get; set; }
            public NpgsqlRange<DateOnly> During { get; set; }
        }

        public sealed class Shift
        {
            public int Id { get; set; }
            public int WorkerId { get; set; }
            public int SiteId { get; set; }
            public string Kind { get; set; } = "";
            public NpgsqlRange<DateOnly> During { get; set; }
            public DateOnly? CancelledOn { get; set; }
        }

        public sealed class Term
        {
            public NpgsqlRange<DateOnly> Span { get; set; }
        }

        public sealed class Lease
        {
            public int Id { get; set; }
            public string Code { get; set; } = "";
            public Term Term { get; set; } = new();
        }

        public sealed class Slot
        {
            public int Id { get; set; }
            public NpgsqlRange<DateOnly> During { get; set; }
        }
        """;

    private const string FiveX = "csharp 5.x";

    /// <summary>A type declared at the start of a line: the sample is a file of its own.</summary>
    [GeneratedRegex(@"^(?:public |internal )?(?:sealed |static |abstract |partial )*(?:class|record|struct|interface) ", RegexOptions.Multiline)]
    private static partial Regex TypeDeclaration();

    private static IEnumerable<Markdown.CodeBlock> Samples()
        => Markdown.Pages().SelectMany(Markdown.CodeBlocks).Where(block => block.Info.StartsWith("csharp", StringComparison.Ordinal));

    public static IEnumerable<object[]> CompiledSamples()
        => Samples().Where(block => block.Info == "csharp").Select(block => new object[] { block });

    public static string SampleName(System.Reflection.MethodInfo method, object[] data) => data[0].ToString()!;

    [TestMethod]
    [DynamicData(nameof(CompiledSamples), DynamicDataDisplayName = nameof(SampleName))]
    public void The_sample_compiles_against_the_shipped_assemblies(Markdown.CodeBlock sample)
    {
        var diagnostics = Compile(sample);

        Assert.AreEqual(0, diagnostics.Count, $"{sample}:\n{string.Join("\n", diagnostics)}");
    }

    /// <summary>A marker that is neither form would take its block out of the test above without a word.</summary>
    [TestMethod]
    public void Every_csharp_block_is_compiled_or_marked_as_5x()
    {
        var unknown = Samples().Where(block => block.Info is not ("csharp" or FiveX)).Select(block => $"{block} ```{block.Info}").ToList();

        Assert.AreEqual(0, unknown.Count, "Fenced as neither ```csharp nor ```csharp 5.x: " + string.Join(", ", unknown));
    }

    /// <summary>
    /// The premise of the test above: the pages with samples are found, and each has one that is
    /// compiled. A layout that found no page would leave the data-driven test with no rows.
    /// </summary>
    [TestMethod]
    public void The_readmes_and_the_guide_have_samples_that_are_compiled()
    {
        var compiled = CompiledSamples().Select(row => Markdown.Relative(((Markdown.CodeBlock)row[0]).File)).ToHashSet();

        foreach (var page in new[] { "README.md", "src/CodoMetis.EFCore.SchemaKit.PostgreSQL/README.md", "docs/moving-from-complexindexes-5x.md" })
            Assert.Contains(page, compiled);
    }

    /// <summary>The test's own premise: a sample that names a member that does not exist is reported, with its place.</summary>
    [TestMethod]
    public void A_sample_that_names_a_member_that_does_not_exist_fails()
    {
        var broken = new Markdown.CodeBlock("README.md", 1, "csharp", "modelBuilder.Entity<RoleGrant>().DeclarePostgres(pg => [pg.Unique(x => x.RoleId)]);");

        var diagnostics = Compile(broken);

        Assert.AreEqual("CS1061", diagnostics.Single().Id, string.Join("\n", diagnostics));
    }

    private static List<Diagnostic> Compile(Markdown.CodeBlock sample)
    {
        string[] sources = TypeDeclaration().IsMatch(sample.Code)
            ? [ImplicitUsings, sample.Code]
            : [ImplicitUsings, ModelOfTheSamples, Fragment.Replace("{0}", sample.Code, StringComparison.Ordinal)];

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location))
            // The shipped assemblies and what they are built on; nothing of this test project.
            .Append(typeof(PgExclusion).Assembly.Location)
            .Append(typeof(KeyTarget).Assembly.Location)
            .Append(typeof(NpgsqlTypes.NpgsqlRange<>).Assembly.Location)
            .Append(typeof(Microsoft.EntityFrameworkCore.NpgsqlDbContextOptionsBuilderExtensions).Assembly.Location)
            .Where(path => !path.EndsWith("CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.dll", StringComparison.Ordinal))
            .Distinct()
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        return [.. CSharpCompilation.Create(
                $"Sample_{Guid.NewGuid():N}",
                sources.Select((source, i) => CSharpSyntaxTree.ParseText(source, path: i == sources.Length - 1 ? sample.ToString() : $"given{i}.cs")),
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("SK", StringComparison.Ordinal))];
    }
}
