using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;

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
}
