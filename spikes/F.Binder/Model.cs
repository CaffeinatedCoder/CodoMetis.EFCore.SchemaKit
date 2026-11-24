using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SchemaKit.Spikes.F;

public enum Status
{
    Draft,
    Active,
}

public sealed record Email(string Value);

public sealed class EmailConverter() : ValueConverter<Email, string>(e => e.Value, s => new Email(s));

public sealed class Order
{
    public int Id { get; set; }

    // Not nullable in C#, nullable in the model: IsRequired(false) comes after the declaration.
    public string Tag { get; set; } = "";

    // Nullable in C#, required in the model.
    public string? Note { get; set; }

    // An enum as the provider stores it by itself, and one stored as its name.
    public Status Status { get; set; }
    public Status Stage { get; set; }

    // A converter set for the whole model, in the context's ConfigureConventions.
    public Email Email { get; set; } = new("");

    // A boolean stored as text.
    public bool Active { get; set; }
    public bool Deleted { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid Key { get; set; }
}

public static class Scenarios
{
    public static readonly Guid Key = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    /// <summary>Every fact of question 1 is set after the declaration that needs it.</summary>
    public static Scenario Facts() => new(
        modelBuilder => modelBuilder.Entity<Order>(order =>
        {
            var cutoff = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            order.Declare(x => x.Tag != "blocked", "tag");
            order.Declare(x => x.Note != "none", "note");
            order.Declare(x => x.Status == Status.Active, "status");
            order.Declare(x => x.Stage == Status.Active, "stage");
            order.Declare(x => x.Email == new Email("a@b.c"), "email");
            order.Declare(x => x.Active, "active");
            order.Declare(x => x.CreatedAt != cutoff, "created");
            order.Declare(x => x.Key == Key, "key");

            // Question 6: the local changes after the call.
            cutoff = new DateTime(1999, 9, 9, 9, 9, 9, DateTimeKind.Utc);

            order.Property(x => x.Tag).IsRequired(false);
            order.Property(x => x.Note).IsRequired();
            order.Property(x => x.Stage).HasConversion<string>();
            order.Property(x => x.Active).HasConversion(new BoolToStringConverter("N", "Y"));
        }));

    /// <summary>Question 4: the same declaration twice, by what it says and by its name.</summary>
    public static Scenario SaidTwice() => new(modelBuilder => modelBuilder.Entity<Order>(order =>
    {
        var tag = "blocked";
        order.Declare(x => x.Tag != "blocked");
        order.Declare(x => x.Tag != tag);
    }));

    public static Scenario NamedTwice() => new(modelBuilder => modelBuilder.Entity<Order>(order =>
    {
        order.Declare(x => x.Tag != "blocked", "one");
        order.Declare(x => x.Active, "one");
    }));

    /// <summary>Question 4: two lambdas that differ at the call and bind to one text.</summary>
    public static Scenario OneTextFromTwo() => new(modelBuilder => modelBuilder.Entity<Order>(order =>
    {
        order.Declare(x => x.Active);
        order.Declare(x => x.Active == true);
    }));

    /// <summary>Question 5: a convention of the consumer's amends what is declared, before finalization.</summary>
    public static Scenario Amended(List<object> before) => new(modelBuilder =>
    {
        modelBuilder.Entity<Order>().Declare(x => x.Tag != "blocked", "tag");
        modelBuilder.Entity<Order>().Declare(x => x.Active);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.Redeclare(d => d with { Condition = SpikeKit.And<Order>(d.Condition, x => !x.Deleted) });
            before.AddRange(entityType.Declared());
        }
    });

    /// <summary>Question 6, the control: the captured local is left in the tree.</summary>
    public static Scenario CaptureLeftInTheTree() => new(modelBuilder => modelBuilder.Entity<Order>(order =>
    {
        var cutoff = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        order.Declare(x => x.CreatedAt != cutoff, "created", snapshotCaptures: false);
        cutoff = new DateTime(1999, 9, 9, 9, 9, 9, DateTimeKind.Utc);
    }));

    /// <summary>Question 1, the guard: a convention that runs after the binding makes a column nullable.</summary>
    public static Scenario ChangedAfterBinding() => new(modelBuilder => modelBuilder.Entity<Order>().Declare(x => x.Tag != "blocked", "tag"))
    {
        Late = model => model.Metadata.FindEntityType(typeof(Order))!.FindProperty(nameof(Order.Tag))!.Builder.IsRequired(false, fromDataAnnotation: true),
    };
}
