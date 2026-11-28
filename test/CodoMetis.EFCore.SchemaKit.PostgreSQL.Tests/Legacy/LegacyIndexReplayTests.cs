using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy;

/// <summary>
/// The indexes of a migration 5.x scaffolded that only 5.x's SQL generator rendered (#28): a
/// <see cref="CreateIndexOperation" /> with the annotation <c>CustomIndex:IndexParts</c>. Replayed,
/// each is the statement the published 5.6.0 wrote for it, character for character: the oracle is
/// 5.x's own script, checked in beside the migrations
/// (<c>test/Fixtures/ComplexIndexes5x/ScaffoldedReplay</c>), and never a statement written here.
/// </summary>
[TestClass]
public sealed class LegacyIndexReplayTests
{
    private const string IndexParts = "CustomIndex:IndexParts";

    private static IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        using var context = LegacyFixture.Context<MovedModels.SlotAlone>();
        return context.GetService<IMigrationsSqlGenerator>().Generate(operations, model: null, options);
    }

    /// <summary>Every statement that creates or drops an index, in its order. Each is one line, in 5.x's script and here.</summary>
    private static List<string> IndexStatements(IEnumerable<string> sql)
        => [.. sql.SelectMany(text => text.Split('\n')).Select(line => line.Trim())
            .Where(line => line.StartsWith("CREATE INDEX", StringComparison.Ordinal)
                           || line.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal)
                           || line.StartsWith("DROP INDEX", StringComparison.Ordinal))];

    private static List<string> Replayed(IReadOnlyList<MigrationOperation> operations)
        => IndexStatements(Generate(operations).Select(command => command.CommandText));

    private static int Stamped(IEnumerable<MigrationOperation> operations) => operations.Count(o => o[IndexParts] is not null);

    [TestMethod]
    public void Up_is_the_statements_5x_wrote()
    {
        var (initial, changed) = (LegacyFixture.Replay[0], LegacyFixture.Replay[1]);
        MigrationOperation[] up = [.. initial.UpOperations, .. changed.UpOperations];
        Assert.AreEqual(12, Stamped(up), "The premise: ten indexes of the first migration and two of the second are 5.x's generator's alone.");

        var expected = IndexStatements(LegacyFixture.ReplayScripts.Up);

        Assert.HasCount(15, expected);
        CollectionAssert.AreEqual(expected, Replayed(up));
    }

    /// <summary>A Down creates what its Up dropped: such an operation is in the way back too.</summary>
    [TestMethod]
    public void Down_is_the_statements_5x_wrote()
    {
        var (initial, changed) = (LegacyFixture.Replay[0], LegacyFixture.Replay[1]);
        MigrationOperation[] down = [.. changed.DownOperations, .. initial.DownOperations];
        Assert.AreEqual(3, Stamped(down), "The premise: the second migration's Down creates three such indexes again.");

        var expected = IndexStatements(LegacyFixture.ReplayScripts.Down);

        Assert.HasCount(5, expected);
        CollectionAssert.AreEqual(expected, Replayed(down));
    }

    /// <summary>
    /// The script as a consumer gets it from <c>migrations script</c>: EF Core's own migrator over the
    /// compiled migrations. From its first transaction to its last it is 5.x's script, but for
    /// the version EF Core records: every statement, in its order, in and out of a transaction.
    /// </summary>
    [TestMethod]
    public void The_migrators_script_is_the_script_5x_wrote()
    {
        using var context = LegacyFixture.ReplayContext();
        var migrator = context.GetService<IMigrator>();

        // From the first migration on: how the history table comes to be is Npgsql's, and 11 writes
        // it otherwise than 10 did.
        static string Comparable(string script)
        {
            var text = script.ReplaceLineEndings("\n").Trim();
            return System.Text.RegularExpressions.Regex.Replace(
                text[text.IndexOf("START TRANSACTION;", StringComparison.Ordinal)..], @"'\d+\.\d+\.\d+[^']*'\);", "'<version>');");
        }

        Assert.AreEqual(
            Comparable(File.ReadAllText(Path.Combine(LegacyFixture.ReplayFolder, "Up.sql"))),
            Comparable(migrator.GenerateScript()));
        Assert.AreEqual(
            Comparable(File.ReadAllText(Path.Combine(LegacyFixture.ReplayFolder, "Down.sql"))),
            Comparable(migrator.GenerateScript(fromMigration: "20260102000000_Changed", toMigration: "0")));
    }

    /// <summary>The fixture of #218: 32 indexes, ten of them 5.x's generator's alone, beside those Npgsql writes.</summary>
    [TestMethod]
    public void The_migration_of_every_kind_of_index_is_the_statements_5x_wrote()
    {
        Assert.AreEqual(10, Stamped(LegacyFixture.IndexesUp));

        var expected = IndexStatements(LegacyFixture.IndexesScript);

        Assert.HasCount(32, expected);
        CollectionAssert.AreEqual(expected, Replayed(LegacyFixture.IndexesUp));
    }

    /// <summary>
    /// An index 5.x built with <c>CONCURRENTLY</c> ran outside the migration's transaction, and so
    /// does its replay: in 5.x's script it stands between a <c>COMMIT</c> and the next
    /// <c>START TRANSACTION</c>.
    /// </summary>
    [TestMethod]
    public void An_index_built_concurrently_runs_outside_the_transaction_and_no_other_does()
    {
        var commands = Generate(LegacyFixture.Replay[0].UpOperations);

        var outside = commands.Where(command => command.TransactionSuppressed).Select(command => command.CommandText.Trim()).ToList();

        CollectionAssert.AreEqual(new[] { """CREATE INDEX CONCURRENTLY ix_entry_code_upper ON ledger.entries ((upper("Code")));""" }, outside);
        var script = LegacyFixture.ReplayScripts.Up.ToList();
        var at = script.IndexOf(outside[0]);
        Assert.IsGreaterThan(0, at, "The premise: 5.x's script holds the statement by itself.");
        Assert.AreEqual("COMMIT;", script[at - 1]);
        StringAssert.StartsWith(script[at + 1], "START TRANSACTION;");
    }

    /// <summary>
    /// Npgsql puts each statement of an idempotent script into a <c>DO</c> block, where PostgreSQL
    /// refuses <c>CREATE INDEX CONCURRENTLY</c> (25001). The published 5.6.0 wrote that script
    /// for this migration, the statement inside the block. The replayed statement is refused as
    /// SchemaKit's own is (#155).
    /// </summary>
    [TestMethod]
    public void An_idempotent_script_with_an_index_built_concurrently_is_refused()
    {
        var up = LegacyFixture.Replay[0].UpOperations;

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Generate(up, MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.Script));

        StringAssert.Contains(refused.Message, "CreateIndexOperation 'ix_entry_code_upper' on table 'ledger.entries'");
        StringAssert.Contains(refused.Message, "CustomIndex:IndexParts");
        StringAssert.Contains(refused.Message, "CREATE INDEX CONCURRENTLY ix_entry_code_upper");

        // Without that one index the same migration is written.
        MigrationOperation[] rest = [.. up.Where(o => o is not CreateIndexOperation { Name: "ix_entry_code_upper" })];
        var written = string.Join("\n", Generate(rest, MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.Script).Select(c => c.CommandText));
        StringAssert.Contains(written, "CREATE INDEX ix_entry_title_search");
        Assert.IsFalse(written.Contains("CONCURRENTLY", StringComparison.Ordinal));
    }

    /// <summary>
    /// The migration's operations are the consumer's compiled code, and EF Core hands the same
    /// list to the generator each time the migration is applied or written into a script.
    /// </summary>
    [TestMethod]
    public void Replaying_leaves_the_migrations_operations_as_they_were()
    {
        var up = LegacyFixture.Replay[0].UpOperations;
        var before = Stamped(up);

        var first = Replayed(up);
        var second = Replayed(up);

        Assert.AreEqual(before, Stamped(up));
        CollectionAssert.AreEqual(first, second);
    }

    private static CreateIndexOperation Index(string parts = """[{"e":true,"v":"lower(\"Name\")"}]""")
    {
        var index = new CreateIndexOperation { Schema = "crm", Table = "Customer", Name = "IX_Customer_lowerName", Columns = ["lower(\"Name\")", "__requires_UseNpgsqlComplexIndexes__"] };
        index.AddAnnotation(IndexParts, parts);
        return index;
    }

    private static CreateIndexOperation With(string annotation, object? value)
    {
        var index = Index();
        index.AddAnnotation(annotation, value);
        return index;
    }

    [TestMethod]
    public void The_operation_the_refusals_start_from_is_written()
        => CollectionAssert.AreEqual(
            new[] { """CREATE INDEX "IX_Customer_lowerName" ON crm."Customer" ((lower("Name")));""" },
            Replayed([Index()]));

    /// <summary>What 5.x never wrote is not read loosely: the index would be built without it.</summary>
    [TestMethod]
    [DataRow("""{"e":true,"v":"x"}""", "no list of parts")]
    [DataRow("[]", "no list of parts, or an empty one")]
    [DataRow("""["lower(x)"]""", "a part of it is no object")]
    [DataRow("""[{"v":"x"}]""", "lacks 'e' or 'v'")]
    [DataRow("""[{"e":true}]""", "lacks 'e' or 'v'")]
    [DataRow("""[{"e":true,"v":""}]""", "lacks 'e' or 'v'")]
    [DataRow("""[{"e":"true","v":"x"}]""", "a part of it holds 'e'")]
    [DataRow("""[{"e":true,"v":"x","d":1}]""", "a part of it holds 'd'")]
    [DataRow("""[{"e":true,"v":"x","n":"Default"}]""", "a part of it holds 'n'")]
    [DataRow("""[{"e":true,"v":"x","n":1}]""", "a part of it holds 'n'")]
    [DataRow("""[{"e":true,"v":"x","c":"C"}]""", "a part of it holds 'c'")]
    [DataRow("""[{"IsExpression":true,"Value":"x"}]""", "a part of it holds 'IsExpression'")]
    [DataRow("[{", "its value is no JSON")]
    public void Parts_in_a_shape_5x_never_wrote_are_refused(string parts, string why)
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Generate([Index(parts)]));

        StringAssert.Contains(refused.Message, "CreateIndexOperation 'IX_Customer_lowerName' on table 'crm.Customer'");
        StringAssert.Contains(refused.Message, why);
    }

    [TestMethod]
    public void A_value_beside_the_parts_in_a_type_5x_never_wrote_is_refused()
    {
        (CreateIndexOperation Index, string Why)[] cases =
        [
            (With("Npgsql:IndexMethod", 1), "'Npgsql:IndexMethod' beside it is of type Int32"),
            (With("Npgsql:IndexOperators", "text_pattern_ops"), "'Npgsql:IndexOperators' beside it is text"),
            (With("Npgsql:IndexOperators", new object[] { 1 }), "'Npgsql:IndexOperators' beside it is of type Object[]"),
            (With("Relational:Collation", "C"), "'Relational:Collation' beside it is text"),
            (With("Npgsql:IndexInclude", "Rank"), "'Npgsql:IndexInclude' beside it is text"),
            (With("Npgsql:NullsDistinct", "false"), "'Npgsql:NullsDistinct' beside it is of type String"),
            (With("Npgsql:CreatedConcurrently", 1), "'Npgsql:CreatedConcurrently' beside it is of type Int32"),
            (With("Npgsql:StorageParameter:fillfactor", null), "'Npgsql:StorageParameter:fillfactor' beside it is null"),
            (With("Npgsql:StorageParameter:fillfactor", new[] { 70 }), "'Npgsql:StorageParameter:fillfactor' beside it is of type Int32[]"),
        ];

        foreach (var (index, why) in cases)
        {
            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Generate([index]), why);
            StringAssert.Contains(refused.Message, why);
        }
    }

    [TestMethod]
    public void The_annotation_with_a_value_that_is_no_text_is_refused()
    {
        var index = new CreateIndexOperation { Table = "Customer", Name = "IX_Customer_lowerName", Columns = ["x"] };
        index.AddAnnotation(IndexParts, true);

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Generate([index]));

        StringAssert.Contains(refused.Message, "its value is of type Boolean, and 5.x wrote text");
    }

    [TestMethod]
    public void The_annotation_on_another_operation_is_refused()
    {
        var drop = new DropIndexOperation { Table = "Customer", Name = "IX_Customer_lowerName" };
        drop.AddAnnotation(IndexParts, """[{"e":true,"v":"x"}]""");

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Generate([drop]));

        StringAssert.Contains(refused.Message, "DropIndexOperation");
        StringAssert.Contains(refused.Message, "5.x put the annotation on a CreateIndexOperation alone");
    }

    /// <summary>
    /// 5.x's generator read the parts and not the operation's columns, and wrote nothing for an
    /// annotation it did not know. The replay is that generator's statement, so neither changes it.
    /// </summary>
    [TestMethod]
    public void What_5x_passed_over_changes_nothing()
    {
        var index = With("Npgsql:IndexSortOrder", new[] { "Descending" });
        index.Columns = ["anything"];
        index.IsDescending = [true];

        CollectionAssert.AreEqual(Replayed([Index()]), Replayed([index]));
    }
}
