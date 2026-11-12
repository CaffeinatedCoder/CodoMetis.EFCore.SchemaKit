using System.Text;

namespace SchemaKit.Spikes.E;

/// <summary>
/// One test per cell of the impact table: an object, an operation, a server, and what the server
/// did. The expectation is the table the plan's section 3.7 is written from.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Cells
{
    private static readonly Dictionary<string, Server> Open = [];

    public TestContext TestContext { get; set; } = null!;

    private static Server Get(string server)
    {
        if (Open.TryGetValue(server, out var open)) return open;

        return Open[server] = server switch
        {
            "PostgreSQL 16"   => Postgres.Open(server, "SPIKE_E_PG16"),
            "PostgreSQL 18"   => Postgres.Open(server, "SPIKE_E_PG18"),
            "SQL Server 2022" => SqlServer.Open(server, "SPIKE_E_MSSQL"),
            _                 => throw new ArgumentOutOfRangeException(nameof(server)),
        };
    }

    [ClassCleanup]
    public static void Close()
    {
        foreach (var server in Open.Values) server.Dispose();
        Open.Clear();
    }

    public static IEnumerable<object[]> All => Expected.Cells.Select(c => new object[] { c.Server, c.Object, c.Operation, c.Outcome });

    [TestMethod, DynamicData(nameof(All))]
    public void Cell(string server, string @object, string operation, Outcome expected)
    {
        var on = Get(server);
        var measured = on.Measure(on.Objects.Single(o => o.Name == @object), on.Operations.Single(o => o.Name == operation));

        TestContext.WriteLine($"{server} | {@object} | {operation} | {measured}");
        Assert.AreEqual(expected, measured.Outcome, $"{server}: {operation} with {@object}: {measured}");
    }

    /// <summary>
    /// A refusal is the object's doing only if the operation succeeds without it. This is the row
    /// that makes a <see cref="Outcome.Blocks" /> mean what it says.
    /// </summary>
    [TestMethod, DataRow("PostgreSQL 16"), DataRow("PostgreSQL 18"), DataRow("SQL Server 2022")]
    public void Every_operation_succeeds_on_a_table_nothing_depends_on(string server)
    {
        var on = Get(server);

        foreach (var operation in on.Operations)
            Assert.IsNull(on.Baseline(operation), $"{server}: {operation.Name}");
    }

    /// <summary>The expected table has a cell for every object and operation of every server: none is left unmeasured.</summary>
    [TestMethod, DataRow("PostgreSQL 16"), DataRow("PostgreSQL 18"), DataRow("SQL Server 2022")]
    public void The_table_is_complete(string server)
    {
        var on = Get(server);
        var expected = Expected.Cells.Where(c => c.Server == server).Select(c => (c.Object, c.Operation)).ToList();
        var all = on.Objects.SelectMany(o => on.Operations.Select(op => (o.Name, op.Name))).ToList();

        CollectionAssert.AreEquivalent(all, expected);
    }

    /// <summary>
    /// Measures every cell and writes the table, with the server's own words for a refusal. It
    /// asserts nothing: the cells do. Schema names are numbered per cell, so they are replaced.
    /// </summary>
    [TestMethod, DataRow("PostgreSQL 16"), DataRow("PostgreSQL 18"), DataRow("SQL Server 2022")]
    public void Print_the_table(string server)
    {
        var on = Get(server);
        StringBuilder table = new();
        table.AppendLine($"| {server} | {string.Join(" | ", on.Operations.Select(o => o.Name))} |");
        table.AppendLine($"|---|{string.Concat(on.Operations.Select(_ => "---|"))}");
        StringBuilder details = new();

        foreach (var kind in on.Objects)
        {
            List<string> row = [];
            foreach (var operation in on.Operations)
            {
                var measured = on.Measure(kind, operation);
                row.Add(measured.Outcome.ToString());
                if (measured.Detail.Length > 0)
                    details.AppendLine($"- `{kind.Name}`, {operation.Name}: {System.Text.RegularExpressions.Regex.Replace(measured.Detail, @"\bc\d+\b", "s")}");
            }

            table.AppendLine($"| `{kind.Name}` | {string.Join(" | ", row)} |");
        }

        StringBuilder statements = new();
        foreach (var operation in on.Operations)
            statements.AppendLine($"- {operation.Name}: `{string.Join(" ", on.Statements(operation)).ReplaceLineEndings(" ")}`");

        var report = $"{table}\nWhat the server said, and what it rebuilt:\n\n{details}\nThe statements EF Core writes:\n\n{statements}";
        Console.WriteLine(report);

        // CI puts these into the job summary: the table is the spike's result, not only its log.
        if (Environment.GetEnvironmentVariable("SPIKE_E_OUT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{server.Replace(' ', '-')}.md"), report);
        }
    }
}
