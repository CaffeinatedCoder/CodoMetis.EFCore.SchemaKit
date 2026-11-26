using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;

/// <summary>
/// The grants of <see cref="Models" /> with SQL written by hand in a lambda, <see cref="Sql.Raw{T}" />
/// (issue 220), each beside the model that says the same through the contract's own template.
/// </summary>
public static class SqlRawModels
{
    public const string MixedName = "ix_role_grant_mixed";

    private static EntityTypeBuilder<RoleGrant> Grants(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoleGrant>().ToTable("role_grants");

    /// <summary><see cref="IndexModels.ByHandWrittenSql" />, as a consumer writes it.</summary>
    public sealed class IndexByHand : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder)
            .DeclarePostgres(pg => [pg.BTree(x => Sql.Raw<string>("""upper("Note")"""), name: "ix_by_hand")]);
    }

    /// <summary><see cref="Models.WithTheGranteeBesideSqlThatNamesNoColumn" />, as a consumer writes it.</summary>
    public sealed class ExclusionByHand : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period).Equal(x => Sql.Raw<string>("lower(\"Note\")")), name: Models.OverNotes)]);
    }

    /// <summary>
    /// A key that is SQL with a column named through a placeholder, and a condition that is a
    /// lambda with SQL as one of its parts.
    /// </summary>
    public sealed class Mixed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg =>
        [
            pg.BTree(
                [pg.Key(x => Sql.Raw<string>("upper({Note})")).Desc(), pg.Key(x => x.RoleId)],
                where: x => x.GranteeId > 0 && Sql.Raw<bool>("{RoleId} % 2 = 0 OR {Note} ~ '^x'"),
                name: MixedName),
        ]);
    }

    /// <summary>The same with the note's column renamed: the placeholder follows, and the object is rebuilt, since SQL written by hand may read anything.</summary>
    public sealed class MixedWithTheNoteRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Mixed.Build(modelBuilder);
            Grants(modelBuilder).Property(x => x.Note).HasColumnName("memo");
        }
    }

    /// <summary>A placeholder that names no property: the one path of a lambda that no compiler saw.</summary>
    public sealed class WithAPathThatNamesNothing : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder)
            .DeclarePostgres(pg => [pg.BTree(x => x.RoleId, where: x => Sql.Raw<bool>("{Remark} IS NULL"), name: "ix_remark")]);
    }
}
