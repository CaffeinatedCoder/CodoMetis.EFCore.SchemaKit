using System.Reflection;
using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Documentation;

/// <summary>
/// CLAUDE.md is the repository's record of what was decided and why each guard is there, and the
/// skills under <c>.claude/skills</c> are how a review and a guard are done here. They are the
/// documents nothing checks, and out of date they are worse than missing: they are read as the
/// truth and acted on. Ported from <c>EFCore.ComplexIndexes</c>, where a build command stood in
/// CLAUDE.md with a behaviour that had become wrong, and a count of annotation keys stayed after
/// two were dropped.
/// </summary>
/// <remarks>
/// <para>
/// Prose cannot be asserted. This holds the parts of it that can be shown false: a path, a file
/// name, an annotation key this repository writes or reads, a member of one of its types. Those
/// are what a rename leaves behind without a word, and what a reader takes on trust.
/// </para>
/// <para>
/// It stands in this test project because it reads the types of both packages, as the other
/// documentation tests do; the kernel's test project does not reference the PostgreSQL package.
/// </para>
/// </remarks>
[TestClass]
public sealed partial class ClaudeMdConsistencyTests
{
    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex Ticked();

    /// <summary>A member named with its type, as a call or not: <c>KeyTarget.Of</c>, <c>PgSchema.Where&lt;T&gt;(…)</c>.</summary>
    [GeneratedRegex(@"^([A-Z][A-Za-z0-9_]*)(?:<[^<>]*>)?\.([A-Z][A-Za-z0-9_]*)(?:<[^<>]*>)?(?:\(.*\))?$")]
    private static partial Regex MemberOfType();

    /// <summary>An annotation key: <c>CodoMetis:SchemaKit</c>, <c>CustomIndex:IndexParts</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z]+:[A-Za-z]+$")]
    private static partial Regex AnnotationKey();

    /// <summary>A string literal in the source that is such a key.</summary>
    [GeneratedRegex(@"""([A-Za-z]+:[A-Za-z]+)""")]
    private static partial Regex AnnotationLiteral();

    /// <summary>A file cited by its name alone: <c>release.yml</c>, <c>waivers.txt</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\.(?:cs|csproj|props|slnx|sh|json|yml|md|txt|config)$")]
    private static partial Regex FileName();

    /// <summary>CLAUDE.md and every skill, as paths from the root.</summary>
    private static IReadOnlyList<string> Guides { get; } =
    [
        "CLAUDE.md",
        .. RepositoryLayout.Files().Select(RepositoryLayout.Relative)
            .Where(file => file.StartsWith(".claude/skills/", StringComparison.Ordinal) && file.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>What a guide writes in backticks, with the guide it is in.</summary>
    private sealed record Cited(string Guide, string Token)
    {
        public override string ToString() => $"{Token} ({Guide})";
    }

    private static IReadOnlyList<Cited> Tokens { get; } =
    [
        .. Guides.SelectMany(guide => Ticked().Matches(File.ReadAllText(Path.Combine(RepositoryLayout.Root, guide)))
            .Select(match => new Cited(guide, match.Groups[1].Value.Trim()))
            .Distinct()),
    ];

    /// <summary>Every file of the checkout, as a path from its root with forward slashes.</summary>
    private static IReadOnlyList<string> Files { get; } = [.. RepositoryLayout.Files().Select(RepositoryLayout.Relative)];

    /// <summary>Every folder that holds a file, the same way.</summary>
    private static IReadOnlySet<string> Folders { get; } = Files.SelectMany(Ancestors).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> Paths { get; } = Files.Concat(Folders).ToHashSet(StringComparer.Ordinal);

    /// <summary>The assemblies whose types the guides describe: both packages'.</summary>
    private static readonly Assembly[] Shipped = [typeof(KeyTarget).Assembly, typeof(PgExclusion).Assembly];

    /// <summary>The premise of every test below: CLAUDE.md is read, and the three skills beside it.</summary>
    [TestMethod(DisplayName = "CLAUDE.md and the skills are found")]
    public void The_guides_are_found()
        => CollectionAssert.IsSubsetOf(
            new[] { "CLAUDE.md", ".claude/skills/migration-safety-review/SKILL.md", ".claude/skills/plain-docs/SKILL.md", ".claude/skills/verify-the-guard/SKILL.md" },
            Guides.ToList());

    /// <summary>
    /// A path a guide names exists: from the root when it starts with one of the root's entries
    /// (<c>src/CodoMetis.EFCore.SchemaKit/Sql</c>), and as the end of a path somewhere in the
    /// repository when it does not (<c>Harness/TestKit.cs</c>, <c>Scaffolded/</c>), which is how
    /// CLAUDE.md cites a file once its project is named.
    /// </summary>
    [TestMethod(DisplayName = "The paths CLAUDE.md and the skills name exist")]
    public void The_paths_the_guides_name_exist()
    {
        var topLevel = Directory.EnumerateFileSystemEntries(RepositoryLayout.Root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var folderNames = Folders.Select(folder => folder.Split('/')[^1]).ToHashSet(StringComparer.Ordinal);
        var missing = new List<Cited>();
        var fromRoot = 0;
        var relative = 0;

        foreach (var cited in Tokens)
        {
            var token = cited.Token;

            // An ellipsis or a wildcard is an abbreviation; a space, a colon or a bracket is prose, a URL or code.
            if (!token.Contains('/') || token.IndexOfAny([' ', '…', '*', ':', '{', '<', '(', '"']) >= 0) continue;

            var segments = token.Trim('/').Split('/');
            if (segments.Any(segment => segment.Length == 0)) continue;

            var path = string.Join('/', segments);

            if (topLevel.Contains(segments[0]))
            {
                fromRoot++;
                if (!Paths.Contains(path)) missing.Add(cited);
            }
            else if (token.EndsWith('/') || FileName().IsMatch(segments[^1]) || folderNames.Contains(segments[0]))
            {
                // `Legacy/Internal/ComplexIndexes5xExclusionReader` names a class by its file.
                relative++;
                if (!Paths.Any(candidate => EndsWithPath(candidate, path) || EndsWithPath(candidate, path + ".cs"))) missing.Add(cited);
            }
        }

        // The premise: both kinds of path are found in the text.
        Assert.IsTrue(fromRoot > 5 && relative > 5, $"Only {fromRoot} paths from the root and {relative} relative ones were read from {string.Join(", ", Guides)}.");

        Assert.AreEqual(
            0, missing.Count,
            $"These paths do not exist: {string.Join(", ", missing)}. Something was moved or renamed, and the guide was not.");
    }

    [TestMethod(DisplayName = "The files CLAUDE.md and the skills cite by name exist somewhere in the repository")]
    public void The_files_the_guides_cite_by_name_exist()
    {
        var present = Files.Select(file => file.Split('/')[^1]).ToHashSet(StringComparer.Ordinal);
        var cited = Tokens.Where(token => FileName().IsMatch(token.Token)).ToList();
        var missing = cited.Where(token => !present.Contains(token.Token)).ToList();

        Assert.IsTrue(cited.Count > 5, $"Only {cited.Count} file names were read from {string.Join(", ", Guides)}.");
        Assert.AreEqual(0, missing.Count, $"These files do not exist: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// An annotation key a guide names is a string of the shipped source: SchemaKit's own
    /// (<c>CodoMetis:…</c>), and those of <c>EFCore.ComplexIndexes</c> 5.x that it reads or refuses
    /// (<c>Custom…:…</c>). EF Core's and the providers' keys are named there too and are not
    /// this repository's to declare.
    /// </summary>
    /// <remarks>
    /// The keys are a file format: they are in consumers' snapshots and migrations. One that the
    /// source no longer has and the guide still names is the one the next change is written against.
    /// </remarks>
    [TestMethod(DisplayName = "The annotation keys CLAUDE.md and the skills name are in the shipped source")]
    public void The_annotation_keys_the_guides_name_are_in_the_source()
    {
        var declared = RepositoryLayout.Files()
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) && RepositoryLayout.Relative(file).StartsWith("src/", StringComparison.Ordinal))
            .SelectMany(file => AnnotationLiteral().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var named = Tokens
            .Where(token => AnnotationKey().IsMatch(token.Token))
            .Where(token => token.Token.StartsWith("CodoMetis:", StringComparison.Ordinal) || token.Token.StartsWith("Custom", StringComparison.Ordinal))
            .ToList();
        var missing = named.Where(token => !declared.Contains(token.Token)).ToList();

        Assert.IsTrue(named.Count > 3, $"Only {named.Count} annotation keys were read from {string.Join(", ", Guides)}.");
        Assert.AreEqual(0, missing.Count, $"No string under src/ holds these annotation keys: {string.Join(", ", missing)}.");
    }

    [TestMethod(DisplayName = "The members CLAUDE.md and the skills name are on the types they name them on")]
    public void The_members_the_guides_name_are_on_their_types()
    {
        var types = Shipped
            .SelectMany(assembly => assembly.GetTypes())
            .GroupBy(type => type.Name.Split('`')[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var missing = new List<Cited>();
        var read = 0;

        foreach (var cited in Tokens)
        {
            // The member starts with a capital, which is what tells `LegacyFormats.RequireRewritten`
            // from a file name of the same shape. A type that is not ours is not ours to check.
            if (MemberOfType().Match(cited.Token) is not { Success: true } match || !types.TryGetValue(match.Groups[1].Value, out var candidates))
                continue;

            read++;

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

            if (!candidates.Any(type => type.GetMember(match.Groups[2].Value, any).Length > 0))
                missing.Add(cited);
        }

        Assert.IsTrue(read > 5, $"Only {read} members of this repository's types were read from {string.Join(", ", Guides)}.");
        Assert.AreEqual(
            0, missing.Count,
            $"These members are not on their types: {string.Join(", ", missing)}. The type is still there, so nothing "
            + "else fails: the guide describes an API that is gone.");
    }

    private static bool EndsWithPath(string candidate, string path)
        => candidate == path || candidate.EndsWith("/" + path, StringComparison.Ordinal);

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            yield return path[..slash];
    }
}
