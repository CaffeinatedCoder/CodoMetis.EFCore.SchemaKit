using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Design;

namespace CodoMetis.EFCore.SchemaKit.Tests.Wiring;

/// <summary>
/// The 5.x packages put a design-time services reference into the consuming assembly, which would
/// place their differ in front of SchemaKit's at design time. The guard looks for that reference.
/// </summary>
[TestClass]
public sealed class LegacyPackageGuardTests
{
    [TestMethod]
    [DataRow("EFCore.ComplexIndexes.CustomDesignTimeServices, EFCore.ComplexIndexes")]
    [DataRow("EFCore.ComplexIndexes.PostgreSQL.NpgsqlComplexIndexDesignTimeServices, EFCore.ComplexIndexes.PostgreSQL")]
    [DataRow("EFCore.ComplexIndexes.SqlServer.SqlServerComplexIndexDesignTimeServices, EFCore.ComplexIndexes.SqlServer, Version=5.6.0.0")]
    public void A_reference_into_a_5x_assembly_is_found(string typeName)
        => Assert.AreEqual(typeName, LegacyPackageGuard.Find([new DesignTimeServicesReferenceAttribute(typeName)]));

    [TestMethod]
    [DataRow("Npgsql.EntityFrameworkCore.PostgreSQL.Design.Internal.NpgsqlDesignTimeServices, Npgsql.EntityFrameworkCore.PostgreSQL")]
    [DataRow("EFCore.ComplexIndexes.Something, MyApp")]                 // a consumer's own type in a namespace of that name
    [DataRow("MyApp.DesignTimeServices")]                               // no assembly part at all
    public void Any_other_reference_is_left_alone(string typeName)
        => Assert.IsNull(LegacyPackageGuard.Find([new DesignTimeServicesReferenceAttribute(typeName)]));

    [TestMethod]
    public void An_assembly_without_the_reference_passes()
        => LegacyPackageGuard.ThrowIfReferenced(typeof(LegacyPackageGuardTests).Assembly, null, typeof(string).Assembly);
}
