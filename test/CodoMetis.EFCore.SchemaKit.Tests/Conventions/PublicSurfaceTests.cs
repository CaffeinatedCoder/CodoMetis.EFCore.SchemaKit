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

    private static readonly string[] ContributorContract =
    [
        "CodoMetis.EFCore.SchemaKit.ISchemaDialect",
        "CodoMetis.EFCore.SchemaKit.Identifier",
        "CodoMetis.EFCore.SchemaKit.QualifiedName",
        "CodoMetis.EFCore.SchemaKit.SchemaDialect",
        "CodoMetis.EFCore.SchemaKit.SchemaDialectDependencies",
        "CodoMetis.EFCore.SchemaKit.SchemaScope",
        "CodoMetis.EFCore.SchemaKit.SchemaScope+Database",
        "CodoMetis.EFCore.SchemaKit.SchemaScope+Schema",
        "CodoMetis.EFCore.SchemaKit.SchemaScope+Table",
        "CodoMetis.EFCore.SchemaKit.SchemaSql",
        "CodoMetis.EFCore.SchemaKit.SqlFragment",
        "CodoMetis.EFCore.SchemaKit.SqlHandler",
        "CodoMetis.EFCore.SchemaKit.SqlName",
        "CodoMetis.EFCore.SchemaKit.SqlOperator",
        "CodoMetis.EFCore.SchemaKit.TableRef",
    ];

    private static Type[] Exported => typeof(SchemaKitDbContextOptionsBuilderExtensions).Assembly.GetExportedTypes();

    [TestMethod]
    public void The_public_types_are_the_ones_meant_to_be()
        => CollectionAssert.AreEqual(
            Registration.Concat(ContributorContract).Order(StringComparer.Ordinal).ToList(),
            Exported.Select(t => t.FullName).Order(StringComparer.Ordinal).ToList(),
            "The public surface changed: " + string.Join(", ", Exported.Select(t => t.FullName)));

    /// <summary>
    /// Until rc.1 the contract may change, so using it takes a deliberate opt-in: every public type
    /// outside the registration carries the experimental diagnostic, top-level or nested under one
    /// that does.
    /// </summary>
    [TestMethod]
    public void Every_contract_type_is_marked_experimental()
    {
        var unmarked = Exported
            .Where(t => !Registration.Contains(t.FullName))
            .Where(t => (t.DeclaringType ?? t).GetCustomAttribute<System.Diagnostics.CodeAnalysis.ExperimentalAttribute>()?.DiagnosticId != "SK9001")
            .Select(t => t.FullName)
            .ToList();

        Assert.AreEqual(0, unmarked.Count, "Public without [Experimental(\"SK9001\")]: " + string.Join(", ", unmarked));
    }

    [TestMethod]
    public void The_registration_methods_are_the_ones_meant_to_be()
    {
        var methods = Exported
            .Where(t => Registration.Contains(t.FullName))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name).Order().ToList();

        CollectionAssert.AreEqual(new[] { "AddEntityFrameworkSchemaKit", "UseSchemaKit", "UseSchemaKit" }, methods);
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
