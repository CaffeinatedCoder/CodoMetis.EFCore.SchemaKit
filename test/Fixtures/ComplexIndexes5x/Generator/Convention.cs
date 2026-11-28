using EFCore.ComplexIndexes;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace Legacy5x.Convention;

// A condition a convention adds (CodoMetis.EFCore.SchemaKit#249): after every declaration is made,
// a loop over the entity types gives each unique index and each exclusion constraint of a type
// with a certain property one more condition, with AddComplexIndexFilter and
// AddExclusionConstraintFilter. The entity types are known to the loop only when it runs, and the
// property is a shadow property, so no lambda over an entity type can say the condition.
//
// 5.x stored the result as one filter text: the convention's condition alone where the
// declaration had none, and "(its own) AND (the convention's)" where it had one. Each way a
// declaration can have a condition of its own is here once, for an index and for a constraint.
//
// test/…PostgreSQL.Tests/Legacy/LegacyConventionFixture.cs declares the same model with SchemaKit.

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

/// <summary>A type without the property: the convention passes it over.</summary>
public sealed class Memo
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public int AuthorId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
}

public sealed class ConventionContext(DbContextOptions<ConventionContext> options) : DbContext(options)
{
    public const string Live = "{DeletedOn} IS NULL";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Ticket>(ticket =>
        {
            ticket.ToTable("tickets");
            ticket.Property<DateOnly?>("DeletedOn");

            // On a property: without a condition of its own, and with one.
            ticket.ComplexProperty(x => x.Label, label =>
            {
                label.Property(x => x.Name).HasComplexIndex(isUnique: true, indexName: "ux_ticket_label_name");
                label.Property(x => x.Code).HasComplexIndex(isUnique: true, filter: "{ClosedOn} IS NULL", indexName: "ux_ticket_label_code_open");
            });

            // On the entity type: none, one written as SQL, one written as a lambda.
            ticket.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ux_ticket_code");
            ticket.HasComplexCompositeIndex(x => new { x.QueueId, x.Code }, isUnique: true, filter: "\"ClosedOn\" IS NULL", indexName: "ux_ticket_queue_code_open");
            ticket.HasComplexCompositeIndex(x => new { x.OwnerId, x.Title }, x => x.ClosedOn == null && x.QueueId > 0, isUnique: true, indexName: "ux_ticket_owner_title_open");

            // An expression, unique, with a lambda's condition.
            ticket.HasExpressionIndex(x => x.Title.ToLower(), x => x.ClosedOn == null, isUnique: true, indexName: "ux_ticket_title_lower_open");

            // Not unique: the convention leaves these as they are.
            ticket.HasComplexIndex(x => x.OwnerId, indexName: "ix_ticket_owner");
            ticket.HasComplexIndex(x => x.QueueId, x => x.ClosedOn == null, indexName: "ix_ticket_queue_open");

            // Exclusion constraints: none, SQL, a lambda.
            ticket.HasExclusionConstraint(x => x.OwnerId, x => x.Period, name: "ex_ticket_owner");
            ticket.HasExclusionConstraint(x => x.QueueId, x => x.Period, filter: "{ClosedOn} IS NULL", name: "ex_ticket_queue_open");
            ticket.HasExclusionConstraint(x => new { x.OwnerId, x.QueueId }, x => x.Period, x => x.ClosedOn == null && x.QueueId > 0, name: "ex_ticket_owner_queue_open");
        });

        modelBuilder.Entity<Memo>(memo =>
        {
            memo.ToTable("memos");
            memo.HasComplexIndex(x => x.Text, isUnique: true, indexName: "ux_memo_text");
            memo.HasExclusionConstraint(x => x.AuthorId, x => x.Period, name: "ex_memo_author");
        });

        // The convention, last: every entity type that has the property, known only here.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(e => e.FindProperty("DeletedOn") is not null))
        {
            entityType.AddComplexIndexFilter(Live, index => index.IsUnique);
            entityType.AddExclusionConstraintFilter(Live);
        }
    }
}

public sealed class ConventionContextFactory : IDesignTimeDbContextFactory<ConventionContext>
{
    public ConventionContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ConventionContext>()
            .UseNpgsql("Host=localhost;Database=legacy5x")
            .UseNpgsqlComplexIndexes()
            .Options);
}
