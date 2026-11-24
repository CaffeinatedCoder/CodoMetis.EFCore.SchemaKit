using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.Extensions.DependencyInjection;

namespace SchemaKit.Spikes.F;

[TestClass]
public sealed class Questions
{
    private static IEnumerable<object[]> Providers => [["npgsql"], ["sqlserver"], ["sqlite"]];

    // ---- 1: what a finalizing convention sees ------------------------------------------------------

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q1_nullability_is_the_models_and_not_the_annotations(string provider)
    {
        var seen = Built(provider, Scenarios.Facts(), out _).Seen.ToDictionary(s => s.Property);

        Assert.IsTrue(seen["Tag"].Nullable, "string Tag, IsRequired(false) after the declaration");
        Assert.IsFalse(seen["Note"].Nullable, "string? Note, IsRequired() after the declaration");
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q1_a_property_holds_no_mapping_and_no_store_type_yet(string provider)
    {
        var seen = Built(provider, Scenarios.Facts(), out _).Seen;

        Assert.IsTrue(seen.All(s => !s.MappingOnProperty), "FindTypeMapping()");
        Assert.IsTrue(seen.All(s => s.StoreType is null), "GetColumnType()");
        Assert.IsTrue(seen.All(s => s.Column == s.Property), "GetColumnName()");

        // A converter is on the property only when it was set as one. HasConversion<string>() sets a
        // type, and the converter for it is the mapping's.
        var converters = seen.Where(s => s.Converter is not null).ToDictionary(s => s.Property, s => s.Converter);
        CollectionAssert.AreEquivalent(new[] { "Active", "Email" }, converters.Keys.ToArray());
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q1_the_mapping_source_writes_each_literal_as_it_is_stored(string provider)
    {
        Built(provider, Scenarios.Facts(), out var design);

        var expected = provider switch
        {
            "npgsql" => new Dictionary<string, string>
            {
                ["tag"]     = "{Tag} <> 'blocked' OR {Tag} IS NULL",
                ["note"]    = "{Note} <> 'none'",
                ["status"]  = "{Status} = 1",
                ["stage"]   = "{Stage} = 'Active'",
                ["email"]   = "{Email} = 'a@b.c'",
                ["active"]  = "{Active} = 'Y'",
                ["created"] = "{CreatedAt} <> TIMESTAMPTZ '2026-01-02T03:04:05Z'",
                ["key"]     = "{Key} = '0f8fad5b-d9cb-469f-a165-70867728950e'",
            },
            "sqlserver" => new Dictionary<string, string>
            {
                ["tag"]     = "{Tag} <> N'blocked' OR {Tag} IS NULL",
                ["note"]    = "{Note} <> N'none'",
                ["status"]  = "{Status} = 1",
                ["stage"]   = "{Stage} = N'Active'",
                ["email"]   = "{Email} = N'a@b.c'",
                ["active"]  = "{Active} = N'Y'",
                ["created"] = "{CreatedAt} <> '2026-01-02T03:04:05.0000000Z'",
                ["key"]     = "{Key} = '0f8fad5b-d9cb-469f-a165-70867728950e'",
            },
            _ => new Dictionary<string, string>
            {
                ["tag"]     = "{Tag} <> 'blocked' OR {Tag} IS NULL",
                ["note"]    = "{Note} <> 'none'",
                ["status"]  = "{Status} = 1",
                ["stage"]   = "{Stage} = 'Active'",
                ["email"]   = "{Email} = 'a@b.c'",
                ["active"]  = "{Active} = 'Y'",
                ["created"] = "{CreatedAt} <> '2026-01-02 03:04:05'",
                ["key"]     = "{Key} = '0F8FAD5B-D9CB-469F-A165-70867728950E'",
            },
        };

        AssertDeclared(expected, design.FindEntityType(typeof(Order))!);
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q1_a_fact_that_changes_after_the_binding_is_refused_on_the_finalized_model(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.ChangedAfterBinding());

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.Contains(refused.Message, "The model changed after 'tag'");
        StringAssert.Contains(refused.Message, "IS NULL");
    }

    // ---- 2: the text is all that is held -----------------------------------------------------------

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q2_the_design_time_model_and_the_runtime_model_hold_texts_alone(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.Facts());
        var design = context.GetService<IDesignTimeModel>().Model;
        var runtime = context.Model;

        Assert.AreNotSame(design, runtime);
        var designed = design.FindEntityType(typeof(Order))!.Declared();
        var run = runtime.FindEntityType(typeof(Order))!.Declared();

        Assert.HasCount(8, designed);
        Assert.IsTrue(designed.All(d => d is string));
        CollectionAssert.AreEqual(designed.ToArray(), run.ToArray());
        Assert.AreEqual(1, context.Scenario.Finalized, "one model build serves both");
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q2_a_snapshot_holds_the_texts(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.Facts());
        var design = context.GetService<IDesignTimeModel>().Model;
        var (snapshot, code) = Snapshots.Of(context);

        StringAssert.Contains(code, """.HasAnnotation("Spike:Declared:status", "{Status} = 1")""");
        Assert.IsFalse(code.Contains(SpikeKit.Prefix + "~", StringComparison.Ordinal), code);

        var order = snapshot.GetEntityTypes().Single();
        CollectionAssert.AreEqual(design.FindEntityType(typeof(Order))!.Declared().ToArray(), order.Declared().ToArray());

        // What a snapshot does not hold: the converter. The property is the stored type's.
        Assert.AreEqual(typeof(string), order.FindProperty("Active")!.ClrType);
        Assert.IsNull(order.FindProperty("Active")!.GetValueConverter());
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q2_a_compiled_model_holds_the_texts(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.Facts());
        var folder = Directory.CreateTempSubdirectory("spike-f-").FullName;
        try
        {
#pragma warning disable EF1001 // The design-time services are built as dotnet ef builds them. Harness only.
            var services = new Microsoft.EntityFrameworkCore.Design.Internal.DesignTimeServicesBuilder(
                    typeof(Questions).Assembly, typeof(Questions).Assembly, new Microsoft.EntityFrameworkCore.Design.Internal.OperationReporter(handler: null), [])
                .Build(context);
#pragma warning restore EF1001
            var files = services.GetRequiredService<ICompiledModelScaffolder>().ScaffoldModel(
                context.GetService<IDesignTimeModel>().Model,
                folder,
                new CompiledModelCodeGenerationOptions { ContextType = typeof(SpikeContext), ModelNamespace = "Compiled" });

            var code = string.Concat(files.Select(File.ReadAllText));
            StringAssert.Contains(code, "Spike:Declared:tag");
            StringAssert.Contains(code, "{Tag} <> ");
            Assert.IsFalse(code.Contains(SpikeKit.Prefix + "~", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- 3: a snapshot is never bound again --------------------------------------------------------

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q3_the_convention_does_not_run_for_a_snapshot(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.Facts());
        var design = context.GetService<IDesignTimeModel>().Model;
        var (snapshot, _) = Snapshots.Of(context);
        Assert.AreEqual(1, context.Scenario.Finalized);

        // As EF Core's own tools prepare a snapshot for a diff.
        var prepared = context.GetService<IModelRuntimeInitializer>().Initialize(((IMutableModel)snapshot).FinalizeModel(), designTime: true, validationLogger: null);
        var differ = context.GetService<IMigrationsModelDiffer>();

        Assert.IsEmpty(differ.GetDifferences(prepared.GetRelationalModel(), design.GetRelationalModel()));
        Assert.IsEmpty(differ.GetDifferences(design.GetRelationalModel(), prepared.GetRelationalModel()));
        Assert.AreEqual(1, context.Scenario.Finalized, "the snapshot's model was finalized without the convention");
        CollectionAssert.AreEqual(design.FindEntityType(typeof(Order))!.Declared().ToArray(), prepared.GetEntityTypes().Single().Declared().ToArray());
    }

    // ---- 4: the key --------------------------------------------------------------------------------

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q4_what_is_said_twice_is_refused_at_the_call(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.SaidTwice());

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.StartsWith(refused.Message, "Declared twice on Order");
        StringAssert.Contains(refused.StackTrace!, nameof(SpikeKit.Declare), "thrown by the call, inside OnModelCreating");
        Assert.AreEqual(0, context.Scenario.Finalized);
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q4_a_name_taken_twice_is_refused_at_the_call(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.NamedTwice());

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.StartsWith(refused.Message, "Declared twice on Order: 'one'");
        Assert.AreEqual(0, context.Scenario.Finalized);
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q4_two_lambdas_that_bind_to_one_text_are_refused_at_finalization_naming_both(string provider)
    {
        using var context = new SpikeContext(provider, Scenarios.OneTextFromTwo());

        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => context.GetService<IDesignTimeModel>().Model);
        StringAssert.Contains(refused.Message, "x => x.Active and x => (x.Active == True) are both {Active} = ");
        Assert.AreEqual(1, context.Scenario.Finalized, "the call accepted both");
    }

    // ---- 5: Redeclare and the read model before finalization ---------------------------------------

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q5_a_declaration_is_amended_before_it_is_bound(string provider)
    {
        List<object> before = [];
        Built(provider, Scenarios.Amended(before), out var design);

        // Before finalization the read model hands back the declarations as they were written.
        Assert.HasCount(2, before);
        Assert.IsTrue(before.All(d => d is Pending));

        var no = provider switch { "npgsql" => "FALSE", "sqlserver" => "CAST(0 AS bit)", _ => "0" };
        var yes = provider switch { "npgsql" => "TRUE", "sqlserver" => "CAST(1 AS bit)", _ => "1" };
        var text = provider == "sqlserver" ? "N'blocked'" : "'blocked'";
        var declared = design.FindEntityType(typeof(Order))!.Declared().Cast<string>().ToList();

        CollectionAssert.Contains(declared, $"({{Tag}} <> {text}) AND ({{Deleted}} = {no})");
        CollectionAssert.Contains(declared, $"({{Active}} = {yes}) AND ({{Deleted}} = {no})");
        Assert.HasCount(2, declared);
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q5_nothing_is_amended_after_finalization(string provider)
    {
        Built(provider, Scenarios.Facts(), out var design);
        var order = (IMutableEntityType)design.FindEntityType(typeof(Order))!;

        // Nothing is unbound any more, so there is nothing to hand the amendment; and the model takes no annotation.
        Assert.AreEqual(0, order.Redeclare(d => d with { Name = "other" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => order.SetAnnotation(SpikeKit.Prefix + "late", "x"));
    }

    // ---- 6: captured values ------------------------------------------------------------------------

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q6_a_captured_local_is_read_at_the_call(string provider)
    {
        Built(provider, Scenarios.Facts(), out var design);

        var created = (string)design.FindEntityType(typeof(Order))!.FindAnnotation(SpikeKit.Prefix + "created")!.Value!;
        StringAssert.Contains(created, "2026-01-02");
    }

    [TestMethod]
    [DynamicData(nameof(Providers))]
    public void Q6_left_in_the_tree_it_is_read_at_finalization(string provider)
    {
        Built(provider, Scenarios.CaptureLeftInTheTree(), out var design);

        var created = (string)design.FindEntityType(typeof(Order))!.FindAnnotation(SpikeKit.Prefix + "created")!.Value!;
        StringAssert.Contains(created, "1999-09-09", "the value the local had when the model was finalized, not when the declaration was written");
    }

    private static Scenario Built(string provider, Scenario scenario, out IModel design)
    {
        using var context = new SpikeContext(provider, scenario);
        design = context.GetService<IDesignTimeModel>().Model;
        return scenario;
    }

    private static void AssertDeclared(Dictionary<string, string> expected, IReadOnlyEntityType entityType)
    {
        var actual = entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith(SpikeKit.Prefix, StringComparison.Ordinal))
            .ToDictionary(a => a.Name[SpikeKit.Prefix.Length..], a => (string)a.Value!);

        CollectionAssert.AreEquivalent(expected.Select(e => $"{e.Key}: {e.Value}").ToArray(), actual.Select(a => $"{a.Key}: {a.Value}").ToArray());
    }
}
