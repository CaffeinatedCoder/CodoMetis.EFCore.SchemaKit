using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>One model of a planner test. A context type per model, so no test reads a model another cached.</summary>
public interface IPlanModel
{
    static abstract void Build(ModelBuilder modelBuilder);
}

public sealed class Planned<TModel>(DbContextOptions<Planned<TModel>> options) : DbContext(options)
    where TModel : IPlanModel
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => TModel.Build(modelBuilder);
}

public class Animal
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Dog : Animal
{
    public string Trick { get; set; } = "";
}

/// <summary>An entity for two tables: the code in the first, the weight and the note in the second.</summary>
public sealed class Parcel
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public int Weight { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A hierarchy for TPC: an abstract root, a type with a derived type, and two types without.</summary>
public abstract class Vehicle
{
    public int Id { get; set; }
    public string Plate { get; set; } = "";
}

public class Car : Vehicle;

public sealed class Roadster : Car;

public sealed class Truck : Vehicle;

/// <summary>A type between the root and a leaf that has no table: it is abstract.</summary>
public abstract class Trailer : Vehicle;

public sealed class Caravan : Trailer;

public static class PlanModels
{
    private static readonly TestGuard.Unique AnimalName = new([new GuardKey("Name")]) { Name = "ux_animal" };

    /// <summary>Two types in one table, and the object declared on the derived one.</summary>
    public sealed class OnADerivedTypeOfASharedTable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>();
            modelBuilder.Entity<Dog>().Metadata.Declare([AnimalName]);
        }
    }

    /// <summary>The same object, declared on the type the table belongs to.</summary>
    public sealed class OnTheBaseTypeOfASharedTable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([AnimalName]);
            modelBuilder.Entity<Dog>();
        }
    }

    /// <summary>The derived type in a table of its own, with an object on a column only it has.</summary>
    public sealed class OnADerivedTypeWithItsOwnTable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().UseTptMappingStrategy();
            modelBuilder.Entity<Dog>().ToTable("Dogs").Metadata.Declare([new TestGuard.Unique([new GuardKey("Trick")]) { Name = "ux_trick" }]);
        }
    }

    private static readonly TestGuard.Unique Plate = new([new GuardKey("Plate")]) { Name = "ux_plate" };

    /// <summary>A table for each concrete type; the keys are not generated, which TPC would otherwise warn about on some providers.</summary>
    private static void Vehicles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Vehicle>().UseTpcMappingStrategy().Property(v => v.Id).ValueGeneratedNever();
        modelBuilder.Entity<Car>().ToTable("Cars");
        modelBuilder.Entity<Roadster>().ToTable("Roadsters");
        modelBuilder.Entity<Truck>().ToTable("Trucks");
    }

    public sealed class VehiclesUndeclared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Vehicles(modelBuilder);
    }

    /// <summary>Declared on the root of a TPC hierarchy, which has no table of its own here.</summary>
    public sealed class OnTheRootOfTpc : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Vehicles(modelBuilder);
            modelBuilder.Entity<Vehicle>().Metadata.Declare([Plate]);
        }
    }

    /// <summary>Declared on the root, with an abstract type between it and the one table of the hierarchy.</summary>
    public sealed class OnTheRootOfTpcAboveAnAbstractType : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vehicle>().UseTpcMappingStrategy().Property(v => v.Id).ValueGeneratedNever();
            modelBuilder.Entity<Trailer>();
            modelBuilder.Entity<Caravan>().ToTable("Caravans");
            modelBuilder.Entity<Vehicle>().Metadata.Declare([Plate]);
        }
    }

    /// <summary>Declared on a type of a TPC hierarchy that has a table and a derived type with another.</summary>
    public sealed class OnATypeOfTpcWithADerivedType : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Vehicles(modelBuilder);
            modelBuilder.Entity<Car>().Metadata.Declare([Plate]);
        }
    }

    /// <summary>The hierarchy before the roadster has a table of its own: Car is a type without derived types.</summary>
    public sealed class OnACarBeforeTheRoadster : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vehicle>().UseTpcMappingStrategy().Property(v => v.Id).ValueGeneratedNever();
            modelBuilder.Entity<Car>().ToTable("Cars").Metadata.Declare([Plate]);
            modelBuilder.Entity<Truck>().ToTable("Trucks");
        }
    }

    /// <summary>The same declaration on Car, with the roadsters' table under another name.</summary>
    public sealed class OnACarWithTheRoadstersRenamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vehicle>().UseTpcMappingStrategy().Property(v => v.Id).ValueGeneratedNever();
            modelBuilder.Entity<Car>().ToTable("Cars").Metadata.Declare([Plate]);
            modelBuilder.Entity<Roadster>().ToTable("Sportscars");
            modelBuilder.Entity<Truck>().ToTable("Trucks");
        }
    }

    /// <summary>The derived type in a table of its own, with an object on a column it inherits: that column is in the base type's table.</summary>
    public sealed class OnAnInheritedColumnOfATypeWithItsOwnTable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().UseTptMappingStrategy();
            modelBuilder.Entity<Dog>().ToTable("Dogs").Metadata.Declare([new TestGuard.Unique([new GuardKey("Name")]) { Name = "ux_dog_name" }]);
        }
    }

    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Parcel> Parcels(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Parcel>().ToTable("Parcels").SplitToTable("ParcelDetails", details =>
        {
            details.Property(parcel => parcel.Weight);
            details.Property(parcel => parcel.Note);
        });

    private static TestGuard.Unique On(string name, params string[] columns) => new([.. columns.Select(column => new GuardKey(column))]) { Name = name };

    public sealed class ParcelsUndeclared : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Parcels(modelBuilder);
    }

    /// <summary>An entity split over two tables, with an object for each table and one on the key, which both have.</summary>
    public sealed class OnASplitEntity : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Parcels(modelBuilder).Metadata.Declare([On("ux_code", "Code"), On("ux_weight", "Weight", "Note"), On("ux_id", "Id"), On("ux_id_weight", "Id", "Weight")]);
    }

    /// <summary>An object over a column of each table.</summary>
    public sealed class AcrossTheTablesOfASplitEntity : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Parcels(modelBuilder).Metadata.Declare([On("ux_code_weight", "Code", "Weight")]);
    }

    /// <summary>An object on a property the split entity does not have.</summary>
    public sealed class OnNoPropertyOfASplitEntity : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Parcels(modelBuilder).Metadata.Declare([On("ux_nothing", "Colour")]);
    }

    /// <summary>A type mapped to a view: it has no table, and nothing derives from it.</summary>
    public sealed class OnATypeWithoutATable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Animal>().ToView("AnimalsView").Metadata.Declare([AnimalName]);
    }

    /// <summary>On the root without a name: each table's object gets the name the kernel builds for it.</summary>
    public sealed class OnTheRootOfTpcWithoutAName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Vehicles(modelBuilder);
            modelBuilder.Entity<Vehicle>().Metadata.Declare([Plate with { Name = null }]);
        }
    }

    /// <summary>An object whose name is taken within its table: a trigger.</summary>
    public sealed class ATriggerOnACarOfTpc : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Vehicles(modelBuilder);
            modelBuilder.Entity<Car>().Metadata.Declare([new TestGuard.Trigger("Plate") { Name = "tr_plate" }]);
        }
    }

    /// <summary>Declared on the types of a TPC hierarchy that have no derived types, on a column they inherit.</summary>
    public sealed class OnTheLeavesOfTpc : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Vehicles(modelBuilder);
            modelBuilder.Entity<Roadster>().Metadata.Declare([Plate with { Name = "ux_roadster_plate" }]);
            modelBuilder.Entity<Truck>().Metadata.Declare([Plate with { Name = "ux_truck_plate" }]);
        }
    }

    /// <summary>A column only the derived type has, named from the base type.</summary>
    public sealed class ADerivedTypesColumnFromTheBase : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>().Metadata.Declare([new TestGuard.Unique([new GuardKey("Trick")]) { Name = "ux_trick" }]);
            modelBuilder.Entity<Dog>();
        }
    }

    private static readonly TestGuard.Unique Name = new([new GuardKey("Name")]) { Name = "ux_name" };

    private static void Declare(ModelBuilder modelBuilder, params ISchemaObject[] declarations)
        => modelBuilder.Entity<Thing>().Metadata.Declare(declarations);

    public sealed class Bare : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Thing>();
    }

    public sealed class Guarded : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name);
    }

    public sealed class Renamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Name = "ux_name2" });
    }

    /// <summary>The same name on other columns, and a column EF Core adds.</summary>
    public sealed class OnIdWithAColumn : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property<int>("Extra");
            Declare(modelBuilder, Name with { Keys = [new GuardKey("Id")] });
        }
    }

    /// <summary>Another name and other columns: nothing says it is the object there was.</summary>
    public sealed class RenamedAndChanged : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Name = "ux_other", Keys = [new GuardKey("Id")] });
    }

    /// <summary>Differs from <see cref="Guarded" /> in how the index is built, not in what it is.</summary>
    public sealed class Concurrently : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Concurrently = true });
    }

    /// <summary>Another contributor's object that resolves to the very descriptor <see cref="Guarded" />'s does.</summary>
    public sealed class LooseOfTheSameName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new LooseGuard("Name") { Name = "ux_name" });
    }

    public sealed class RenamedWithANewOne : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Declare(modelBuilder, Name with { Name = "ux_name2" }, new LooseGuard("Name") { Name = "lx_name" });
    }

    /// <summary>One object of each placement: before the tables, and after them at 5 and at 10.</summary>
    public sealed class Mixed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Declare(modelBuilder, Name, new LooseGuard("Name") { Name = "lx_name" }, new TestDomain("integer") { Name = "dom" });
    }

    /// <summary><see cref="Guarded" />, and EF Core alters the column the index is on.</summary>
    public sealed class GuardedLonger : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).HasMaxLength(50);
            Declare(modelBuilder, Name);
        }
    }

    /// <summary><see cref="Guarded" />, and EF Core renames the column the index is on.</summary>
    public sealed class GuardedTitle : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).HasColumnName("Title");
            Declare(modelBuilder, Name);
        }
    }

    /// <summary><see cref="Guarded" />, and EF Core renames the table.</summary>
    public sealed class GuardedThings : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().ToTable("Things");
            Declare(modelBuilder, Name);
        }
    }

    /// <summary><see cref="Guarded" />, and EF Core renames the table and the column.</summary>
    public sealed class ThingsTitle : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().ToTable("Things").Property(t => t.Name).HasColumnName("Title");
            Declare(modelBuilder, Name);
        }
    }

    /// <summary><see cref="Guarded" />, and EF Core renames the column and then alters it.</summary>
    public sealed class GuardedTitleLonger : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).HasColumnName("Title").HasMaxLength(50);
            Declare(modelBuilder, Name);
        }
    }

    /// <summary><see cref="Guarded" />, and EF Core makes the column nullable: an AlterColumn on every provider.</summary>
    public sealed class GuardedOptional : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).IsRequired(false);
            Declare(modelBuilder, Name);
        }
    }

    /// <summary>A trigger, whose drop names its table.</summary>
    public sealed class Triggered : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestGuard.Trigger("Name") { Name = "tr" });
    }

    /// <summary><see cref="Triggered" /> on another column, and EF Core renames the table.</summary>
    public sealed class ThingsTriggeredOnId : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().ToTable("Things");
            Declare(modelBuilder, new TestGuard.Trigger("Id") { Name = "tr" });
        }
    }

    /// <summary>An index with no name of its own: the kernel's default, from the table and the column.</summary>
    public sealed class Unnamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new LooseGuard("Name"));
    }

    /// <summary><see cref="Unnamed" />, and EF Core renames the table and the column.</summary>
    public sealed class ThingsTitleUnnamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().ToTable("Things").Property(t => t.Name).HasColumnName("Title");
            Declare(modelBuilder, new LooseGuard("Name"));
        }
    }

    public sealed class OnId : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, Name with { Name = "ux_id", Keys = [new GuardKey("Id")] });
    }

    /// <summary><see cref="OnId" />, and EF Core alters a column the index does not read.</summary>
    public sealed class OnIdLonger : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).HasMaxLength(50);
            Declare(modelBuilder, Name with { Name = "ux_id", Keys = [new GuardKey("Id")] });
        }
    }

    /// <summary>Two domains of one extension's type, and an index: three objects, one extension.</summary>
    public sealed class TwoDomainsOfAnExtension : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(
            modelBuilder,
            Name,
            new TestDomain("citext") { Name = "dom", Extension = "citext" },
            new TestDomain("citext") { Name = "dom2", Extension = "citext" });
    }

    /// <summary>A domain of the extension's type that says nothing of the extension, and the same one that does.</summary>
    public sealed class DomainWithoutItsExtension : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("citext") { Name = "dom" });
    }

    public sealed class DomainWithItsExtension : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("citext") { Name = "dom", Extension = "citext" });
    }

    /// <summary><see cref="Guarded" />, and the column the index is on becomes a computed one.</summary>
    public sealed class GuardedComputed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).HasComputedColumnSql("'x'", stored: true);
            Declare(modelBuilder, Name);
        }
    }

    public sealed class Domain : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("integer") { Name = "dom" });
    }

    public sealed class DomainOfAnotherType : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("bigint") { Name = "dom" });
    }

    /// <summary><see cref="Domain" />, and EF Core alters a column of the table it was declared on.</summary>
    public sealed class DomainLonger : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Property(t => t.Name).HasMaxLength(50);
            Declare(modelBuilder, new TestDomain("integer") { Name = "dom" });
        }
    }

    /// <summary><see cref="Domain" />, and EF Core renames the table it was declared on and a column of it.</summary>
    public sealed class DomainThingsTitle : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().ToTable("Things").Property(t => t.Name).HasColumnName("Title");
            Declare(modelBuilder, new TestDomain("integer") { Name = "dom" });
        }
    }

    public sealed class DomainRenamed : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder) => Declare(modelBuilder, new TestDomain("integer") { Name = "dom2" });
    }
}

/// <summary>
/// The planner, through the host: what two models' declarations amount to, in the list of
/// operations a migration gets. The statements are the sample contributors'; which of them are
/// written, and where they stand among EF Core's operations, is the planner's.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaOperationPlannerTests
{
    private const string CreateIndex = """CREATE UNIQUE INDEX ux_name ON "Thing" ("Name");""";
    private const string DropIndex = "DROP INDEX ux_name;";

    internal static Planned<TModel> Context<TModel>(Provider provider = Provider.Npgsql, Action<IServiceCollection>? more = null, string? connection = null)
        where TModel : IPlanModel
    {
        var builder = new DbContextOptionsBuilder<Planned<TModel>>().UseProvider(provider, connection);
        ServicesExtension.Add(builder, more is null ? "guards" : $"guards+{more.Method.Name}", services =>
        {
            services.AddSchemaContributor<TestGuardContributor>().AddSchemaContributor<LooseGuardContributor>().AddSchemaContributor<TestDomainContributor>();
            more?.Invoke(services);
        });
        return new Planned<TModel>(builder.UseSchemaKit().Options);
    }

    internal static IRelationalModel Model(DbContext context) => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    /// <summary>The model a later diff starts from: the generated snapshot, compiled, as <c>dotnet ef</c> prepares it.</summary>
    private static IRelationalModel Snapshot(DbContext context)
        => context.GetService<IModelRuntimeInitializer>()
            .Initialize(Snapshots.Of(context).Model, designTime: true, validationLogger: null)
            .GetRelationalModel();

    /// <summary>A statement as its SQL, an operation of EF Core's as its type.</summary>
    internal static List<string> Describe(IEnumerable<MigrationOperation> operations)
        => [.. operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name)];

    private static List<string> Diff<TFrom, TTo>(Action<TestGuardContributor>? arrange = null)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = Context<TFrom>();
        using var to = Context<TTo>();
        arrange?.Invoke((TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!);

        return Describe(to.GetService<IMigrationsModelDiffer>().GetDifferences(Model(from), Model(to)));
    }

    // ---- the same on both sides ----------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_model_against_its_own_snapshot_plans_nothing(Provider provider)
    {
        using var context = Context<PlanModels.Mixed>(provider);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var snapshot = Snapshot(context);

        Assert.AreEqual(0, differ.GetDifferences(snapshot, Model(context)).Count);
        Assert.IsFalse(differ.HasDifferences(snapshot, Model(context)));

        // And it is not that the planner sees nothing: from no model at all, each declaration is a statement.
        Assert.AreEqual(3, differ.GetDifferences(null, Model(context)).OfType<SqlOperation>().Count());
        Assert.AreEqual(3, differ.GetDifferences(snapshot, null).OfType<SqlOperation>().Count());
    }

    [TestMethod]
    public void A_difference_in_how_an_object_is_built_is_no_difference()
        => Assert.AreEqual(0, Diff<PlanModels.Guarded, PlanModels.Concurrently>().Count);

    // ---- a table two types share -------------------------------------------------------------------------

    /// <summary>
    /// An object is attached to a table. Declared on a derived type that shares its table, a unique
    /// index or an exclusion constraint would hold among the rows of the sibling types too: the
    /// migration applies, and the database refuses writes the model allows. So it is refused, and
    /// the message names the type to declare it on.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_declaration_on_a_derived_type_that_shares_its_table_is_refused(Provider provider)
    {
        using var context = Context<PlanModels.OnADerivedTypeOfASharedTable>(provider);
        var differ = context.GetService<IMigrationsModelDiffer>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => differ.GetDifferences(null, Model(context)));
        StringAssert.Contains(error.Message, "Dog declares Test.Guard unique 'ux_animal'");
        StringAssert.Contains(error.Message, "Declare it on Animal");
        Assert.ThrowsExactly<InvalidOperationException>(() => differ.HasDifferences(null, Model(context)));
    }

    [TestMethod]
    public void On_the_base_type_or_on_a_derived_type_with_a_table_of_its_own_it_is_declared()
    {
        using var onBase = Context<PlanModels.OnTheBaseTypeOfASharedTable>();
        using var ownTable = Context<PlanModels.OnADerivedTypeWithItsOwnTable>();

        CollectionAssert.Contains(Describe(onBase.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(onBase))), """CREATE UNIQUE INDEX ux_animal ON "Animal" ("Name");""");
        CollectionAssert.Contains(Describe(ownTable.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(ownTable))), """CREATE UNIQUE INDEX ux_trick ON "Dogs" ("Trick");""");
    }

    /// <summary>
    /// The refusal is for the context's own model, not for a snapshot. A snapshot that holds
    /// such a declaration describes an object the database has; with the declaration moved to the
    /// base type the object is the same one, and the migration is empty. Refused on the source
    /// side too, there would be no way off such a snapshot.
    /// </summary>
    [TestMethod]
    public void A_snapshot_that_holds_one_is_read_and_moving_it_to_the_base_type_costs_nothing()
    {
        using var derived = Context<PlanModels.OnADerivedTypeOfASharedTable>();
        using var onBase = Context<PlanModels.OnTheBaseTypeOfASharedTable>();
        using var bare = Context<PlanModels.Bare>();

        Assert.AreEqual(0, onBase.GetService<IMigrationsModelDiffer>().GetDifferences(Model(derived), Model(onBase)).Count);
        // And back: EF Core's Down of that move has the snapshot where the diff ends (#98).
        Assert.AreEqual(0, onBase.GetService<IMigrationsModelDiffer>().GetDifferences(Model(onBase), Model(derived)).Count);
        // And it can be dropped with its table, as any object of the snapshot can.
        CollectionAssert.Contains(Describe(bare.GetService<IMigrationsModelDiffer>().GetDifferences(Model(derived), Model(bare))), "DROP INDEX ux_animal;");
    }

    // Each provider delimits in its own way, and PostgreSQL a name with a capital in it.
    private static List<string> Unquoted(IEnumerable<MigrationOperation> operations)
        => [.. Describe(operations).Select(statement => statement.Replace("\"", "").Replace("[", "").Replace("]", ""))];

    private static List<string> Created(Provider provider, DbContext context)
        => [.. Describe(context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)))
            .Where(statement => statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal) || statement.StartsWith("CREATE TRIGGER", StringComparison.Ordinal))
            .Select(statement => statement.Replace("\"", "").Replace("[", "").Replace("]", ""))];

    /// <summary>
    /// The other way round (#101, #118): a type of a TPC hierarchy whose derived types have tables
    /// of their own. Each of those tables repeats the type's columns, and the declaration is an
    /// object in each, as an index of EF Core's declared there is. The declaring type's own table
    /// keeps a name the declaration states, and a derived type's copy has its table's name behind
    /// it, since a relation's name is taken in the whole schema. A root without a table has
    /// copies alone.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_declaration_on_a_type_of_TPC_lands_on_every_table_of_the_hierarchy_below_it(Provider provider)
    {
        using var onTheRoot = Context<PlanModels.OnTheRootOfTpc>(provider);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "CREATE UNIQUE INDEX ux_plate_Cars ON Cars (Plate);",
                "CREATE UNIQUE INDEX ux_plate_Roadsters ON Roadsters (Plate);",
                "CREATE UNIQUE INDEX ux_plate_Trucks ON Trucks (Plate);",
            },
            Created(provider, onTheRoot));

        // A derived type without a table is passed over, and the table below it is reached.
        using var aboveAnAbstractType = Context<PlanModels.OnTheRootOfTpcAboveAnAbstractType>(provider);
        CollectionAssert.AreEqual(new[] { "CREATE UNIQUE INDEX ux_plate_Caravans ON Caravans (Plate);" }, Created(provider, aboveAnAbstractType));

        using var inTheMiddle = Context<PlanModels.OnATypeOfTpcWithADerivedType>(provider);
        CollectionAssert.AreEquivalent(
            new[] { "CREATE UNIQUE INDEX ux_plate ON Cars (Plate);", "CREATE UNIQUE INDEX ux_plate_Roadsters ON Roadsters (Plate);" },
            Created(provider, inTheMiddle));
        Assert.IsTrue(inTheMiddle.GetService<IMigrationsModelDiffer>().HasDifferences(null, Model(inTheMiddle)));
    }

    /// <summary>
    /// The copies' names are made by the resolver's <c>Name</c>. A contributor that names an
    /// object without it gives every table's object one name: here the sample's own default,
    /// which names no table. That is refused as the collision it is, every pair of it, and not
    /// left to the database.
    /// </summary>
    [TestMethod]
    public void A_name_made_without_the_resolver_is_one_name_for_every_copy_and_is_refused()
    {
        using var context = Context<PlanModels.OnTheRootOfTpcWithoutAName>();

        var refused = Assert.ThrowsExactly<SchemaValidationException>(() => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));
        Assert.IsTrue(refused.Diagnostics.All(diagnostic => diagnostic.Id == "SK1001"), refused.Message);
        StringAssert.Contains(refused.Message, "'ux_Plate'");
    }

    /// <summary>
    /// The table's name goes behind a name only where two objects cannot share it. A trigger's
    /// name is taken within its table, as the contributor's claims say, so every copy keeps it:
    /// that is what 5.x did for an index of SQL Server.
    /// </summary>
    [TestMethod]
    public void A_name_that_is_taken_within_its_table_is_every_copys_name()
    {
        using var context = Context<PlanModels.ATriggerOnACarOfTpc>();

        CollectionAssert.AreEquivalent(
            new[] { "CREATE TRIGGER tr_plate ON Cars (Plate);", "CREATE TRIGGER tr_plate ON Roadsters (Plate);" },
            Created(Provider.Npgsql, context));
    }

    /// <summary>
    /// The copies are made when a model is read, on both sides of a diff, and nothing of them is
    /// stored. So a model against its own snapshot is no migration, a derived type that gets a
    /// table gets its copy by the next one, and one that loses its table loses the copy.
    /// </summary>
    [TestMethod]
    public void The_copies_follow_the_hierarchy_from_one_migration_to_the_next()
    {
        using var before = Context<PlanModels.OnACarBeforeTheRoadster>();
        using var after = Context<PlanModels.OnATypeOfTpcWithADerivedType>();
        var differ = after.GetService<IMigrationsModelDiffer>();

        Assert.AreEqual(0, differ.GetDifferences(Snapshot(after), Model(after)).Count);
        Assert.IsFalse(differ.HasDifferences(Snapshot(after), Model(after)));

        var added = Unquoted(differ.GetDifferences(Snapshot(before), Model(after)));
        CollectionAssert.AreEqual(new[] { "CreateTableOperation", "CREATE UNIQUE INDEX ux_plate_Roadsters ON Roadsters (Plate);" }, added);

        var removed = Unquoted(differ.GetDifferences(Snapshot(after), Model(before)));
        CollectionAssert.AreEqual(new[] { "DROP INDEX ux_plate_Roadsters;", "DropTableOperation" }, removed);
    }

    /// <summary>A type without derived types has one table, its own, with the columns it inherits in it.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void On_the_types_of_TPC_without_derived_types_it_is_declared(Provider provider)
    {
        using var context = Context<PlanModels.OnTheLeavesOfTpc>(provider);
        var statements = Describe(context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));

        // Each provider delimits in its own way: the name, its table and the inherited column are what counts.
        Assert.AreEqual(1, statements.Count(statement => statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal)
                                                         && statement.Contains("ux_roadster_plate") && statement.Contains("Roadsters") && statement.Contains("Plate")), string.Join("\n", statements));
        Assert.AreEqual(1, statements.Count(statement => statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal)
                                                         && statement.Contains("ux_truck_plate") && statement.Contains("Trucks") && statement.Contains("Plate")), string.Join("\n", statements));
        Assert.AreEqual(2, statements.Count(statement => statement.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An entity split over two tables (#118, #191): an object is in the one table that has a
    /// column for everything it reads, and its name the kernel builds is that table's. The key's
    /// columns are in every table, so an object on them alone is in the first, and one that reads
    /// the key and a column of the second is in the second. The same on the compiled snapshot.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_declaration_on_a_split_entity_lands_on_the_table_that_has_its_columns(Provider provider)
    {
        using var context = Context<PlanModels.OnASplitEntity>(provider);
        using var undeclared = Context<PlanModels.ParcelsUndeclared>(provider);
        var differ = context.GetService<IMigrationsModelDiffer>();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "CREATE UNIQUE INDEX ux_code ON Parcels (Code);",
                "CREATE UNIQUE INDEX ux_weight ON ParcelDetails (Weight, Note);",
                "CREATE UNIQUE INDEX ux_id ON Parcels (Id);",
                "CREATE UNIQUE INDEX ux_id_weight ON ParcelDetails (Id, Weight);",
            },
            Created(provider, context));

        Assert.AreEqual(0, differ.GetDifferences(Snapshot(context), Model(context)).Count);
        Assert.AreEqual(4, Unquoted(differ.GetDifferences(Snapshot(context), Model(undeclared))).Count(statement => statement.StartsWith("DROP INDEX ux_", StringComparison.Ordinal)));
        Assert.AreEqual(4, Unquoted(differ.GetDifferences(Model(undeclared), Snapshot(context))).Count(statement => statement.StartsWith("CREATE UNIQUE INDEX ux_", StringComparison.Ordinal)));
    }

    /// <summary>An object is in one table. One that reads a column of each is refused, with the tables named.</summary>
    [TestMethod]
    public void A_declaration_over_columns_of_two_tables_of_a_split_entity_is_refused()
    {
        using var context = Context<PlanModels.AcrossTheTablesOfASplitEntity>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));
        StringAssert.Contains(refused.Message, "Parcel declares Test.Guard unique 'ux_code_weight', and Parcel is split over the tables Parcels, ParcelDetails");
        Assert.IsInstanceOfType<InvalidOperationException>(refused.InnerException);
        StringAssert.Contains(refused.InnerException.Message, "has no column in the table");
    }

    /// <summary>
    /// Only the refusal of a property without a column in the table asked is taken for "not this
    /// table". A path that names no property fails as itself, on a split entity as anywhere.
    /// </summary>
    [TestMethod]
    public void A_path_that_names_no_property_of_a_split_entity_fails_as_itself()
    {
        using var context = Context<PlanModels.OnNoPropertyOfASplitEntity>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));
        StringAssert.Contains(refused.Message, "Colour");
        Assert.IsFalse(refused.Message.Contains("is split over", StringComparison.Ordinal), refused.Message);
    }

    /// <summary>
    /// A derived type with a table of its own does not have its inherited columns there: they are
    /// in the base type's table, where an object holds among the rows of every type. The refusal
    /// names the type to declare it on. A column of the derived type's own is declared on as before.
    /// </summary>
    [TestMethod]
    public void A_declaration_on_a_derived_type_that_reads_an_inherited_column_names_the_base_type()
    {
        using var inherited = Context<PlanModels.OnAnInheritedColumnOfATypeWithItsOwnTable>();
        using var own = Context<PlanModels.OnADerivedTypeWithItsOwnTable>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => inherited.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(inherited)));
        StringAssert.Contains(refused.Message, "Dog declares Test.Guard unique 'ux_dog_name', which reads 'Name'. Dog inherits that property from Animal");
        StringAssert.Contains(refused.Message, "declare it on Animal");

        CollectionAssert.AreEqual(new[] { "CREATE UNIQUE INDEX ux_trick ON Dogs (Trick);" }, Created(Provider.Npgsql, own));
    }

    /// <summary>A declaration that lands on no table would produce nothing, without an error: it is refused.</summary>
    [TestMethod]
    public void A_declaration_on_a_type_without_a_table_is_refused()
    {
        using var context = Context<PlanModels.OnATypeWithoutATable>();

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));
        StringAssert.Contains(refused.Message, "Animal declares Test.Guard unique 'ux_animal', but is not mapped to a table");
    }

    /// <summary>
    /// A copy follows a rename of its own table, like any object: it is resolved again in that
    /// table, not in the declaring type's, and what is left is its name, which has the table in it.
    /// </summary>
    [TestMethod]
    public void A_copy_follows_the_rename_of_its_table()
    {
        using var before = Context<PlanModels.OnATypeOfTpcWithADerivedType>();
        using var after = Context<PlanModels.OnACarWithTheRoadstersRenamed>();

        var got = Unquoted(after.GetService<IMigrationsModelDiffer>().GetDifferences(Snapshot(before), Model(after)));
        // EF Core renames the table and its primary key; nothing is dropped or created for the copy.
        CollectionAssert.AreEqual(
            new[] { "DropPrimaryKeyOperation", "RenameTableOperation", "AddPrimaryKeyOperation", "ALTER INDEX ux_plate_Roadsters RENAME TO ux_plate_Sportscars;" },
            got, string.Join(" | ", got));
    }

    /// <summary>
    /// The function itself, on a model and on its compiled snapshot: the tables are the same and
    /// in the same order, the declaring type's own first and marked as no copy. A type that is not
    /// mapped with TPC has its own table and nothing else, whatever derives from it.
    /// </summary>
    [TestMethod]
    public void The_landings_of_a_type_are_the_same_on_a_model_and_on_its_snapshot()
    {
        using var vehicles = Context<PlanModels.OnATypeOfTpcWithADerivedType>();
        using var animals = Context<PlanModels.OnTheBaseTypeOfASharedTable>();

        foreach (var model in new[] { Model(vehicles).Model, Snapshot(vehicles).Model })
        {
            CollectionAssert.AreEqual(new[] { "Cars copy", "Roadsters copy", "Trucks copy" }, Of(model, typeof(Vehicle)));
            CollectionAssert.AreEqual(new[] { "Cars", "Roadsters copy" }, Of(model, typeof(Car)));
            CollectionAssert.AreEqual(new[] { "Roadsters" }, Of(model, typeof(Roadster)));
        }

        foreach (var model in new[] { Model(animals).Model, Snapshot(animals).Model })
        {
            CollectionAssert.AreEqual(new[] { "Animal" }, Of(model, typeof(Animal)));
            CollectionAssert.AreEqual(new[] { "Animal" }, Of(model, typeof(Dog)));
        }

        static List<string> Of(IModel model, Type type)
            => [.. Landings.Of(model.FindEntityType(type.FullName!)!).Select(landing => landing.Table.Name + (landing.IsCopy ? " copy" : ""))];
    }

    /// <summary>A snapshot is read by the same rule, whichever side of a diff it is on (#98): every copy is dropped, and every copy comes back.</summary>
    [TestMethod]
    public void A_snapshot_with_a_declaration_on_such_a_type_of_TPC_is_read_by_the_same_rule()
    {
        using var declared = Context<PlanModels.OnATypeOfTpcWithADerivedType>();
        using var undeclared = Context<PlanModels.VehiclesUndeclared>();
        var differ = undeclared.GetService<IMigrationsModelDiffer>();

        CollectionAssert.AreEquivalent(
            new[] { "DROP INDEX ux_plate;", "DROP INDEX ux_plate_Roadsters;" },
            Unquoted(differ.GetDifferences(Snapshot(declared), Model(undeclared))));
        Assert.AreEqual(2, Describe(differ.GetDifferences(Model(undeclared), Snapshot(declared))).Count(statement => statement.Contains("ux_plate")));
    }

    /// <summary>
    /// What the rule costs for now, held as a fact: a column only the derived type has cannot be
    /// named from the base type, so an object on it cannot be declared while the two share a table.
    /// </summary>
    [TestMethod]
    public void A_column_of_the_derived_type_cannot_be_named_from_the_base_type()
    {
        using var context = Context<PlanModels.ADerivedTypesColumnFromTheBase>();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));
        StringAssert.Contains(error.Message, "Trick");
    }

    // ---- extensions ------------------------------------------------------------------------------------

    /// <summary>A dialect as a provider's package brings it: it can install an extension.</summary>
    private sealed class InstallingDialect(SchemaDialectDependencies dependencies) : SchemaDialect(dependencies)
    {
        public override void EnsureExtension(SqlName extension, IReadOnlyModel model, SchemaSql sql)
            => sql.Statement($"CREATE EXTENSION {extension} FOR {model.GetEntityTypes().Count()}");
    }

    private static void Installing(IServiceCollection services)
        => services.AddScoped<ISchemaDialect>(provider => new InstallingDialect(new SchemaDialectDependencies(
            provider.GetRequiredService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>(),
            provider.GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalTypeMappingSource>())));

    /// <summary>
    /// An extension is asked for once, however many created objects list it, and ahead of every
    /// statement, those of what tables are made of included. The dialect is handed the model the
    /// migration leads to: one entity type here.
    /// </summary>
    [TestMethod]
    public void An_extension_created_objects_depend_on_is_ensured_once_and_first()
    {
        using var bare = Context<PlanModels.Bare>(more: Installing);
        using var context = Context<PlanModels.TwoDomainsOfAnExtension>(more: Installing);
        var differ = context.GetService<IMigrationsModelDiffer>();

        CollectionAssert.AreEqual(
            new[]
            {
                "CREATE EXTENSION citext FOR 1;",
                // In the order of their annotation keys, as every list of declarations is.
                "CREATE DOMAIN dom2 AS citext;",
                "CREATE DOMAIN dom AS citext;",
                CreateIndex,
            },
            Describe(differ.GetDifferences(Model(bare), Model(context))));

        // Nothing created, nothing ensured: not for what is there already, and not for what is dropped.
        Assert.AreEqual(0, differ.GetDifferences(Snapshot(context), Model(context)).Count);
        Assert.IsFalse(Describe(differ.GetDifferences(Model(context), Model(bare))).Any(statement => statement.Contains("EXTENSION")));
    }

    /// <summary>
    /// An object that stays can come to depend on an extension: the database rebuilds an index
    /// under a column's new type, and the operator class it then needs is an extension's. Nothing
    /// is created, and the extension is ensured all the same. Not the other way round, and not
    /// when both sides depend on it.
    /// </summary>
    [TestMethod]
    public void An_extension_an_object_comes_to_depend_on_is_ensured_though_nothing_is_created()
    {
        using var without = Context<PlanModels.DomainWithoutItsExtension>(more: Installing);
        using var with = Context<PlanModels.DomainWithItsExtension>(more: Installing);
        var differ = with.GetService<IMigrationsModelDiffer>();

        CollectionAssert.AreEqual(new[] { "CREATE EXTENSION citext FOR 1;" }, Describe(differ.GetDifferences(Model(without), Model(with))));
        Assert.IsTrue(differ.HasDifferences(Model(without), Model(with)));
        Assert.AreEqual(0, differ.GetDifferences(Model(with), Model(without)).Count);
        Assert.AreEqual(0, differ.GetDifferences(Model(with), Model(with)).Count);
    }

    /// <summary>
    /// The kernel's own dialect, of a provider without a SchemaKit package, has no statement for an
    /// extension. It refuses, since the alternative is an object created as if its dependency were met.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void The_kernels_dialect_refuses_a_dependency_on_an_extension(Provider provider)
    {
        using var context = Context<PlanModels.TwoDomainsOfAnExtension>(provider);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context)));
        StringAssert.Contains(error.Message, "'citext'");
    }

    // ---- creates and drops -----------------------------------------------------------------------------

    [TestMethod]
    public void A_new_declaration_is_created_after_the_table_it_is_on()
    {
        CollectionAssert.AreEqual(new[] { CreateIndex }, Diff<PlanModels.Bare, PlanModels.Guarded>());

        using var context = Context<PlanModels.Guarded>();
        CollectionAssert.AreEqual(
            new[] { nameof(CreateTableOperation), CreateIndex },
            Describe(context.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(context))));
    }

    [TestMethod]
    public void A_declaration_that_is_gone_is_dropped_before_the_table_it_was_on()
    {
        CollectionAssert.AreEqual(new[] { DropIndex }, Diff<PlanModels.Guarded, PlanModels.Bare>());

        using var context = Context<PlanModels.Guarded>();
        CollectionAssert.AreEqual(
            new[] { DropIndex, nameof(DropTableOperation) },
            Describe(context.GetService<IMigrationsModelDiffer>().GetDifferences(Model(context), null)));
    }

    // ---- a changed pair: the contributor's answer -------------------------------------------------------

    [TestMethod]
    public void A_name_that_changed_is_the_contributors_statement_in_place()
        => CollectionAssert.AreEqual(new[] { "ALTER INDEX ux_name RENAME TO ux_name2;" }, Diff<PlanModels.Guarded, PlanModels.Renamed>());

    [TestMethod]
    public void A_rebuild_drops_before_ef_cores_operations_and_creates_after_them()
        => CollectionAssert.AreEqual(
            new[] { DropIndex, nameof(AddColumnOperation), """CREATE UNIQUE INDEX ux_name ON "Thing" ("Id");""" },
            Diff<PlanModels.Guarded, PlanModels.OnIdWithAColumn>());

    /// <summary>
    /// The same name with other content is the same object, changed: what it takes is the
    /// contributor's to say, and it may be less than a rebuild.
    /// </summary>
    [TestMethod]
    public void A_changed_object_of_the_same_name_is_the_contributors_to_plan()
        => CollectionAssert.AreEqual(
            new[] { nameof(AddColumnOperation), "ALTER INDEX ux_name SET (fillfactor = 70);" },
            Diff<PlanModels.Guarded, PlanModels.OnIdWithAColumn>(guard => guard.PlanOverride = sql =>
            {
                var index = new QualifiedName(null, sql.Dialect.Identifier("ux_name"));
                sql.Statement($"ALTER INDEX {index} SET (fillfactor = {70})");
                return new SchemaChange.InPlace();
            }));

    /// <summary>
    /// Two contributors may share a descriptor type. An equal descriptor of another contributor is
    /// another object: here a unique index gives way to a plain one of the same name and column.
    /// </summary>
    [TestMethod]
    public void Equal_descriptors_of_two_contributors_are_two_objects()
        => CollectionAssert.AreEqual(
            new[] { DropIndex, """CREATE INDEX ux_name ON "Thing" ("Name");""" },
            Diff<PlanModels.Guarded, PlanModels.LooseOfTheSameName>());

    /// <summary>The descriptors differ, and the contributor says the database need not hear of it.</summary>
    [TestMethod]
    public void An_answer_of_none_adds_nothing()
    {
        using var from = Context<PlanModels.Guarded>();
        using var to = Context<PlanModels.Renamed>();
        ((TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!).PlanOverride = _ => new SchemaChange.None();
        var differ = to.GetService<IMigrationsModelDiffer>();

        Assert.AreEqual(0, differ.GetDifferences(Model(from), Model(to)).Count);
        Assert.IsFalse(differ.HasDifferences(Model(from), Model(to)));
    }

    /// <summary>With another name and other columns nothing says the two are one object, so the contributor is not asked.</summary>
    [TestMethod]
    public void Two_objects_that_share_neither_name_nor_content_are_a_drop_and_a_create()
        => CollectionAssert.AreEqual(
            new[] { DropIndex, """CREATE UNIQUE INDEX ux_other ON "Thing" ("Id");""" },
            Diff<PlanModels.Guarded, PlanModels.RenamedAndChanged>(guard => guard.PlanOverride = _ => throw new InvalidOperationException("Plan was asked.")));

    // ---- order -------------------------------------------------------------------------------------------

    [TestMethod]
    public void Statements_in_place_come_before_creates()
        => CollectionAssert.AreEqual(
            new[] { "ALTER INDEX ux_name RENAME TO ux_name2;", """CREATE INDEX lx_name ON "Thing" ("Name");""" },
            Diff<PlanModels.Guarded, PlanModels.RenamedWithANewOne>());

    /// <summary>
    /// What tables are made of is created before them and dropped after them; what is attached to
    /// them is created after them by placement, and dropped before them in the reverse.
    /// </summary>
    [TestMethod]
    public void Contributors_are_ordered_by_their_placement_around_ef_cores_operations()
    {
        using var context = Context<PlanModels.Mixed>();
        var differ = context.GetService<IMigrationsModelDiffer>();

        CollectionAssert.AreEqual(
            new[] { "CREATE DOMAIN dom AS integer;", nameof(CreateTableOperation), """CREATE INDEX lx_name ON "Thing" ("Name");""", CreateIndex },
            Describe(differ.GetDifferences(null, Model(context))));
        CollectionAssert.AreEqual(
            new[] { DropIndex, "DROP INDEX lx_name;", nameof(DropTableOperation), "DROP DOMAIN dom;" },
            Describe(differ.GetDifferences(Model(context), null)));
    }

    [TestMethod]
    public void What_tables_are_made_of_changes_in_place_or_not_at_all()
    {
        CollectionAssert.AreEqual(new[] { "ALTER DOMAIN dom RENAME TO dom2;" }, Diff<PlanModels.Domain, PlanModels.DomainRenamed>());

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<PlanModels.Domain, PlanModels.DomainOfAnotherType>());
        StringAssert.Contains(error.Message, "TestDomainContributor'.Plan answered Rebuild for 'dom', and its placement is BeforeTables");
    }

    // ---- what the planner refuses, and what it leaves alone ---------------------------------------------

    /// <summary>
    /// A declaration nothing renders would be left out of every migration, in silence. The context's
    /// own model cannot hold one any more, since it is refused where it is written (#113); a
    /// snapshot can, scaffolded while the package that declared it was still registered.
    /// </summary>
    [TestMethod]
    public void A_declaration_with_no_contributor_is_refused_on_either_side()
    {
        using var declaring = Context<PlanModels.Guarded>();
        var builder = new DbContextOptionsBuilder<Planned<PlanModels.Bare>>().UseProvider(Provider.Npgsql);
        using var context = new Planned<PlanModels.Bare>(builder.UseSchemaKit().Options);
        var differ = context.GetService<IMigrationsModelDiffer>();

        foreach (var (source, target) in new[] { ((IRelationalModel?)null, (IRelationalModel?)Model(declaring)), (Model(declaring), null) })
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => differ.GetDifferences(source, target));
            StringAssert.Contains(error.Message, "Thing carries a declaration of 'Test.Guard', and no contributor with that owner is registered");
        }
    }

    /// <summary>
    /// The kernel's <c>UseSchemaKit()</c> stamps a model and renders nothing. A declaration whose
    /// contributor the options do not register was stored on that stamp and refused at the first
    /// diff; it is refused where it is written, on every provider, and names what to register (#113).
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_declaration_whose_contributor_the_options_do_not_register_is_refused_where_it_is_written(Provider provider)
    {
        var builder = new DbContextOptionsBuilder<Planned<PlanModels.Guarded>>().UseProvider(provider);
        using var context = new Planned<PlanModels.Guarded>(builder.UseSchemaKit().Options);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => context.Model);

        StringAssert.Contains(error.Message, "Thing declares a 'Test.Guard' object");
        StringAssert.Contains(error.Message, "UseSchemaKit() is there, the registration of CodoMetis.EFCore.SchemaKit.Tests is not");
    }

    /// <summary>The allowed twin, and what the refusal reads: one contributor is registered, and only its owner may be declared.</summary>
    [TestMethod]
    public void Only_the_owners_the_options_register_may_be_declared()
    {
        static Planned<TModel> OnlyTheGuard<TModel>() where TModel : IPlanModel
        {
            var builder = new DbContextOptionsBuilder<Planned<TModel>>().UseProvider(Provider.Npgsql);
            ServicesExtension.Add(builder, "the guard alone", services => services.AddSchemaContributor<TestGuardContributor>());
            return new Planned<TModel>(builder.UseSchemaKit().Options);
        }

        using var guarded = OnlyTheGuard<PlanModels.Guarded>();
        Assert.AreEqual(1, guarded.Model.GetEntityTypes().Single().GetDeclarations<TestGuard>().Count);

        using var domain = OnlyTheGuard<PlanModels.Domain>();
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => domain.Model);
        StringAssert.Contains(error.Message, "declares a 'Test.Domain' object");
    }

    /// <summary>
    /// A context that declares nothing pays nothing: the dialect, which reads the design-time model,
    /// is not built. The second half shows the first is not vacuous.
    /// </summary>
    [TestMethod]
    public void A_context_that_declares_nothing_never_builds_the_dialect()
    {
        static void Poisoned(IServiceCollection services)
            => services.AddScoped<ISchemaDialect>(_ => throw new InvalidOperationException("The dialect was built."));

        using var bare = Context<PlanModels.Bare>(more: Poisoned);
        Assert.AreEqual(1, bare.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(bare)).Count);

        using var guarded = Context<PlanModels.Guarded>(more: Poisoned);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => guarded.GetService<IMigrationsModelDiffer>().GetDifferences(null, Model(guarded)));
        Assert.AreEqual("The dialect was built.", error.Message);
    }
}
