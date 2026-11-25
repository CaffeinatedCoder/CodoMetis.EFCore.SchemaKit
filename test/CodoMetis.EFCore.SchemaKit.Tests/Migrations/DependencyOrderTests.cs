using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

public sealed class Workshop
{
    public int Id { get; set; }
}

public sealed class Shed
{
    public int Id { get; set; }

    public string Lock { get; set; } = "";
}

public static class NeedyModels
{
    /// <summary>A routine on the workshop that needs the workshop's trigger, and that trigger declared on the workshop.</summary>
    public sealed class NeedsItsOwnTablesTrigger : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var workshop = modelBuilder.Entity<Workshop>().Metadata;
            workshop.Declare([new TestRoutine(["Test.Guard:tr_lock@"]) { Name = "saw" }]);
            workshop.Declare([(TestGuard)new TestGuard.Trigger("Id") { Name = "tr_lock" }]);
        }
    }

    /// <summary>The same routine, and a trigger of that name on another table alone.</summary>
    public sealed class NeedsATriggerAnotherTableHas : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Workshop>().Metadata.Declare([new TestRoutine(["Test.Guard:tr_lock@"]) { Name = "saw" }]);
            modelBuilder.Entity<Shed>().Metadata.Declare([(TestGuard)new TestGuard.Trigger("Lock") { Name = "tr_lock" }]);
        }
    }

    private static TestRoutine Routine(string name, params string[] needs) => new([.. needs.Select(need => $"Test.Routine:{need}")]) { Name = name };

    private static TestType Type(string name, params string[] needs) => new([.. needs]) { Name = name };

    private static void Declare(ModelBuilder modelBuilder, params object[] declarations)
    {
        var workshop = modelBuilder.Entity<Workshop>().Metadata;
        workshop.Declare([.. declarations.OfType<TestRoutine>()]);
        workshop.Declare([.. declarations.OfType<TestType>()]);
    }

    public sealed class Bare : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Workshop>();
    }

    /// <summary>A chain of three, written with the one that needs the most first: saw needs bench, bench needs floor.</summary>
    public sealed class AChainWrittenBackwards : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Routine("saw", "bench"), Routine("bench", "floor"), Routine("floor"));
    }

    /// <summary>Three that need nothing, in the order written.</summary>
    public sealed class ThreeThatNeedNothing : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Routine("saw"), Routine("bench"), Routine("floor"));
    }

    /// <summary>Two that need each other.</summary>
    public sealed class ACircle : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Routine("saw", "bench"), Routine("bench", "saw"));
    }

    /// <summary>One that needs what the model does not declare.</summary>
    public sealed class NeedsWhatIsNotDeclared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Routine("saw", "bench"));
    }

    /// <summary>A routine, created after the tables, that needs a type, created before them.</summary>
    public sealed class ARoutineThatNeedsAType : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestRoutine(["Test.Type:plank"]) { Name = "saw" }, Type("plank"));
    }

    /// <summary>A type, created before the tables, that says it needs a routine, created after them.</summary>
    public sealed class ATypeThatNeedsARoutine : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Type("plank", "Test.Routine:saw"), Routine("saw"));
    }

    /// <summary>A chain of three types: beam needs board, board needs plank.</summary>
    public sealed class AChainOfTypes : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Declare(modelBuilder, Type("beam", "Test.Type:board"), Type("board", "Test.Type:plank"), Type("plank"));
    }

    /// <summary>The same three, needing nothing.</summary>
    public sealed class ThreeTypesThatNeedNothing : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Type("beam"), Type("board"), Type("plank"));
    }

    /// <summary>A type that needs the type plank, in a model whose only plank is a routine.</summary>
    public sealed class NeedsATypeWhereARoutineHasTheName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Type("board", "Test.Type:plank"), Routine("plank"));
    }
}

/// <summary>
/// What an object says it needs decides when it is created and dropped (#115, #195): after what
/// it needs and before it, whatever the model's order says. The sample objects are routines,
/// created after the tables, and types, created before them.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DependencyOrderTests
{
    private static Planned<TModel> Context<TModel>()
        where TModel : IPlanModel
        => SchemaOperationPlannerTests.Context<TModel>(more: Needy);

    private static void Needy(IServiceCollection services)
        => services.AddSchemaContributor<TestRoutineContributor>().AddSchemaContributor<TestTypeContributor>();

    private static List<string> Statements<TFrom, TTo>()
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = Context<TFrom>();
        using var to = Context<TTo>();
        return [.. SchemaOperationPlannerTests.Describe(
                to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to)))
            .Select(statement => statement.Replace("\"", ""))];
    }

    private static string Refused<TModel>()
        where TModel : IPlanModel
    {
        using var context = Context<TModel>();
        return Assert.ThrowsExactly<InvalidOperationException>(
            () => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, SchemaOperationPlannerTests.Model(context))).Message;
    }

    /// <summary>Created from what needs nothing up, and dropped from what nothing needs down, though the model wrote them the other way round.</summary>
    [TestMethod]
    public void A_chain_is_created_from_its_foot_and_dropped_from_its_head()
    {
        CollectionAssert.AreEqual(
            new[] { "CREATE ROUTINE floor;", "CREATE ROUTINE bench;", "CREATE ROUTINE saw;" },
            Statements<NeedyModels.Bare, NeedyModels.AChainWrittenBackwards>());

        CollectionAssert.AreEqual(
            new[] { "DROP ROUTINE saw;", "DROP ROUTINE bench;", "DROP ROUTINE floor;" },
            Statements<NeedyModels.AChainWrittenBackwards, NeedyModels.Bare>());
    }

    /// <summary>
    /// The control: what needs nothing keeps the model's order, which is the order of the stored
    /// keys and not the one written, created that way and dropped the other way round. That order
    /// is not the chain's, so the test above is held by the dependencies and by nothing else.
    /// </summary>
    [TestMethod]
    public void Objects_that_need_nothing_keep_the_order_they_had()
    {
        var creates = Statements<NeedyModels.Bare, NeedyModels.ThreeThatNeedNothing>();
        var drops = Statements<NeedyModels.ThreeThatNeedNothing, NeedyModels.Bare>();

        CollectionAssert.AreEquivalent(new[] { "CREATE ROUTINE floor;", "CREATE ROUTINE bench;", "CREATE ROUTINE saw;" }, creates);
        CollectionAssert.AreNotEqual(new[] { "CREATE ROUTINE floor;", "CREATE ROUTINE bench;", "CREATE ROUTINE saw;" }, creates);
        CollectionAssert.AreEqual(creates.Select(create => create.Replace("CREATE", "DROP")).Reverse().ToList(), drops);
    }

    /// <summary>Among what is created before the tables the same holds, and what is dropped after them.</summary>
    [TestMethod]
    public void What_tables_are_made_of_is_ordered_among_itself()
    {
        CollectionAssert.AreEqual(
            new[] { "CREATE TYPE plank;", "CREATE TYPE board;", "CREATE TYPE beam;" },
            Statements<NeedyModels.Bare, NeedyModels.AChainOfTypes>());

        CollectionAssert.AreEqual(
            new[] { "DROP TYPE beam;", "DROP TYPE board;", "DROP TYPE plank;" },
            Statements<NeedyModels.AChainOfTypes, NeedyModels.Bare>());

        // The control: without the dependencies neither order is the chain's.
        CollectionAssert.AreNotEqual(
            new[] { "CREATE TYPE plank;", "CREATE TYPE board;", "CREATE TYPE beam;" },
            Statements<NeedyModels.Bare, NeedyModels.ThreeTypesThatNeedNothing>());
        CollectionAssert.AreNotEqual(
            new[] { "DROP TYPE beam;", "DROP TYPE board;", "DROP TYPE plank;" },
            Statements<NeedyModels.ThreeTypesThatNeedNothing, NeedyModels.Bare>());
    }

    /// <summary>
    /// A routine needs a type: the type is created before the tables and the routine after them,
    /// and the routine is dropped before them and the type after. The phases give that order
    /// already. The other way round no order does, and it is refused.
    /// </summary>
    [TestMethod]
    public void What_is_created_before_the_tables_cannot_need_what_is_created_after_them()
    {
        CollectionAssert.AreEqual(
            new[] { "CREATE TYPE plank;", "CREATE ROUTINE saw;" },
            Statements<NeedyModels.Bare, NeedyModels.ARoutineThatNeedsAType>());
        CollectionAssert.AreEqual(
            new[] { "DROP ROUTINE saw;", "DROP TYPE plank;" },
            Statements<NeedyModels.ARoutineThatNeedsAType, NeedyModels.Bare>());

        StringAssert.Contains(
            Refused<NeedyModels.ATypeThatNeedsARoutine>(),
            "Test.Type type 'plank' on Workshop is created before the tables and needs Test.Routine routine 'saw' on Workshop, which is created after them");
    }

    /// <summary>
    /// Which object is needed is its owner, its scope and its name together. A trigger's name is
    /// taken within its table, so a trigger of that name on another table is another object, and
    /// does not stand for the one that is needed.
    /// </summary>
    [TestMethod]
    public void An_object_of_the_same_name_in_another_scope_is_not_the_one_that_is_needed()
    {
        var created = Statements<NeedyModels.Bare, NeedyModels.NeedsItsOwnTablesTrigger>();
        Assert.IsTrue(created.IndexOf("CREATE ROUTINE saw;") >= 0, string.Join(" ", created));

        StringAssert.Contains(
            Refused<NeedyModels.NeedsATriggerAnotherTableHas>(),
            "Test.Routine routine 'saw' on Workshop needs the object 'tr_lock' of Test.Guard, and the model declares no such object");
    }

    /// <summary>And its owner counts: a routine of the name a type is needed under is not that type.</summary>
    [TestMethod]
    public void An_object_of_the_same_name_of_another_owner_is_not_the_one_that_is_needed()
        => StringAssert.Contains(
            Refused<NeedyModels.NeedsATypeWhereARoutineHasTheName>(),
            "Test.Type type 'board' on Workshop needs the object 'plank' of Test.Type, and the model declares no such object");

    [TestMethod]
    public void Objects_that_need_each_other_in_a_circle_are_refused_with_their_names()
    {
        var refused = Refused<NeedyModels.ACircle>();

        StringAssert.Contains(refused, "need each other in a circle");
        StringAssert.Contains(refused, "Test.Routine routine 'saw' on Workshop");
        StringAssert.Contains(refused, "Test.Routine routine 'bench' on Workshop");
    }

    /// <summary>
    /// In the consumer's own model only: a snapshot that holds such a dependency describes a
    /// database that exists, and is read, so that the way off it is a diff.
    /// </summary>
    [TestMethod]
    public void A_dependency_on_an_object_the_model_does_not_declare_is_refused_in_the_contexts_own_model()
    {
        StringAssert.Contains(
            Refused<NeedyModels.NeedsWhatIsNotDeclared>(),
            "Test.Routine routine 'saw' on Workshop needs the object 'bench' of Test.Routine, and the model declares no such object");

        CollectionAssert.AreEqual(new[] { "DROP ROUTINE saw;" }, Statements<NeedyModels.NeedsWhatIsNotDeclared, NeedyModels.Bare>());

        // And as the end of a diff another context asks for, which is how EF Core asks for Down.
        using var bare = Context<NeedyModels.Bare>();
        using var needy = Context<NeedyModels.NeedsWhatIsNotDeclared>();
        CollectionAssert.AreEqual(
            new[] { "CREATE ROUTINE saw;" },
            SchemaOperationPlannerTests.Describe(bare.GetService<IMigrationsModelDiffer>().GetDifferences(
                SchemaOperationPlannerTests.Model(bare), SchemaOperationPlannerTests.Model(needy))));
    }
}
