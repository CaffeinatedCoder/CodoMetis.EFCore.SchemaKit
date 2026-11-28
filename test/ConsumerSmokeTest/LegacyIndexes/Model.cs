using CodoMetis.EFCore.SchemaKit;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;
using Smoke;

// The consumer's own namespace and types, unchanged by the move: the scaffolded files name them.
// The model is test/Fixtures/ComplexIndexes5x/Generator/Indexes.cs written with SchemaKit, as
// MovedIndexes.Declared in the PostgreSQL package's tests (Legacy/LegacyIndexFixture.cs) writes it.
// The three are kept alike by hand: the first row of this consumer in run.sh fails when they are not.
namespace Legacy5x.Indexes;

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
        modelBuilder.Entity<Vehicle>().UseTpcMappingStrategy().ToTable("vehicles");
        modelBuilder.Entity<Car>().ToTable("cars");
        modelBuilder.Entity<Boat>().ToTable("boats");

        var account = modelBuilder.Entity<Account>().ToTable("accounts");
        account.ComplexProperty(x => x.Contact);
        account.ComplexProperty(x => x.Prefs, prefs => prefs.ToJson("prefs"));
        account.ComplexProperty(x => x.Payload, payload => payload.ToJson("payload"));
        account.DeclarePostgres(OfAccounts);

        modelBuilder.Entity<Vehicle>().DeclarePostgres(pg => [pg.Unique(x => x.Plate, name: "ux_vehicle_plate"), pg.BTree(x => x.OwnerId)]);
    }

    private static PgIndex[] OfAccounts(PgSchema<Account> pg) =>
    [
        // On a property.
        pg.BTree(x => x.Contact.City),
        pg.Unique(x => x.Contact.Phone, where: x => x.DeletedOn == null, name: "ux_account_phone"),
        pg.BTree(x => x.Prefs.Theme),

        // On the entity type: a condition 5.x took as SQL is SQL written by hand here.
        pg.Unique(x => x.Email, where: x => Sql.Raw<bool>("\"DeletedOn\" IS NULL"), name: "ux_account_email_active"),
        pg.BTree(x => x.Email, name: "ix_account_email_all"),
        pg.Unique([pg.Key(x => x.GranteeId), pg.Key(x => x.Kind)]),
        pg.BTree([pg.Key(x => x.Kind), pg.Key(x => x.Rank).Desc(), pg.Key(x => x.Score).NullsFirst()]),

        // Npgsql's options.
        pg.BTree(
            [pg.Key(x => x.Title).Collate("C"), pg.Key(x => x.Nick)],
            include: [x => x.Rank, x => x.Contact.City],
            fillFactor: 70,
            deduplicateItems: false),
        pg.Unique([pg.Key(x => x.Nick).Ops(BTreeOpClass.TextPatternOps)], nullsDistinct: false, name: "ux_account_nick"),
        pg.BTree(x => x.Rank, concurrently: true, name: "ix_account_rank"),

        // The other access methods.
        pg.Gin(x => x.Tags),
        pg.Gist(x => x.Period),
        pg.Brin(x => x.CreatedAt, pagesPerRange: 16),
        pg.SpGist(x => x.Title, name: "ix_account_title_sp"),
        pg.Hash(x => x.Kind, name: "ix_account_kind_hash"),

        // Expressions: SQL 5.x took as written is SQL written by hand, a lambda is a lambda.
        pg.BTree(x => Sql.Raw<string>("lower(\"Email\")")),
        pg.Unique(x => x.Title.ToLower()),
        pg.BTree(x => (x.Nick ?? x.Kind) + " " + x.Title),
        pg.Unique(
            [pg.Key(x => Sql.Raw<string>("\"Kind\"")), pg.Key(x => Sql.Raw<string>("upper(\"Title\")")).Desc().NullsLast()],
            where: x => Sql.Raw<bool>("\"Rank\" > 0"),
            name: "ux_account_kind_title"),

        // Conditions written as lambdas.
        pg.BTree(x => x.Kind, where: x => x.DeletedOn == null, name: "ix_account_kind_live"),
        pg.BTree([pg.Key(x => x.GranteeId), pg.Key(x => x.Rank)], where: x => x.DeletedOn != null && x.Kind == "federated"),
        pg.BTree(x => x.Email.ToUpper(), where: x => x.Nick != "blocked"),

        // Members of a JSON document, and the document.
        pg.Unique(x => x.Prefs.Level, name: "ux_account_level"),
        pg.BTree(x => x.Prefs.Address.Zip),
        pg.BTree([pg.Key(x => x.Kind), pg.Key(x => x.Prefs.Active)], where: x => x.Prefs.Level > 3),
        pg.Gin(x => x.Payload, GinOpClass.JsonbPathOps),
    ];
}

/// <summary>The factory the 5.x project had, with SchemaKit's registration in place of 5.x's.</summary>
public sealed class IndexesContextFactory : IDesignTimeDbContextFactory<IndexesContext>
{
    public IndexesContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<IndexesContext>().UsePostgres().Options);
}

public static class Program
{
    public static void Main()
    {
    }
}
