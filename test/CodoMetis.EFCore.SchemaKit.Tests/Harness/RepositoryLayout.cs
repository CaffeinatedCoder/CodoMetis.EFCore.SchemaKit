namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>Finds the checkout from the test's output folder, for the tests that read the repository.</summary>
internal static class RepositoryLayout
{
    public static string Root { get; } = Find();

    public static string Source => Path.Combine(Root, "src");

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;

        throw new InvalidOperationException($"No repository root (global.json beside src/) above {AppContext.BaseDirectory}.");
    }
}
