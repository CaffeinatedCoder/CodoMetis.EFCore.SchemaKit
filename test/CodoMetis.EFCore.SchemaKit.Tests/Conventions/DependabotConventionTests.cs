using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// Holds that Dependabot sees every test project's packages (#236). Two of four had no entry, and
/// their test dependencies drifted from the kernel tests' without a pull request saying so.
/// </summary>
/// <remarks>
/// A test project is a project file directly under a folder of <c>test/</c>. What is deeper, the
/// smoke test's consumers and the 5.x fixture, pins what it measures and has no entry on purpose.
/// </remarks>
[TestClass]
public sealed class DependabotConventionTests
{
    private static readonly Regex TestProject = new(@"^test/[^/]+/[^/]+\.csproj$");

    [TestMethod(DisplayName = "Every test project with a package reference has a NuGet entry in dependabot.yml")]
    public void Every_test_project_has_a_dependabot_entry()
    {
        var directories = NuGetDirectories(File.ReadAllLines(Path.Combine(RepositoryLayout.Root, ".github", "dependabot.yml")));

        var projects = RepositoryLayout.Files()
            .Where(file => TestProject.IsMatch(RepositoryLayout.Relative(file)))
            .Where(file => File.ReadAllText(file).Contains("<PackageReference", StringComparison.Ordinal))
            .Select(file => "/" + RepositoryLayout.Relative(Path.GetDirectoryName(file)!))
            .ToList();
        var missing = projects.Where(folder => !directories.Any(pattern => Covers(pattern, folder))).ToList();

        // A pattern that finds no project would hold nothing: the four that are there are counted.
        Assert.IsGreaterThanOrEqualTo(4, projects.Count, "Fewer test projects with package references than test/ holds: " + string.Join(", ", projects));

        Assert.IsEmpty(missing, "No NuGet entry in .github/dependabot.yml for " + string.Join(", ", missing) + ".");
    }

    // The NuGet entries' `directory:` and the items of their `directories:`. The file is ours and
    // written one way, so a reader of that way is enough; an entry it cannot read is a miss.
    private static List<string> NuGetDirectories(string[] lines)
    {
        var found = new List<string>();
        var nuget = false;
        var inList = false;
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].TrimEnd();
            var text = line.Trim();
            if (text.StartsWith("- package-ecosystem:", StringComparison.Ordinal))
            {
                nuget = text.EndsWith("nuget", StringComparison.Ordinal);
                inList = false;
            }
            else if (!nuget) { }
            else if (text.StartsWith("directory:", StringComparison.Ordinal))
                found.Add(text["directory:".Length..].Trim());
            else if (text == "directories:")
                inList = true;
            else if (inList && text.StartsWith("- ", StringComparison.Ordinal))
                found.Add(text[2..].Trim());
            else if (text.Length > 0)
                inList = false;
        }

        return found;
    }

    // Dependabot's patterns: a `*` is one folder.
    private static bool Covers(string pattern, string folder)
        => Regex.IsMatch(folder, "^" + Regex.Escape(pattern.TrimEnd('/')).Replace(@"\*", "[^/]+") + "$");
}
