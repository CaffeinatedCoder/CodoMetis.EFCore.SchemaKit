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
/// Prose cannot be asserted. This holds the parts of it that can be shown false: a path, in
/// backticks or in a command of a code block; a file name; an annotation key this repository
/// writes or reads; a member written with its type; a name written alone, be it a type, a
/// member or a call; a test class; what a command's <c>--filter</c> names. Those are what a
/// rename leaves behind without a word, and what a reader takes on trust.
/// </para>
/// <para>
/// How firmly each is held differs. A member of a shipped type is asked of the assembly. One of
/// a test type, which this project does not reference, is looked for in the file that declares
/// the type. A name written alone (<c>SchemaKitModelDiffer</c>, <c>Declare</c>) only has to occur
/// in a C# or project file of the repository: nothing says which type it is a member of, and a
/// name that lingers in a comment still counts.
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

    /// <summary>A name written alone, with its type arguments or as a call: <c>SchemaKitModelDiffer</c>, <c>GetDeclarations&lt;T&gt;()</c>.</summary>
    [GeneratedRegex(@"^(?<name>[A-Z][A-Za-z0-9_]+)(?:<[^<>]*>)?(?:\(.*\))?$")]
    private static partial Regex NameAlone();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    /// <summary>
    /// The names a guide writes alone that are not this repository's and that nothing in its
    /// source uses, each with whose it is. A name that a guide no longer writes, or that the source
    /// has by now, is taken off: the list would let the next mistyped name through.
    /// </summary>
    private static readonly Dictionary<string, string> Elsewhere = new(StringComparer.Ordinal)
    {
        ["AddUniqueConstraint"] = "EF Core's: the call a migration of 5.x holds",
        ["MigrationHarness"] = "EFCore.ComplexIndexes': named as what is not ported",
    };

    /// <summary>A test class named alone: <c>DeclarationStoreTests</c>.</summary>
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9_]*Tests$")]
    private static partial Regex TestClassName();

    /// <summary>What a command's filter names: <c>ClassName~WiringConditionTests</c>, <c>FullyQualifiedName~C05_replace_service_before</c>.</summary>
    [GeneratedRegex(@"\b(?<kind>ClassName|FullyQualifiedName)~(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Filter();

    /// <summary>A type declared in a C# file.</summary>
    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Z][A-Za-z0-9_]*)")]
    private static partial Regex TypeDeclaration();

    /// <summary>A fenced code block: three backticks at the start of a line, to the next such line.</summary>
    [GeneratedRegex(@"^```[^\n]*\n(?<code>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

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

    private static IReadOnlyDictionary<string, string> Texts { get; } =
        Guides.ToDictionary(guide => guide, guide => File.ReadAllText(Path.Combine(RepositoryLayout.Root, guide)).ReplaceLineEndings("\n"), StringComparer.Ordinal);

    private static IReadOnlyList<Cited> Tokens { get; } =
    [
        .. Texts.SelectMany(guide => Ticked().Matches(guide.Value)
            .Select(match => new Cited(guide.Key, match.Groups[1].Value.Trim()))
            .Distinct()),
    ];

    /// <summary>
    /// The words of the guides' code blocks: what a command is given. A word that holds a
    /// variable, a quote, a pattern or a placeholder in angle brackets is no path to look up.
    /// </summary>
    private static IReadOnlyList<Cited> CommandWords { get; } =
    [
        .. Texts.SelectMany(guide => FencedBlock().Matches(guide.Value)
            .SelectMany(block => block.Groups["code"].Value.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Where(word => word.IndexOfAny(['$', '"', '\'', '<', '>', '*', '…', '=', '(', ')', '{', '}', ';', '|', '`', '~', '#', '@', ':']) < 0)
            .Select(word => new Cited(guide.Key, word))
            .Distinct()),
    ];

    /// <summary>Every file of the checkout, as a path from its root with forward slashes.</summary>
    private static IReadOnlyList<string> Files { get; } = [.. RepositoryLayout.Files().Select(RepositoryLayout.Relative)];

    /// <summary>Every folder that holds a file, the same way.</summary>
    private static IReadOnlySet<string> Folders { get; } = Files.SelectMany(Ancestors).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> Paths { get; } = Files.Concat(Folders).ToHashSet(StringComparer.Ordinal);

    /// <summary>The text of every C# and project file of the repository, by its path.</summary>
    private static IReadOnlyDictionary<string, string> Sources { get; } = Files
        .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".csproj", StringComparison.Ordinal) || file.EndsWith(".props", StringComparison.Ordinal))
        .ToDictionary(file => file, file => File.ReadAllText(Path.Combine(RepositoryLayout.Root, file)), StringComparer.Ordinal);

    /// <summary>The types the C# files declare, each with the files that declare it.</summary>
    private static IReadOnlyDictionary<string, List<string>> Declared { get; } = Sources
        .Where(source => source.Key.EndsWith(".cs", StringComparison.Ordinal))
        .SelectMany(source => TypeDeclaration().Matches(source.Value).Select(match => (Name: match.Groups["name"].Value, File: source.Key)))
        .GroupBy(declared => declared.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(declared => declared.File).Distinct().ToList(), StringComparer.Ordinal);

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

    /// <summary>
    /// A path or a file a command is given exists: the project a <c>dotnet test</c> names, the
    /// script that is run. In <c>EFCore.ComplexIndexes</c> it was a command in CLAUDE.md that had
    /// gone wrong, and a command stands in a code block, where nothing is in backticks.
    /// </summary>
    [TestMethod(DisplayName = "The paths in the commands of CLAUDE.md and the skills exist")]
    public void The_paths_in_the_guides_commands_exist()
    {
        var topLevel = Directory.EnumerateFileSystemEntries(RepositoryLayout.Root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var names = Files.Select(file => file.Split('/')[^1]).ToHashSet(StringComparer.Ordinal);
        var missing = new List<Cited>();
        var read = 0;

        foreach (var cited in CommandWords)
        {
            var segments = cited.Token.Trim('/').Split('/');

            if (segments.Length > 1 && topLevel.Contains(segments[0]))
            {
                read++;
                if (!Paths.Contains(string.Join('/', segments))) missing.Add(cited);
            }
            else if (segments.Length == 1 && FileName().IsMatch(segments[0]))
            {
                read++;
                if (!names.Contains(segments[0])) missing.Add(cited);
            }
        }

        Assert.IsTrue(read > 5, $"Only {read} paths were read from the commands of {string.Join(", ", Guides)}.");
        Assert.AreEqual(
            0, missing.Count,
            $"These paths of a command do not exist: {string.Join(", ", missing)}. The command fails for whoever copies it.");
    }

    /// <summary>
    /// A test class a guide names exists, and so does what a command's <c>--filter</c> names. A
    /// filter that matches nothing passes: a command that names a class that was renamed runs no
    /// test and reports no failure.
    /// </summary>
    [TestMethod(DisplayName = "The test classes CLAUDE.md and the skills name, and what a filter names, exist")]
    public void The_test_classes_the_guides_name_exist()
    {
        var classes = Tokens.Where(cited => TestClassName().IsMatch(cited.Token)).ToList();
        var filters = Texts
            .SelectMany(guide => Filter().Matches(guide.Value).Select(match => (Guide: guide.Key, Kind: match.Groups["kind"].Value, Name: match.Groups["name"].Value)))
            .Distinct()
            .ToList();

        Assert.IsTrue(classes.Count > 10 && filters.Count > 2, $"Only {classes.Count} test classes and {filters.Count} filters were read from {string.Join(", ", Guides)}.");

        var missing = classes.Where(cited => !Declared.ContainsKey(cited.Token)).Select(cited => cited.ToString()).ToList();

        // `~` is "contains": a class whose name holds the text, or, for FullyQualifiedName, a
        // method of a test whose name does.
        var tests = Sources.Where(source => source.Key.EndsWith(".cs", StringComparison.Ordinal) && (source.Key.StartsWith("test/", StringComparison.Ordinal) || source.Key.StartsWith("spikes/", StringComparison.Ordinal))).ToList();

        missing.AddRange(filters
            .Where(filter => !Declared.Keys.Any(type => type.Contains(filter.Name, StringComparison.Ordinal))
                             && (filter.Kind == "ClassName" || !tests.Any(source => Regex.IsMatch(source.Value, $@"{Regex.Escape(filter.Name)}\w*\s*(?:<[^<>]*>)?\("))))
            .Select(filter => $"{filter.Kind}~{filter.Name} ({filter.Guide})"));

        Assert.AreEqual(
            0, missing.Count,
            $"These test classes, or what a filter names, do not exist: {string.Join(", ", missing)}.");
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
        var declared = Sources
            .Where(source => source.Key.EndsWith(".cs", StringComparison.Ordinal) && source.Key.StartsWith("src/", StringComparison.Ordinal))
            .SelectMany(source => AnnotationLiteral().Matches(source.Value).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var named = Tokens
            .Where(token => AnnotationKey().IsMatch(token.Token))
            .Where(token => token.Token.StartsWith("CodoMetis:", StringComparison.Ordinal) || token.Token.StartsWith("Custom", StringComparison.Ordinal))
            .ToList();
        var missing = named.Where(token => !declared.Contains(token.Token)).ToList();

        Assert.IsTrue(named.Count > 3, $"Only {named.Count} annotation keys were read from {string.Join(", ", Guides)}.");
        Assert.AreEqual(0, missing.Count, $"No string under src/ holds these annotation keys: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// A name a guide writes alone is in the source: a type, a member, a call, an option, a
    /// diagnostic's id. The guide says of each what it does, and a name that is nowhere in the
    /// repository is one that was renamed, removed or mistyped.
    /// </summary>
    /// <remarks>
    /// It only has to occur, in a C# or project file. That is less than the member test asks, and
    /// it is what can be asked of a name that stands alone. CLAUDE.md wrote 151 of them when this
    /// was added, and three were someone else's.
    /// </remarks>
    [TestMethod(DisplayName = "The names CLAUDE.md and the skills write alone are in the source")]
    public void The_names_the_guides_write_alone_are_in_the_source()
    {
        // The names of the list are written in this file, which is no use of them.
        const string self = "test/CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests/Documentation/ClaudeMdConsistencyTests.cs";

        Assert.IsTrue(Sources.ContainsKey(self), $"This test is no longer at {self}, so the names of its own list count as the source's.");

        var words = Sources
            .SelectMany(source => Word().Matches(source.Value).Select(match => match.Value).Where(word => source.Key != self || !Elsewhere.ContainsKey(word)))
            .ToHashSet(StringComparer.Ordinal);
        var named = Tokens
            .Select(cited => (Cited: cited, Name: NameAlone().Match(cited.Token)))
            .Where(candidate => candidate.Name.Success)
            .Select(candidate => (candidate.Cited, Name: candidate.Name.Groups["name"].Value))
            .ToList();

        Assert.IsTrue(named.Count > 50, $"Only {named.Count} names were read from {string.Join(", ", Guides)}.");

        var missing = named.Where(candidate => !words.Contains(candidate.Name) && !Elsewhere.ContainsKey(candidate.Name)).Select(candidate => candidate.Cited).ToList();

        Assert.AreEqual(
            0, missing.Count,
            $"These names are in no C# or project file of the repository: {string.Join(", ", missing)}. Each was renamed, removed "
            + $"or mistyped. A name that is another library's and that the source does not use belongs in {nameof(Elsewhere)}, with whose it is.");

        var stale = Elsewhere.Keys.Where(name => words.Contains(name) || named.All(candidate => candidate.Name != name)).Order(StringComparer.Ordinal).ToList();

        Assert.AreEqual(
            0, stale.Count,
            $"{string.Join(", ", stale)} is in {nameof(Elsewhere)} and is in the source by now, or no guide writes it any more. Take it off the list.");
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

        var ofTestTypes = 0;

        foreach (var cited in Tokens)
        {
            // The member starts with a capital, which is what tells `LegacyFormats.RequireRewritten`
            // from a file name of the same shape. A type that is not ours is not ours to check.
            if (MemberOfType().Match(cited.Token) is not { Success: true } match) continue;

            var member = match.Groups[2].Value;

            if (types.TryGetValue(match.Groups[1].Value, out var candidates))
            {
                read++;

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

                if (!candidates.Any(type => type.GetMember(member, any).Length > 0))
                    missing.Add(cited);
            }
            else if (Declared.TryGetValue(match.Groups[1].Value, out var files))
            {
                // A type of the tests, the probes or a spike: no assembly of it is here to ask, so
                // the member is looked for as a name in the file that declares the type.
                ofTestTypes++;

                if (!files.Any(file => Regex.IsMatch(Sources[file], $@"\b{Regex.Escape(member)}\b")))
                    missing.Add(cited);
            }
        }

        Assert.IsTrue(read > 5, $"Only {read} members of this repository's types were read from {string.Join(", ", Guides)}.");
        Assert.IsTrue(ofTestTypes > 0, $"No member of a test type was read from {string.Join(", ", Guides)}.");
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
