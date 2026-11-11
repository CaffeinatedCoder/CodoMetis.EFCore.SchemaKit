using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SchemaKit.ApiProbes.Probes;

/// <summary>
/// The probes that have moved from the prototype to the shipping kernel: each forbidden form is
/// compiled against <c>CodoMetis.EFCore.SchemaKit.dll</c> as a contributor's assembly would be, and
/// must fail with the diagnostic claimed, beside an allowed twin that compiles clean.
/// </summary>
/// <remarks>
/// As a type leaves the prototype for the kernel, its probes move from <see cref="ProbeCatalogue" />
/// to <see cref="All" />. The prototype's own copy stays until the prototype is deleted.
/// </remarks>
[TestClass]
public sealed class KernelProbeTests
{
    private const string OptIn = "#pragma warning disable SK9001\n";

    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using CodoMetis.EFCore.SchemaKit;

        """;

    private const string FullSwitch = """
        public static class Scopes
        {
            public static string Describe(SchemaScope scope) => scope switch
            {
                SchemaScope.Table t => "table",
                SchemaScope.Schema s => "schema",
                SchemaScope.Database => "database",
            };
        }
        """;

    public static IReadOnlyList<Probe> All { get; } =
    [
        // ── E: nothing reaches a statement unquoted ───────────────────────────────────────
        new("E1.string-in-ddl", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static void Drop(SchemaSql sql, Identifier name) => sql.Statement($\"DROP INDEX {name}\"); }",
            "public static class Ddl { public static void Drop(SchemaSql sql, string name) => sql.Statement($\"DROP INDEX {name}\"); }",
            "CS1503"),
        new("E2.fragment-from-string", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static SqlFragment Make(SchemaSql sql) => sql.Fragment($\"lower(email)\"); }",
            "public static class Ddl { public static SqlFragment Make(SchemaSql sql) => new SqlFragment(\"lower(email)\"); }",
            "CS1729"),
        new("E3.identifier-from-string", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static Identifier Make(ISchemaDialect dialect) => dialect.Identifier(\"ix_people_name\"); }",
            "public static class Ddl { public static Identifier Make(ISchemaDialect dialect) => \"ix_people_name\"; }",
            "CS0029"),
        new("E4.identifier-not-constructible", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static Identifier Make(ISchemaDialect dialect) => dialect.Identifier(\"ix_people_name\"); }",
            "public static class Ddl { public static Identifier Make() => new Identifier(\"ix_people_name\"); }",
            "CS1729"),
        new("E5.qualified-name-from-string", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static QualifiedName Make(ISchemaDialect dialect) => new(null, dialect.Identifier(\"ix\")); }",
            "public static class Ddl { public static QualifiedName Make(ISchemaDialect dialect) => new(null, \"ix\"); }",
            "CS1503"),
        new("E6.operator-from-string", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static SqlOperator Make(ISchemaDialect dialect) => dialect.Operator(\"&&\"); }",
            "public static class Ddl { public static SqlOperator Make(ISchemaDialect dialect) => \"&&\"; }",
            "CS0029"),
        new("E7.object-in-ddl", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static void Set(SchemaSql sql) => sql.Statement($\"ALTER INDEX x SET (fillfactor = {70})\"); }",
            "public static class Ddl { public static void Set(SchemaSql sql) => sql.Statement($\"ALTER INDEX x SET (fillfactor = {(object)70})\"); }",
            "CS1503"),
        new("E8.formatted-hole", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static void Set(SchemaSql sql) => sql.Statement($\"ALTER INDEX x SET (fillfactor = {70})\"); }",
            "public static class Ddl { public static void Set(SchemaSql sql) => sql.Statement($\"ALTER INDEX x SET (fillfactor = {70:D3})\"); }",
            "CS1739"),
        new("E9.builder-not-constructible", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static SchemaSql Same(SchemaSql sql) => sql; }",
            "public static class Ddl { public static SchemaSql Make(ISchemaDialect dialect) => new SchemaSql(dialect); }",
            "CS1729"),

        // ── S: the scope is a closed union ────────────────────────────────────────────────
        new("S1.scope-missing-arm", "S", ProbeShape.TopLevel,
            FullSwitch,
            FullSwitch.Replace("        SchemaScope.Database => \"database\",\n", ""),
            "CS8509"),
        new("S2.scope-no-foreign-case", "S", ProbeShape.TopLevel,
            "public static class Uses { public static SchemaScope Whole() => new SchemaScope.Database(); }",
            "public sealed record Cluster : SchemaScope;",
            "CS9382"),
    ];

    /// <summary>The contract is experimental: without the opt-in, naming one of its types is an error.</summary>
    private static readonly Probe OptInProbe = new("C1.contract-without-opt-in", "C", ProbeShape.TopLevel,
        "public static class Ddl { public static string Bare(Identifier name) => name.Name; }",
        "public static class Ddl { public static string Bare(Identifier name) => name.Name; }",
        "SK9001");

    private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

    public static IEnumerable<object[]> Probes => All.Select(p => new object[] { p });

    [TestMethod]
    [DynamicData(nameof(Probes))]
    public void Allowed_form_compiles_clean(Probe probe)
    {
        var errors = Compile(probe.Id, OptIn + Usings + probe.Allowed);

        Assert.IsEmpty(errors, $"{probe.Id}: the allowed form must compile clean.\n{Describe(errors)}");
    }

    [TestMethod]
    [DynamicData(nameof(Probes))]
    public void Forbidden_form_fails_with_the_claimed_diagnostic(Probe probe)
    {
        var errors = Compile(probe.Id, OptIn + Usings + probe.Forbidden);

        Assert.IsTrue(errors.Any(d => d.Id == probe.Diagnostic), $"{probe.Id}: expected {probe.Diagnostic}.\n{Describe(errors)}");
    }

    [TestMethod]
    public void The_contract_needs_the_opt_in()
    {
        Assert.IsEmpty(Compile(OptInProbe.Id, OptIn + Usings + OptInProbe.Allowed));

        var errors = Compile(OptInProbe.Id, Usings + OptInProbe.Forbidden);
        Assert.IsTrue(errors.Any(d => d.Id == OptInProbe.Diagnostic), $"Expected {OptInProbe.Diagnostic}.\n{Describe(errors)}");
    }

    /// <summary>The probes are about the kernel: its assembly is referenced, and the prototype's is not.</summary>
    [TestMethod]
    public void The_snippets_are_compiled_against_the_kernel_and_not_the_prototype()
    {
        var names = References.Select(r => Path.GetFileName(r.Display)).ToList();

        CollectionAssert.Contains(names, "CodoMetis.EFCore.SchemaKit.dll");
        CollectionAssert.DoesNotContain(names, "SchemaKit.ApiProbes.dll");
    }

    /// <summary>Prints every forbidden form's diagnostic, for the design document's table.</summary>
    [TestMethod]
    public void Print_diagnostics()
    {
        foreach (var probe in All)
        {
            var first = Compile(probe.Id, OptIn + Usings + probe.Forbidden).FirstOrDefault(d => d.Id == probe.Diagnostic);
            Console.WriteLine($"| `{probe.Id}` | {first?.Id} | {first?.GetMessage(CultureInfo.InvariantCulture)} |");
        }
    }

    private static IReadOnlyList<Diagnostic> Compile(string id, string source)
    {
        var compilation = CSharpCompilation.Create(
            $"KernelProbe_{id.Replace('.', '_').Replace('-', '_')}",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"  {d.Id} {d.GetMessage(CultureInfo.InvariantCulture)}"));

    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var local = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");
        return [.. platform.Concat(local)
            .Where(path => !Path.GetFileName(path).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            .Where(path => Path.GetFileName(path) != "SchemaKit.ApiProbes.dll")
            .GroupBy(Path.GetFileName)
            .Select(group => MetadataReference.CreateFromFile(group.First()))];
    }
}
