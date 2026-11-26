using EFCore.ComplexIndexes;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace Legacy5x.Indexes;

// Every kind of index 5.x stored (CodoMetis.EFCore.SchemaKit#27, slice E): declared on a property
// and on the entity type, with keys that are columns, expressions, typed lambdas and members of a
// JSON document, each access method, and each option of Npgsql's that 5.x carried.
//
// test/…PostgreSQL.Tests/Legacy/LegacyIndexFixture.cs declares the same model with SchemaKit. The
// two are kept alike by hand; LegacyIndexHandoverTests fails when they are not.

public sealed class Contact
{
    public string Phone { get; set; } = "";
    public string City { get; set; } = "";
}

public sealed class Place
{
    public string Zip { get; set; } = "";
}

public sealed class Settings
{
    public int Level { get; set; }
    public string Theme { get; set; } = "";
    public bool Active { get; set; }
    public Place Address { get; set; } = new();
}

public sealed class Account
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string? Nick { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int GranteeId { get; set; }
    public int Rank { get; set; }
    public int? Score { get; set; }
    public DateOnly? DeletedOn { get; set; }
    public DateTime CreatedAt { get; set; }
    public string[] Tags { get; set; } = [];
    public NpgsqlRange<DateOnly> Period { get; set; }
    public Contact Contact { get; set; } = new();
    public Settings Prefs { get; set; } = new();
    public Settings Payload { get; set; } = new();
}

public class Vehicle
{
    public int Id { get; set; }
    public string Plate { get; set; } = "";
    public int OwnerId { get; set; }
}

public sealed class Car : Vehicle;

public sealed class Boat : Vehicle;

public sealed class IndexesContext(DbContextOptions<IndexesContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(account =>
        {
            account.ToTable("accounts");

            // On a property: the default name, and one with everything the parameters take.
            account.ComplexProperty(x => x.Contact, contact =>
            {
                contact.Property(x => x.City).HasComplexIndex();
                contact.Property(x => x.Phone).HasComplexIndex(isUnique: true, filter: "{DeletedOn} IS NULL", indexName: "ux_account_phone");
            });

            account.ComplexProperty(x => x.Prefs, prefs =>
            {
                prefs.ToJson("prefs");

                // On a property that is a member of a JSON document.
                prefs.Property(x => x.Theme).HasComplexIndex();
            });
            account.ComplexProperty(x => x.Payload, payload => payload.ToJson("payload"));

            // On the entity type: one column with a filter written as SQL, and the same column again.
            account.HasComplexIndex(x => x.Email, isUnique: true, filter: "\"DeletedOn\" IS NULL", indexName: "ux_account_email_active");
            account.HasComplexIndex(x => x.Email, indexName: "ix_account_email_all");

            // Several columns: as paths, and with an order.
            account.HasComplexCompositeIndex(x => new { x.GranteeId, x.Kind }, isUnique: true);
            account.HasComplexCompositeIndex(x => new { x.Kind, Rank = DbOrder.Desc(x.Rank), Score = DbOrder.NullsFirst(x.Score) });

            // Npgsql's options.
            account.HasComplexCompositeIndex(x => new { x.Title, x.Nick }, idx => idx
                .UseCollation("C", "")
                .HasStorageParameter("fillfactor", 70)
                .HasStorageParameter("deduplicate_items", false)
                .IncludeProperties("Rank", "Contact.City"));
            account.HasComplexIndex(x => x.Nick, idx => idx.IsUnique().AreNullsDistinct(false).HasOperators("text_pattern_ops").HasName("ux_account_nick"));
            account.HasComplexIndex(x => x.Rank, idx => idx.IsCreatedConcurrently().HasName("ix_account_rank"));

            // The other access methods.
            account.HasComplexIndex(x => x.Tags, idx => idx.UseGin());
            account.HasComplexIndex(x => x.Period, idx => idx.UseGist());
            account.HasComplexIndex(x => x.CreatedAt, idx => idx.UseBrin().HasStorageParameter("pages_per_range", 16));
            account.HasComplexIndex(x => x.Title, idx => idx.UseSpGist().HasName("ix_account_title_sp"));
            account.HasComplexIndex(x => x.Kind, idx => idx.UseHash().HasName("ix_account_kind_hash"));

            // Expressions: as SQL, as a lambda, and built part by part.
            account.HasExpressionIndex("lower(\"Email\")");
            account.HasExpressionIndex(x => x.Title.ToLower(), isUnique: true);
            account.HasExpressionIndex(x => (x.Nick ?? x.Kind) + " " + x.Title);
            account.HasExpressionIndex(idx => idx
                .Expression("\"Kind\"")
                .Expression("upper(\"Title\")").Descending().NullsLast()
                .IsUnique()
                .HasFilter("\"Rank\" > 0")
                .HasName("ux_account_kind_title"));

            // Conditions written as lambdas.
            account.HasComplexIndex(x => x.Kind, x => x.DeletedOn == null, indexName: "ix_account_kind_live");
            account.HasComplexCompositeIndex(x => new { x.GranteeId, x.Rank }, x => x.DeletedOn != null && x.Kind == "federated");
            account.HasExpressionIndex(x => x.Email.ToUpper(), x => x.Nick != "blocked");

            // Members of a JSON document, and the document.
            account.HasComplexIndex(x => x.Prefs.Level, isUnique: true, indexName: "ux_account_level");
            account.HasComplexIndex(x => x.Prefs.Address.Zip);
            account.HasComplexCompositeIndex(x => new { x.Kind, x.Prefs.Active }, x => x.Prefs.Level > 3);
            account.HasComplexIndex(x => x.Payload, idx => idx.UseGin().HasOperators("jsonb_path_ops"));
        });

        // A TPC hierarchy: a copy in every table, under a stated name and under the default one.
        modelBuilder.Entity<Vehicle>(vehicle =>
        {
            vehicle.UseTpcMappingStrategy().ToTable("vehicles");
            vehicle.HasComplexIndex(x => x.Plate, isUnique: true, indexName: "ux_vehicle_plate");
            vehicle.HasComplexIndex(x => x.OwnerId);
        });
        modelBuilder.Entity<Car>().ToTable("cars");
        modelBuilder.Entity<Boat>().ToTable("boats");
    }
}

public sealed class IndexesContextFactory : IDesignTimeDbContextFactory<IndexesContext>
{
    public IndexesContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<IndexesContext>()
            .UseNpgsql("Host=localhost;Database=legacy5x")
            .UseNpgsqlComplexIndexes()
            .Options);
}
