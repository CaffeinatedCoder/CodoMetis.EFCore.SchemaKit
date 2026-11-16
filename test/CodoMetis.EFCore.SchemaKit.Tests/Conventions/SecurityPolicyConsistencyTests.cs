using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// SECURITY.md says which versions receive fixes, and that is prose: nothing ties it to what is
/// released. A statement of support that is wrong is worse than none, since it reads as a
/// considered one, and it is the line a release forgets. Ported from
/// <c>EFCore.ComplexIndexes</c>, whose test records a sibling repository that shipped 6.2.0 with
/// a table that still said 6.1.x.
/// </summary>
/// <remarks>
/// <para>
/// What is released is read from the changelog: a version without a suffix whose heading has its
/// date, which it gets in the commit that is tagged. <c>EFCore.ComplexIndexes</c> reads a
/// <c>PackageValidationBaselineVersion</c>, which this repository does not have: no version is
/// out to validate against.
/// </para>
/// <para>
/// So the policy has two states. Until the first version without a suffix is dated, no row says
/// that a version is supported, and the policy says that none is released; a preview is never a
/// supported version. From then on there is one row, the latest minor, and the row of what is no
/// longer supported meets it. The commit that dates <c>11.0.0</c> fails here until the policy has
/// its table.
/// </para>
/// </remarks>
[TestClass]
public sealed class SecurityPolicyConsistencyTests
{
    // "| 11.0.x | ✅ |": the minor that receives fixes.
    private static readonly Regex SupportedRow =
        new(@"^\|\s*(?<major>\d+)\.(?<minor>\d+)\.x\s*\|\s*✅\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "| < 11.0 | ❌ |": everything before it.
    private static readonly Regex UnsupportedRow =
        new(@"^\|\s*<\s*(?<major>\d+)\.(?<minor>\d+)\s*\|\s*❌\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "**11.x** targets **EF Core 11**": the platform the line's support is tied to.
    private static readonly Regex LineTargets =
        new(@"\*\*(?<line>\d+)\.x\*\*[^\n]*?targets \*\*EF Core (?<efcore>\d+)\*\*", RegexOptions.Compiled);

    /// <summary>What the policy says while nothing is out. Asserted while that is so, so that a reworded sentence fails here and is not passed over later.</summary>
    private const string NothingReleased = "No version is released yet";

    /// <summary>The package every other one depends on: its EF Core reference is the floor of all of them.</summary>
    private const string Kernel = "CodoMetis.EFCore.SchemaKit";

    private static string Policy => File.ReadAllText(RepositoryLayout.SecurityPolicy);

    /// <summary>
    /// The line that is shipped says which EF Core it targets, and that is the EF Core the kernel
    /// references, and the package's own major.
    /// </summary>
    /// <remarks>
    /// The policy's horizon is stated against the platform ("for as long as Microsoft supports EF
    /// Core 11"), so a floor that moved without the sentence would promise support against a
    /// release the package no longer targets. A new major is a new EF Core major and nothing else
    /// (CONTRIBUTING.md, Branches and releases), which is why the three numbers are one.
    /// </remarks>
    [TestMethod(DisplayName = "SECURITY.md names the EF Core major the shipped line targets, which is the kernel's")]
    public void The_shipped_line_names_the_ef_core_major_it_targets()
    {
        var shipped = RepositoryLayout.ShippedVersion;
        var targets = LineTargets.Matches(Policy)
            .GroupBy(match => int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => int.Parse(group.First().Groups["efcore"].Value, CultureInfo.InvariantCulture));

        Assert.IsTrue(
            targets.ContainsKey(shipped.Major),
            $"Directory.Build.props ships {shipped}, and SECURITY.md has no sentence '**{shipped.Major}.x** targets **EF Core N**'. "
            + "A consumer has to know which platform the line's support is tied to.");

        var kernel = Path.Combine(RepositoryLayout.Source, Kernel, Kernel + ".csproj");
        var references = XDocument.Load(kernel).Descendants("PackageReference")
            .Where(reference => reference.Attribute("Include")?.Value.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
            .ToList();

        Assert.AreEqual(1, references.Count, $"Expected one EF Core reference in {RepositoryLayout.Relative(kernel)}, the floor of every package.");

        var floor = int.Parse(Regex.Match(references[0].Attribute("Version")!.Value, @"\d+").Value, CultureInfo.InvariantCulture);

        Assert.AreEqual(
            floor, targets[shipped.Major],
            $"SECURITY.md says {shipped.Major}.x targets EF Core {targets[shipped.Major]}, and the kernel references EF Core {floor}. "
            + "The sentence moves with the floor.");

        Assert.AreEqual(
            floor, shipped.Major,
            $"Directory.Build.props ships {shipped} on EF Core {floor}. A major of SchemaKit is an EF Core major, and nothing else.");
    }

    /// <summary>
    /// Nothing is said to be supported before a version without a suffix is released; from then
    /// on one row names the latest minor, and the row of what is unsupported meets it.
    /// </summary>
    [TestMethod(DisplayName = "SECURITY.md supports what is released: nothing before 11.0.0, then the latest minor, in one row")]
    public void The_policy_supports_what_is_released()
    {
        var text = Policy;
        var rows = SupportedRow.Matches(text)
            .Select(match => (Major: int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
                              Minor: int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        var latest = ChangelogConsistencyTests.Released().Where(version => !version.IsPrerelease).DefaultIfEmpty().Max();

        if (latest is null)
        {
            Assert.AreEqual(
                0, rows.Count,
                $"SECURITY.md lists {Describe(rows)} as supported, and CHANGELOG.md has no dated section of a version "
                + "without a suffix: the row promises fixes for a line that is not on NuGet. A preview is never a supported version.");

            StringAssert.Contains(
                text, NothingReleased,
                $"SECURITY.md no longer says '{NothingReleased}', and no version without a suffix is dated in CHANGELOG.md. "
                + "If the sentence was reworded, change it here too: this test holds it until the first release, and its absence after.");

            return;
        }

        Assert.IsFalse(
            text.Contains(NothingReleased, StringComparison.Ordinal),
            $"SECURITY.md still says '{NothingReleased}', and CHANGELOG.md dates {latest}. The section gets its table with the release.");

        Assert.AreEqual(
            1, rows.Count,
            $"SECURITY.md has {rows.Count} rows '| x.y.x | ✅ |' ({Describe(rows)}). This repository ships one line, so one minor "
            + $"receives fixes: {latest.Major}.{latest.Minor}.x, the latest release ({latest}).");

        Assert.AreEqual(
            (latest.Major, latest.Minor), rows[0],
            $"SECURITY.md lists {Describe(rows)} as supported, and the latest release is {latest}. A reporter reads the table to "
            + "know whether their version still receives fixes, so it names what is on NuGet.");

        var unsupported = UnsupportedRow.Match(text);

        Assert.IsTrue(unsupported.Success, "SECURITY.md has no row '| < x.y | ❌ |' for everything before the supported minor.");

        var below = (int.Parse(unsupported.Groups["major"].Value, CultureInfo.InvariantCulture),
                     int.Parse(unsupported.Groups["minor"].Value, CultureInfo.InvariantCulture));

        Assert.AreEqual(
            rows[0], below,
            $"SECURITY.md says versions below {below.Item1}.{below.Item2} are unsupported, and the supported minor is "
            + $"{rows[0].Major}.{rows[0].Minor}.x: the two rows meet at one minor, or a range is described by neither.");
    }

    private static string Describe(IEnumerable<(int Major, int Minor)> rows)
        => string.Join(" and ", rows.Select(row => $"{row.Major}.{row.Minor}.x")) is { Length: > 0 } listed ? listed : "nothing";
}
