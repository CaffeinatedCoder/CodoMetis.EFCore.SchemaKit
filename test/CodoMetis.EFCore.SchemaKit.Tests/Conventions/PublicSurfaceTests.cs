using System.Reflection;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// The kernel's public surface is what is listed here and nothing else. Everything the host is made
/// of is internal on purpose: the contributor contract is not frozen before rc.1, and a type that
/// slipped out in a preview is one a consumer may already depend on.
/// </summary>
[TestClass]
public sealed class PublicSurfaceTests
{
    [TestMethod]
    public void The_public_types_are_the_ones_meant_to_be()
    {
        var exported = typeof(SchemaKitDbContextOptionsBuilderExtensions).Assembly.GetExportedTypes()
            .Select(t => t.FullName).Order().ToList();

        CollectionAssert.AreEqual(
            new[]
            {
                "CodoMetis.EFCore.SchemaKit.SchemaKitDbContextOptionsBuilderExtensions",
                "CodoMetis.EFCore.SchemaKit.SchemaKitServiceCollectionExtensions",
            },
            exported,
            "The public surface changed: " + string.Join(", ", exported));
    }

    [TestMethod]
    public void The_public_methods_are_the_ones_meant_to_be()
    {
        var methods = typeof(SchemaKitDbContextOptionsBuilderExtensions).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => m.Name).Order().ToList();

        CollectionAssert.AreEqual(new[] { "AddEntityFrameworkSchemaKit", "UseSchemaKit", "UseSchemaKit" }, methods);
    }
}
