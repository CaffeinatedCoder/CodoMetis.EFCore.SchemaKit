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

    /// <summary>
    /// A complete contributor as a package outside SchemaKit writes it against the kernel alone: its
    /// own closed declaration union with its codec, its descriptor, its contributor. It is the ltree
    /// walkthrough of the design document on the decided contract; the X, M and P probes change one
    /// line of it each.
    /// </summary>
    private const string LtreeExtension = """
        public closed record LtreeObject : ISchemaDeclaration<LtreeObject>
        {
            public static string OwnerId => "Ltree.Path";
            public string Owner => OwnerId;
            public string? Name { get; init; }
            public int Epoch { get; init; } = 1;
            public string Kind => this switch
            {
                MaintainedPath => "maintained",
                PathCheck => "check",
            };

            public void Write(SchemaJsonWriter writer)
            {
                if (this is MaintainedPath m) { writer.String("path", m.Path); writer.String("parent", m.Parent); }
                else if (this is PathCheck c) writer.String("path", c.Path);
            }

            public static LtreeObject Read(SchemaDeclarationHeader header, SchemaJsonReader reader) => header.Kind switch
            {
                "maintained" => new MaintainedPath(reader.String("path"), reader.String("parent")) { Name = header.Name, Epoch = header.Epoch },
                "check" => new PathCheck(reader.String("path")) { Name = header.Name, Epoch = header.Epoch },
                var kind => throw new InvalidOperationException(kind),
            };

            public sealed record MaintainedPath(string Path, string Parent) : LtreeObject;
            public sealed record PathCheck(string Path) : LtreeObject;
        }

        public sealed record LtreeDescriptor(TableRef Table, QualifiedName Name, Identifier Column, SqlFragment Ddl, bool IsTrigger) : ISchemaDescriptor
        {
            public SchemaScope Scope => new SchemaScope.Table(Table);
            public ValueList<Dependency> Dependencies => [new Dependency.Column(Table, Column)];
            public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
        }

        public sealed class LtreeContributor(SchemaContributorDependencies dependencies)
            : SchemaContributor<LtreeObject, LtreeDescriptor>(dependencies)
        {
            public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 30);

            protected override LtreeDescriptor Resolve(LtreeObject declaration, SchemaResolver r) => declaration switch
            {
                LtreeObject.MaintainedPath m => new(r.Table, Named(r, m.Name ?? "tr_path"), r.Dialect.Identifier(m.Path),
                    r.Sql.Fragment($"BEFORE INSERT OR UPDATE ON {r.Table} FOR EACH ROW EXECUTE FUNCTION codometis_ltree_path()"), true),
                /*CHECK-ARM*/LtreeObject.PathCheck c => new(r.Table, Named(r, c.Name ?? "ck_path"), r.Dialect.Identifier(c.Path),
                    r.Sql.Fragment($"CHECK (nlevel({r.Dialect.Identifier(c.Path)}) > 0)"), false),/*END-CHECK-ARM*/
            };

            private static QualifiedName Named(SchemaResolver r, string name) => new(null, r.Dialect.Identifier(name));

            protected override void Create(LtreeDescriptor d, SchemaSql sql)
            {
                if (d.IsTrigger) sql.Statement($"CREATE TRIGGER {d.Name.Name} {d.Ddl}");
                else sql.Statement($"ALTER TABLE {d.Table} ADD CONSTRAINT {d.Name.Name} {d.Ddl}");
            }

            protected override void Drop(LtreeDescriptor d, SchemaSql sql)
            {
                if (d.IsTrigger) sql.Statement($"DROP TRIGGER {/*NAME*/d.Name.Name} ON {d.Table}");
                else sql.Statement($"ALTER TABLE {d.Table} DROP CONSTRAINT {d.Name.Name}");
            }

            /*PLAN*/protected override SchemaChange Plan(LtreeDescriptor from, LtreeDescriptor to, SchemaSql sql) => new SchemaChange.Rebuild();/*END-PLAN*/

            /*IMPACT*/protected override Impact ImpactOf(LtreeDescriptor d, BaseChange change) => change switch
            {
                BaseChange.ColumnRenamed => new Impact.Follows(),
                BaseChange.ColumnAltered => new Impact.RebuildAround(),
                BaseChange.ColumnDropped => new Impact.Lost(),
                BaseChange.TableRenamed => new Impact.Follows(),
                BaseChange.TableMoved => new Impact.Follows(),
                BaseChange.TableDropped => new Impact.Lost(),
                /*REBUILT-ARM*/BaseChange.TableRebuilt => new Impact.Lost(),/*END-REBUILT-ARM*/
            };/*END-IMPACT*/

            protected override IEnumerable<NameClaim> Claims(LtreeDescriptor d) =>
                d.IsTrigger ? [new NameClaim.Trigger(d.Table, d.Name.Name)] : [new NameClaim.Constraint(d.Table, d.Name.Name)];
        }

        public static class LtreeUse
        {
            public static LtreeObject Named(LtreeObject.PathCheck check)
            {
                /*VALUE*/var named = check with { Name = "ck_path" };/*END-VALUE*/
                return named;
            }
        }
        """;

    private static string Without(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        var end = source.IndexOf(to, StringComparison.Ordinal) + to.Length;
        return start < 0 || end < to.Length ? throw new InvalidOperationException($"No {from}…{to} in the snippet.") : source.Remove(start, end - start);
    }

    private static string Changed(string source, string from, string to)
        => source.Contains(from, StringComparison.Ordinal) ? source.Replace(from, to) : throw new InvalidOperationException($"No '{from}' in the snippet.");

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
        // ── X: a contributor outside the kernel, end to end ───────────────────────────────
        new("E10.render-a-declaration", "E", ProbeShape.TopLevel,
            LtreeExtension,
            Changed(LtreeExtension, "protected override void Create(LtreeDescriptor d, SchemaSql sql)", "protected override void Create(LtreeObject d, SchemaSql sql)"),
            "CS0115"),
        new("X1.extension-misses-its-own-case", "X", ProbeShape.TopLevel,
            LtreeExtension,
            Without(LtreeExtension, "/*CHECK-ARM*/", "/*END-CHECK-ARM*/"),
            "CS8509"),
        new("X2.extension-renders-a-raw-name", "X", ProbeShape.TopLevel,
            LtreeExtension,
            Changed(LtreeExtension, "{/*NAME*/d.Name.Name}", "{d.Name.Name.Name}"),
            "CS1503"),
        new("X4.resolver-of-ones-own", "X", ProbeShape.TopLevel,
            "public static class Uses { public static TableRef Of(SchemaResolver resolver) => resolver.Table; }",
            "public sealed class OwnResolver(TableRef table, ISchemaDialect dialect) : SchemaResolver { public override TableRef Table => table; public override ISchemaDialect Dialect => dialect; }",
            "CS0122"),

        // ── M: Plan and ImpactOf are mandatory, and ImpactOf answers every base change ────
        new("M1.impact-is-mandatory", "M", ProbeShape.TopLevel,
            LtreeExtension,
            Without(LtreeExtension, "/*IMPACT*/", "/*END-IMPACT*/"),
            "CS0534"),
        new("M2.plan-is-mandatory", "M", ProbeShape.TopLevel,
            LtreeExtension,
            Without(LtreeExtension, "/*PLAN*/", "/*END-PLAN*/"),
            "CS0534"),
        new("M3.impact-misses-a-base-change", "M", ProbeShape.TopLevel,
            LtreeExtension,
            Without(LtreeExtension, "/*REBUILT-ARM*/", "/*END-REBUILT-ARM*/"),
            "CS8509"),

        // ── P8: declarations are values ───────────────────────────────────────────────────
        new("P8.no-mutation", "P8", ProbeShape.TopLevel,
            LtreeExtension,
            Changed(LtreeExtension, "/*VALUE*/var named = check with { Name = \"ck_path\" };/*END-VALUE*/", "var named = check; named.Name = \"ck_path\";"),
            "CS8852"),

        // ── P9: the kernel's unions are closed to a contributor ───────────────────────────
        new("P9.no-foreign-change", "P9", ProbeShape.TopLevel,
            "public static class Uses { public static SchemaChange Keep() => new SchemaChange.None(); }",
            "public sealed record Renamed : SchemaChange;",
            "CS9382"),
        new("P9.no-foreign-impact", "P9", ProbeShape.TopLevel,
            "public static class Uses { public static Impact Keep() => new Impact.Unaffected(); }",
            "public sealed record Degraded : Impact;",
            "CS9382"),
        new("P9.no-foreign-base-change", "P9", ProbeShape.TopLevel,
            "public static class Uses { public static BaseChange Drop(TableRef table) => new BaseChange.TableDropped(table); }",
            "public sealed record TableTruncated(TableRef Table) : BaseChange;",
            "CS9382"),
        new("P9.no-foreign-dependency", "P9", ProbeShape.TopLevel,
            "public static class Uses { public static Dependency On(TableRef table) => new Dependency.WholeTable(table); }",
            "public sealed record Function(QualifiedName Name) : Dependency;",
            "CS9382"),
        new("P9.no-foreign-claim", "P9", ProbeShape.TopLevel,
            "public static class Uses { public static NameClaim Of(TableRef table, Identifier name) => new NameClaim.Trigger(table, name); }",
            "public sealed record Policy(TableRef Table, Identifier Name) : NameClaim;",
            "CS9382"),
        new("P9.no-foreign-placement", "P9", ProbeShape.TopLevel,
            "public static class Uses { public static SchemaPlacement Last() => SchemaPlacement.AfterTables(order: 99); }",
            "public sealed record AfterEverything : SchemaPlacement;",
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
