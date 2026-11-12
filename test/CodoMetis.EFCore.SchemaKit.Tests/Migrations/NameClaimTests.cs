using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

public static class ClaimModels
{
    private static TestGuard.Unique Unique(string name) => new([new GuardKey("Name")]) { Name = name };

    /// <summary>The sample guard is an index whose name is taken in the schema, as PostgreSQL's is.</summary>
    public sealed class OneNameOnTwoTables : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([Unique("ux_shared")]);
            modelBuilder.Entity<Thing>().Metadata.Declare([Unique("ux_shared")]);
        }
    }

    public sealed class TwoNamesThatDifferInCase : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([Unique("ux_shared")]);
            modelBuilder.Entity<Thing>().Metadata.Declare([Unique("UX_SHARED")]);
        }
    }

    /// <summary>One of the two is of a kind whose names the database compares without regard to case.</summary>
    public sealed class TwoNamesThatDifferInCaseOneOfThemLoose : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([Unique("ux_shared")]);
            modelBuilder.Entity<Thing>().Metadata.Declare([new LooseGuard("Name") { Name = "UX_SHARED" }]);
        }
    }

    public sealed class LikeATable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([Unique("Thing")]);
            modelBuilder.Entity<Thing>();
        }
    }

    /// <summary>Without a provider's package an index of EF Core's is named within its table.</summary>
    public sealed class LikeAnIndexOfAnotherTable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([Unique("ix_shared")]);
            modelBuilder.Entity<Thing>().HasIndex(x => x.Name).HasDatabaseName("ix_shared");
        }
    }

    /// <summary>A trigger and an index are named in two namespaces.</summary>
    public sealed class ATriggerAndAnIndexOfOneName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Animal>().Metadata.Declare([Unique("shared"), new TestGuard.Trigger("Name") { Name = "shared" }]);
    }

    /// <summary>A trigger is named within its table. Two of one name on one table are one declaration key, which the store refuses.</summary>
    public sealed class TwoTriggersOfOneNameOnTwoTables : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([new TestGuard.Trigger("Name") { Name = "shared" }]);
            modelBuilder.Entity<Thing>().Metadata.Declare([new TestGuard.Trigger("Name") { Name = "shared" }]);
        }
    }
}

/// <summary>
/// The planner asks every contributor what names its objects take and refuses a model in which
/// one is taken twice (#96, #44), on every provider. What EF Core's own objects take is the
/// dialect's answer; these are the kernel's defaults.
/// </summary>
[TestClass]
public sealed class NameClaimTests
{
    private static List<string> Create<TModel>(Provider provider) where TModel : IPlanModel
    {
        using var context = SchemaOperationPlannerTests.Context<TModel>(provider);
        return SchemaOperationPlannerTests.Describe(
            context.GetService<IMigrationsModelDiffer>().GetDifferences(null, SchemaOperationPlannerTests.Model(context)));
    }

    private static SchemaDiagnostic.NameCollision TheCollision<TModel>(Provider provider) where TModel : IPlanModel
    {
        var refused = Assert.ThrowsExactly<SchemaValidationException>(() => Create<TModel>(provider));
        Assert.HasCount(1, refused.Diagnostics, refused.Message);
        return (SchemaDiagnostic.NameCollision)refused.Diagnostics[0];
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void Two_objects_that_claim_one_name_in_one_namespace_are_refused(Provider provider)
    {
        var collision = TheCollision<ClaimModels.OneNameOnTwoTables>(provider);

        Assert.AreEqual("ux_shared", collision.Name);
        // In the order of the model's entity types.
        Assert.AreEqual("Test.Guard unique 'ux_shared' on Thing", collision.First);
        Assert.AreEqual("Test.Guard unique 'ux_shared' on Animal", collision.Second);
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void An_object_named_like_a_table_of_EF_Cores_is_refused(Provider provider)
        => Assert.AreEqual("the table 'Thing'", TheCollision<ClaimModels.LikeATable>(provider).Second);

    [TestMethod]
    public void Names_that_differ_in_case_are_two_names()
        => Assert.AreEqual(2, Create<ClaimModels.TwoNamesThatDifferInCase>(Provider.Npgsql).Count(statement => statement.Contains("UNIQUE INDEX")));

    [TestMethod]
    public void Names_that_differ_in_case_are_one_when_either_declaration_says_so()
        => Assert.AreEqual("Test.Loose loose 'UX_SHARED' on Thing", TheCollision<ClaimModels.TwoNamesThatDifferInCaseOneOfThemLoose>(Provider.Npgsql).First);

    [TestMethod]
    public void Without_a_providers_rule_an_index_of_EF_Cores_is_named_within_its_table()
        => CollectionAssert.Contains(Create<ClaimModels.LikeAnIndexOfAnotherTable>(Provider.Npgsql), "CreateIndexOperation");

    [TestMethod]
    public void One_name_in_two_namespaces_is_taken_once_in_each()
        => Assert.AreEqual(2, Create<ClaimModels.ATriggerAndAnIndexOfOneName>(Provider.Npgsql).Count(statement => statement.Contains("shared")));

    [TestMethod]
    public void A_name_taken_within_a_table_is_free_in_another()
        => Assert.AreEqual(2, Create<ClaimModels.TwoTriggersOfOneNameOnTwoTables>(Provider.Npgsql).Count(statement => statement.Contains("shared")));

    /// <summary>The kernel's dialect, for a provider without a package: every kind of EF Core's objects and the namespace its name is in.</summary>
    [TestMethod]
    [DataRow(SchemaModelObject.Table, typeof(NameClaim.Relation))]
    [DataRow(SchemaModelObject.Sequence, typeof(NameClaim.Relation))]
    [DataRow(SchemaModelObject.Index, typeof(NameClaim.TableIndex))]
    [DataRow(SchemaModelObject.PrimaryKey, typeof(NameClaim.Constraint))]
    [DataRow(SchemaModelObject.UniqueConstraint, typeof(NameClaim.Constraint))]
    [DataRow(SchemaModelObject.ForeignKey, typeof(NameClaim.Constraint))]
    [DataRow(SchemaModelObject.CheckConstraint, typeof(NameClaim.Constraint))]
    public void The_default_scopes(SchemaModelObject kind, Type expected)
    {
        using var context = SchemaOperationPlannerTests.Context<LegacyModels.Undeclared>();
        var dialect = context.GetService<ISchemaDialect>();
        var table = new TableRef(dialect.Identifier("app"), dialect.Identifier("things"));

        var claims = dialect.Claims(kind, table, dialect.Identifier("named")).ToList();

        Assert.HasCount(1, claims);
        Assert.AreEqual(expected, claims[0].GetType());
    }

    [TestMethod]
    public void Every_kind_of_object_has_a_scope()
        => Assert.HasCount(7, Enum.GetValues<SchemaModelObject>());
}
