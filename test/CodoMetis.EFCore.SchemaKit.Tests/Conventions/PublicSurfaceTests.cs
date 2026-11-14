using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// The kernel's public surface is what is listed here and nothing else: the registration methods,
/// and the part of the contributor contract that exists, every type of which is marked experimental.
/// The rest of the host is internal on purpose. A type that slipped out in a preview is one a
/// consumer may already depend on.
/// </summary>
[TestClass]
public sealed class PublicSurfaceTests
{
    private static readonly string[] Registration =
    [
        "CodoMetis.EFCore.SchemaKit.SchemaKitDbContextOptionsBuilderExtensions",
        "CodoMetis.EFCore.SchemaKit.SchemaKitServiceCollectionExtensions",
    ];

    /// <summary>
    /// What a consumer reads and amends declarations with. The class is unmarked; the one method
    /// of it that belongs to the contract carries the mark itself.
    /// </summary>
    private static readonly string[] ReadModel =
    [
        "CodoMetis.EFCore.SchemaKit.SchemaDeclarationExtensions",
    ];

    private static readonly string[] ContributorContract =
    [
        "CodoMetis.EFCore.SchemaKit.BaseChange",
        "CodoMetis.EFCore.SchemaKit.BaseChange+ColumnAltered",
        "CodoMetis.EFCore.SchemaKit.BaseChange+ColumnDropped",
        "CodoMetis.EFCore.SchemaKit.BaseChange+ColumnRenamed",
        "CodoMetis.EFCore.SchemaKit.BaseChange+TableDropped",
        "CodoMetis.EFCore.SchemaKit.BaseChange+TableMoved",
        "CodoMetis.EFCore.SchemaKit.BaseChange+TableRebuilt",
        "CodoMetis.EFCore.SchemaKit.BaseChange+TableRenamed",
        "CodoMetis.EFCore.SchemaKit.Dependency",
        "CodoMetis.EFCore.SchemaKit.Dependency+Column",
        "CodoMetis.EFCore.SchemaKit.Dependency+Extension",
        "CodoMetis.EFCore.SchemaKit.Dependency+SchemaObject",
        "CodoMetis.EFCore.SchemaKit.Dependency+WholeTable",
        "CodoMetis.EFCore.SchemaKit.ISchemaContributor",
        "CodoMetis.EFCore.SchemaKit.ISchemaDeclaration`1",
        "CodoMetis.EFCore.SchemaKit.ISchemaDescriptor",
        "CodoMetis.EFCore.SchemaKit.ISchemaDialect",
        "CodoMetis.EFCore.SchemaKit.ISchemaObject",
        "CodoMetis.EFCore.SchemaKit.Identifier",
        "CodoMetis.EFCore.SchemaKit.Impact",
        "CodoMetis.EFCore.SchemaKit.Impact+Follows",
        "CodoMetis.EFCore.SchemaKit.Impact+Lost",
        "CodoMetis.EFCore.SchemaKit.Impact+RebuildAround",
        "CodoMetis.EFCore.SchemaKit.Impact+Unaffected",
        "CodoMetis.EFCore.SchemaKit.NameClaim",
        "CodoMetis.EFCore.SchemaKit.NameClaim+Constraint",
        "CodoMetis.EFCore.SchemaKit.NameClaim+Relation",
        "CodoMetis.EFCore.SchemaKit.NameClaim+TableIndex",
        "CodoMetis.EFCore.SchemaKit.NameClaim+Trigger",
        "CodoMetis.EFCore.SchemaKit.Operational`1",
        "CodoMetis.EFCore.SchemaKit.QualifiedName",
        "CodoMetis.EFCore.SchemaKit.SchemaChange",
        "CodoMetis.EFCore.SchemaKit.SchemaChange+InPlace",
        "CodoMetis.EFCore.SchemaKit.SchemaChange+None",
        "CodoMetis.EFCore.SchemaKit.SchemaChange+Rebuild",
        "CodoMetis.EFCore.SchemaKit.SchemaContributor`2",
        "CodoMetis.EFCore.SchemaKit.SchemaContributorDependencies",
        "CodoMetis.EFCore.SchemaKit.SchemaDeclarationHeader",
        "CodoMetis.EFCore.SchemaKit.SchemaDialect",
        "CodoMetis.EFCore.SchemaKit.SchemaDialectDependencies",
        "CodoMetis.EFCore.SchemaKit.SchemaJsonReader",
        "CodoMetis.EFCore.SchemaKit.SchemaKitPackages",
        "CodoMetis.EFCore.SchemaKit.SchemaJsonWriter",
        "CodoMetis.EFCore.SchemaKit.SchemaNameComparison",
        "CodoMetis.EFCore.SchemaKit.SchemaPlacement",
        "CodoMetis.EFCore.SchemaKit.SchemaResolver",
        "CodoMetis.EFCore.SchemaKit.LegacyDeclarationReader`1",
        "CodoMetis.EFCore.SchemaKit.SchemaModelObject",
        "CodoMetis.EFCore.SchemaKit.SchemaScope",
        "CodoMetis.EFCore.SchemaKit.SchemaScope+Database",
        "CodoMetis.EFCore.SchemaKit.SchemaScope+Schema",
        "CodoMetis.EFCore.SchemaKit.SchemaScope+Table",
        "CodoMetis.EFCore.SchemaKit.SchemaSql",
        "CodoMetis.EFCore.SchemaKit.SqlFragment",
        "CodoMetis.EFCore.SchemaKit.SqlHandler",
        "CodoMetis.EFCore.SchemaKit.SqlName",
        "CodoMetis.EFCore.SchemaKit.SqlOperator",
        "CodoMetis.EFCore.SchemaKit.SqlSpelling",
        "CodoMetis.EFCore.SchemaKit.TableRef",
    ];

    /// <summary>
    /// What a declaration is made of: a consumer writes these and reads them back, so they carry no
    /// experimental mark. A type added here is one a consumer depends on from the first preview.
    /// </summary>
    private static readonly string[] DeclarationValues =
    [
        "CodoMetis.EFCore.SchemaKit.KeyTarget",
        "CodoMetis.EFCore.SchemaKit.KeyTarget+Column",
        "CodoMetis.EFCore.SchemaKit.KeyTarget+Template",
        "CodoMetis.EFCore.SchemaKit.Predicate",
        "CodoMetis.EFCore.SchemaKit.ValueList",
        "CodoMetis.EFCore.SchemaKit.ValueList`1",
    ];

    /// <summary>
    /// What a consumer catches and reads when a model is refused for what it declares. Unmarked for
    /// the same reason: nobody implements these.
    /// </summary>
    private static readonly string[] Diagnostics =
    [
        "CodoMetis.EFCore.SchemaKit.SchemaDiagnostic",
        "CodoMetis.EFCore.SchemaKit.SchemaDiagnostic+NameCollision",
        "CodoMetis.EFCore.SchemaKit.SchemaValidationException",
    ];

    private static Type[] Exported => typeof(SchemaKitDbContextOptionsBuilderExtensions).Assembly.GetExportedTypes();

    [TestMethod]
    public void The_public_types_are_the_ones_meant_to_be()
        => CollectionAssert.AreEqual(
            Registration.Concat(ReadModel).Concat(ContributorContract).Concat(DeclarationValues).Concat(Diagnostics).Order(StringComparer.Ordinal).ToList(),
            Exported.Select(t => t.FullName).Order(StringComparer.Ordinal).ToList(),
            "The public surface changed: " + string.Join(", ", Exported.Select(t => t.FullName)));

    /// <summary>
    /// A lambda becomes one provider's SQL, so whatever in the kernel takes one is handed that
    /// provider's spelling and is the contract's, not a consumer's (#132). A public method that
    /// took a lambda and no spelling would spell it as some provider's for every caller, which is
    /// how PostgreSQL's text would reach a declaration for another database.
    /// </summary>
    [TestMethod]
    public void Whatever_takes_a_lambda_takes_the_providers_spelling_and_is_marked()
    {
        var takingALambda = Exported
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetParameters().Any(p => typeof(System.Linq.Expressions.LambdaExpression).IsAssignableFrom(p.ParameterType)))
            .ToList();

        CollectionAssert.AreEquivalent(new[] { "KeyTarget.Of", "Predicate.Of" }, takingALambda.Select(m => $"{m.DeclaringType!.Name}.{m.Name}").ToList());

        foreach (var method in takingALambda)
        {
            Assert.IsTrue(method.GetParameters().Any(p => p.ParameterType == typeof(SqlSpelling)), $"{method.DeclaringType!.Name}.{method.Name} takes no spelling");
            Assert.AreEqual("SK9001", method.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>()?.DiagnosticId, $"{method.DeclaringType!.Name}.{method.Name} is not marked");
        }
    }

    /// <summary>
    /// Until rc.1 the contract may change, so using it takes a deliberate opt-in: every public type
    /// outside the registration and the declaration values carries the experimental diagnostic,
    /// top-level or nested under one that does.
    /// </summary>
    [TestMethod]
    public void Every_contract_type_is_marked_experimental()
    {
        var unmarked = Exported
            .Where(t => !Registration.Contains(t.FullName) && !ReadModel.Contains(t.FullName) && !DeclarationValues.Contains(t.FullName) && !Diagnostics.Contains(t.FullName))
            .Where(t => (t.DeclaringType ?? t).GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>()?.DiagnosticId != "SK9001")
            .Select(t => t.FullName)
            .ToList();

        Assert.AreEqual(0, unmarked.Count, "Public without [Experimental(\"SK9001\")]: " + string.Join(", ", unmarked));
    }

    /// <summary>
    /// Reading and amending carry no mark; <c>Declare</c>, which only a contributor's package
    /// calls, does. A method added to the class has to be put on one side here.
    /// </summary>
    [TestMethod]
    public void Of_the_declaration_methods_only_Declare_is_marked_experimental()
    {
        var marked = typeof(SchemaDeclarationExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToDictionary(m => m.Name, m => m.GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>()?.DiagnosticId);

        Assert.IsNull(typeof(SchemaDeclarationExtensions).GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>());
        CollectionAssert.AreEquivalent(
            new Dictionary<string, string?> { ["Declare"] = "SK9001", ["Redeclare"] = null, ["GetDeclarations"] = null, ["FindDeclaration"] = null },
            marked);
    }

    /// <summary>The other way round: a consumer who writes a declaration is asked for no opt-in.</summary>
    [TestMethod]
    public void No_declaration_value_and_no_diagnostic_is_marked_experimental()
    {
        var marked = Exported
            .Where(t => DeclarationValues.Contains(t.FullName) || Diagnostics.Contains(t.FullName))
            .Where(t => (t.DeclaringType ?? t).GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>() is not null)
            .Select(t => t.FullName)
            .ToList();

        Assert.AreEqual(0, marked.Count, "Marked experimental: " + string.Join(", ", marked));
    }

    [TestMethod]
    public void The_registration_methods_are_the_ones_meant_to_be()
    {
        var methods = Exported
            .Where(t => Registration.Contains(t.FullName))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name).Order().ToList();

        CollectionAssert.AreEqual(new[] { "AddEntityFrameworkSchemaKit", "AddLegacyDeclarationReader", "AddSchemaContributor", "UseSchemaKit", "UseSchemaKit" }, methods);
    }

    /// <summary>The registration methods that belong to the contract carry its mark, on a type that does not.</summary>
    [TestMethod]
    [DataRow(nameof(SchemaKitServiceCollectionExtensions.AddSchemaContributor))]
    [DataRow(nameof(SchemaKitServiceCollectionExtensions.AddLegacyDeclarationReader))]
    public void The_registrations_of_the_contract_are_marked_experimental(string method)
        => Assert.AreEqual(
            "SK9001",
            typeof(SchemaKitServiceCollectionExtensions).GetMethod(method)!
                .GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>()?.DiagnosticId);

    /// <summary>
    /// The only resolver is the kernel's: one of someone else's could answer the two sides of a diff
    /// differently. And a statement builder, a writer or a reader is handed out, never made.
    /// </summary>
    [TestMethod]
    public void What_the_kernel_hands_out_cannot_be_made_outside_it()
    {
        var offenders = new[] { typeof(SchemaResolver), typeof(SchemaSql), typeof(SchemaJsonWriter), typeof(SchemaJsonReader) }
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Any(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly))
            .Select(t => t.Name)
            .ToList();

        Assert.AreEqual(0, offenders.Count, "Constructible outside the kernel: " + string.Join(", ", offenders));
    }

    /// <summary>No value a statement is built from can be made from text by a caller.</summary>
    [TestMethod]
    public void No_value_type_of_the_vocabulary_has_a_public_constructor_taking_a_string()
    {
        var offenders = new[] { typeof(Identifier), typeof(SqlName), typeof(SqlOperator), typeof(SqlFragment), typeof(SchemaSql) }
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(string) || p.ParameterType == typeof(object)))
                        || t.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name is "op_Implicit" or "op_Explicit"))
            .Select(t => t.Name)
            .ToList();

        Assert.AreEqual(0, offenders.Count, "Constructible or convertible from text: " + string.Join(", ", offenders));
    }

    /// <summary>The handler has an overload per safe kind and none that would take a string, an object or a format.</summary>
    [TestMethod]
    public void The_handler_has_no_hole_for_text()
    {
        var holes = typeof(SqlHandler).GetMethods().Where(m => m.Name == "AppendFormatted").ToList();
        var types = holes.Select(m => m.GetParameters().Single().ParameterType).ToList();

        CollectionAssert.AreEquivalent(
            new[] { typeof(Identifier), typeof(TableRef), typeof(QualifiedName), typeof(SqlName), typeof(SqlOperator), typeof(SqlFragment), typeof(int), typeof(long) },
            types);
        Assert.IsFalse(holes.Any(m => m.IsGenericMethod), "A generic AppendFormatted<T> would accept a string.");
    }
}
