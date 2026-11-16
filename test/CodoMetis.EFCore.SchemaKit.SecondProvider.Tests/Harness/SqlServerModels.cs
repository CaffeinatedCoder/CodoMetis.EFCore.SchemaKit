using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Number { get; set; } = "";
    public decimal Total { get; set; }
    public DateTime? ShippedAt { get; set; }
    public string? Note { get; set; }
}

public sealed class Invoice
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
    public DateTime? PaidAt { get; set; }
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>The models of the SQL Server tests. Each differs from <see cref="Indexed" /> in the one thing its name says.</summary>
public static class Sales
{
    public const string Name = "IX_orders_open";

    /// <summary>The open orders of a customer, with their totals at hand: a filtered nonclustered index with an included column.</summary>
    public static readonly ProbeIndex Open = new(["CustomerId"])
    {
        Name = Name,
        Include = ["Total"],
        Where = new Predicate("{ShippedAt} IS NULL"),
    };

    private static EntityTypeBuilder<Order> Orders(ModelBuilder modelBuilder, string table = "Orders", string? schema = null)
    {
        var orders = modelBuilder.Entity<Order>().ToTable(table, schema);
        orders.Property(x => x.Number).HasMaxLength(40);
        orders.Property(x => x.Total).HasPrecision(18, 2);
        return orders;
    }

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder);
    }

    public sealed class Indexed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open);
    }

    public sealed class Renamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open with { Name = "IX_orders_unshipped" });
    }

    /// <summary>The default name, <c>IX_Orders_CustomerId</c>: built from the table and the key.</summary>
    public sealed class Unnamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open with { Name = null });
    }

    // ── The index changes under its name: SQL Server alters none of this, so each is a drop and a create ──

    public sealed class IndexedOnTheShipped : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Where = new Predicate("{ShippedAt} IS NOT NULL") });
    }

    public sealed class IndexedWithTheNoteIncluded : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open with { Include = ["Total", "Note"] });
    }

    public sealed class IndexedUniquely : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open with { IsUnique = true });
    }

    // ── More than one key and more than one included column: their order is the index's ──

    public static readonly ProbeIndex ByCustomerAndNumber = new(["CustomerId", "Number"]) { Include = ["Total", "Note"] };

    public sealed class IndexedByCustomerAndNumber : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(ByCustomerAndNumber);
    }

    public sealed class IndexedByNumberAndCustomer : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(new ProbeIndex(["Number", "CustomerId"]) { Include = ["Note", "Total"] });
    }

    // ── A filter that names its column itself, with no placeholder: it is taken to read every column ──

    public sealed class FilteredByRawSql : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Where = new Predicate("[ShippedAt] IS NULL") });
    }

    public sealed class FilteredByRawSqlAndTheNoteRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Where = new Predicate("[ShippedAt] IS NULL") }).Property(x => x.Note).HasColumnName("Remark");
    }

    public sealed class FilteredByRawSqlAndItsColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Where = new Predicate("[SentAt] IS NULL") }).Property(x => x.ShippedAt).HasColumnName("SentAt");
    }

    // ── A table in a schema of its own: every statement has to say the schema ──

    public sealed class MovedBare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder, schema: "sales");
    }

    public sealed class MovedAndRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder, schema: "sales").DeclareProbe(Open with { Name = "IX_orders_unshipped" });
    }

    public sealed class MovedAndKeyColumnWidened : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder, schema: "sales").DeclareProbe(Open).Property(x => x.CustomerId).HasColumnType("bigint");
    }

    // ── EF Core renames what the index reads ──

    public sealed class KeyColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.CustomerId).HasColumnName("BuyerId");
    }

    /// <summary>The default name is built from the key, so it changes with the column: <c>IX_Orders_BuyerId</c>.</summary>
    public sealed class UnnamedKeyColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Name = null }).Property(x => x.CustomerId).HasColumnName("BuyerId");
    }

    public sealed class IncludedColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.Total).HasColumnName("Amount");
    }

    public sealed class FilterColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.ShippedAt).HasColumnName("SentAt");
    }

    public sealed class TableRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder, "Purchases").DeclareProbe(Open);
    }

    public sealed class UnnamedTableRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder, "Purchases").DeclareProbe(Open with { Name = null });
    }

    public sealed class TableAndFilterColumnRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder, "Purchases").DeclareProbe(Open).Property(x => x.ShippedAt).HasColumnName("SentAt");
    }

    public sealed class TableMoved : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder, schema: "sales").DeclareProbe(Open);
    }

    /// <summary>The schema a table without one is in already, now said: nothing moves.</summary>
    public sealed class TableInDboSaid : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder, schema: "dbo").DeclareProbe(Open);
    }

    // ── EF Core alters or drops what the index reads ──

    public sealed class KeyColumnWidened : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.CustomerId).HasColumnType("bigint");
    }

    public sealed class IncludedColumnWidened : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.Total).HasPrecision(19, 4);
    }

    public sealed class FilterColumnRetyped : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.ShippedAt).HasColumnType("datetime");
    }

    /// <summary>The key becomes a computed column, which EF Core writes as a drop and an add.</summary>
    public sealed class KeyColumnComputed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open).Property(x => x.CustomerId).HasComputedColumnSql("[Id] % 10", stored: true);
    }

    /// <summary>The included column is gone from the model and from the index.</summary>
    public sealed class WithoutTheTotal : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Include = [] }).Ignore(x => x.Total);
    }

    /// <summary>The column the filter reads is gone, and the filter with it.</summary>
    public sealed class WithoutTheShippingDate : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Where = null }).Ignore(x => x.ShippedAt);
    }

    /// <summary>The key is gone, and the index is on the order's number instead.</summary>
    public sealed class WithoutTheCustomer : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Keys = ["Number"] }).Ignore(x => x.CustomerId);
    }

    /// <summary>A column the index does not read is gone.</summary>
    public sealed class WithoutTheNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open).Ignore(x => x.Note);
    }

    // ── An alteration EF Core writes no ALTER COLUMN for, and one the server takes under an index ──

    public static readonly ProbeIndex ByNumber = new(["Number"]) { Name = "IX_orders_number", Include = ["Total"] };

    public sealed class IndexedByNumber : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(ByNumber);
    }

    public sealed class IndexedByAnOptionalNumber : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(ByNumber).Property(x => x.Number).IsRequired(false);
    }

    public sealed class IndexedByANumberWithADefault : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(ByNumber).Property(x => x.Number).HasDefaultValue("none");
    }

    // ── Names ──

    public sealed class NamedLikeThePrimaryKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open with { Name = "PK_Orders" });
    }

    public sealed class NamedLikeThePrimaryKeyInAnotherCase : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open with { Name = "pk_orders" });
    }

    /// <summary>An alternate key, which SQL Server holds as a unique constraint on an index of its name.</summary>
    public sealed class NamedLikeAUniqueConstraint : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Name = "AK_Orders_Number" }).HasAlternateKey(x => x.Number);
    }

    /// <summary>A foreign key is an object of the schema and no index: the name is free among the table's indexes.</summary>
    public sealed class NamedLikeItsForeignKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToTable("Customers");
            Orders(modelBuilder).DeclareProbe(Open with { Name = "FK_Orders_Customers_CustomerId" })
                .HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId);
        }
    }

    public sealed class NamedLikeACheckConstraint : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Name = "CK_Orders_Total" })
                .ToTable(t => t.HasCheckConstraint("CK_Orders_Total", "[Total] >= 0"));
    }

    /// <summary>Two names an ordinal comparison keeps apart and one that ignores case keeps apart too: <c>ß</c> and <c>ss</c>.</summary>
    public sealed class TwoIndexesTheCollationTakesForOne : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Name = "IX_Straße" }, ByNumber with { Name = "IX_Strasse" });
    }

    public sealed class TwoIndexesThatDifferInCase : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Name = "IX_orders" }, ByNumber with { Name = "IX_ORDERS" });
    }

    /// <summary>A default whose constraint has a name EF Core gives it: an object of the schema, like a check constraint.</summary>
    public sealed class WithANamedDefault : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.UseNamedDefaultConstraints();
            Orders(modelBuilder).DeclareProbe(Open).Property(x => x.Total).HasDefaultValue(0m);
        }
    }

    // ── Two tables ──

    private static readonly ProbeIndex Unpaid = new(["CustomerId"]) { Name = Name, Include = ["Total"], Where = new Predicate("{PaidAt} IS NULL") };

    private static EntityTypeBuilder<Invoice> Invoices(ModelBuilder modelBuilder)
    {
        var invoices = modelBuilder.Entity<Invoice>().ToTable("Invoices");
        invoices.Property(x => x.Total).HasPrecision(18, 2);
        return invoices;
    }

    /// <summary>One index name on two tables, which SQL Server allows: an index is named within its table.</summary>
    public sealed class TwoTables : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Orders(modelBuilder).DeclareProbe(Open);
            Invoices(modelBuilder).DeclareProbe(Unpaid);
        }
    }

    /// <summary>The invoices are gone, table and index.</summary>
    public sealed class OrdersAlone : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Orders(modelBuilder).DeclareProbe(Open);
    }

    public sealed class TwoTablesOneIndex : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Orders(modelBuilder);
            Invoices(modelBuilder).DeclareProbe(Unpaid);
        }
    }

    private static EntityTypeBuilder<Invoice> ArchivedOrders(ModelBuilder modelBuilder)
    {
        var archived = modelBuilder.Entity<Invoice>().ToTable("Orders", "archive");
        archived.Property(x => x.Total).HasPrecision(18, 2);
        return archived;
    }

    /// <summary>Two tables of one name in two schemas, each with an index of one name: only the schema tells the two apart.</summary>
    public sealed class TwoSchemas : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Orders(modelBuilder).DeclareProbe(Open);
            ArchivedOrders(modelBuilder).DeclareProbe(Unpaid);
        }
    }

    public sealed class TwoSchemasOneIndex : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Orders(modelBuilder).DeclareProbe(Open);
            ArchivedOrders(modelBuilder);
        }
    }

    public sealed class TwoSchemasAndTheArchivesIndexRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            Orders(modelBuilder).DeclareProbe(Open);
            ArchivedOrders(modelBuilder).DeclareProbe(Unpaid with { Name = "IX_orders_unpaid" });
        }
    }

    // ── Objects of the schema, which the dialect answers for ──

    /// <summary>A table in <c>dbo</c> by name, a foreign key to it, a check constraint and a sequence: one of each kind SQL Server keeps in the schema.</summary>
    public sealed class WithObjectsOfTheSchema : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence<int>("Numbers");
            modelBuilder.Entity<Customer>().ToTable("Customers", "dbo");
            Orders(modelBuilder).DeclareProbe(Open)
                .ToTable(t => t.HasCheckConstraint("CK_Orders_Total", "[Total] >= 0"))
                .HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId);
        }
    }

    // ── A name a rename frees or takes in the same migration (#99, on the second provider) ──

    /// <summary>The index renamed, and an index of EF Core's own under the name it gave up.</summary>
    public sealed class RenamedAndItsOldNameReused : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(Open with { Name = "IX_orders_unshipped" }).HasIndex(x => x.Number).HasDatabaseName(Name);
    }

    // ── Behaviour ──

    /// <summary>No two open orders share a number; shipped ones may.</summary>
    public sealed class OpenNumbersAreUnique : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Orders(modelBuilder).DeclareProbe(new ProbeIndex(["Number"]) { Name = "UX_orders_open_number", IsUnique = true, Where = new Predicate("{ShippedAt} IS NULL") });
    }
}
