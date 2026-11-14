using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// A consumer's code, compiled against the shipped assemblies with nothing suppressed. Every other
/// test here is in a project that opts into the experimental contributor contract (<c>SK9001</c>),
/// so none of them would notice a feature that asks a consumer for the same.
/// </summary>
[TestClass]
public sealed class ConsumerCompileTests
{
    private const string Model = """
        using System;
        using System.Linq;
        using Microsoft.EntityFrameworkCore;
        using NpgsqlTypes;
        using CodoMetis.EFCore.SchemaKit;
        using CodoMetis.EFCore.SchemaKit.PostgreSQL;

        public interface IWithdrawable { DateOnly? RevokedOn { get; } }

        public sealed class Grant : IWithdrawable
        {
            public int Id { get; set; }
            public int GranteeId { get; set; }
            public NpgsqlRange<DateOnly> Period { get; set; }
            public DateOnly? RevokedOn { get; set; }
        }

        public static class Grants
        {
            private static readonly PgSchema<Grant> Pg = PgSchema.For<Grant>();

            public static readonly PgExclusion NoOverlap = Pg.Exclusion(
                Pg.Equal(x => x.GranteeId).Overlaps(x => x.Period),
                deferral: Deferral.InitiallyDeferred,
                name: "ex_grant");
        }
        """;

    /// <summary>The design's own examples: a configuration, a value kept in a field, the read model, and a convention that amends.</summary>
    private const string Consumer = """
        public sealed class Context(DbContextOptions<Context> options) : DbContext(options)
        {
            public static DbContextOptions<Context> Options(string connection)
                => new DbContextOptionsBuilder<Context>().UseNpgsql(connection, o => o.UseSchemaKit()).Options;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.SuppressBtreeGistInjection();

                modelBuilder.Entity<Grant>().DeclarePostgres(pg =>
                [
                    Grants.NoOverlap,
                    pg.Exclusion(pg.Overlaps(x => x.Period).WithUncheckedOperator(x => x.GranteeId, "<>"), where: x => x.RevokedOn == null),
                ]);

                var notRevoked = PgSchema.Where<IWithdrawable>(x => x.RevokedOn == null);

                foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => e.ClrType.IsAssignableTo(typeof(IWithdrawable))))
                {
                    var changed = entity.Redeclare<PgExclusion>(constraint => constraint.AndWhere(notRevoked));

                    foreach (var constraint in entity.GetDeclarations<PgExclusion>())
                    {
                        var where = constraint.Where?.Template;
                        var named = constraint with { Name = constraint.Name ?? "ex_default" };

                        if (constraint is PgExclusion.Gist gist)
                        {
                            foreach (var element in gist.Elements)
                            {
                                var column = element.Target is KeyTarget.Column target ? target.Path : null;
                                var op = element.Operator == ExclusionOperator.Overlaps;
                            }
                        }
                    }

                    var found = entity.FindDeclaration<PgExclusion>("ex_grant");

                    // The class named, as a call without the extension syntax names it. A mark on
                    // the class reaches only this form: the compiler reports a marked type where
                    // it is written, and an extension call does not write it.
                    var again = SchemaDeclarationExtensions.GetDeclarations<PgExclusion>(entity);
                }
            }
        }
        """;

    /// <summary>What only a contributor's package does: declaring through the kernel's own method.</summary>
    private const string CallsDeclare = """
        public static class Contributor
        {
            public static void Apply(ModelBuilder modelBuilder) => modelBuilder.Entity<Grant>().Metadata.Declare([Grants.NoOverlap]);
        }
        """;

    /// <summary>What only a provider's package does: having a lambda spelled through the kernel's own factories.</summary>
    private const string CallsTheKernelsFactories = """
        public static class Spelled
        {
            public static readonly Predicate NotRevoked = Predicate.Of<IWithdrawable>(x => x.RevokedOn == null, SqlSpelling.PostgreSql);
        }
        """;

    private static List<Diagnostic> Compile(params string[] sources)
    {
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
                $"Consumer_{Guid.NewGuid():N}",
                sources.Select(source => CSharpSyntaxTree.ParseText(source)),
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("SK", StringComparison.Ordinal))];
    }

    [TestMethod]
    public void A_consumers_declarations_read_model_and_convention_compile_without_an_opt_in()
    {
        var diagnostics = Compile(Model, "using System.Linq; using Microsoft.EntityFrameworkCore; using CodoMetis.EFCore.SchemaKit; using CodoMetis.EFCore.SchemaKit.PostgreSQL;\n" + Consumer);

        Assert.AreEqual(0, diagnostics.Count, string.Join("\n", diagnostics));
    }

    /// <summary>
    /// The premise of the test above: the compilation does report the mark where it is. Without
    /// this, a compiler that reported no experimental diagnostic at all would pass it.
    /// </summary>
    [TestMethod]
    public void The_kernels_own_Declare_asks_for_the_opt_in()
    {
        var diagnostics = Compile(Model, "using Microsoft.EntityFrameworkCore; using CodoMetis.EFCore.SchemaKit;\n" + CallsDeclare);

        Assert.AreEqual(1, diagnostics.Count, string.Join("\n", diagnostics));
        Assert.AreEqual("SK9001", diagnostics[0].Id);
    }

    /// <summary>
    /// A lambda is spelled for one provider, by that provider's package (#132). A consumer who
    /// reaches for the kernel's factory is told so by the compiler, twice: for the method and for
    /// the spelling it has to name. The provider's own factory, in the first test, asks nothing.
    /// </summary>
    [TestMethod]
    public void The_kernels_lambda_factories_ask_for_the_opt_in()
    {
        var diagnostics = Compile(Model, "using CodoMetis.EFCore.SchemaKit;\n" + CallsTheKernelsFactories);

        Assert.IsTrue(diagnostics.Count >= 2, string.Join("\n", diagnostics));
        Assert.IsTrue(diagnostics.All(d => d.Id == "SK9001"), string.Join("\n", diagnostics));
    }
}
