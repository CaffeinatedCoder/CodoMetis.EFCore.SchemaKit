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

    /// <summary>
    /// A table without a schema, in a model without a default schema, and one in a schema that
    /// one database or another puts such a table in. Only a provider's dialect knows which (#100).
    /// </summary>
    public sealed class WithoutASchemaAndInOne : IPlanModel
    {
        public const string Schema = "public";

        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([Unique("Thing")]);
            modelBuilder.Entity<Thing>().ToTable("Thing", Schema);
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

    /// <summary>
    /// Only the context's own model is held to its names. A snapshot that holds a collision is
    /// read on either side of a diff: where one starts, and where EF Core's Down ends (#98).
    /// </summary>
    [TestMethod]
    public void A_snapshot_that_holds_a_collision_is_read_on_either_side_of_a_diff()
    {
        using var colliding = SchemaOperationPlannerTests.Context<ClaimModels.OneNameOnTwoTables>();
        using var bare = SchemaOperationPlannerTests.Context<PlanModels.Bare>();
        var differ = bare.GetService<IMigrationsModelDiffer>();
        var snapshot = SchemaOperationPlannerTests.Model(colliding);

        CollectionAssert.Contains(SchemaOperationPlannerTests.Describe(differ.GetDifferences(snapshot, SchemaOperationPlannerTests.Model(bare))), "DROP INDEX ux_shared;");
        Assert.AreEqual(2, SchemaOperationPlannerTests.Describe(differ.GetDifferences(SchemaOperationPlannerTests.Model(bare), snapshot)).Count(statement => statement.Contains("ux_shared")));
    }

    /// <summary>The context's own model is held to its names where it stands first as well: Down of a first migration starts from it.</summary>
    [TestMethod]
    public void The_contexts_own_model_is_held_to_its_names_where_a_diff_starts_too()
    {
        using var colliding = SchemaOperationPlannerTests.Context<ClaimModels.OneNameOnTwoTables>();

        Assert.ThrowsExactly<SchemaValidationException>(() => colliding.GetService<IMigrationsModelDiffer>()
            .GetDifferences(SchemaOperationPlannerTests.Model(colliding), null));
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void An_object_named_like_a_table_of_EF_Cores_is_refused(Provider provider)
        => Assert.AreEqual("the table 'Thing'", TheCollision<ClaimModels.LikeATable>(provider).Second);

    /// <summary>
    /// Which schema a table without one is in is the database's, and a provider without a package
    /// has no dialect to say it: the kernel does not guess (#100). The same name in a named schema
    /// and in none is two names here, and a provider's package says otherwise where it knows.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer)]
    public void Without_a_providers_rule_a_table_without_a_schema_is_in_no_named_one(Provider provider)
        => Assert.AreEqual(1, Create<ClaimModels.WithoutASchemaAndInOne>(provider).Count(statement => statement.Contains("UNIQUE INDEX")));

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_kernels_dialect_knows_no_default_schema(Provider provider)
    {
        using var context = SchemaOperationPlannerTests.Context<LegacyModels.Undeclared>(provider);

        Assert.IsNull(context.GetService<ISchemaDialect>().DefaultSchema);
    }

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
