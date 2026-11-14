namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Documentation;

/// <summary>
/// The links of the pages a reader is sent to. A broken link renders like any other, so nothing but
/// a test notices a page that was moved or a heading that was reworded. The idea is
/// <c>EFCore.ComplexIndexes</c>' <c>DocumentationLinkTests</c>.
/// </summary>
[TestClass]
public sealed class DocumentationLinkTests
{
    private const string Repository = "https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/";

    /// <summary>A file or a folder on <c>main</c>. Issues, pull requests and the rest are GitHub's to resolve.</summary>
    private static readonly string[] OnMain = [Repository + "blob/main/", Repository + "tree/main/"];

    private static bool IsAbsolute(string target) => Uri.IsWellFormedUriString(target, UriKind.Absolute);

    [TestMethod]
    public void A_relative_link_names_a_file_that_exists_and_a_heading_it_has()
    {
        var broken = new List<string>();

        foreach (var page in Markdown.Pages())
            foreach (var target in Markdown.LinkTargets(page).Where(target => !IsAbsolute(target)))
                if (Resolve(Path.GetDirectoryName(page)!, target, page) is { } problem)
                    broken.Add($"{Markdown.Relative(page)} → {target} ({problem})");

        Assert.AreEqual(0, broken.Count, string.Join("\n", broken));
    }

    /// <summary>
    /// The changelog and the package READMEs link into the repository with absolute links, which a
    /// reader follows from nuget.org or a release page: the test above does not see them.
    /// </summary>
    [TestMethod]
    public void An_absolute_link_to_a_file_on_main_names_a_file_that_exists_and_a_heading_it_has()
    {
        var broken = new List<string>();

        foreach (var page in Markdown.Pages())
            foreach (var target in Markdown.LinkTargets(page))
            {
                var path = OnMain.Select(prefix => target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : null).FirstOrDefault(p => p is not null);
                if (path is not null && Resolve(Markdown.Root, path, page) is { } problem)
                    broken.Add($"{Markdown.Relative(page)} → {target} ({problem})");
            }

        Assert.AreEqual(0, broken.Count, string.Join("\n", broken));
    }

    /// <summary>nuget.org shows a package's README with nothing to resolve a relative link against.</summary>
    [TestMethod]
    public void A_package_readme_links_with_absolute_links_only()
    {
        var relative = Markdown.PackageReadmes()
            .SelectMany(readme => Markdown.LinkTargets(readme).Where(target => !IsAbsolute(target)).Select(target => $"{Markdown.Relative(readme)} → {target}"))
            .ToList();

        Assert.AreEqual(0, relative.Count, string.Join("\n", relative));
    }

    /// <summary>The premise of the tests above: the pages are found and their links are read.</summary>
    [TestMethod]
    public void The_pages_and_their_links_are_found()
    {
        var pages = Markdown.Pages().Select(Markdown.Relative).ToList();

        CollectionAssert.IsSubsetOf(
            new[] { "README.md", "CHANGELOG.md", "docs/moving-from-complexindexes-5x.md", "src/CodoMetis.EFCore.SchemaKit/README.md", "src/CodoMetis.EFCore.SchemaKit.PostgreSQL/README.md" },
            pages);
        Assert.IsTrue(Markdown.LinkTargets(Path.Combine(Markdown.Root, "README.md")).Any(target => !IsAbsolute(target)));
        Assert.IsTrue(Markdown.LinkTargets(Path.Combine(Markdown.Root, "docs", "moving-from-complexindexes-5x.md")).Any(target => target.StartsWith('#')));
    }

    /// <summary>What is wrong with a link to <paramref name="target" /> from <paramref name="directory" />, or null.</summary>
    private static string? Resolve(string directory, string target, string page)
    {
        var parts = target.Split('#', 2);
        var file = parts[0].Length == 0 ? page : Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(parts[0])));

        if (!File.Exists(file) && !Directory.Exists(file)) return "no such file";
        if (parts.Length == 2 && file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Markdown.Anchors(file).Contains(parts[1]))
            return "no such heading";
        return null;
    }
}
