using CodoMetis.EFCore.SchemaKit.Tests.TranslatorParity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CodoMetis.EFCore.SchemaKit.Tests.Integration;

/// <summary>
/// The translator's parity sweep on a live server, ported from 5.x. The unit sweep compares
/// renderings; this runs them.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class TranslatorParityLiveTests
{
    private NpgsqlConnection _server = null!;
    private string _connection = "";

    [TestInitialize]
    public void Connect()
    {
        _server = PostgresServer.Open();
        _connection = Environment.GetEnvironmentVariable(PostgresServer.Variable)!;

        _server.Execute($"DROP TABLE IF EXISTS {ParityModel.Table}");
        using var context = new ParityQueryContext(ParityModel.Options(_connection));
        context.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    [TestCleanup]
    public void Disconnect()
    {
        // Without a server the test went inconclusive before it connected, and there is nothing to tidy up.
        if (_server is null) return;

        _server.Execute($"DROP TABLE IF EXISTS {ParityModel.Table}");
        _server.Dispose();
    }

    /// <summary>
    /// A pair the canonical form calls equal, or a waiver calls equivalent, must select the same
    /// rows (predicates) and compute the same values (expressions) as EF Core's SQL on PostgreSQL
    /// 18, over rows where every nullable operand is both null and set. An expression a waiver
    /// calls equivalent must also serve EF Core's query from an index built on the translator's
    /// SQL, which is the point of writing it alike.
    /// </summary>
    [TestMethod]
    public void Translated_predicates_and_expressions_answer_on_postgres_as_ef_cores_queries_do()
    {
        var results    = ParitySweep.Run(_connection);
        var assessment = ParityAssessment.Of(results, ParityWaivers.Load());

        using (var context = new ParityQueryContext(ParityModel.Options(_connection)))
        {
            context.Rows.AddRange(ParitySeed.Rows());
            context.SaveChanges();
        }

        var claimed = results.Where(r => r.Package is not null && r.EfCore is not null)
                             .Where(r => r.Outcome == ParityOutcome.Agree
                                      || assessment.Waived.TryGetValue(r, out var waiver) && waiver.Kind is WaiverKind.Rule or WaiverKind.Same)
                             .ToList();

        Assert.IsTrue(claimed.Count >= 1400, $"Rows claimed to answer alike: {claimed.Count}");

        var failures = new List<string>();
        foreach (var result in claimed)
        {
            var (package, efCore) = result.Case.Kind == ParityKind.Filter
                                        ? ($"SELECT \"Id\"::text FROM {ParityModel.Table} WHERE {result.Package} ORDER BY 1",
                                           $"SELECT p.\"Id\"::text FROM {ParityModel.Table} AS p WHERE {result.EfCore} ORDER BY 1")
                                        : ($"SELECT \"Id\" || ': ' || coalesce(({result.Package})::text, 'NULL') FROM {ParityModel.Table} ORDER BY \"Id\"",
                                           $"SELECT p.\"Id\" || ': ' || coalesce(({result.EfCore})::text, 'NULL') FROM {ParityModel.Table} AS p ORDER BY p.\"Id\"");

            try
            {
                var packageAnswer = Answer(package);
                var efCoreAnswer  = Answer(efCore);
                if (packageAnswer != efCoreAnswer)
                    failures.Add($"{result}\n    answers: package [{packageAnswer}], EF Core [{efCoreAnswer}]");
            }
            catch (PostgresException e)
            {
                failures.Add($"{result}\n    fails on PostgreSQL: {e.MessageText}");
            }
        }

        // Rendered differently and called equivalent: PostgreSQL must match the index to the query.
        foreach (var result in claimed.Where(r => r.Case.Kind == ParityKind.Expression && r.Outcome == ParityOutcome.Differ))
        {
            try
            {
                _server.Execute($"CREATE INDEX parity_probe ON {ParityModel.Table} (({result.Package}))");
                var plan = Answer($"SET enable_seqscan = off; EXPLAIN SELECT p.\"Id\" FROM {ParityModel.Table} AS p WHERE ({result.EfCore}) IS NULL");
                if (!plan.Contains("parity_probe", StringComparison.Ordinal))
                    failures.Add($"{result}\n    EF Core's query does not use an index on the translator's expression:\n{plan}");
            }
            catch (PostgresException e)
            {
                failures.Add($"{result}\n    index fails on PostgreSQL: {e.MessageText}");
            }
            finally
            {
                // The probe may not exist when its CREATE failed; this is the harness tidying up, not a migration.
                _server.Execute("DROP INDEX IF EXISTS parity_probe");
            }
        }

        Assert.AreEqual(0, failures.Count, $"{failures.Count} of {claimed.Count} rows answer differently on PostgreSQL:\n\n" + string.Join("\n\n", failures.Take(20)));
    }

    /// <summary>
    /// The failure the concatenation rule was written after: <c>"Str" || "NStr"</c> is NULL for a
    /// null <c>NStr</c>, and a unique index ignores NULL keys, so two rows went in although
    /// <c>Str + NStr</c> is the same string for both.
    /// </summary>
    [TestMethod]
    public void A_unique_index_on_a_translated_concatenation_refuses_a_duplicate_where_an_operand_is_null()
    {
        System.Linq.Expressions.Expression<Func<ParityRow, string>> key = x => x.Str + x.NStr;
        using var context = new ParityQueryContext(ParityModel.Options(_connection));
        var entityType = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ParityRow))!;
        var expression = context.GetService<CodoMetis.EFCore.SchemaKit.Resolution.Internal.SchemaResolverFactory>()
            .Create(entityType, new Harness.TestGuard.Trigger("Id"))
            .Template(CodoMetis.EFCore.SchemaKit.Translation.Internal.LambdaTranslator.Expression(key));

        _server.Execute($"CREATE UNIQUE INDEX ux_parity_label ON {ParityModel.Table} (({expression}))");
        context.Rows.Add(Row(1));
        context.SaveChanges();

        context.Rows.Add(Row(2));
        var error = Assert.ThrowsExactly<DbUpdateException>(() => context.SaveChanges());

        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, ((PostgresException)error.InnerException!).SqlState);
    }

    // No char is left at its default: PostgreSQL's text and jsonb cannot hold U+0000.
    private static ParityRow Row(int id) => new() { Id = id, Str = "A", Chr = 'x', Doc = new ParityDoc { Chr = 'x' } };

    private string Answer(string sql)
    {
        using var command = new NpgsqlCommand(sql, _server);
        using var reader  = command.ExecuteReader();
        var rows = new List<string>();
        do
        {
            while (reader.Read())
                rows.Add(reader.GetString(0));
        } while (reader.NextResult());

        return string.Join(" | ", rows);
    }
}
