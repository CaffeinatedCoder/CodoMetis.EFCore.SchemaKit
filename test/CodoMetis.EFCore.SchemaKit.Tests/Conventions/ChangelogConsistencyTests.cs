using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// The form of <c>CHANGELOG.md</c> (CONTRIBUTING.md, Changelog). A version's section is the text of
/// its GitHub release, word for word, and <c>release.yml</c> and <c>pr-hygiene.sh</c> find it by
/// its heading, so a section in another style is no section to either. Ported from
/// <c>EFCore.ComplexIndexes</c>, where a release once went out with a section nothing had read.
/// </summary>
/// <remarks>
/// <para>
/// One changelog, at the root. The packages have none of their own: their READMEs send a reader to
/// the repository. The tests of <c>EFCore.ComplexIndexes</c> that kept four changelogs in step
/// have no subject here.
/// </para>
/// <para>
/// The heading is asserted and not only parsed: <c>release.yml</c> takes the notes from
/// <c>## [x.y.z] — </c> up to the next <c>##</c>, and every test below reads a section the same
/// way. <see cref="Every_level_two_heading_is_a_version_heading" /> is what ties the two together.
/// </para>
/// </remarks>
[TestClass]
public sealed class ChangelogConsistencyTests
{
    /// <summary>What stands in the date's place until the commit that is tagged.</summary>
    private const string Unreleased = "unreleased";

    /// <summary>
    /// The one heading: <c>## [11.0.0-preview.1] — 2026-11-17</c>, or, for the version that is
    /// being prepared, <c>## [11.0.0-preview.1] — unreleased</c>.
    /// </summary>
    private static readonly Regex VersionHeading =
        new($@"^## \[(?<version>{ReleaseVersion.Pattern})\] — (?<date>\d{{4}}-\d{{2}}-\d{{2}}|{Unreleased})$", RegexOptions.Compiled);

    /// <summary>A version heading in any style: <c>## 11.0.0</c>, <c>## [11.0.0]</c>, <c>## v11.0.0</c>.</summary>
    private static readonly Regex AnyVersionHeading = new($@"^##+ \[?v?{ReleaseVersion.Pattern}\b", RegexOptions.Compiled);

    /// <summary>A reference-style link definition: <c>[11.0.0]: https://…</c>.</summary>
    private static readonly Regex ReferenceDefinition = new(@"^\[[^\]]+\]:\s+\S+$", RegexOptions.Compiled);

    /// <summary>The target of an inline link: <c>](target)</c>.</summary>
    private static readonly Regex LinkTarget = new(@"\]\((?<target>[^)\s]*)\)", RegexOptions.Compiled);

    /// <summary>
    /// The section a minor opens with when it changes what an existing model scaffolds
    /// (CONTRIBUTING.md, Branches and releases).
    /// </summary>
    private const string ChangedAnswers = "Changed answers";

    /// <summary>The sections a version may have, in the order they come in.</summary>
    private static readonly string[] SectionOrder = [ChangedAnswers, "Breaking", "Added", "Changed", "Fixed"];

    private sealed record Section(ReleaseVersion Version, string Date, int HeadingLine, List<(int Number, string Line)> Lines)
    {
        public bool IsUnreleased => Date == Unreleased;
    }

    /// <summary>
    /// Each version section: the lines after its heading up to the next <c>##</c> heading, with
    /// their line numbers. What stands above the first heading is the file's own header.
    /// </summary>
    private static List<Section> Sections()
    {
        var sections = new List<Section>();
        Section? current = null;
        var all = File.ReadAllLines(RepositoryLayout.Changelog);

        for (var index = 0; index < all.Length; index++)
        {
            var line = all[index].TrimEnd();

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = VersionHeading.Match(line);
                current = heading.Success
                    ? new Section(ReleaseVersion.Parse(heading.Groups["version"].Value), heading.Groups["date"].Value, index + 1, [])
                    : null;
                if (current is not null) sections.Add(current);
            }
            else
            {
                current?.Lines.Add((index + 1, line));
            }
        }

        return sections;
    }

    /// <summary>
    /// The versions that are out: those whose heading has its date, which a version gets in the
    /// commit that is tagged. <c>SecurityPolicyConsistencyTests</c> reads what is supported from it.
    /// </summary>
    internal static IReadOnlyList<ReleaseVersion> Released()
        => [.. Sections().Where(section => !section.IsUnreleased).Select(section => section.Version)];

    [TestMethod(DisplayName = "The changelog has a section for the version being shipped")]
    public void The_changelog_has_a_section_for_the_shipped_version()
    {
        var version = RepositoryLayout.ShippedVersion;

        Assert.IsTrue(
            Sections().Any(section => section.Version == version),
            $"Directory.Build.props ships {version}, and CHANGELOG.md has no '## [{version}] — yyyy-mm-dd' section "
            + $"(or '— {Unreleased}' until the tag). release.yml refuses a tag without one, on the day of the release; "
            + "open the section with the version.");
    }

    [TestMethod(DisplayName = "No section is for a version newer than the one being shipped")]
    public void No_section_runs_ahead_of_the_shipped_version()
    {
        var version = RepositoryLayout.ShippedVersion;
        var ahead = Sections().Where(section => section.Version > version).Select(section => section.Version).ToList();

        Assert.AreEqual(
            0, ahead.Count,
            $"CHANGELOG.md documents {string.Join(", ", ahead)}, which is newer than the {version} in "
            + "Directory.Build.props: the version was not set with the section.");
    }

    [TestMethod(DisplayName = "The sections run from the newest version to the oldest, each version once")]
    public void Sections_run_from_the_newest_version_to_the_oldest()
    {
        var documented = Sections().Select(section => section.Version).ToList();

        Assert.AreNotEqual(0, documented.Count, "CHANGELOG.md has no version section this test could read.");
        CollectionAssert.AreEqual(
            documented.Distinct().OrderByDescending(v => v).ToList(),
            documented,
            $"CHANGELOG.md's sections are out of order or repeated: {string.Join(", ", documented)}.");
    }

    /// <summary>
    /// Every <c>##</c> heading is a version heading in the one style. A heading in another style
    /// is no section to any test here, so its lines would go unchecked, and <c>release.yml</c>
    /// would not find it.
    /// </summary>
    [TestMethod(DisplayName = "Every '##' heading of the changelog is a version heading")]
    public void Every_level_two_heading_is_a_version_heading()
    {
        var offenders = File.ReadAllLines(RepositoryLayout.Changelog)
            .Select((line, index) => (Line: line.TrimEnd(), Number: index + 1))
            .Where(entry => entry.Line.StartsWith("## ", StringComparison.Ordinal) && !VersionHeading.IsMatch(entry.Line))
            .Select(entry => $"CHANGELOG.md:{entry.Number}: '{entry.Line}'")
            .ToList();

        Assert.AreEqual(
            0, offenders.Count,
            $"These headings are not '## [x.y.z] — yyyy-mm-dd' (or '— {Unreleased}' for the version being prepared). "
            + "release.yml takes the release's notes from that heading, and the other changelog tests read nothing else:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// <c>unreleased</c> stands for the date of one version only: the one
    /// <c>Directory.Build.props</c> names, which is the first section.
    /// </summary>
    /// <remarks>
    /// The date replaces it in the commit that is tagged, and <c>release.yml</c> refuses a tag
    /// while it stands (<c>ReleaseWiringConventionTests</c> runs that step). So a section that is
    /// still <c>unreleased</c> below a newer one is a version that was skipped or whose date was
    /// forgotten: its heading says "not out yet" for good, on a page readers take for the record.
    /// A floating <c>## [Unreleased]</c> with no version is no heading at all here, for the same
    /// reason as in <c>EFCore.ComplexIndexes</c>: every commit on <c>main</c> is a tag away from
    /// being published, under the version the build states.
    /// </remarks>
    [TestMethod(DisplayName = "Only the version being shipped may be unreleased, and it is the first section")]
    public void Only_the_shipped_version_may_be_unreleased()
    {
        var version = RepositoryLayout.ShippedVersion;
        var sections = Sections();
        var offenders = sections
            .Where(section => section.IsUnreleased && (section.Version != version || section != sections[0]))
            .Select(section => $"CHANGELOG.md:{section.HeadingLine}: [{section.Version}]")
            .ToList();

        Assert.AreEqual(
            0, offenders.Count,
            $"These sections are '— {Unreleased}' and are not the first section for {version}, the version in "
            + "Directory.Build.props. A version that was tagged has its date; one that never was has no section:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The READMEs are the landing pages, and the packed ones are what nuget.org shows. A version
    /// section in one is a second copy of the changelog that nothing keeps in step, in the place
    /// where most consumers would read it.
    /// </summary>
    [TestMethod(DisplayName = "No README holds a changelog section")]
    public void No_readme_holds_a_changelog_section()
    {
        var readmes = RepositoryLayout.ShippingProjects()
            .Select(project => Path.Combine(Path.GetDirectoryName(project)!, "README.md"))
            .Prepend(Path.Combine(RepositoryLayout.Root, "README.md"))
            .ToList();

        // The root's and one per package: found, or a README went unread.
        Assert.IsTrue(readmes.Count > 1 && readmes.All(File.Exists), $"Expected a README at the root and in each package: {string.Join(", ", readmes.Select(RepositoryLayout.Relative))}.");

        var offenders = readmes
            .SelectMany(readme => File.ReadAllLines(readme)
                .Select((line, index) => (Line: line.TrimEnd(), Number: index + 1))
                .Where(entry => AnyVersionHeading.IsMatch(entry.Line))
                .Select(entry => $"{RepositoryLayout.Relative(readme)}:{entry.Number}: '{entry.Line}'"))
            .ToList();

        Assert.AreEqual(
            0, offenders.Count,
            "These READMEs have a heading that is a version. What changed in a version is in CHANGELOG.md; a copy "
            + "in a README is kept in step by nothing, and the packed ones are what nuget.org shows:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// A section is terse: at most one introductory line, then <c>###</c> sections from
    /// <see cref="SectionOrder" />, in that order, each holding changes of one line.
    /// </summary>
    /// <remarks>
    /// The section is the GitHub release's notes. A continuation line, a nested bullet, a code
    /// block or a second paragraph is how an explanation comes back into it, so each is refused:
    /// the explanation belongs on the page the line links to. Since <c>### Changed answers</c> is
    /// first in the order, this also keeps it at the top of its entry, where
    /// <c>pr-hygiene.sh</c> looks for a <c>silent</c> pull request's row.
    /// </remarks>
    [TestMethod(DisplayName = "Every version section holds one-line changes under known sections")]
    public void Every_version_section_holds_one_line_changes_under_known_sections()
    {
        var offenders = new List<string>();
        var sections = Sections();

        // A reader that finds no section would pass everything below.
        Assert.AreNotEqual(0, sections.Count, "CHANGELOG.md has no version section this test could read, so its form went unchecked.");
        Assert.IsTrue(sections.Any(section => section.Lines.Any(entry => entry.Line.StartsWith("- ", StringComparison.Ordinal))), "No section of CHANGELOG.md has a change this test could read.");

        foreach (var section in sections)
        {
            var intro = 0;
            var current = -1;

            foreach (var (number, line) in section.Lines)
            {
                var where = $"CHANGELOG.md:{number} ({section.Version})";

                if (line.Length == 0 || ReferenceDefinition.IsMatch(line)) continue;

                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    var index = Array.IndexOf(SectionOrder, line[4..].Trim());

                    if (index < 0)
                        offenders.Add($"{where}: '{line}' is not one of {string.Join(", ", SectionOrder)}");
                    else if (index <= current)
                        offenders.Add($"{where}: '{line}' is repeated or out of order; the sections go {string.Join(", ", SectionOrder)}");
                    else
                        current = index;
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (current < 0) offenders.Add($"{where}: a change outside a ### section");
                }
                else if (char.IsWhiteSpace(line[0]) || line.StartsWith("```", StringComparison.Ordinal))
                {
                    offenders.Add($"{where}: a continuation line, a nested bullet or a code block; say the change in one line");
                }
                else if (line.StartsWith('#'))
                {
                    offenders.Add($"{where}: '{Truncate(line)}' is a heading below the ### sections; use none");
                }
                else if (current >= 0 || ++intro > 1)
                {
                    offenders.Add($"{where}: a paragraph; a version has at most one introductory line, before its sections");
                }
            }
        }

        Assert.AreEqual(
            0, offenders.Count,
            "These lines of the changelog are not in its form (CONTRIBUTING.md, Changelog):\n" + string.Join("\n", offenders));
    }

    /// <summary>A link inside a version section is an absolute URL.</summary>
    /// <remarks>
    /// The section is published as the GitHub release's notes, where a relative link resolves
    /// against the release's page and leads nowhere. <c>DocumentationLinkTests</c> holds that the
    /// absolute ones into this repository name a file and a heading that exist.
    /// </remarks>
    [TestMethod(DisplayName = "Every link of a version section is absolute")]
    public void Every_link_of_a_version_section_is_absolute()
    {
        var links = Sections()
            .SelectMany(section => section.Lines.SelectMany(entry => LinkTarget.Matches(entry.Line)
                .Select(match => (section.Version, entry.Number, Target: match.Groups["target"].Value))))
            .ToList();

        Assert.AreNotEqual(0, links.Count, "No link was found in any section of CHANGELOG.md, so none was checked.");

        var offenders = links
            .Where(link => !link.Target.StartsWith("https://", StringComparison.Ordinal))
            .Select(link => $"CHANGELOG.md:{link.Number} ({link.Version}): {link.Target}")
            .ToList();

        Assert.AreEqual(
            0, offenders.Count,
            "These links are relative, and break on the release's page. Write them as "
            + "https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/blob/main/…:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// <c>### Changed answers</c> stands in a minor only. A major, and so each of its previews,
    /// lists a changed answer under Breaking, and a patch may not change one at all.
    /// </summary>
    /// <remarks>
    /// The section exists so that a silent fix can ship without a major, which here means without
    /// a new EF Core. A patch that carried one would be read as "nothing to check" by everyone
    /// who takes patches unattended, which is why the rule says minor, and why
    /// <c>pr-hygiene.sh</c> refuses a <c>silent</c> pull request into a patch.
    /// </remarks>
    [TestMethod(DisplayName = "'### Changed answers' stands in a minor only")]
    public void Changed_answers_stand_in_a_minor_only()
    {
        var offenders = Sections()
            .Where(section => !section.Version.IsMinor)
            .SelectMany(section => section.Lines
                .Where(entry => entry.Line.StartsWith("### ", StringComparison.Ordinal) && entry.Line[4..].Trim() == ChangedAnswers)
                .Select(entry => $"CHANGELOG.md:{entry.Number} ({section.Version})"))
            .ToList();

        Assert.AreEqual(
            0, offenders.Count,
            $"These versions have a '### {ChangedAnswers}' section and are no minor (x.y.0 with y > 0): "
            + $"{string.Join(", ", offenders)}. A major and its previews list a changed answer under Breaking, and a "
            + "patch may not change one: it ships as a minor (CONTRIBUTING.md, Branches and releases).");
    }

    private static string Truncate(string line) => line.Length <= 60 ? line : line[..57] + "...";
}
