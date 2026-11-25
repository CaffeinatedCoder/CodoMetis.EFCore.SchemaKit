using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>
/// What Npgsql's generator does with an operation is the package's dialect's to say (#116, #198),
/// and each of its rules is held here against the generator itself: a patch of the provider that
/// changes the SQL fails this before a consumer's migration meets it.
/// </summary>
[TestClass]
public sealed class NpgsqlOperationEffectTests
{
    private static (OperationEffect Effect, string Sql) Asked(MigrationOperation operation)
    {
        using var context = Contexts.Of<Models.Scheduling>();

        // Asked first, as the planner asks at diff time: Npgsql's generator rewrites the operation
        // it is handed (it turns the tsvector annotations into a computation).
        var effect = context.GetService<ISchemaDialect>().EffectOf(operation);
        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate([operation], context.GetService<IDesignTimeModel>().Model)
            .Select(command => command.CommandText));
        return (effect, sql);
    }

    private static AlterColumnOperation Alter(Action<AlterColumnOperation>? now = null, Action<AddColumnOperation>? before = null)
    {
        var old = new AddColumnOperation { ClrType = typeof(string), IsNullable = false };
        before?.Invoke(old);
        var alter = new AlterColumnOperation { Table = "role_grants", Name = "Note", ClrType = typeof(string), IsNullable = false, OldColumn = old };
        now?.Invoke(alter);
        return alter;
    }

    private static void Vector(ColumnOperation column, params string[] over)
    {
        column.ColumnType = "tsvector";
        column.AddAnnotation("Npgsql:TsVectorConfig", "english");
        column.AddAnnotation("Npgsql:TsVectorProperties", over);
    }

    public static IEnumerable<object[]> Alterations =>
    [
        ["becomes a generated tsvector", Alter(now: a => Vector(a, "Id"))],
        ["a tsvector over other columns", Alter(now: a => Vector(a, "Id"), before: a => Vector(a, "RoleId"))],
        ["a tsvector as it was", Alter(now: a => { Vector(a, "Id"); a.IsNullable = true; }, before: a => Vector(a, "Id"))],
        ["becomes computed", Alter(now: a => { a.ComputedColumnSql = "'x'"; a.IsStored = true; })],
        ["another type", Alter(now: a => a.ColumnType = "character varying(50)")],
        ["nullable", Alter(now: a => a.IsNullable = true)],
    ];

    /// <summary>
    /// Which alterations Npgsql writes as <c>DROP COLUMN</c> and <c>ADD COLUMN</c>: where it drops
    /// the column, PostgreSQL drops an index and an exclusion constraint with it without a word,
    /// and the dialect has to have said a dropped column. The generated <c>tsvector</c> is
    /// Npgsql's own rule; the computed column is the kernel's, which the dialect hands on.
    /// </summary>
    [TestMethod, DynamicData(nameof(Alterations))]
    public void The_dialect_says_a_dropped_column_where_the_generator_drops_one(string what, AlterColumnOperation operation)
    {
        var (effect, sql) = Asked(operation);

        var change = ((OperationEffect.Changes)effect).List.Single();
        Assert.AreEqual(sql.Contains("DROP COLUMN", StringComparison.Ordinal), change is BaseChange.ColumnDropped, $"{what}:\n{sql}");
        Assert.IsTrue(change is BaseChange.ColumnDropped or BaseChange.ColumnAltered, what);
    }

    /// <summary>
    /// A table that moves into a schema is moved by a statement, and so is one that moves to a
    /// schema the operation names. For one that moves back to the default schema, which the
    /// operation says as no schema, the generator writes nothing (#105): the dialect says so,
    /// with the tables and the way to have it written. A table already in the default schema
    /// under its name is where it is to be, and that is no move to write.
    /// </summary>
    [TestMethod]
    public void A_move_back_to_the_default_schema_is_not_written_and_the_dialect_says_so()
    {
        static RenameTableOperation Move(string? from, string? to) => new() { Name = "role_grants", Schema = from, NewName = "role_grants", NewSchema = to };

        var into = Asked(Move(null, "grants"));
        StringAssert.Contains(into.Sql, "ALTER TABLE role_grants SET SCHEMA grants;");
        Assert.IsInstanceOfType<BaseChange.TableMoved>(((OperationEffect.Changes)into.Effect).List.Single());

        var named = Asked(Move("grants", "public"));
        StringAssert.Contains(named.Sql, "ALTER TABLE grants.role_grants SET SCHEMA public;");
        Assert.IsInstanceOfType<BaseChange.TableMoved>(((OperationEffect.Changes)named.Effect).List.Single());

        var back = Asked(Move("grants", null));
        Assert.AreEqual("", back.Sql.Trim(), "Npgsql writes the move back: the rule and its refusal can go.");
        var notWritten = (OperationEffect.NotWritten)back.Effect;
        CollectionAssert.AreEqual(new[] { "grants.role_grants", "role_grants" }, notWritten.Tables.Select(table => table.ToString()).ToList());
        StringAssert.Contains(notWritten.WayOut, """ToTable("…", "public")""");

        var same = Asked(Move("public", null));
        Assert.AreEqual("", same.Sql.Trim());
        Assert.IsInstanceOfType<OperationEffect.Changes>(same.Effect);
    }

    /// <summary>Every operation type Npgsql brings is answered: one it gains fails this before a consumer's migration is refused for it.</summary>
    [TestMethod]
    public void Every_operation_type_of_Npgsql_is_described()
    {
        using var context = Contexts.Of<Models.Scheduling>();
        var dialect = context.GetService<ISchemaDialect>();

        var types = typeof(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(MigrationOperation).IsAssignableFrom(type))
            .ToList();

        Assert.IsTrue(types.Count >= 2, "Npgsql's operation types were not found.");
        foreach (var type in types)
            Assert.IsNotInstanceOfType<OperationEffect.Unknown>(dialect.EffectOf((MigrationOperation)Activator.CreateInstance(type)!), type.FullName);
    }

    /// <summary>
    /// What the planner does with the move that is not written: with an object on the table it
    /// refuses the migration, since the object would be kept or created where the table is not;
    /// with nothing declared there it reads no operation and is not in the way. With the default
    /// schema named in the model the move is an operation Npgsql writes, and nothing is refused.
    /// </summary>
    [TestMethod]
    public void A_move_that_is_not_written_is_refused_under_an_object_of_the_table()
    {
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => Contexts.Diff<Models.SchedulingMoved, Models.Scheduling>());
        StringAssert.Contains(refused.Message, "is on the table 'grants.role_grants', and this migration holds a RenameTableOperation");
        StringAssert.Contains(refused.Message, """Name the schema in the model, ToTable("…", "public")""");

        CollectionAssert.AreEqual(new[] { nameof(RenameTableOperation) }, Contexts.Diff<Models.GrantsMovedUndeclared, Models.GrantsUndeclared>().Shape());
        CollectionAssert.AreEqual(
            new[] { nameof(EnsureSchemaOperation), nameof(RenameTableOperation) },
            Contexts.Diff<Models.SchedulingMoved, Models.SchedulingInPublic>().Shape());
        CollectionAssert.AreEqual(new[] { nameof(RenameTableOperation) }, Contexts.Diff<Models.SchedulingInPublic, Models.Scheduling>().Shape());
    }
}
