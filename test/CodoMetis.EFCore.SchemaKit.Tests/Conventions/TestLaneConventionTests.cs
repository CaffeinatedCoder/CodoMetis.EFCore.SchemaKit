using System.Xml.Linq;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// Holds that CI runs every test project of the solution. <c>dotnet.yml</c> names the projects
/// of each lane, so that a project that ran no test fails its step; the other side of that is a
/// project nobody added to the list, whose tests pass on a laptop and are run by no lane.
/// </summary>
/// <remarks>
/// The steps are run, with a command in <c>dotnet</c>'s place that records what it was called
/// with: a list read from the text would be satisfied by a name in a comment.
/// </remarks>
[TestClass]
public sealed class TestLaneConventionTests
{
    private static readonly WorkflowFile Build = WorkflowFile.Load("dotnet.yml");

    // Says what it was called with, and what a run that passed a test says: the steps read that back.
    private const string Recorder = "dotnet() { echo \"$*\" >> \"$CALLS\"; echo 'Passed:     1'; }\n";

    private static List<string> Calls(string job, string step)
    {
        var folder = Directory.CreateTempSubdirectory("schemakit-lanes-").FullName;

        try
        {
            var calls = Path.Combine(folder, "calls.log");
            var result = Build.RunStep(job, step, folder, new Dictionary<string, string> { ["CALLS"] = calls }, Recorder);

            Assert.AreEqual(0, result.ExitCode, $"The step '{step}' of `{job}` failed with dotnet replaced:\n{result}");
            return [.. File.ReadAllLines(calls)];
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The test projects of the solution, as paths from the root.</summary>
    private static List<string> TestProjects()
    {
        var projects = XDocument.Load(Path.Combine(RepositoryLayout.Root, "CodoMetis.EFCore.SchemaKit.slnx"))
            .Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value)
            .Where(path => path.StartsWith("test/", StringComparison.Ordinal))
            .ToList();

        // A pattern that finds no project would hold nothing: the four that are there are counted.
        Assert.IsGreaterThanOrEqualTo(4, projects.Count, "Fewer test projects than the solution holds: " + string.Join(", ", projects));
        return projects;
    }

    [TestMethod(DisplayName = "Every test project of the solution is run by the test lane, without its integration tests")]
    public void Every_test_project_of_the_solution_is_run_by_the_test_lane()
    {
        var calls = Calls("test", "Test");

        var missing = TestProjects()
            .Where(project => !calls.Any(call => call.StartsWith("test ", StringComparison.Ordinal)
                && call.Contains(project, StringComparison.Ordinal)
                && call.Contains("TestCategory!=Integration", StringComparison.Ordinal)))
            .ToList();

        Assert.IsEmpty(missing, "The `test` job of dotnet.yml does not run " + string.Join(", ", missing) + ". It ran:\n" + string.Join("\n", calls));
    }

    [TestMethod(DisplayName = "Every test project with integration tests is run by a lane that has a server")]
    public void Every_test_project_with_integration_tests_is_run_by_an_integration_lane()
    {
        var calls = Calls("integration", "Integration tests").Concat(Calls("integration-sqlserver", "Integration tests")).ToList();

        var withIntegrationTests = TestProjects()
            .Where(project => RepositoryLayout.Files().Any(file =>
                RepositoryLayout.Relative(file).StartsWith(Path.GetDirectoryName(project)!.Replace('\\', '/') + "/", StringComparison.Ordinal)
                && file.EndsWith(".cs", StringComparison.Ordinal)
                && File.ReadAllText(file).Contains("[TestCategory(\"Integration\")]", StringComparison.Ordinal)))
            .ToList();

        Assert.IsGreaterThanOrEqualTo(4, withIntegrationTests.Count, "Fewer projects with integration tests than there are: " + string.Join(", ", withIntegrationTests));

        var missing = withIntegrationTests
            .Where(project => !calls.Any(call => call.StartsWith("test ", StringComparison.Ordinal)
                && call.Contains(project, StringComparison.Ordinal)
                && call.Contains("TestCategory=Integration", StringComparison.Ordinal)))
            .ToList();

        Assert.IsEmpty(missing, "No integration lane of dotnet.yml runs " + string.Join(", ", missing) + ". They ran:\n" + string.Join("\n", calls));
    }
}
