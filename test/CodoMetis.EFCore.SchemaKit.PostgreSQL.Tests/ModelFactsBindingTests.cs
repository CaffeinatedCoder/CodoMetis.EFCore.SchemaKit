using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests;

public enum TicketState { Open, Closed }

public sealed record Mail(string Value);

public sealed class Ticket
{
    public int Id { get; set; }
    public NpgsqlRange<DateOnly> During { get; set; }

    // An enum as the provider stores it by itself, and one stored as its name.
    public TicketState State { get; set; }
    public TicketState Stage { get; set; }

    public Mail Mail { get; set; } = new("");

    // A boolean stored as text.
    public bool Active { get; set; }

    // Not nullable in C# and nullable in the model, and the other way round.
    public string Tag { get; set; } = "";
    public string? Note { get; set; }

    public DateTime OpenedAt { get; set; }
    public Guid Key { get; set; }

    // Two numbers, stored as text in one model.
    public int Low { get; set; }
    public int High { get; set; }
}

/// <summary>
/// What a lambda is bound from when its model is finalized (issue 180): a column's nullability
/// and a value as the column stores it are the model's facts, not the C# types'. Every condition
/// here is also what EF Core writes for the same lambda in a query on the same model, which is
/// what makes an index of it one a query uses.
/// </summary>
[TestClass]
public sealed class ModelFactsBindingTests
{
    private static readonly Guid Key = new("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTime Opened = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // Every fact is set after the declaration that needs it: at the call nothing knows it yet.
    private static EntityTypeBuilder<Ticket> Configured(EntityTypeBuilder<Ticket> ticket)
    {
        ticket.Property(x => x.Stage).HasConversion<string>();
        ticket.Property(x => x.Mail).HasConversion(mail => mail.Value, text => new Mail(text));
        ticket.Property(x => x.Active).HasConversion(new BoolToStringConverter("N", "Y"));
        ticket.Property(x => x.Tag).IsRequired(false);
        ticket.Property(x => x.Note).IsRequired();
        return ticket;
    }

    private static readonly (string Name, Expression<Func<Ticket, bool>> Condition, string Bound)[] Cases =
    [
        ("by_state",    x => x.State == TicketState.Open,   "{State} = 0"),
        ("not_state",   x => x.State != TicketState.Closed, "{State} <> 1"),
        ("above_state", x => x.State > TicketState.Open,    "{State} > 0"),
        ("by_stage",    x => x.Stage == TicketState.Closed, "{Stage} = 'Closed'"),
        ("not_stage",   x => x.Stage != TicketState.Closed, "{Stage} <> 'Closed'"),
        ("by_mail",     x => x.Mail == new Mail("a@b.c"),   "{Mail} = 'a@b.c'"),
        ("active",      x => x.Active,                      "{Active} = 'Y'"),
        ("inactive",    x => !x.Active,                     "{Active} = 'N'"),
        ("is_active",   x => x.Active == true,              "{Active} = 'Y'"),
        ("by_tag",      x => x.Tag != "blocked",            "({Tag} <> 'blocked' OR {Tag} IS NULL)"),
        ("by_note",     x => x.Note != "none",              "{Note} <> 'none'"),
        ("by_opened",   x => x.OpenedAt > Opened,           "{OpenedAt} > TIMESTAMPTZ '2026-01-02T03:04:05Z'"),
        ("by_key",      x => x.Key == Key,                  "{Key} = '0f8fad5b-d9cb-469f-a165-70867728950e'"),
    ];

    private sealed class Facts : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var ticket = modelBuilder.Entity<Ticket>();
            ticket.DeclarePostgres(pg => [.. Cases.Select(c => pg.Exclusion(pg.Overlaps(x => x.During), where: c.Condition, name: c.Name))]);
            Configured(ticket);
        }
    }

    [TestMethod]
    public void A_condition_is_bound_from_the_models_facts()
    {
        using var context = Contexts.Of<Facts>();
        var ticket = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Ticket))!;

        foreach (var (name, _, bound) in Cases)
            Assert.AreEqual(bound, ticket.FindDeclaration<PgExclusion>(name)!.Where!.Template.ToString(), name);
    }

    /// <summary>
    /// The oracle: the same lambda in a query on the same model. EF Core writes the column with
    /// its table's alias and leaves the outermost parentheses out; nothing else may differ.
    /// </summary>
    [TestMethod]
    public void Each_is_what_EF_Core_writes_for_the_lambda_in_a_query()
    {
        using var context = Contexts.Of<Facts>();
        var ticket = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Ticket))!;

        foreach (var (name, condition, _) in Cases)
        {
            var bound = ticket.FindDeclaration<PgExclusion>(name)!.Where!.Template.ToString();
            var ours = System.Text.RegularExpressions.Regex.Replace(bound, @"\{(\w+)\}", "t.\"$1\"");
            if (ours.StartsWith('(') && ours.EndsWith(')')) ours = ours[1..^1];

            var query = context.Set<Ticket>().Where(condition).ToQueryString();
            var where = query[(query.IndexOf("WHERE ", StringComparison.Ordinal) + 6)..].Trim();

            Assert.AreEqual(where, ours, name);
        }
    }

    // ---- what the model cannot write is refused when it is finalized --------------------------------

    private sealed class OrderedByWhatIsStoredAsText : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => Configured(modelBuilder.Entity<Ticket>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During), where: x => x.Stage > TicketState.Open)]));
    }

    private sealed class TwoPropertiesOrderedByWhatIsStoredAsText : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var ticket = Configured(modelBuilder.Entity<Ticket>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During), where: x => x.Low < x.High)]));
            ticket.Property(x => x.Low).HasConversion<string>();
            ticket.Property(x => x.High).HasConversion<string>();
        }
    }

    private sealed class TwoPropertiesEqualAsStored : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
        {
            var ticket = Configured(modelBuilder.Entity<Ticket>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During), where: x => x.Low != x.High)]));
            ticket.Property(x => x.Low).HasConversion<string>();
            ticket.Property(x => x.High).HasConversion<string>();
        }
    }

    private sealed class ADateTheColumnCannotHold : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Ticket>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During), where: x => x.OpenedAt > new DateTime(2026, 1, 1))]);
    }

    /// <summary>
    /// An enum stored as its name orders as text, <c>Closed</c> before <c>Open</c>, where C# orders
    /// by the number. The comparison is accepted where it is written, since nothing there knows how
    /// the enum is stored, and refused when the model says.
    /// </summary>
    [TestMethod]
    public void An_order_by_what_a_converter_stores_as_text_is_refused()
    {
        using var context = Contexts.Of<OrderedByWhatIsStoredAsText>();

        var refused = Assert.ThrowsExactly<NotSupportedException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.Contains(refused.Message, "orders by 'Stage', which is stored through a value converter");
    }

    /// <summary>
    /// Two properties ordered by each other order as what is stored too: two ints stored as text
    /// compared '10' with '2', and the condition said otherwise than the lambda (#242). Equality
    /// keeps its meaning through the converter, and is taken.
    /// </summary>
    [TestMethod]
    public void An_order_of_two_properties_stored_as_text_is_refused()
    {
        using (var context = Contexts.Of<TwoPropertiesOrderedByWhatIsStoredAsText>())
        {
            var refused = Assert.ThrowsExactly<NotSupportedException>(() => context.GetService<IDesignTimeModel>().Model);
            StringAssert.Contains(refused.Message, "orders by 'Low', which is stored through a value converter");
        }

        using (var context = Contexts.Of<TwoPropertiesEqualAsStored>())
            Assert.IsNotNull(context.GetService<IDesignTimeModel>().Model);
    }

    /// <summary>The mapping's own refusal reaches the consumer with the property it is about.</summary>
    [TestMethod]
    public void A_value_the_columns_type_cannot_hold_is_refused()
    {
        using var context = Contexts.Of<ADateTheColumnCannotHold>();

        var refused = Assert.ThrowsExactly<NotSupportedException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.Contains(refused.Message, "A value compared with 'OpenedAt' on Ticket cannot be written as that column stores it");
        StringAssert.Contains(refused.Message, "UTC");
    }

    // ---- a fact that changes after the binding -------------------------------------------------------

    private sealed class Tagged : ITestModel
    {
        public static void Build(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Ticket>()
                .DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During), where: x => x.Tag != "blocked", name: "by_tag")])
                .Property(x => x.Mail).HasConversion(mail => mail.Value, text => new Mail(text));
    }

    /// <summary>Another package's convention, registered after SchemaKit's and so run after its binding.</summary>
    private sealed class LatePlugin : IConventionSetPlugin
    {
        public ConventionSet ModifyConventions(ConventionSet conventionSet)
        {
            conventionSet.ModelFinalizingConventions.Add(new MakesTagNullable());
            return conventionSet;
        }

        private sealed class MakesTagNullable : IModelFinalizingConvention
        {
            public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
                => modelBuilder.Metadata.FindEntityType(typeof(Ticket))?.FindProperty(nameof(Ticket.Tag))?.Builder.IsRequired(false, fromDataAnnotation: true);
        }
    }

    private sealed class LateExtension : IDbContextOptionsExtension
    {
        public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);
        public void ApplyServices(IServiceCollection services) => services.AddScoped<IConventionSetPlugin, LatePlugin>();
        public void Validate(IDbContextOptions options) { }

        private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
        {
            public override bool IsDatabaseProvider => false;
            public override string LogFragment => "";
            public override int GetServiceProviderHashCode() => 0;
            public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
            public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) { }
        }
    }

    /// <summary>
    /// SchemaKit's binding is the last finalizing convention it can see; a package registered after
    /// it runs its own behind that. One that makes a column nullable leaves a stored condition
    /// that drops the rows the lambda keeps. The declaration is bound a second time on the
    /// finalized model, and the difference is refused.
    /// </summary>
    [TestMethod]
    public void A_fact_that_changes_after_the_binding_is_refused_on_the_finalized_model()
    {
        using (var untouched = Contexts.Of<Tagged>())
            Assert.AreEqual("{Tag} <> 'blocked'", untouched.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Ticket))!.FindDeclaration<PgExclusion>("by_tag")!.Where!.Template.ToString());

        var options = new DbContextOptionsBuilder<Db<Tagged>>()
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
            .UseNpgsql(Contexts.Offline, o => o.UseSchemaKit());
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new LateExtension());
        using var context = new Db<Tagged>(options.Options);

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.Contains(refused.Message, "The model changed after the declaration Pg.Exclusion gist 'by_tag' on Ticket was bound");
        StringAssert.Contains(refused.Message, "IS NULL");
    }
}
