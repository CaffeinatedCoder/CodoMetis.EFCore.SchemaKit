using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;

public enum Mode
{
    Quiet,
    Loud,
}

public sealed record Mail(string Value);

public sealed class Place
{
    public string City { get; set; } = "";
    public int Zip { get; set; }
    public int OddZip { get; set; }
}

/// <summary>A document with a member of each kind a query reads differently.</summary>
public sealed class Prefs
{
    public int Rank { get; set; }
    public string Theme { get; set; } = "";
    public string? Nick { get; set; }
    public bool Active { get; set; }
    public bool? Flag { get; set; }
    public int? Level { get; set; }
    public long Big { get; set; }
    public short Small { get; set; }
    public decimal Price { get; set; }
    public decimal Money { get; set; }
    public double Ratio { get; set; }
    public float Share { get; set; }
    public Guid Key { get; set; }
    public Mode Mode { get; set; }
    public Mode ModeText { get; set; }
    public char Letter { get; set; }
    public byte[] Blob { get; set; } = [];
    public string Renamed { get; set; } = "";
    public string Odd { get; set; } = "";
    public string Code { get; set; } = "";
    public Mail Mail { get; set; } = new("");
    public int[] Tags { get; set; } = [];
    public Place Address { get; set; } = new();

    // The members a query reads through a cast that is not immutable.
    public DateTime Seen { get; set; }
    public DateOnly Day { get; set; }
    public TimeOnly At { get; set; }
    public TimeSpan Span { get; set; }
    public DateTimeOffset Stamp { get; set; }
}

public sealed class Item
{
    public string Name { get; set; } = "";
    public int Qty { get; set; }
}

/// <summary>A person with a required document, an optional one and a collection of documents.</summary>
public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public NpgsqlRange<DateOnly> Period { get; set; }
    public Prefs Prefs { get; set; } = new();
    public Prefs? Extra { get; set; }
    public List<Item> Items { get; set; } = [];
}

/// <summary>The models of the tests of members of a JSON document.</summary>
public static class JsonModels
{
    public static readonly PgSchema<Person> Pg = PgSchema.For<Person>();

    public static EntityTypeBuilder<Person> People(ModelBuilder modelBuilder, string container = "prefs")
    {
        var people = modelBuilder.Entity<Person>().ToTable("people");
        people.ComplexProperty(x => x.Prefs, prefs =>
        {
            prefs.ToJson(container);
            prefs.Property(x => x.Renamed).HasJsonPropertyName("re_named");
            prefs.Property(x => x.Odd).HasJsonPropertyName("odd name's");
            prefs.Property(x => x.ModeText).HasConversion<string>();
            prefs.Property(x => x.Money).HasPrecision(10, 2);
            prefs.Property(x => x.Code).HasMaxLength(20);
            prefs.Property(x => x.Mail).HasConversion(mail => mail.Value, value => new Mail(value));
            prefs.ComplexProperty(x => x.Address, address =>
            {
                address.HasJsonPropertyName("addr");
                address.Property(x => x.OddZip).HasJsonPropertyName("odd-zip");
            });
        });
        people.ComplexProperty(x => x.Extra, extra =>
        {
            extra.ToJson();
            extra.Property(x => x.Mail).HasConversion(mail => mail.Value, value => new Mail(value));
        });
        people.ComplexCollection(x => x.Items, items => items.ToJson());
        return people;
    }

    public sealed class Bare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder);
    }

    /// <summary>
    /// One index for each member a query reads its own way, named after the member. The same
    /// names are the rows of <c>PgJsonMemberTests</c>, which holds each key against the query.
    /// </summary>
    public sealed class EveryMember : ITestModel
    {
        public static readonly PgIndex[] Declared =
        [
            Pg.BTree(x => x.Prefs.Rank, name: "ix_rank"),
            Pg.BTree(x => x.Prefs.Theme, name: "ix_theme"),
            Pg.BTree(x => x.Prefs.Nick, name: "ix_nick"),
            Pg.BTree(x => x.Prefs.Active, name: "ix_active"),
            Pg.BTree(x => x.Prefs.Flag, name: "ix_flag"),
            Pg.BTree(x => x.Prefs.Level, name: "ix_level"),
            Pg.BTree(x => x.Prefs.Big, name: "ix_big"),
            Pg.BTree(x => x.Prefs.Small, name: "ix_small"),
            Pg.BTree(x => x.Prefs.Price, name: "ix_price"),
            Pg.BTree(x => x.Prefs.Money, name: "ix_money"),
            Pg.BTree(x => x.Prefs.Ratio, name: "ix_ratio"),
            Pg.BTree(x => x.Prefs.Share, name: "ix_share"),
            Pg.BTree(x => x.Prefs.Key, name: "ix_key"),
            Pg.BTree(x => x.Prefs.Mode, name: "ix_mode"),
            Pg.BTree(x => x.Prefs.ModeText, name: "ix_modetext"),
            Pg.BTree(x => x.Prefs.Letter, name: "ix_letter"),
            Pg.BTree(x => x.Prefs.Blob, name: "ix_blob"),
            Pg.BTree(x => x.Prefs.Renamed, name: "ix_renamed"),
            Pg.BTree(x => x.Prefs.Odd, name: "ix_odd"),
            Pg.BTree(x => x.Prefs.Code, name: "ix_code"),
            Pg.BTree(x => x.Prefs.Mail, name: "ix_mail"),
            Pg.Gin(x => x.Prefs.Tags, name: "ix_tags"),
            Pg.BTree(x => x.Prefs.Address.City, name: "ix_city"),
            Pg.BTree(x => x.Prefs.Address.Zip, name: "ix_zip"),
            Pg.BTree(x => x.Prefs.Address.OddZip, name: "ix_oddzip"),
            Pg.BTree(x => x.Extra!.Rank, name: "ix_extra_rank"),
        ];

        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(_ => Declared);
    }

    public sealed class Documents : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Gin(x => x.Prefs, GinOpClass.JsonbPathOps, name: "ix_doc"),
            pg.Gin(x => x.Extra, name: "ix_extra"),
            pg.Gin(x => x.Items, name: "ix_items"),
            pg.Gin(x => x.Prefs.Address, name: "ix_addr"),
            pg.BTree(x => x.Name, include: [x => x.Prefs], name: "ix_doc_incl"),
        ]);
    }

    public sealed class ADocumentsClassOnAMember : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(pg => [pg.Gin(x => x.Prefs.Rank, GinOpClass.JsonbPathOps, name: "ix_wrong")]);
    }

    public sealed class Unnamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg =>
        [
            pg.BTree(x => x.Prefs.Rank),
            pg.BTree([pg.Key(x => x.Prefs.Address.City), pg.Key(x => x.Name)]),
            pg.Gin(x => x.Prefs),
            pg.BTree(x => x.Prefs.Theme.ToLower()),
        ]);
    }

    public sealed class Conditions : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg =>
        [
            pg.BTree(x => x.Name, where: x => x.Prefs.Active && x.Prefs.Rank > 5, name: "ix_cond"),
            pg.BTree(x => x.Name, where: x => x.Prefs.Nick != "a", name: "ix_nick"),
            pg.BTree(x => x.Name, where: x => x.Prefs.ModeText == Mode.Loud && x.Prefs.Mode == Mode.Loud, name: "ix_mode"),
            pg.BTree(x => x.Name, where: x => x.Extra!.Rank != 5, name: "ix_optional"),
            pg.BTree(x => x.Prefs.Theme.ToLower(), name: "ix_expr"),
        ]);
    }

    public sealed class Excluding : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.Prefs.Rank).Overlaps(x => x.Period), where: x => x.Prefs.Active, name: "ex_rank")]);
    }

    /// <summary><see cref="Excluding" /> without the document, whose column EF Core drops: the period alone.</summary>
    public sealed class ExcludingWithoutTheDocument : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var people = People(modelBuilder);
            people.Ignore(x => x.Prefs);
            people.DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), name: "ex_rank")]);
        }
    }

    /// <summary>An index on a member, and the model without the document and without the index.</summary>
    public sealed class WithoutTheDocument : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).Ignore(x => x.Prefs);
    }

    public sealed class ADateAsAKey : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.Prefs.Day, name: "ix_day")]);
    }

    public sealed class ATimestampInACondition : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.Name, where: x => x.Extra!.Seen > new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), name: "ix_seen")]);
    }

    public sealed class ATimeInAnExpression : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(_ => [new PgIndex.BTree([new BTreeKey(new KeyTarget.Template(SqlTemplate.FromPlaceholders("{Prefs.At} IS NULL")))]) { Name = "ix_at" }]);
    }

    public sealed class AnIntervalInAnElement : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.Prefs.Span).Overlaps(x => x.Period), name: "ex_span")]);
    }

    public sealed class AnOffsetInAConstraintsCondition : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg =>
            [pg.Exclusion(pg.Equal(x => x.Id).Overlaps(x => x.Period), where: x => x.Prefs.Stamp > DateTimeOffset.UnixEpoch, name: "ex_stamp")]);
    }

    public sealed class Dated
    {
        public int Id { get; set; }
        public DateOnly? Born { get; set; }
    }

    public sealed class DatedBare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Dated>().ToTable("dated");
    }

    public sealed class ADateColumn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Dated>().ToTable("dated").DeclarePostgres(pg => [pg.BTree(x => x.Born, where: x => x.Born != null, name: "ix_born")]);
    }

    public sealed class AMemberWithAColumnType : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var people = People(modelBuilder);
            people.ComplexProperty(x => x.Prefs).Property(x => x.Price).HasColumnType("numeric(12,4)");
            people.DeclarePostgres(pg => [pg.BTree(x => x.Prefs.Price, name: "ix_price")]);
        }
    }

    public sealed class AMemberIncluded : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.Name, include: [x => x.Prefs.Rank], name: "ix_incl")]);
    }

    public sealed class ThroughACollection : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(_ => [new PgIndex.BTree([new BTreeKey(new KeyTarget.Column("Items.Name"))]) { Name = "ix_item" }]);
    }

    /// <summary><see cref="EveryMember" /> with the document in a column of another name.</summary>
    public sealed class EveryMemberInARenamedColumn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder, container: "preferences").DeclarePostgres(_ => EveryMember.Declared);
    }

    /// <summary>One unnamed index on a member, and the same with the document's column renamed.</summary>
    public sealed class UnnamedOnRank : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.Prefs.Rank)]);
    }

    public sealed class UnnamedOnRankInARenamedColumn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => People(modelBuilder, container: "preferences").DeclarePostgres(pg => [pg.BTree(x => x.Prefs.Rank)]);
    }

    /// <summary>No two people of one rank, among the active ones: a unique index on a member, with a condition on another.</summary>
    public sealed class OneActivePerRank : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => People(modelBuilder).DeclarePostgres(pg => [pg.Unique(x => x.Prefs.Rank, where: x => x.Prefs.Active, name: "ux_rank")]);
    }

    public sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public CustomerPrefs Prefs { get; set; } = new();
        public CustomerPrefs Owned { get; set; } = new();
    }

    public sealed class CustomerPrefs
    {
        public int Rank { get; set; }
        public bool Active { get; set; }
    }

    public sealed class CustomersBare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers");
            customer.Ignore(x => x.Owned);
            customer.ComplexProperty(x => x.Prefs, prefs => prefs.ToJson("prefs"));
        });
    }

    /// <summary>The README's sample of members of a JSON document, as it stands there.</summary>
    public sealed class AsInTheReadme : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers");
            customer.Ignore(x => x.Owned);
            customer.ComplexProperty(x => x.Prefs, prefs => prefs.ToJson("prefs"));
            customer.DeclarePostgres(pg =>
            [
                pg.BTree(x => x.Prefs.Rank, name: "ix_customer_rank"),
                pg.BTree(x => x.Name, where: x => x.Prefs.Active, name: "ix_customer_active"),
                pg.Gin(x => x.Prefs, GinOpClass.JsonbPathOps, name: "ix_customer_prefs"),
            ]);
        });
    }

    /// <summary>A path into an owned type mapped to JSON, which is a navigation and no complex property.</summary>
    public sealed class IntoAnOwnedDocument : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers");
            customer.ComplexProperty(x => x.Prefs, prefs => prefs.ToJson("prefs"));
            customer.OwnsOne(x => x.Owned, owned => owned.ToJson("owned"));
            customer.DeclarePostgres(pg => [pg.BTree(x => x.Owned.Rank, name: "ix_owned")]);
        });
    }
}

