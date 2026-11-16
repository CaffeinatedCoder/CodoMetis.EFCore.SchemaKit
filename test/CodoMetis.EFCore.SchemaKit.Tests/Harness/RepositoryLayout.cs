using System.Diagnostics;
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
    /// Every file of the repository: what git tracks, and is on the disk. Not what the folder
    /// holds: a build output, a file of the contributor's own that was never added, and another
    /// worktree under <c>.claude/worktrees</c>, which is a whole copy of the repository, are on
    /// the disk and in no commit, so a test that read them would answer otherwise here than in CI.
    /// A file that is new has to be added (<c>git add</c>) before a test sees it.
    /// </summary>
    public static IReadOnlyList<string> Files() => Tracked.Value;

    private static readonly Lazy<IReadOnlyList<string>> Tracked = new(() =>
    {
        var start = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"`git ls-files` failed in {Root}, so the repository's files are not known:{Environment.NewLine}{error.Result}");

        return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(file => Path.Combine(Root, file.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)];
    });

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
