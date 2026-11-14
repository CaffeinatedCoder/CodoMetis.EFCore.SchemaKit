using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Tests.TranslatorParity;

internal enum ParityOutcome
{
    /// <summary>Both render, and PostgreSQL builds the same tree from both.</summary>
    Agree,

    /// <summary>The translator refuses the lambda at the declaration. Always allowed.</summary>
    RefusedAtDeclaration,

    /// <summary>Both render, differently.</summary>
    Differ,

    /// <summary>The package renders a lambda EF Core cannot translate on the server.</summary>
    PackageOnly,

    /// <summary>
    /// Accepted at the declaration, then refused by the resolver, which is where <c>migrations add</c>
    /// fails: never allowed unwaived.
    /// </summary>
    FailedAtMigrationsAdd,

    /// <summary>The declaration threw something other than <see cref="NotSupportedException"/>.</summary>
    CrashedAtDeclaration,
}

internal sealed record ParityResult(ParityCase Case, LambdaExpression? Lambda, ParityOutcome Outcome, string? Package, string? EfCore, string? Detail)
{
    public bool NeedsWaiver => Outcome is not (ParityOutcome.Agree or ParityOutcome.RefusedAtDeclaration);

    public override string ToString()
        => $"{Outcome}: {Case.Id}\n    package: {Package ?? "-"}\n    EF Core: {EfCore ?? "-"}{(Detail is null ? "" : "\n    " + Detail)}";
}

/// <summary>
/// Runs the catalogue: compiles each row with Roslyn, renders it through the translator and the
/// kernel's resolver and through EF Core's query translation, and classifies the pair.
/// </summary>
internal static class ParitySweep
{
    private static readonly Lazy<IReadOnlyList<ParityResult>> Cached = new(() => Run(OfflineConnection));

    /// <summary>A connection string nothing connects to: both sides render without a server.</summary>
    public const string OfflineConnection = "Host=localhost;Database=schemakit_tests";

    /// <summary>The sweep against the offline model, computed once per test run.</summary>
    public static IReadOnlyList<ParityResult> Results => Cached.Value;

    public static IReadOnlyList<ParityResult> Run(string connection)
    {
        var cases   = ParityCatalogue.Build();
        var lambdas = Compile(cases);

        // The conversions a row exercises are in the tree the compiler built for it.
        cases = cases.Select((c, i) => lambdas[i] is { } lambda
                                           ? c with { Constructs = new HashSet<string>(c.Constructs.Concat(ParityCatalogue.ConversionsIn(lambda))) }
                                           : c)
                     .ToList();

        // ── The package's side: the translator, then the resolver ──

        var templates = new Dictionary<int, string>();
        var refused   = new Dictionary<int, (ParityOutcome, string)>();

        for (var i = 0; i < cases.Count; i++)
        {
            if (lambdas[i] is not { } lambda)
                continue;

            try
            {
                templates[i] = cases[i].Kind == ParityKind.Filter
                                   ? LambdaTranslator.Predicate(lambda)
                                   : LambdaTranslator.Expression(lambda);
            }
            catch (NotSupportedException e)
            {
                refused[i] = (ParityOutcome.RefusedAtDeclaration, e.Message);
            }
            catch (Exception e)
            {
                refused[i] = (ParityOutcome.CrashedAtDeclaration, $"{e.GetType().Name}: {e.Message}");
            }
        }

        var rendered = new Dictionary<int, string>();
        var failed   = new Dictionary<int, string>();
        Resolve(templates, connection, rendered, failed);

        // ── EF Core's side: the same lambda in a query ──

        // Query translation is the slow half and each row is independent; one context per thread.
        var options = ParityModel.Options(connection);
        using var contexts = new ThreadLocal<ParityQueryContext>(() => new ParityQueryContext(options), trackAllValues: true);

        var results = new ParityResult?[cases.Count];
        Parallel.For(0, cases.Count, i =>
        {
            var @case  = cases[i];
            var lambda = lambdas[i];

            if (lambda is null)
                return;

            if (refused.TryGetValue(i, out var refusal))
            {
                results[i] = new ParityResult(@case, lambda, refusal.Item1, null, null, refusal.Item2);
                return;
            }

            if (failed.TryGetValue(i, out var failure))
            {
                results[i] = new ParityResult(@case, lambda, ParityOutcome.FailedAtMigrationsAdd, templates[i], null, failure);
                return;
            }

            var package = rendered[i];
            var efCore  = EfCoreSql(contexts.Value!, @case.Kind, lambda);

            if (efCore is null)
            {
                results[i] = new ParityResult(@case, lambda, ParityOutcome.PackageOnly, package, null, null);
                return;
            }

            string? detail = null;
            bool    agree;
            try
            {
                agree = PostgresCanonicalForm.Of(package) == PostgresCanonicalForm.Of(efCore);
            }
            catch (FormatException e)
            {
                agree  = false;
                detail = "could not parse: " + e.Message;
            }

            results[i] = new ParityResult(@case, lambda, agree ? ParityOutcome.Agree : ParityOutcome.Differ, package, efCore, detail);
        });

        foreach (var context in contexts.Values)
            context.Dispose();

        return results.OfType<ParityResult>().ToList();
    }

    // ── Compilation ──

    // Each row is one method on one line, so a compiler error names its row. Rows that are not C#
    // (x.Bool < true) are dropped; the coverage check guards against dropping a whole construct.
    private static LambdaExpression?[] Compile(IReadOnlyList<ParityCase> cases)
    {
        var excluded = new HashSet<int>();

        while (true)
        {
            var source = new StringBuilder();
            source.AppendLine("#nullable enable");
            source.AppendLine("using System; using System.Linq.Expressions; using CodoMetis.EFCore.SchemaKit.Tests.TranslatorParity;");
            source.AppendLine("public static class ParityRows {");
            source.AppendLine("static LambdaExpression E<T>(Expression<Func<ParityRow, T>> e) => e;");
            source.AppendLine("static LambdaExpression P(Expression<Func<ParityRow, bool>> p) => p;");
            const int firstRowLine = 5;

            for (var i = 0; i < cases.Count; i++)
            {
                var helper = cases[i].Kind == ParityKind.Filter ? "P" : "E";
                source.AppendLine(excluded.Contains(i)
                                      ? "//"
                                      : $"public static LambdaExpression R{i}() {{ {cases[i].Captures} return {helper}({cases[i].Lambda}); }}");
            }

            source.AppendLine("}");

            var compilation = CSharpCompilation.Create(
                "TranslatorParity.Generated",
                [CSharpSyntaxTree.ParseText(source.ToString())],
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var stream = new MemoryStream();
            var       result = compilation.Emit(stream);

            if (!result.Success)
            {
                var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                var rows   = errors.Select(d => d.Location.GetLineSpan().StartLinePosition.Line - firstRowLine).ToHashSet();

                if (rows.Any(r => r < 0 || r >= cases.Count || excluded.Contains(r)))
                    throw new InvalidOperationException("The sweep's generated source does not compile:\n" + string.Join("\n", errors));

                excluded.UnionWith(rows);
                continue;
            }

            var type = Assembly.Load(stream.ToArray()).GetType("ParityRows")!;
            return cases.Select((_, i) => excluded.Contains(i)
                                              ? null
                                              : (LambdaExpression)type.GetMethod($"R{i}")!.Invoke(null, null)!)
                        .ToArray();
        }
    }

    private static readonly Lazy<List<MetadataReference>> References = new(
        () => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
             .Split(Path.PathSeparator)
             .Append(typeof(ParityRow).Assembly.Location)
             .Distinct()
             .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
             .ToList());

    // ── The package's rendering ──

    /// <summary>The declaration a resolver is made for: the sweep is about templates, and any of epoch 1 will do.</summary>
    private static readonly ISchemaObject Declaration = new TestGuard.Trigger("Id");

    // Each template through the kernel's resolver, against the plain model: what a contributor's
    // Resolve gets for it, and where a template that is accepted at the declaration and names
    // nothing the model maps is refused.
    private static void Resolve(Dictionary<int, string> templates, string connection, Dictionary<int, string> rendered, Dictionary<int, string> failed)
    {
        using var context = new ParityQueryContext(ParityModel.Options(connection));
        var entityType = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ParityRow))!;
        var resolver   = context.GetService<SchemaResolverFactory>().Create(entityType, Declaration);

        foreach (var (row, template) in templates)
        {
            try
            {
                rendered[row] = resolver.Template(template).ToString();
            }
            catch (InvalidOperationException e)
            {
                failed[row] = $"{e.GetType().Name}: {e.Message}";
            }
        }
    }

    // ── EF Core's rendering ──

    // Captured values are inlined first, as constants: the package inlines them, and EF Core would
    // otherwise render a parameter.
    private static string? EfCoreSql(ParityQueryContext context, ParityKind kind, LambdaExpression lambda)
    {
        lambda = (LambdaExpression)new CaptureInliner().Visit(lambda);

        try
        {
            if (kind == ParityKind.Filter)
            {
                var sql   = context.Rows.Where((Expression<Func<ParityRow, bool>>)lambda).ToQueryString();
                var where = sql.IndexOf("\nWHERE ", StringComparison.Ordinal);
                return where < 0 ? "TRUE" : sql[(where + "\nWHERE ".Length)..];
            }

            // Select evaluates an untranslatable top-level projection on the client and still shows
            // a query; ORDER BY has no client fallback, so it says whether EF Core translates it.
            var key = Expression.Lambda(lambda.Body, lambda.Parameters);
            ((IQueryable)context.Rows).Provider.CreateQuery<ParityRow>(
                       Expression.Call(typeof(Queryable), nameof(Queryable.OrderBy), [typeof(ParityRow), lambda.ReturnType],
                                       context.Rows.AsQueryable().Expression, Expression.Quote(key)))
                   .ToQueryString();

            var select = ((IQueryable)context.Rows).Provider.CreateQuery(
                                    Expression.Call(typeof(Queryable), nameof(Queryable.Select), [typeof(ParityRow), lambda.ReturnType],
                                                    context.Rows.AsQueryable().Expression, Expression.Quote(lambda)))
                                .ToQueryString();
            var from = select.IndexOf("\nFROM ", StringComparison.Ordinal);
            return select["SELECT ".Length..from];
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class CaptureInliner : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
            => node.Expression is ConstantExpression closure
                   ? Expression.Constant(node.Member is FieldInfo field ? field.GetValue(closure.Value) : ((PropertyInfo)node.Member).GetValue(closure.Value),
                                         node.Type)
                   : base.VisitMember(node);
    }
}

/// <summary>The sweep's results held against the waiver file.</summary>
internal sealed record ParityAssessment(
    IReadOnlyList<ParityResult>                Unwaived,
    IReadOnlyList<string>                      Stale,
    IReadOnlyDictionary<ParityResult, Waiver> Waived)
{
    public static ParityAssessment Of(IReadOnlyList<ParityResult> results, IReadOnlyList<Waiver> waivers)
    {
        var rules   = waivers.Where(w => w.Kind == WaiverKind.Rule).ToDictionary(w => w.Title);
        var unknown = rules.Keys.Except(PostgresCanonicalForm.Rules).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"waivers.txt switches on rules the sweep does not know: {string.Join(", ", unknown)}.");

        var rows = new Dictionary<string, Waiver>();
        foreach (var waiver in waivers.Where(w => w.Kind != WaiverKind.Rule))
        foreach (var row in waiver.Rows)
            if (!rows.TryAdd(row, waiver))
                throw new InvalidOperationException($"waivers.txt lists '{row}' twice.");

        var unwaived  = new List<ParityResult>();
        var waived    = new Dictionary<ParityResult, Waiver>();
        var usedRules = new HashSet<string>();
        var usedRows  = new HashSet<string>();
        var all       = rules.Keys.ToHashSet();

        foreach (var result in results.Where(r => r.NeedsWaiver))
        {
            if (result.Outcome == ParityOutcome.Differ && all.Count > 0 && AgreeUnder(result, all))
            {
                // Each rule this row cannot do without is in use; the first one is its waiver.
                var needed = all.Where(rule => !AgreeUnder(result, all.Where(r => r != rule).ToHashSet())).ToList();
                usedRules.UnionWith(needed);
                waived[result] = rules[needed.FirstOrDefault() ?? all.First()];
                continue;
            }

            if (rows.TryGetValue(result.Case.Id, out var waiver))
            {
                waived[result] = waiver;
                usedRows.Add(result.Case.Id);
                continue;
            }

            unwaived.Add(result);
        }

        var stale = rules.Keys.Where(rule => !usedRules.Contains(rule)).Select(rule => $"rule: {rule}")
                         .Concat(rows.Keys.Where(row => !usedRows.Contains(row)).Select(row => $"- {row}  (under '{rows[row].Title}')"))
                         .ToList();

        return new ParityAssessment(unwaived, stale, waived);
    }

    private static bool AgreeUnder(ParityResult result, IReadOnlySet<string> rules)
    {
        try
        {
            return PostgresCanonicalForm.Of(result.Package!, rules) == PostgresCanonicalForm.Of(result.EfCore!, rules);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
