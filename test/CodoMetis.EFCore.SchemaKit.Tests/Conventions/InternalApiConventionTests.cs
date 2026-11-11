using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// The shipping projects use no EF-internal API. The compiler enforces it, EF1001 being an error
/// like every warning; these tests hold that nothing switches the compiler's check off, which is
/// the part a reviewer would not see in a diff of one line.
/// </summary>
/// <remarks>
/// The allowlist is <see cref="Allowed" />: a shipping file that has to name an EF-internal type
/// goes there with its reason, and nowhere else. It is empty for the kernel, and the PostgreSQL
/// package will add Npgsql's range-mapping interface, which lives in an internal namespace.
/// </remarks>
[TestClass]
public sealed class InternalApiConventionTests
{
    /// <summary>Path relative to src/, with forward slashes, to the reason it may disable EF1001.</summary>
    private static readonly Dictionary<string, string> Allowed = [];

    private static IEnumerable<string> ShippingFiles(string pattern)
        => Directory.EnumerateFiles(RepositoryLayout.Source, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string Relative(string file) => Path.GetRelativePath(RepositoryLayout.Source, file).Replace('\\', '/');

    [TestMethod]
    public void There_are_shipping_sources_to_check()
        => Assert.IsTrue(ShippingFiles("*.cs").Count() > 5, "Found almost no source under src/: the checks below would pass on nothing.");

    [TestMethod]
    public void No_shipping_source_disables_EF1001()
    {
        var offenders = ShippingFiles("*.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"#pragma\s+warning\s+disable[^\n]*EF1001|SuppressMessage\([^\n]*EF1001"))
            .Select(Relative)
            .Where(f => !Allowed.ContainsKey(f))
            .ToList();

        Assert.AreEqual(0, offenders.Count, "These files disable EF1001 without an entry in the allowlist: " + string.Join(", ", offenders));
    }

    [TestMethod]
    public void No_shipping_project_disables_EF1001()
    {
        var offenders = ShippingFiles("*.csproj").Concat(ShippingFiles("Directory.Build.*"))
            .Where(f => XDocument.Load(f).Descendants()
                .Where(e => e.Name.LocalName is "NoWarn" or "WarningsNotAsErrors")
                .Any(e => e.Value.Contains("EF1001", StringComparison.OrdinalIgnoreCase)))
            .Select(Relative)
            .ToList();

        Assert.AreEqual(0, offenders.Count, "These projects exempt EF1001: " + string.Join(", ", offenders));
    }

    [TestMethod]
    public void Warnings_are_errors_for_every_project()
    {
        var props = XDocument.Load(Path.Combine(RepositoryLayout.Root, "Directory.Build.props"));

        Assert.AreEqual("true", props.Descendants().Single(e => e.Name.LocalName == "TreatWarningsAsErrors").Value.Trim());
        Assert.IsFalse(
            props.Descendants().Where(e => e.Name.LocalName is "NoWarn" or "WarningsNotAsErrors").Any(e => e.Value.Contains("EF1001")),
            "Directory.Build.props exempts EF1001 for every project.");
    }

    [TestMethod]
    public void Every_allowlist_entry_names_a_file_that_still_disables_it()
    {
        foreach (var (file, reason) in Allowed)
        {
            var path = Path.Combine(RepositoryLayout.Source, file);
            Assert.IsTrue(File.Exists(path), $"The allowlist names '{file}' ({reason}), which does not exist.");
            StringAssert.Contains(File.ReadAllText(path), "EF1001", $"'{file}' no longer disables EF1001; remove it from the allowlist.");
        }
    }
}
