using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;

public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
}

public sealed class Pet
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>The models of the SQLite tests. Each differs from <see cref="Indexed" /> in the one thing its name says.</summary>
public static class People
{
    public const string Name = "IX_people_lower_name";

    /// <summary>People by their name without regard to case: an index on an expression, which EF Core has no API for.</summary>
    public static readonly ProbeSqliteIndex ByLowerName = new(new KeyTarget.Template("lower({Name})")) { Name = Name };

    private static EntityTypeBuilder<Person> Persons(ModelBuilder modelBuilder, string table = "People")
        => modelBuilder.Entity<Person>().ToTable(table);

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder);
    }

    public sealed class Indexed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName);
    }

    /// <summary>The default name, <c>IX_People_lowerName</c>: the table, and the expression's letters and digits.</summary>
    public sealed class Unnamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = null });
    }

    public sealed class Renamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = "IX_people_name_ci" });
    }

    public sealed class Unique : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName with { IsUnique = true });
    }

    public sealed class NameColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Persons(modelBuilder).DeclareProbe(ByLowerName).Property(x => x.Name).HasColumnName("FullName");
    }

    /// <summary>The table renamed, and its primary key with it, as EF Core names one by convention: <c>PK_Persons</c>.</summary>
    public sealed class TableRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder, "Persons").DeclareProbe(ByLowerName);
    }

    /// <summary><see cref="Indexed" /> with a primary key whose name does not say the table's.</summary>
    public sealed class IndexedWithANamedKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName).HasKey(x => x.Id).HasName("PK_person");
    }

    public sealed class TableRenamedWithANamedKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder, "Persons").DeclareProbe(ByLowerName).HasKey(x => x.Id).HasName("PK_person");
    }

    // ── A change SQLite has no ALTER for: its provider creates the table again ──

    /// <summary>The index unchanged, and a column the index does not read altered.</summary>
    public sealed class IndexedWithARequiredEmail : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Persons(modelBuilder).DeclareProbe(ByLowerName).Property(x => x.Email).IsRequired();
    }

    public sealed class BareWithARequiredEmail : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).Property(x => x.Email).IsRequired();
    }

    public sealed class RenamedWithARequiredEmail : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = "IX_people_name_ci" }).Property(x => x.Email).IsRequired();
    }

    /// <summary>The index on the address, under the name the other models use.</summary>
    public sealed class IndexedByEmail : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Persons(modelBuilder).DeclareProbe(new ProbeSqliteIndex(new KeyTarget.Template("lower({Email})")) { Name = Name });
    }

    /// <summary>The address is gone, and the index of that name is on the person's name.</summary>
    public sealed class IndexedWithoutTheEmail : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName).Ignore(x => x.Email);
    }

    // ── Names: one namespace for the tables and indexes of a database ──

    /// <summary>The index named like an index of EF Core's own on another table.</summary>
    public sealed class NamedLikeAnIndexOfAnotherTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Pet>().ToTable("Pets").HasIndex(x => x.Name).HasDatabaseName("IX_shared");
            Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = "IX_shared" });
        }
    }

    /// <summary>A key's name and a check constraint's are taken nowhere in SQLite.</summary>
    public sealed class NamedLikeItsPrimaryKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = "PK_People" });
    }

    public sealed class NamedLikeACheckConstraint : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = "CK_People_Name" })
                .ToTable(t => t.HasCheckConstraint("CK_People_Name", "\"Name\" <> ''"));
    }

    public sealed class NamedLikeATable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Pet>().ToTable("Pets");
            Persons(modelBuilder).DeclareProbe(ByLowerName with { Name = "Pets" });
        }
    }

    /// <summary>Two names SQLite keeps apart: it folds the case of ASCII letters alone.</summary>
    public sealed class TwoIndexesThatDifferInTheCaseOfAnUmlaut : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Persons(modelBuilder).DeclareProbe(
                ByLowerName with { Name = "IX_Ä" },
                new ProbeSqliteIndex(new KeyTarget.Template("lower({Email})")) { Name = "IX_ä" });
    }
}

/// <summary>A database file of a test's own, deleted when the test is done.</summary>
internal sealed class SqliteDatabase : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"schemakit-probe-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_file};Pooling=False";

    public void Execute(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public List<string> Column(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>Every index with a statement of its own, as the database keeps it; the indexes behind keys have none.</summary>
    public List<string> Indexes()
        => Column("SELECT tbl_name || ' | ' || sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY 1");

    public List<string> Tables()
        => Column("SELECT sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");

    public void Create<TModel>() where TModel : ITestModel => Apply<TModel>(source: null, arrange: null);

    /// <summary>Applies what the differ scaffolds to get from one model to the other, through the provider's generator.</summary>
    /// <param name="arrange">Breaks a rule of the contributor, for a test that shows what the rule is for.</param>
    public void Migrate<TFrom, TTo>(Action<ProbeSqliteIndexContributor>? arrange = null)
        where TFrom : ITestModel
        where TTo : ITestModel
    {
        using var from = Contexts.Sqlite<TFrom>();
        Apply<TTo>(from.Relational(), arrange);
    }

    private void Apply<TTo>(IRelationalModel? source, Action<ProbeSqliteIndexContributor>? arrange) where TTo : ITestModel
    {
        using var context = Contexts.Sqlite<TTo>(ConnectionString);
        arrange?.Invoke(context.Contributor<ProbeSqliteIndexContributor>());

        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(source, context.Relational());
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.GetService<IDesignTimeModel>().Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
    }

    public void Dispose() => File.Delete(_file);
}
