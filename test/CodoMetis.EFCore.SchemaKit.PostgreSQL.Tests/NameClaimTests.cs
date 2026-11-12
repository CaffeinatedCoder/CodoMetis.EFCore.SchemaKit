using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

/// <summary>Models in which an exclusion constraint takes a name something else has, or seems to.</summary>
public static class ClaimModels
{
    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    private static EntityTypeBuilder<RoleGrant> Grants(ModelBuilder modelBuilder) => modelBuilder.Entity<RoleGrant>().ToTable("role_grants");

    private static EntityTypeBuilder<Booking> Bookings(ModelBuilder modelBuilder) => modelBuilder.Entity<Booking>().ToTable("bookings");

    private static PgExclusion Named(string name) => Pg.Exclusion(Pg.Overlaps(x => x.Period), name: name);

    private static PgExclusion OnBookings(PgSchema<Booking> pg, string name) => pg.Exclusion(pg.Overlaps(x => x.Period), name: name);

    public sealed class LikeItsPrimaryKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Named("PK_role_grants")]);
    }

    /// <summary>PostgreSQL keeps the case of a delimited name: this is another name than the key's.</summary>
    public sealed class LikeItsPrimaryKeyInAnotherCase : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Named("pk_role_grants")]);
    }

    public sealed class LikeAnIndexOfAnotherTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("ix_shared")]);
            Bookings(modelBuilder).HasIndex(x => x.RoomId).HasDatabaseName("ix_shared");
        }
    }

    public sealed class LikeATable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("bookings")]);
            Bookings(modelBuilder);
        }
    }

    public sealed class LikeASequence : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence<int>("numbers");
            Grants(modelBuilder).DeclarePostgres(_ => [Named("numbers")]);
        }
    }

    public sealed class LikeACheckConstraintOfItsTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).ToTable(t => t.HasCheckConstraint("ck_shared", "\"RoleId\" > 0")).DeclarePostgres(_ => [Named("ck_shared")]);
    }

    /// <summary>A check constraint has no index, so its name is taken in its own table and nowhere else.</summary>
    public sealed class LikeACheckConstraintOfAnotherTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("ck_shared")]);
            Bookings(modelBuilder).ToTable(t => t.HasCheckConstraint("ck_shared", "\"RoomId\" > 0"));
        }
    }

    public sealed class LikeAForeignKeyOfItsTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Bookings(modelBuilder);
            Grants(modelBuilder).DeclarePostgres(_ => [Named("fk_shared")])
                .HasOne<Booking>().WithMany().HasForeignKey(x => x.RoleId).HasConstraintName("fk_shared");
        }
    }

    /// <summary>An index of EF Core's named like a table of EF Core's: the database refuses it, and it is not SchemaKit's to say.</summary>
    public sealed class TwoOfEFCoresOwnCollide : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("ex_fine")]);
            Bookings(modelBuilder).HasIndex(x => x.RoomId).HasDatabaseName("role_grants");
        }
    }

    public sealed class LikeAUniqueConstraintOfAnotherTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("ak_shared")]);
            Bookings(modelBuilder).HasAlternateKey(x => x.RoomId).HasName("ak_shared");
        }
    }

    /// <summary>The backing indexes are relations of one schema, though the constraints are on two tables.</summary>
    public sealed class OneNameOnTwoTables : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("ex_shared")]);
            Bookings(modelBuilder).DeclarePostgres(pg => [OnBookings(pg, "ex_shared")]);
        }
    }

    /// <summary>The model's default schema is where a table without one is: the index and the constraint are in one schema.</summary>
    public sealed class InTheDefaultSchemaNamedAndNot : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("app");
            Grants(modelBuilder).DeclarePostgres(_ => [Named("ix_shared")]);
            modelBuilder.Entity<Booking>().ToTable("bookings", "app").HasIndex(x => x.RoomId).HasDatabaseName("ix_shared");
        }
    }

    public sealed class TwoCollisions : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Grants(modelBuilder).DeclarePostgres(_ => [Named("PK_role_grants"), Named("bookings")]);
            Bookings(modelBuilder);
        }
    }

    /// <summary>In a schema, so that a name written with it would show.</summary>
    public sealed class TwoOfOneNameInASchema : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(_ => [Named("ex_shared")]);
            modelBuilder.Entity<Booking>().ToTable("bookings", "grants").DeclarePostgres(pg => [OnBookings(pg, "ex_shared")]);
        }
    }
}

/// <summary>
/// A name taken twice is found at the diff, not by the database in the middle of a migration
/// (#96, #44). Which names PostgreSQL keeps once, and where, is held against a server by
/// <c>NameClaimLiveTests</c>, row for row.
/// </summary>
[TestClass]
public sealed class NameClaimTests
{
    private static SchemaValidationException Refused<TModel>() where TModel : ITestModel
        => Assert.ThrowsExactly<SchemaValidationException>(() => Contexts.Diff<Models.Bare, TModel>());

    private static SchemaDiagnostic.NameCollision TheCollision<TModel>() where TModel : ITestModel
    {
        var refused = Refused<TModel>();
        Assert.HasCount(1, refused.Diagnostics, refused.Message);
        return (SchemaDiagnostic.NameCollision)refused.Diagnostics[0];
    }

    private const string Relations = "the relations of the default schema (tables, indexes, sequences, and the indexes behind constraints)";

    [TestMethod]
    public void Two_unnamed_constraints_that_differ_in_an_option_take_one_default_name()
    {
        var collision = TheCollision<Models.TwoUnnamedThatDifferInTheCondition>();

        Assert.AreEqual("SK1001", collision.Id);
        Assert.AreEqual("EX_role_grants_GranteeId_Period", collision.Name);
        Assert.AreEqual("the constraints of table 'role_grants'", collision.Namespace);
        Assert.AreEqual("Pg.Exclusion gist 'EX_role_grants_GranteeId_Period' on RoleGrant", collision.First);
        Assert.AreEqual(collision.First, collision.Second);
    }

    [TestMethod]
    public void A_constraint_named_like_its_tables_primary_key_is_refused()
    {
        var collision = TheCollision<ClaimModels.LikeItsPrimaryKey>();

        Assert.AreEqual("the constraints of table 'role_grants'", collision.Namespace);
        Assert.AreEqual("Pg.Exclusion gist 'PK_role_grants' on RoleGrant", collision.First);
        Assert.AreEqual("the primary key 'PK_role_grants' of 'role_grants'", collision.Second);
    }

    [TestMethod]
    public void A_name_that_differs_in_case_is_another_name()
        => Assert.HasCount(1, Contexts.Diff<Models.Bare, ClaimModels.LikeItsPrimaryKeyInAnotherCase>().Sql());

    [TestMethod]
    public void A_constraint_named_like_an_index_of_another_table_is_refused()
    {
        var collision = TheCollision<ClaimModels.LikeAnIndexOfAnotherTable>();

        Assert.AreEqual(Relations, collision.Namespace);
        Assert.AreEqual("the index 'ix_shared' on 'bookings'", collision.Second);
    }

    [TestMethod]
    public void A_constraint_named_like_a_table_is_refused()
        => Assert.AreEqual("the table 'bookings'", TheCollision<ClaimModels.LikeATable>().Second);

    [TestMethod]
    public void A_constraint_named_like_a_sequence_is_refused()
        => Assert.AreEqual("the sequence 'numbers'", TheCollision<ClaimModels.LikeASequence>().Second);

    [TestMethod]
    public void A_constraint_named_like_a_check_constraint_of_its_table_is_refused()
    {
        var collision = TheCollision<ClaimModels.LikeACheckConstraintOfItsTable>();

        Assert.AreEqual("the constraints of table 'role_grants'", collision.Namespace);
        Assert.AreEqual("the check constraint 'ck_shared' of 'role_grants'", collision.Second);
    }

    [TestMethod]
    public void A_constraint_named_like_a_check_constraint_of_another_table_is_not()
        => Assert.HasCount(1, Contexts.Diff<Models.Bare, ClaimModels.LikeACheckConstraintOfAnotherTable>().Sql());

    [TestMethod]
    public void A_constraint_named_like_a_foreign_key_of_its_table_is_refused()
    {
        var collision = TheCollision<ClaimModels.LikeAForeignKeyOfItsTable>();

        Assert.AreEqual("the constraints of table 'role_grants'", collision.Namespace);
        Assert.AreEqual("the foreign key 'fk_shared' of 'role_grants'", collision.Second);
    }

    [TestMethod]
    public void A_collision_between_two_of_EF_Cores_own_objects_is_not_reported_here()
        => Assert.HasCount(1, Contexts.Diff<Models.Bare, ClaimModels.TwoOfEFCoresOwnCollide>().Sql());

    [TestMethod]
    public void A_constraint_named_like_a_unique_constraint_of_another_table_is_refused()
    {
        var collision = TheCollision<ClaimModels.LikeAUniqueConstraintOfAnotherTable>();

        Assert.AreEqual(Relations, collision.Namespace);
        Assert.AreEqual("the unique constraint 'ak_shared' of 'bookings'", collision.Second);
    }

    [TestMethod]
    public void One_name_on_two_tables_of_a_schema_is_refused_for_the_index_behind_it()
    {
        var collision = TheCollision<ClaimModels.OneNameOnTwoTables>();

        Assert.AreEqual(Relations, collision.Namespace);
        // In the order of the model, which is the order of its entity types.
        Assert.AreEqual("Pg.Exclusion gist 'ex_shared' on Booking", collision.First);
        Assert.AreEqual("Pg.Exclusion gist 'ex_shared' on RoleGrant", collision.Second);
    }

    /// <summary>The same name in two schemas is two relations and two tables' constraints.</summary>
    [TestMethod]
    public void One_name_in_two_schemas_is_not_refused()
        => Assert.AreEqual(2, Contexts.Diff<Models.Bare, Models.TwoSchemas>().Sql().Count(sql => sql.Contains("EXCLUDE")));

    [TestMethod]
    public void A_table_without_a_schema_is_in_the_models_default_schema()
    {
        var collision = TheCollision<ClaimModels.InTheDefaultSchemaNamedAndNot>();

        Assert.AreEqual("the relations of schema 'app' (tables, indexes, sequences, and the indexes behind constraints)", collision.Namespace);
        Assert.AreEqual("the index 'ix_shared' on 'app.bookings'", collision.Second);
    }

    /// <summary>A constraint is named within its table: its name is written without the schema, wherever the table is (#84).</summary>
    [TestMethod]
    public void A_party_is_named_as_the_model_declares_it()
    {
        var refused = Refused<ClaimModels.TwoOfOneNameInASchema>();

        Assert.AreEqual(
            "SK1001: Pg.Exclusion gist 'ex_shared' on Booking and Pg.Exclusion gist 'ex_shared' on RoleGrant both claim the name 'ex_shared' among "
            + "the relations of schema 'grants' (tables, indexes, sequences, and the indexes behind constraints). "
            + "The database keeps a name once there: give one of them another name.",
            refused.Message);
    }

    [TestMethod]
    public void Every_collision_of_the_model_arrives_at_once()
    {
        var refused = Refused<ClaimModels.TwoCollisions>();

        CollectionAssert.AreEqual(
            new[] { "the primary key 'PK_role_grants' of 'role_grants'", "the table 'bookings'" },
            refused.Diagnostics.Cast<SchemaDiagnostic.NameCollision>().Select(collision => collision.Second).ToList());
        StringAssert.StartsWith(refused.Message, "The model declares 2 things the database would refuse:");
        StringAssert.Contains(refused.Message, refused.Diagnostics[0].Message);
        StringAssert.Contains(refused.Message, refused.Diagnostics[1].Message);
    }

    /// <summary>A snapshot that holds a collision is a model to get away from, and the way is a diff.</summary>
    [TestMethod]
    public void A_collision_where_the_migration_starts_does_not_stop_it()
        => Assert.HasCount(2, Contexts.Diff<Models.TwoUnnamedThatDifferInTheCondition, Models.Bare>().Sql());

    [TestMethod]
    public void A_new_database_and_the_pending_changes_check_are_refused_alike()
    {
        using var context = Contexts.Of<ClaimModels.LikeItsPrimaryKey>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        Assert.ThrowsExactly<SchemaValidationException>(() => differ.GetDifferences(null, context.Relational()));
        Assert.ThrowsExactly<SchemaValidationException>(() => differ.HasDifferences(null, context.Relational()));
    }
}
