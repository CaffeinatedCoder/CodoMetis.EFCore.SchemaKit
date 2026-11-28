using System.Reflection;
using System.Runtime.CompilerServices;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using CodoMetis.ValueRanges;
using Microsoft.EntityFrameworkCore;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests;

/// <summary>
/// The premise of the cross-tests (#4): SchemaKit and CodoMetis.ValueRanges do not know each
/// other. What connects them is a column's store type, which this project's other tests hold.
/// </summary>
[TestClass]
public sealed class IndependenceTests
{
    private static readonly Assembly Kernel = typeof(SchemaValidationException).Assembly;

    private static readonly Assembly PostgreSql = typeof(PgExclusion).Assembly;

    /// <summary>One type of each of the four packages.</summary>
    private static readonly Assembly[] ValueRangesAssemblies =
    [
        typeof(DateRange).Assembly,
        typeof(LocalDateRange).Assembly,
        typeof(NpgsqlValueRangesDbContextOptionsBuilderExtensions).Assembly,
        typeof(NpgsqlValueRangesNodaTimeDbContextOptionsBuilderExtensions).Assembly,
    ];

    [TestMethod]
    public void The_four_packages_of_ValueRanges_are_four_assemblies_at_the_version_the_project_names()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                "CodoMetis.ValueRanges",
                "CodoMetis.ValueRanges.NodaTime",
                "CodoMetis.ValueRanges.EFCore.PostgreSQL",
                "CodoMetis.ValueRanges.EFCore.PostgreSQL.NodaTime",
            },
            ValueRangesAssemblies.Select(assembly => assembly.GetName().Name).ToList());

        foreach (var assembly in ValueRangesAssemblies)
        {
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Assert.AreEqual("11.0.0-preview.1", version, assembly.GetName().Name);
        }
    }

    [TestMethod]
    public void No_assembly_of_SchemaKit_references_ValueRanges()
    {
        foreach (var assembly in new[] { Kernel, PostgreSql })
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToList();

            // The reading is of something: each of them references EF Core.
            Assert.IsTrue(references.Any(name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)), assembly.GetName().Name);
            Assert.IsFalse(
                references.Any(name => name.Contains("ValueRanges", StringComparison.OrdinalIgnoreCase)),
                $"{assembly.GetName().Name} references {string.Join(", ", references)}.");
        }
    }

    [TestMethod]
    public void No_assembly_of_ValueRanges_references_SchemaKit()
    {
        foreach (var assembly in ValueRangesAssemblies)
        {
            var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToList();

            Assert.IsFalse(
                references.Any(name => name.Contains("SchemaKit", StringComparison.OrdinalIgnoreCase) || name.Contains("ComplexIndexes", StringComparison.OrdinalIgnoreCase)),
                $"{assembly.GetName().Name} references {string.Join(", ", references)}.");
        }
    }

    /// <summary>
    /// A reference that nothing compiled uses leaves no trace in the assembly and is still a
    /// dependency of the package. So the project files are read as well, and what every project
    /// under <c>src/</c> imports.
    /// </summary>
    [TestMethod]
    public void No_project_file_under_src_names_ValueRanges()
    {
        var files = RepositoryLayout.Files()
            .Where(file => RepositoryLayout.Relative(file) is var path
                && (path.StartsWith("src/", StringComparison.Ordinal) || !path.Contains('/'))
                && Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
            .ToList();

        // Both packages' project files and Directory.Build.props, or the pattern found nothing to read.
        Assert.IsGreaterThanOrEqualTo(3, files.Count, string.Join(", ", files.Select(RepositoryLayout.Relative)));

        var naming = files.Where(file => File.ReadAllText(file).Contains("ValueRanges", StringComparison.OrdinalIgnoreCase)).Select(RepositoryLayout.Relative).ToList();
        Assert.IsEmpty(naming, "ValueRanges is named in " + string.Join(", ", naming) + ". Only this test project may reference it.");
    }

    /// <summary>What compiles here compiles for a consumer: the PostgreSQL package and the kernel grant this assembly no internals.</summary>
    [TestMethod]
    public void SchemaKit_grants_this_assembly_no_internals()
    {
        var mine = typeof(IndependenceTests).Assembly.GetName().Name;

        foreach (var assembly in new[] { Kernel, PostgreSql })
        {
            var grants = assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(grant => grant.AssemblyName).ToList();

            Assert.IsNotEmpty(grants, assembly.GetName().Name);
            CollectionAssert.DoesNotContain(grants, mine);
        }
    }
}
