using System.Xml.Linq;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>Finds the checkout from the test's output folder, for the tests that read the repository.</summary>
internal static class RepositoryLayout
{
    public static string Root { get; } = Find();

    public static string Source => Path.Combine(Root, "src");

    public static string Changelog => Path.Combine(Root, "CHANGELOG.md");

    public static string BuildProps => Path.Combine(Root, "Directory.Build.props");

    public static string SecurityPolicy => Path.Combine(Root, "SECURITY.md");

    public static string Workflows => Path.Combine(Root, ".github", "workflows");

    /// <summary>The version every package ships at: the one <c>Directory.Build.props</c> states.</summary>
    public static ReleaseVersion ShippedVersion
        => ReleaseVersion.Parse(XDocument.Load(BuildProps).Descendants("Version").Single().Value);

    /// <summary>The project file of each package: one per folder under <c>src/</c>, named after the package's id.</summary>
    public static IReadOnlyList<string> ShippingProjects()
        => [.. Directory.EnumerateDirectories(Source)
            .Select(directory => Path.Combine(directory, Path.GetFileName(directory) + ".csproj"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every file of the checkout that is no build output: nothing under <c>bin</c>, <c>obj</c>,
    /// <c>.git</c>, <c>.idea</c>, <c>TestResults</c> or <c>dist</c>, and nothing of another
    /// worktree, which holds a copy of every file and would answer for one that is gone here.
    /// </summary>
    public static IEnumerable<string> Files()
    {
        var pending = new Stack<string>([Root]);

        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                yield return file;

            foreach (var child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not ("bin" or "obj" or ".git" or ".idea" or "TestResults" or "dist" or "worktrees"))
                    pending.Push(child);
        }
    }

    /// <summary>A path below the root as the messages write it, with forward slashes.</summary>
    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;

        throw new InvalidOperationException($"No repository root (global.json beside src/) above {AppContext.BaseDirectory}.");
    }
}
