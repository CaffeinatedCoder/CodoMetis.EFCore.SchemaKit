using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;

/// <summary>A document, with a column for each kind of value an access method other than B-tree is for.</summary>
public sealed class Document
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int Rank { get; set; }
    public string[] Tags { get; set; } = [];

    /// <summary>A <c>jsonb</c> column, held as its text.</summary>
    public string Payload { get; set; } = "{}";

    public NpgsqlRange<DateOnly> Period { get; set; }
}

/// <summary>The models of the index tests: the grants of <see cref="Models" />, with indexes declared on them.</summary>
public static class IndexModels
{
    public const string ActiveName = "ux_role_grant_active";

    public const string ByNoteName = "ix_role_grant_note";

    private static readonly PgSchema<RoleGrant> Pg = PgSchema.For<RoleGrant>();

    /// <summary>
    /// The design's first declaration: one grant per grantee and role among those not revoked,
    /// nulls not distinct, carrying the note.
    /// </summary>
    public static readonly PgIndex.BTree Active = Pg.Unique(
        [Pg.Key(x => x.GranteeId), Pg.Key(x => x.RoleId)],
        where: x => x.RevokedOn == null,
        include: [x => x.Note],
        nullsDistinct: false,
        name: ActiveName);

    /// <summary>The design's third: an expression, and a second key descending with its nulls last.</summary>
    public static readonly PgIndex.BTree ByNote = Pg.BTree(
        [Pg.Key(x => x.Note!.ToLower()), Pg.Key(x => x.RevokedOn).Desc().NullsLast()],
        name: ByNoteName);

    private static EntityTypeBuilder<RoleGrant> Grants(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoleGrant>().ToTable("role_grants");

    public sealed class WithActive : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active]);
    }

    public sealed class WithByNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [ByNote]);
    }

    public sealed class WithBoth : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active, ByNote]);
    }

    /// <summary>One column and nothing else, through the lambda alone: the name is the default one.</summary>
    public sealed class OnGrantee : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.GranteeId)]);
    }

    /// <summary>No name, a column and an expression: the default name takes the column's name and the expression's letters.</summary>
    public sealed class Unnamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.GranteeId), pg.Key(x => x.Note!.ToLower())])]);
    }

    /// <summary><see cref="WithActive" />, built without locking the table against writes.</summary>
    public sealed class WithActiveConcurrently : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Concurrently = true }]);
    }

    public sealed class ActiveConcurrentlyRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Concurrently = true, Name = "ux_renamed" }]);
    }

    /// <summary>Another index under the name, still built concurrently: a rebuild.</summary>
    public sealed class ActiveConcurrentlyNotUnique : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Concurrently = true, Unique = null }]);
    }

    /// <summary>An index built in the migration's transaction, and one built outside it.</summary>
    public sealed class ByNoteAndActiveConcurrently : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(_ => [ByNote, Active with { Concurrently = true }]);
    }

    /// <summary>The grants with the grantee as a <c>bigint</c>, and no index.</summary>
    public sealed class LongGrantees : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).Property(x => x.GranteeId).HasColumnType("bigint");
    }

    /// <summary><see cref="LongGrantees" /> with <see cref="Active" /> built concurrently: from <see cref="Models.Bare" />, a column altered and then the index.</summary>
    public sealed class ActiveConcurrentlyForLongGrantees : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnType("bigint");
            grants.DeclarePostgres(_ => [Active with { Concurrently = true }]);
        }
    }

    /// <summary>The grants with one more column and <see cref="Active" /> built concurrently: from <see cref="Models.Bare" />, a column added and then the index.</summary>
    public sealed class ActiveConcurrentlyWithOneMoreColumn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property<int?>("Seat");
            grants.DeclarePostgres(_ => [Active with { Concurrently = true }]);
        }
    }

    public sealed class UnnamedConcurrently : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.GranteeId), pg.Key(x => x.Note!.ToLower())], concurrently: true)]);
    }

    public sealed class ActiveRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Name = "ux_renamed" }]);
    }

    /// <summary><see cref="WithActive" /> on a table in a schema of its own.</summary>
    public sealed class ActiveInASchema : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(_ => [Active]);
    }

    public sealed class ActiveRenamedInASchema : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(_ => [Active with { Name = "ux_renamed" }]);
    }

    // ── What is another index under the same name ──

    public sealed class ActiveNotUnique : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Unique = null }]);
    }

    public sealed class ActiveWithDistinctNulls : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Unique = new Uniqueness() }]);
    }

    public sealed class ActiveWithoutTheNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Include = [] }]);
    }

    public sealed class ActiveWithoutACondition : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Where = null }]);
    }

    public sealed class ActiveTheOtherWayRound : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Keys = [Active.Keys[1], Active.Keys[0]] }]);
    }

    public sealed class ActiveDescending : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(_ => [Active with { Keys = [Active.Keys[0].Desc(), Active.Keys[1]] }]);
    }

    /// <summary>
    /// <see cref="ByNote" /> with every order written out that PostgreSQL has anyway: the first
    /// key's nulls last, as an ascending key has them. The same index.
    /// </summary>
    public sealed class ByNoteWithItsDefaultsWritten : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(_ => [ByNote with { Keys = [ByNote.Keys[0].NullsLast(), ByNote.Keys[1]] }]);
    }

    /// <summary>A descending key, whose nulls are first, and the same key saying so.</summary>
    public sealed class Descending : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.RevokedOn).Desc()], name: "ix_revoked")]);
    }

    public sealed class DescendingNullsFirst : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.RevokedOn).Desc().NullsFirst()], name: "ix_revoked")]);
    }

    public sealed class AscendingNullsFirst : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.RevokedOn).NullsFirst()], name: "ix_revoked")]);
    }

    // ── EF Core changes what the indexes read ──

    /// <summary>Both indexes, and EF Core renames the table and a column of each kind: a key, an included one, the condition's, an expression's.</summary>
    public sealed class BothRenamedAround : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = modelBuilder.Entity<RoleGrant>().ToTable("grants");
            grants.Property(x => x.GranteeId).HasColumnName("grantee");
            grants.Property(x => x.RevokedOn).HasColumnName("revoked");
            grants.Property(x => x.Note).HasColumnName("memo");
            grants.DeclarePostgres(_ => [Active, ByNote]);
        }
    }

    /// <summary><see cref="Unnamed" />, and EF Core renames a column its default name is built from.</summary>
    public sealed class UnnamedRenamedAround : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnName("grantee");
            grants.DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.GranteeId), pg.Key(x => x.Note!.ToLower())])]);
        }
    }

    public sealed class BothMoved : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(_ => [Active, ByNote]);
    }

    public sealed class UnnamedMoved : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants")
                .DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.GranteeId), pg.Key(x => x.Note!.ToLower())])]);
    }

    /// <summary>Both indexes, and a key column of another type.</summary>
    public sealed class BothWithALongGrantee : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnType("bigint");
            grants.DeclarePostgres(_ => [Active, ByNote]);
        }
    }

    /// <summary>Both indexes, and the note of another type: included in one, in an expression of the other.</summary>
    public sealed class BothWithAShortNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasMaxLength(40);
            grants.DeclarePostgres(_ => [Active, ByNote]);
        }
    }

    /// <summary>An index whose condition reads the grantee, and no key of it does.</summary>
    public sealed class ForGrantees : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.BTree(x => x.RoleId, where: x => x.GranteeId > 0, name: "ix_role_for_grantees")]);
    }

    /// <summary><see cref="ForGrantees" />, and the column its condition reads of another type.</summary>
    public sealed class ForLongGrantees : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.GranteeId).HasColumnType("bigint");
            grants.DeclarePostgres(pg => [pg.BTree(x => x.RoleId, where: x => x.GranteeId > 0, name: "ix_role_for_grantees")]);
        }
    }

    /// <summary><see cref="Active" /> without the role, whose column EF Core drops: a key.</summary>
    public sealed class ActiveWithoutTheRole : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.RoleId);
            grants.DeclarePostgres(_ => [Active with { Keys = [Active.Keys[0]] }]);
        }
    }

    /// <summary><see cref="Active" /> without its condition, and EF Core drops the column only the condition read.</summary>
    public sealed class ActiveWithoutRevocation : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.RevokedOn);
            grants.DeclarePostgres(_ => [Active with { Where = null }]);
        }
    }

    /// <summary><see cref="Active" /> without the note, and EF Core drops the note: an included column.</summary>
    public sealed class ActiveAndNoNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.Note);
            grants.DeclarePostgres(_ => [Active with { Include = [] }]);
        }
    }

    /// <summary><see cref="ByNote" /> on the revocation alone, and EF Core drops the note: a column an expression read.</summary>
    public sealed class ByNoteAndNoNote : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.Note);
            grants.DeclarePostgres(_ => [ByNote with { Keys = [ByNote.Keys[1]] }]);
        }
    }

    /// <summary>The table without the note and with nothing declared: the index went with its column.</summary>
    public sealed class NoNoteAndNothingDeclared : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).Ignore(x => x.Note);
    }

    /// <summary><see cref="Active" /> and a key that is SQL written by hand, which names no column through a placeholder.</summary>
    public sealed class ByHandWrittenSql : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Grants(modelBuilder).DeclarePostgres(_ =>
            [new PgIndex.BTree([new BTreeKey(new KeyTarget.Template(SqlTemplate.Raw("""upper("Note")""")))]) { Name = "ix_by_hand" }]);
    }

    /// <summary><see cref="ByHandWrittenSql" />, and EF Core drops a column the SQL does not read.</summary>
    public sealed class ByHandWrittenSqlWithoutTheRole : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Ignore(x => x.RoleId);
            grants.DeclarePostgres(_ =>
                [new PgIndex.BTree([new BTreeKey(new KeyTarget.Template(SqlTemplate.Raw("""upper("Note")""")))]) { Name = "ix_by_hand" }]);
        }
    }

    private static PgIndex.BTree ByHand(string sql) => new([new BTreeKey(new KeyTarget.Template(SqlTemplate.Raw(sql)))]) { Name = "ix_by_hand" };

    /// <summary><see cref="ByHandWrittenSql" />, and EF Core renames the column the SQL reads. The SQL still says the old name.</summary>
    public sealed class ByHandWrittenSqlWithTheNoteRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasColumnName("memo");
            grants.DeclarePostgres(_ => [ByHand("""upper("Note")""")]);
        }
    }

    /// <summary>The same rename, with the SQL written for the new name.</summary>
    public sealed class ByHandWrittenSqlForTheMemo : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.Note).HasColumnName("memo");
            grants.DeclarePostgres(_ => [ByHand("upper(memo)")]);
        }
    }

    /// <summary><see cref="ByHandWrittenSql" /> on a table EF Core renames.</summary>
    public sealed class ByHandWrittenSqlOnARenamedTable : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().ToTable("grants").DeclarePostgres(_ => [ByHand("""upper("Note")""")]);
    }

    /// <summary><see cref="ByHandWrittenSql" /> on a table EF Core moves to another schema.</summary>
    public sealed class ByHandWrittenSqlInASchema : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(_ => [ByHand("""upper("Note")""")]);
    }

    /// <summary><see cref="ByHandWrittenSql" />, and EF Core renames a column the SQL does not read.</summary>
    public sealed class ByHandWrittenSqlWithTheRoleRenamed : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.Property(x => x.RoleId).HasColumnName("role");
            grants.DeclarePostgres(_ => [ByHand("""upper("Note")""")]);
        }
    }

    // ── Names ──

    /// <summary>An index of EF Core's under the name the default name of <see cref="OnGrantee" /> is.</summary>
    public sealed class OnGranteeBesideEfCoresOwn : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.HasIndex(x => x.GranteeId);
            grants.DeclarePostgres(pg => [pg.BTree(x => x.GranteeId)]);
        }
    }

    /// <summary><see cref="ActiveRenamed" />, and EF Core creates an index under the name the index gives up.</summary>
    public sealed class ActiveRenamedAndItsOldNameReused : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var grants = Grants(modelBuilder);
            grants.HasIndex(x => x.RoleId).HasDatabaseName(ActiveName);
            grants.DeclarePostgres(_ => [Active with { Name = "ux_renamed" }]);
        }
    }

    /// <summary>One index name in two schemas.</summary>
    public sealed class TwoSchemas : ITestModel
    {
        public const string Shared = "ix_period";

        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants").DeclarePostgres(pg => [pg.BTree(x => x.GranteeId, name: Shared)]);
            modelBuilder.Entity<Booking>().ToTable("bookings", "rooms").DeclarePostgres(pg => [pg.BTree(x => x.RoomId, name: Shared)]);
        }
    }

    /// <summary><see cref="TwoSchemas" /> without the index on the grants.</summary>
    public sealed class TwoSchemasOneIndex : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoleGrant>().ToTable("role_grants", "grants");
            modelBuilder.Entity<Booking>().ToTable("bookings", "rooms").DeclarePostgres(pg => [pg.BTree(x => x.RoomId, name: TwoSchemas.Shared)]);
        }
    }

    /// <summary>One note per grantee, and a grant without a note counts as a note: the second such grant is refused.</summary>
    public sealed class OneNotePerGrantee : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.Unique([pg.Key(x => x.GranteeId), pg.Key(x => x.Note)], nullsDistinct: false, name: "ux_note")]);
    }

    /// <summary>The same as PostgreSQL has it unasked: two grants without a note are two keys.</summary>
    public sealed class OneNotePerGranteeWhereThereIsOne : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Grants(modelBuilder).DeclarePostgres(pg => [pg.Unique([pg.Key(x => x.GranteeId), pg.Key(x => x.Note)], name: "ux_note")]);
    }

    // ── A hierarchy with a table per type ──

    private static void ReservationsInTablesOfTheirOwn(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reservation>().UseTpcMappingStrategy().ToTable("reservations");
        modelBuilder.Entity<Stay>().ToTable("stays");
        modelBuilder.Entity<Hold>().ToTable("holds");
    }

    /// <summary>A unique index declared on the root: each table gets its own, under a name of its own.</summary>
    public sealed class OneReservationPerRoom : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            ReservationsInTablesOfTheirOwn(modelBuilder);
            modelBuilder.Entity<Reservation>().DeclarePostgres(pg => [pg.Unique(x => x.RoomId, name: "ux_reservation_room")]);
        }
    }

    /// <summary>The same without a name.</summary>
    public sealed class OneReservationPerRoomWithoutAName : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            ReservationsInTablesOfTheirOwn(modelBuilder);
            modelBuilder.Entity<Reservation>().DeclarePostgres(pg => [pg.Unique(x => x.RoomId)]);
        }
    }

    // ── The other access methods ──

    private static readonly PgSchema<Document> Docs = PgSchema.For<Document>();

    private static EntityTypeBuilder<Document> Documents(ModelBuilder modelBuilder)
    {
        var documents = modelBuilder.Entity<Document>().ToTable("documents");
        documents.Property(x => x.Payload).HasColumnType("jsonb");
        return documents;
    }

    public sealed class DocumentsBare : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder);
    }

    /// <summary>One index of each access method, each with every option its method has.</summary>
    public sealed class EveryMethod : ITestModel
    {
        public static readonly PgIndex[] Declared =
        [
            Docs.Hash(x => x.Title, fillFactor: 80, name: "ix_doc_title_hash"),
            Docs.Gin(x => x.Payload, GinOpClass.JsonbPathOps, fastUpdate: false, pendingListLimit: 128, name: "ix_doc_payload"),
            Docs.Gin([Docs.Key(x => x.Tags)], name: "ix_doc_tags"),
            Docs.Gist([Docs.Key(x => x.Rank), Docs.Key(x => x.Period)], include: [x => x.Title], fillFactor: 90, buffering: GistBuffering.Off, name: "ix_doc_rank_period"),
            Docs.SpGist(Docs.Key(x => x.Title).Ops(SpGistOpClass.TextOps), include: [x => x.Rank], fillFactor: 70, name: "ix_doc_title_sp"),
            Docs.Brin([Docs.Key(x => x.Rank)], where: x => x.Rank > 0, pagesPerRange: 16, autosummarize: true, name: "ix_doc_rank_brin"),
            Docs.BTree([Docs.Key(x => x.Title.ToLower()).Collate("C").Ops(BTreeOpClass.TextPatternOps).Desc()], fillFactor: 70, deduplicateItems: false, name: "ix_doc_title"),
        ];

        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(_ => Declared);
    }

    /// <summary><see cref="EveryMethod" /> with one thing about each index changed, under its name.</summary>
    public sealed class EveryMethodChanged : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Hash(x => x.Title, fillFactor: 60, name: "ix_doc_title_hash"),
            pg.Gin(x => x.Payload, GinOpClass.JsonbOps, fastUpdate: false, pendingListLimit: 128, name: "ix_doc_payload"),
            pg.BTree([pg.Key(x => x.Tags)], name: "ix_doc_tags"),
            pg.Gist([pg.Key(x => x.Rank), pg.Key(x => x.Period)], include: [x => x.Title], fillFactor: 90, buffering: GistBuffering.On, name: "ix_doc_rank_period"),
            pg.SpGist(pg.Key(x => x.Title), include: [x => x.Rank], fillFactor: 70, name: "ix_doc_title_sp"),
            pg.Brin([pg.Key(x => x.Rank)], where: x => x.Rank > 0, pagesPerRange: 16, autosummarize: false, name: "ix_doc_rank_brin"),
            pg.BTree([pg.Key(x => x.Title.ToLower()).Collate("POSIX").Ops(BTreeOpClass.TextPatternOps).Desc()], fillFactor: 70, deduplicateItems: false, name: "ix_doc_title"),
        ]);
    }

    /// <summary><see cref="EveryMethod" />, each index built <c>CONCURRENTLY</c>.</summary>
    public sealed class EveryMethodConcurrently : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Documents(modelBuilder).DeclarePostgres(_ => EveryMethod.Declared.Select(index => index with { Concurrently = true }));
    }

    /// <summary>The range alone under GiST: its type has an operator class without any extension.</summary>
    public sealed class GistOnTheRangeAlone : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(pg => [pg.Gist(x => x.Period, name: "ix_doc_period")]);
    }

    /// <summary>A class of GIN's for documents on a column of arrays: PostgreSQL has no index for the pair.</summary>
    public sealed class ADocumentsClassOnAnArray : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(pg => [pg.Gin(x => x.Tags, GinOpClass.JsonbPathOps, name: "ix_doc_tags")]);
    }

    /// <summary>A class this package does not know, which an extension may bring: the server's to judge.</summary>
    public sealed class AClassOfAnExtension : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(pg => [pg.Gin(x => x.Title, new GinOpClass("gin_trgm_ops"), name: "ix_doc_title_trgm")]);
    }

    /// <summary>A class and a collation on a key of each method that <see cref="EveryMethod" /> gives none.</summary>
    public sealed class KeysWithClasses : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Hash(pg.Key(x => x.Title).Collate("C").Ops(new HashOpClass("text_ops")), name: "ix_doc_title_hash"),
            pg.Brin([pg.Key(x => x.Rank).Ops(new BrinOpClass("int4_minmax_multi_ops")), pg.Key(x => x.Title).Collate("C")], name: "ix_doc_rank_brin"),
            pg.Gist([pg.Key(x => x.Period).Ops(GistOpClass.RangeOps)], name: "ix_doc_period"),
            pg.Gin([pg.Key(x => x.Tags).Ops(GinOpClass.ArrayOps)], name: "ix_doc_tags"),
        ]);
    }

    /// <summary>The README's sample of the other access methods, as it stands there.</summary>
    public sealed class AsInTheReadme : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder) => Documents(modelBuilder).DeclarePostgres(pg =>
        [
            pg.Gin(x => x.Tags, name: "ix_doc_tags"),
            pg.Gist([pg.Key(x => x.Rank), pg.Key(x => x.Period)], include: [x => x.Title], name: "ix_doc_rank_period"),
            pg.Hash(x => x.Title, fillFactor: 80, name: "ix_doc_title_hash"),
            pg.Brin(x => x.Rank, pagesPerRange: 16, name: "ix_doc_rank_brin"),
            pg.BTree([pg.Key(x => x.Title).Collate("C").Ops(BTreeOpClass.TextPatternOps)], name: "ix_doc_title"),
        ]);
    }
}
