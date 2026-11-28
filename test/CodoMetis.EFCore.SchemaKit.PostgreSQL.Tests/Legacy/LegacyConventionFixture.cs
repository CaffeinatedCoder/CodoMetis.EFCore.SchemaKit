using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

// The consumer's own namespace, as in LegacyFixture.cs: the scaffolded files name its context and
// its entity types.
namespace Legacy5x.Convention
{
    public sealed class Label
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
    }

    public sealed class Ticket
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Title { get; set; } = "";
        public int OwnerId { get; set; }
        public int QueueId { get; set; }
        public NpgsqlRange<DateOnly> Period { get; set; }
        public DateOnly? ClosedOn { get; set; }
        public Label Label { get; set; } = new();
    }

    public sealed class Memo
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int AuthorId { get; set; }
        public NpgsqlRange<DateOnly> Period { get; set; }
    }

    /// <summary>The type 5.x's snapshot and migration name in their attributes.</summary>
    public abstract class ConventionContext : DbContext;
}

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy
{
    using Legacy5x.Convention;

    /// <summary>
    /// The model of <c>test/Fixtures/ComplexIndexes5x/Generator/Convention.cs</c> after the move (#249):
    /// each index and constraint declared with SchemaKit with the condition of its own, and the
    /// condition 5.x's <c>AddComplexIndexFilter</c> and <c>AddExclusionConstraintFilter</c> added
    /// to them added here by the same kind of loop, over entity types it knows only when it runs.
    /// The two are kept alike by hand; <c>LegacyConventionHandoverTests</c> fails when they are not.
    /// </summary>
    public static class MovedConvention
    {
        /// <summary>What the last run of <see cref="Convention" /> changed: indexes, then constraints.</summary>
        public static (int Indexes, int Constraints) Changed { get; private set; }

        private static void Declarations(ModelBuilder modelBuilder)
        {
            var ticket = modelBuilder.Entity<Ticket>().ToTable("tickets");
            ticket.Property<DateOnly?>("DeletedOn");
            ticket.ComplexProperty(x => x.Label);
            ticket.DeclarePostgres(pg =>
            [
                // On a property in 5.x: without a condition of its own, and with one.
                pg.Unique(x => x.Label.Name, name: "ux_ticket_label_name"),
                pg.Unique(x => x.Label.Code, where: x => x.ClosedOn == null, name: "ux_ticket_label_code_open"),

                // On the entity type: none, one 5.x took as SQL, one written as a lambda.
                pg.Unique(x => x.Code, name: "ux_ticket_code"),
                pg.Unique([pg.Key(x => x.QueueId), pg.Key(x => x.Code)], where: x => Sql.Raw<bool>("\"ClosedOn\" IS NULL"), name: "ux_ticket_queue_code_open"),
                pg.Unique([pg.Key(x => x.OwnerId), pg.Key(x => x.Title)], where: x => x.ClosedOn == null && x.QueueId > 0, name: "ux_ticket_owner_title_open"),
                pg.Unique(x => x.Title.ToLower(), where: x => x.ClosedOn == null, name: "ux_ticket_title_lower_open"),

                // Not unique: the convention leaves these as they are.
                pg.BTree(x => x.OwnerId, name: "ix_ticket_owner"),
                pg.BTree(x => x.QueueId, where: x => x.ClosedOn == null, name: "ix_ticket_queue_open"),

                pg.Exclusion(pg.Equal(x => x.OwnerId).Overlaps(x => x.Period), name: "ex_ticket_owner"),
                pg.Exclusion(pg.Equal(x => x.QueueId).Overlaps(x => x.Period), where: x => x.ClosedOn == null, name: "ex_ticket_queue_open"),
                pg.Exclusion(
                    pg.Equal(x => x.OwnerId).Equal(x => x.QueueId).Overlaps(x => x.Period),
                    where: x => x.ClosedOn == null && x.QueueId > 0,
                    name: "ex_ticket_owner_queue_open"),
            ]);

            modelBuilder.Entity<Memo>().ToTable("memos").DeclarePostgres(pg =>
            [
                pg.Unique(x => x.Text, name: "ux_memo_text"),
                pg.Exclusion(pg.Equal(x => x.AuthorId).Overlaps(x => x.Period), name: "ex_memo_author"),
            ]);
        }

        /// <summary>
        /// The convention: one more condition for every unique index and every exclusion constraint
        /// of each entity type that has the property. The condition is written once, for no entity
        /// type in particular, and names the property by its path, a shadow property here.
        /// </summary>
        public static void Convention(ModelBuilder modelBuilder)
        {
            var live = PgSchema.Where<object>(x => Sql.Raw<bool>("{DeletedOn} IS NULL"));
            var (indexes, constraints) = (0, 0);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(e => e.FindProperty("DeletedOn") is not null))
            {
                indexes += entityType.Redeclare<PgIndex>(index => index is PgIndex.BTree { Unique: not null } ? index.AndWhere(live) : index);
                constraints += entityType.Redeclare<PgExclusion>(exclusion => exclusion.AndWhere(live));
            }

            Changed = (indexes, constraints);
        }

        public sealed class Declared : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
            {
                Declarations(modelBuilder);
                Convention(modelBuilder);
            }
        }

        /// <summary>The convention run a second time, as one that runs for every module of an application does.</summary>
        public sealed class DeclaredWithTheConventionTwice : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
            {
                Declarations(modelBuilder);
                Convention(modelBuilder);
                Convention(modelBuilder);
            }
        }

        /// <summary>The control: the declarations without the convention's condition.</summary>
        public sealed class DeclaredWithoutTheConvention : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder) => Declarations(modelBuilder);
        }
    }
}
