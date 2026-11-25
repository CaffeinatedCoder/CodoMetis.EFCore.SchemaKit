using System.Reflection;
using System.Text.RegularExpressions;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Documentation;

/// <summary>
/// The API the pages name in their prose. <see cref="DocumentationSampleTests" /> compiles every
/// <c>csharp</c> block, so a sample cannot name a member that does not exist; a name in a sentence
/// or in a table is compiled by nothing, and a rename that is done in the source and forgotten
/// there reads as a method that works until someone types it. Ported from
/// <c>EFCore.ComplexIndexes</c>, where the samples were not compiled and this test read them too.
/// </summary>
/// <remarks>
/// <para>
/// The second test holds a promise a page makes by where it stands. A package's README is what
/// nuget.org shows for that package: the kernel's, naming <c>DeclarePostgres</c>, would promise
/// something the package a reader installed does not contain. The packages are found under
/// <c>src/</c>, so the SQL Server package is held to it without an edit here.
/// </para>
/// <para>
/// The changelog is left out on purpose. It is a record: an entry that says a preview shipped a
/// method stays true after the method is renamed, and a test over it would turn every rename
/// into a reason to rewrite what was published.
/// </para>
/// </remarks>
[TestClass]
public sealed partial class DocumentationApiTests
{
    private const string Kernel = "CodoMetis.EFCore.SchemaKit";

    /// <summary>
    /// What a sentence may call that is not SchemaKit's: EF Core, Npgsql, the BCL and dependency
    /// injection. Everything else has to be this repository's and has to exist, which is what makes
    /// the test say anything. A call that is added to a page belongs here when it is not ours, and
    /// the failure names it.
    /// </summary>
    private static readonly HashSet<string> ExternalApi = new(StringComparer.Ordinal)
    {
        // EF Core
        "EnsureCreated", "GenerateCreateScript", "ToTable", "HasIndex", "HasKey", "HasAlternateKey", "HasForeignKey",
        "HasFilter", "HasDatabaseName", "IsDescending", "IsUnique", "HasFillFactor", "IncludeProperties", "UseCollation",
        "HasConversion", "IsRequired",
        // Npgsql
        "UseNpgsql", "AddEntityFrameworkNpgsql", "HasPostgresExtension", "HasMethod", "HasOperators", "HasNullSortOrder",
        "IsCreatedConcurrently", "AreNullsDistinct", "HasStorageParameter", "WithoutOverlaps", "WithPeriod",
        // SQL Server
        "IsClustered", "IsCreatedOnline", "SortInTempDb", "UseDataCompression",
        // The BCL
        "ToLower",
    };

    /// <summary>
    /// What <c>EFCore.ComplexIndexes</c> 5.x called, where the guide says in a sentence what became
    /// of it. Its tables name 5.x's API in a column of their own, which is not read
    /// (<see cref="Mentions" />). Nothing here has 5.x's assemblies, so these names are held by
    /// review alone: add one only for a sentence about 5.x.
    /// </summary>
    private static readonly HashSet<string> ComplexIndexes5x = new(StringComparer.Ordinal) { "AddNpgsqlComplexIndexes", "WithExpression" };

    /// <summary>A call as a sentence writes it: <c>UseSchemaKit()</c>, <c>GetDeclarations&lt;T&gt;()</c>.</summary>
    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9]*)(?:<[^<>()]*>)?\(")]
    private static partial Regex Invocation();

    /// <summary>A member named with its type: <c>Deferral.InitiallyDeferred</c>, <c>KeyTarget.Column</c>.</summary>
    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9]*)\.([A-Z][A-Za-z0-9]*)\b")]
    private static partial Regex MemberOfType();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex InlineCode();

    /// <summary>A <c>|</c> that separates two cells: one in a cell is written <c>\|</c>.</summary>
    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellSeparator();

    /// <summary>The shipped assemblies, one per project under <c>src/</c>, by the package's id.</summary>
    private static IReadOnlyDictionary<string, Assembly> Packages { get; } = Load();

    private static Dictionary<string, Assembly> Load()
    {
        var packages = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        foreach (var id in Directory.EnumerateDirectories(Path.Combine(Markdown.Root, "src")).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
        {
            try
            {
                packages[id] = Assembly.Load(new AssemblyName(id));
            }
            catch (FileNotFoundException)
            {
                Assert.Fail(
                    $"src/{id} is a package this test project does not reference, so what its pages name cannot be checked. "
                    + "Reference it here, or move the documentation tests to a project that references every package.");
            }
        }

        return packages;
    }

    [TestMethod(DisplayName = "Every API a page names in its prose exists")]
    public void Every_api_a_page_names_in_its_prose_exists()
    {
        var own = Packages.Values.SelectMany(PublicNames).ToHashSet(StringComparer.Ordinal);
        var types = Packages.Values.SelectMany(assembly => assembly.GetExportedTypes())
            .GroupBy(type => NameOf(type), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var missing = new List<string>();
        var external = new HashSet<string>(StringComparer.Ordinal);
        var read = 0;

        foreach (var page in Pages())
        foreach (var span in Mentions(page))
        {
            foreach (Match call in Invocation().Matches(span))
            {
                read++;
                var name = call.Groups[1].Value;
                if (own.Contains(name)) continue;

                if (ExternalApi.Contains(name) || ComplexIndexes5x.Contains(name))
                    external.Add(name);
                else
                    missing.Add($"{Markdown.Relative(page)} → {name}()");
            }

            foreach (Match member in MemberOfType().Matches(span))
            {
                // Only where the type is one of ours: `x.Period` and `EFCore.ComplexIndexes` have the same shape.
                if (!types.TryGetValue(member.Groups[1].Value, out var candidates)) continue;

                read++;
                if (!candidates.Any(type => type.GetMember(member.Groups[2].Value, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy).Length > 0))
                    missing.Add($"{Markdown.Relative(page)} → {member.Value}");
            }
        }

        // The premise: the pages are found and their inline code is read.
        Assert.IsTrue(read > 20, $"Only {read} names were read from the pages' prose: the pages or their inline code are no longer found.");

        Assert.AreEqual(
            0, missing.Distinct().Count(),
            "These pages name API that does not exist. It was renamed and the sentence was not, or the name is mistyped; "
            + "either reads as a method that works until someone types it. A call of EF Core, Npgsql or the BCL belongs in "
            + $"{nameof(ExternalApi)}:\n{string.Join("\n", missing.Distinct())}");

        // A name that is let through and that no page names any more would let the next mistyped one through.
        var unused = ExternalApi.Concat(ComplexIndexes5x).Except(external).Order(StringComparer.Ordinal).ToList();

        Assert.AreEqual(
            0, unused.Count,
            $"{string.Join(", ", unused)} is let through as another library's API, and no page names it, or SchemaKit has it now. Take it off the list.");
    }

    [TestMethod(DisplayName = "A package's pages name no API that only another provider's package has")]
    public void A_packages_pages_name_no_api_of_another_providers_package()
    {
        var misplaced = new List<string>();
        var scoped = 0;

        // What only one package declares, once every other package's names are taken off.
        var exclusive = Packages.ToDictionary(
            package => package.Key,
            package => PublicNames(package.Value)
                .Except(Packages.Where(other => other.Key != package.Key).SelectMany(other => PublicNames(other.Value)))
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var provider in Packages.Keys.Where(id => id != Kernel))
            Assert.AreNotEqual(0, exclusive[provider].Count, $"{provider} has no public name of its own, so no page could be found naming one.");

        foreach (var page in Pages())
        {
            if (ScopeOf(page) is not { } scope) continue;

            scoped++;

            // The kernel is every package's dependency: its API is there for a reader of any page.
            var foreign = Packages.Keys.Where(id => id != scope && id != Kernel).ToList();

            foreach (var span in Mentions(page))
            foreach (Match call in Invocation().Matches(span))
            foreach (var other in foreign.Where(id => exclusive[id].Contains(call.Groups[1].Value)))
                misplaced.Add($"{Markdown.Relative(page)} → {call.Groups[1].Value}(), which only {other} has");
        }

        Assert.AreNotEqual(0, scoped, "No page was found that belongs to one package, so none was checked.");

        Assert.AreEqual(
            0, misplaced.Distinct().Count(),
            "These pages name another package's API. A reader of the page cannot call it with the package the page is about:\n"
            + string.Join("\n", misplaced.Distinct()));
    }

    /// <summary>
    /// The package a page is about, or null for a page about all of them. A package's README is
    /// its own. A page under <c>docs/</c> belongs to a provider's package when its file name holds
    /// the provider's name (<c>postgresql</c>, <c>sqlserver</c>); the root README and a guide named
    /// after neither tell of every package.
    /// </summary>
    private static string? ScopeOf(string page)
    {
        foreach (var id in Packages.Keys)
            if (page == Path.Combine(Markdown.Root, "src", id, "README.md"))
                return id;

        if (!Markdown.Relative(page).StartsWith("docs/", StringComparison.Ordinal)) return null;

        var name = Path.GetFileNameWithoutExtension(page);
        return Packages.Keys.FirstOrDefault(id => id != Kernel && name.Contains(id[(Kernel.Length + 1)..], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a consumer reads: the root README, the guides under <c>docs/</c> and each package's README.</summary>
    private static IEnumerable<string> Pages()
        => Markdown.Pages().Where(page =>
        {
            var relative = Markdown.Relative(page);
            return relative == "README.md" || relative.StartsWith("docs/", StringComparison.Ordinal) || relative.StartsWith("src/", StringComparison.Ordinal);
        });

    /// <summary>
    /// The inline code of a page's prose: every <c>`span`</c> outside a fenced block. The
    /// <c>csharp</c> blocks are compiled, the <c>csharp 5.x</c> ones are 5.x's code, and the rest is
    /// SQL and shell. In a table whose first column is headed <c>5.x</c> that column is 5.x's API
    /// and is left out; what the row says became of it is read.
    /// </summary>
    private static IEnumerable<string> Mentions(string page)
    {
        var fenced = false;
        var table5x = false;

        foreach (var line in File.ReadLines(page))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced) continue;

            var prose = line;

            if (line.StartsWith('|'))
            {
                var cells = CellSeparator().Split(line);
                if (cells.Length > 1 && cells[1].Trim() == "5.x") table5x = true;
                if (table5x) prose = string.Join('|', cells.Skip(2));
            }
            else
            {
                table5x = false;
            }

            foreach (Match span in InlineCode().Matches(prose))
                yield return span.Groups[1].Value;
        }
    }

    private static string NameOf(Type type) => type.Name.Split('`')[0];

    /// <summary>The names an assembly makes public: its types, nested ones too, and what each declares.</summary>
    private static HashSet<string> PublicNames(Assembly assembly)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetExportedTypes())
        {
            names.Add(NameOf(type));

            // DeclaredOnly: an inherited member is the declaring assembly's.
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                names.Add(member.Name.Split('`')[0]);
        }

        return names;
    }
}
