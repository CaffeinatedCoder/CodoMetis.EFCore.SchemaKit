using CodoMetis.EFCore.SchemaKit.Contributors.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodoMetis.EFCore.SchemaKit.Tests.Migrations;

/// <summary>Models in which a declared object is renamed and EF Core takes or gives up one of its two names in the same migration.</summary>
public static class RenameModels
{
    private static readonly TestGuard.Unique Name = new([new GuardKey("Name")]) { Name = "ux_name" };

    /// <summary>The sample guard's name is taken among the relations of the schema, as a table's is.</summary>
    public sealed class RenamedAndItsOldNameATable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Metadata.Declare([Name with { Name = "ux_name2" }]);
            modelBuilder.Entity<Gadget>().ToTable("ux_name");
        }
    }

    public sealed class GuardedBesideATableNamedLikeItsNextName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Metadata.Declare([Name]);
            modelBuilder.Entity<Gadget>().ToTable("ux_name2");
        }
    }

    /// <summary>Another guard holds the name the rename takes, on another table.</summary>
    public sealed class GuardedBesideAGuardNamedLikeItsNextName : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Metadata.Declare([Name]);
            modelBuilder.Entity<Gadget>().Metadata.Declare([new TestGuard.Unique([new GuardKey("Label")]) { Name = "ux_name2" }]);
        }
    }

    /// <summary><see cref="PlanModels.Renamed" />, and the table the other guard was on stays without it.</summary>
    public sealed class RenamedBesideAnUnguardedGadget : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Metadata.Declare([Name with { Name = "ux_name2" }]);
            modelBuilder.Entity<Gadget>();
        }
    }

    /// <summary>
    /// The guard on <see cref="Gadget" /> gives its name up to the one on <see cref="Thing" />, and
    /// EF Core alters the column it is on.
    /// </summary>
    public sealed class RenamedIntoTheNameOfAGuardOnAnAlteredColumn : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Metadata.Declare([Name with { Name = "ux_name2" }]);
            var gadget = modelBuilder.Entity<Gadget>();
            gadget.Property(x => x.Label).HasMaxLength(40);
            gadget.Metadata.Declare([new TestGuard.Unique([new GuardKey("Label")]) { Name = "ux_gadget" }]);
        }
    }

    /// <summary>A domain is made before the tables. Renamed while a new table takes its old name, it cannot be rebuilt around the table.</summary>
    public sealed class DomainRenamedAndItsOldNameATable : IPlanModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Thing>().Metadata.Declare([new TestDomain("integer") { Name = "dom2" }]);
            modelBuilder.Entity<Gadget>().ToTable("dom");
        }
    }
}

/// <summary>
/// A change in place runs after EF Core's operations. A rename whose old name EF Core takes in the
/// same migration, or whose new name something still holds then, is rebuilt: the drop runs before
/// EF Core's operations and the create after them (#99). The rule is the kernel's, so it holds on
/// every provider, through the claims of <c>NameClaims</c>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RenameInPlaceTests
{
    /// <summary>Each statement by its first two words, each operation of EF Core's by its type.</summary>
    private static List<string> Diff<TFrom, TTo>(Provider provider, Action<DbContext>? arrange = null)
        where TFrom : IPlanModel
        where TTo : IPlanModel
    {
        using var from = SchemaOperationPlannerTests.Context<TFrom>(provider);
        using var to = SchemaOperationPlannerTests.Context<TTo>(provider);
        arrange?.Invoke(to);

        return [.. SchemaOperationPlannerTests.Describe(to.GetService<IMigrationsModelDiffer>().GetDifferences(SchemaOperationPlannerTests.Model(from), SchemaOperationPlannerTests.Model(to)))
            .Select(described => string.Join(' ', described.Split(' ').Take(2)))];
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_rename_without_a_collision_stays_in_place(Provider provider)
        => CollectionAssert.AreEqual(new[] { "ALTER INDEX" }, Diff<PlanModels.Guarded, PlanModels.Renamed>(provider));

    /// <summary>The rename's statement is the contributor's answer, written into the builder it was handed: it does not reach the migration.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_rename_whose_old_name_EF_Core_takes_is_a_drop_before_and_a_create_after(Provider provider)
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX", "CreateTableOperation", "CREATE UNIQUE" },
            Diff<PlanModels.Guarded, RenameModels.RenamedAndItsOldNameATable>(provider));

    /// <summary>EF Core's drop runs before the rename: the name is free by then.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_rename_into_a_name_EF_Core_gives_up_stays_in_place(Provider provider)
        => CollectionAssert.AreEqual(
            new[] { "DropTableOperation", "ALTER INDEX" },
            Diff<RenameModels.GuardedBesideATableNamedLikeItsNextName, PlanModels.Renamed>(provider));

    /// <summary>And the other way, where EF Core creates the table under the name the rename takes: a create of EF Core's needs the name free.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_rename_into_a_name_EF_Core_takes_first_is_a_rebuild_too(Provider provider)
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX", "CreateTableOperation", "CREATE UNIQUE" },
            Diff<PlanModels.Renamed, RenameModels.GuardedBesideATableNamedLikeItsNextName>(provider));

    /// <summary>A declared object no longer declared is dropped before EF Core's operations, so its name is free when the rename runs.</summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_rename_into_a_name_a_dropped_declaration_gives_up_stays_in_place(Provider provider)
        => CollectionAssert.AreEqual(
            new[] { "DROP INDEX", "ALTER INDEX" },
            Diff<RenameModels.GuardedBesideAGuardNamedLikeItsNextName, RenameModels.RenamedBesideAnUnguardedGadget>(provider));

    /// <summary>
    /// A declared object the database loses with an operation of EF Core's is gone by the time the
    /// rename runs, and is created again after it under its new name. Not on SQLite, which rebuilds
    /// the table for an altered column: that is another change, and a create after a rebuild is not
    /// placed there yet.
    /// </summary>
    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer)]
    public void A_rename_into_a_name_a_lost_declaration_gives_up_stays_in_place(Provider provider)
    {
        var shape = Diff<RenameModels.GuardedBesideAGuardNamedLikeItsNextName, RenameModels.RenamedIntoTheNameOfAGuardOnAnAlteredColumn>(provider, to =>
            ((TestGuardContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Guard")!).ImpactOverride =
                change => change is BaseChange.ColumnAltered ? new Impact.Lost() : new Impact.Follows());

        Assert.AreEqual("ALTER INDEX", shape[^2], string.Join(" | ", shape));
        Assert.AreEqual("CREATE UNIQUE", shape[^1], string.Join(" | ", shape));
        CollectionAssert.DoesNotContain(shape, "DROP INDEX");
    }

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void What_tables_are_made_of_is_not_rebuilt_around_a_collision(Provider provider)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Diff<PlanModels.Domain, RenameModels.DomainRenamedAndItsOldNameATable>(provider, to =>
            ((TestDomainContributor)to.GetService<SchemaContributorRegistry>().Find("Test.Domain")!).ClaimsOverride =
                descriptor => [new NameClaim.Relation(descriptor.Name.Schema, descriptor.Name.Name)]));

        StringAssert.Contains(error.Message, "TestDomainContributor'.Plan answered InPlace for 'dom' becoming 'dom2'");
    }

    /// <summary>Without a claim that collides, the domain's rename stays the statement it was.</summary>
    [TestMethod]
    public void What_tables_are_made_of_is_renamed_where_nothing_collides()
        => CollectionAssert.AreEqual(new[] { "CreateTableOperation", "ALTER DOMAIN" }, Diff<PlanModels.Domain, RenameModels.DomainRenamedAndItsOldNameATable>(Provider.Npgsql));
}
