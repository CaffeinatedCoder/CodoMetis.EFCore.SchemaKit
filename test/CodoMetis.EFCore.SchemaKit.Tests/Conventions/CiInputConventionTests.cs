using System.Text.Json;
using System.Text.RegularExpressions;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// Holds the inputs of CI that change without a diff to their two answers: the SDK is pinned by
/// <c>global.json</c>, and what still floats is run every night, with a failure reported. Ported
/// from <c>EFCore.ComplexIndexes</c>.
/// </summary>
/// <remarks>
/// Both decay without a sign. A job that goes back to <c>dotnet-version: 11.0.x</c> builds with
/// whatever band the runner has that day, and passes; a nightly run whose report no longer sees
/// one of its parts stays green while that part fails every night.
/// </remarks>
[TestClass]
public sealed class CiInputConventionTests
{
    private const string NightlyWorkflow = "nightly.yml";

    private const string BuildWorkflow = "dotnet.yml";

    private const string ReportJob = "report";

    /// <summary>
    /// The pin CLAUDE.md states: the first release of a feature band, its patches let through, and
    /// no prerelease SDK. A machine with only a prerelease SDK cannot build here, on purpose.
    /// </summary>
    [TestMethod(DisplayName = "global.json pins the SDK to a feature band and takes no prerelease")]
    public void Global_json_pins_the_sdk_to_a_feature_band()
    {
        var path = Path.Combine(RepositoryLayout.Root, "global.json");

        Assert.IsTrue(File.Exists(path), "No global.json at the repository's root, so CI builds with whichever SDK the runner has.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var sdk = document.RootElement.GetProperty("sdk");

        StringAssert.Matches(
            sdk.GetProperty("version").GetString(), new Regex(@"^\d+\.\d+\.\d00$"),
            "global.json's sdk.version is not the first release of a feature band (x.y.z00). With rollForward "
            + "latestPatch, that is what pins the band and lets its patches through.");

        Assert.AreEqual(
            "latestPatch", sdk.TryGetProperty("rollForward", out var rollForward) ? rollForward.GetString() : null,
            "global.json's rollForward is not latestPatch. A wider one lets a new feature band, with its compiler "
            + "and its NuGet, arrive without a pull request.");

        Assert.IsTrue(
            sdk.TryGetProperty("allowPrerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.False,
            "global.json's allowPrerelease is not false. Without it a prerelease SDK of the band satisfies the pin, "
            + "and what a contributor builds with is not what CI builds with.");
    }

    [TestMethod(DisplayName = "Every job that builds the repository takes its SDK from global.json")]
    public void Every_job_that_builds_the_repository_takes_its_sdk_from_global_json()
    {
        var floating = new List<string>();
        var building = 0;

        foreach (var fileName in WorkflowFile.All())
        {
            var workflow = WorkflowFile.Load(fileName);

            foreach (var job in workflow.Jobs.Keys)
            {
                var script = workflow.Script(job);

                // A job with no checkout has no global.json to read: release.yml's `publish` pushes
                // what `verify` built.
                if (!script.Contains("actions/setup-dotnet@", StringComparison.Ordinal)
                    || !script.Contains("actions/checkout@", StringComparison.Ordinal))
                    continue;

                building++;

                if (!script.Contains("global-json-file: global.json", StringComparison.Ordinal)
                    || script.Contains("dotnet-version:", StringComparison.Ordinal))
                    floating.Add($"{fileName}: {job}");
            }
        }

        Assert.AreNotEqual(0, building, "No job that checks the repository out and sets .NET up was found, so none was checked.");
        Assert.AreEqual(
            0, floating.Count,
            $"{string.Join(", ", floating)} checks the repository out and does not set .NET up from global.json alone, "
            + "so it builds with an SDK that can change under it overnight.");
    }

    /// <summary>
    /// Every action is used at a commit, with its version in a comment behind it. A tag can be
    /// moved, and then the action a workflow runs has changed with no diff here; in
    /// <c>release.yml</c> that is the job that can mint a token. The comment is what Dependabot
    /// reads to propose the next commit. A workflow of this repository that is called
    /// (<c>./.github/workflows/dotnet.yml</c>) is at the commit that calls it.
    /// </summary>
    [TestMethod(DisplayName = "Every action is used at a commit, with its version in a comment")]
    public void Every_action_is_used_at_a_commit()
    {
        var used = WorkflowFile.All()
            .SelectMany(fileName => File.ReadLines(Path.Combine(RepositoryLayout.Workflows, fileName))
                .Select((line, index) => (fileName, Number: index + 1, Uses: Regex.Match(line, @"^\s*(?:- )?uses:\s*(?<action>\S+)(?<rest>.*)$")))
                .Where(use => use.Uses.Success && !use.Uses.Groups["action"].Value.StartsWith("./", StringComparison.Ordinal)))
            .ToList();

        Assert.AreNotEqual(0, used.Count, "No action was found in any workflow, so none was read.");

        var floating = used
            .Where(use => !Regex.IsMatch(use.Uses.Groups["action"].Value, @"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")
                          || !Regex.IsMatch(use.Uses.Groups["rest"].Value, @"^\s+# v\d+(?:\.\d+)*\s*$"))
            .Select(use => $"{use.fileName}:{use.Number}: {use.Uses.Groups["action"].Value}{use.Uses.Groups["rest"].Value}")
            .ToList();

        Assert.AreEqual(
            0, floating.Count,
            "These actions are not used as `owner/name@<40 hex digits> # vX.Y.Z`. A tag or a branch can move under the "
            + "workflow, and without the comment Dependabot proposes nothing:\n" + string.Join("\n", floating));
    }

    [TestMethod(DisplayName = "The nightly run calls every lane on a schedule")]
    public void The_nightly_run_calls_every_lane_on_a_schedule()
    {
        var nightly = WorkflowFile.Load(NightlyWorkflow);

        Assert.IsTrue(Regex.IsMatch(nightly.Triggers, @"(?m)^  schedule:\s*\n\s*- cron:"), $"{NightlyWorkflow} has no schedule.");

        Assert.IsTrue(
            nightly.Jobs.Keys.Any(job => nightly.Key(job, "uses") == $"./.github/workflows/{BuildWorkflow}"),
            $"No job of {NightlyWorkflow} calls ./.github/workflows/{BuildWorkflow}, so the nightly run runs neither "
            + "the lanes nor their gate.");

        Assert.IsTrue(
            Regex.IsMatch(WorkflowFile.Load(BuildWorkflow).Triggers, @"(?m)^  workflow_call:"),
            $"{BuildWorkflow} has no `workflow_call` trigger, so {NightlyWorkflow} cannot call it.");
    }

    /// <summary>
    /// The report is the nightly run's gate: it has to see every part and run whatever they did,
    /// or a part that fails every night is reported by nobody.
    /// </summary>
    [TestMethod(DisplayName = "The nightly run reports a failure of any of its parts")]
    public void The_nightly_run_reports_a_failure_of_any_of_its_parts()
    {
        var nightly = WorkflowFile.Load(NightlyWorkflow);

        Assert.IsTrue(nightly.Jobs.ContainsKey(ReportJob), $"{NightlyWorkflow} has no `{ReportJob}` job.");

        var needs = nightly.Needs(ReportJob);
        var parts = nightly.Jobs.Keys.Where(job => job != ReportJob).ToList();
        var missing = parts.Where(job => !needs.Contains(job)).ToList();

        Assert.AreNotEqual(0, parts.Count, $"{NightlyWorkflow} has no job but `{ReportJob}`, so there is nothing to report on.");
        Assert.AreEqual(0, missing.Count, $"{string.Join(", ", missing)} is not in the `{ReportJob}` job's needs, so its failures are reported by nobody.");

        Assert.IsTrue(
            nightly.Key(ReportJob, "if") is "always()" or "${{ always() }}",
            $"The `{ReportJob}` job does not run `if: always()`, so it is skipped exactly when a part fails.");

        var script = nightly.Script(ReportJob);

        StringAssert.Contains(script, "toJSON(needs)", $"The `{ReportJob}` job does not read every result from `toJSON(needs)`.");
        StringAssert.Matches(script, new Regex(@"(?m)^\s*issues:\s*write\b"), $"The `{ReportJob}` job cannot open the issue it reports with.");
    }

    /// <summary>
    /// The report's script, run with the results a runner hands it and a <c>gh</c> that writes
    /// down what it is called with: every result but success opens the issue, or comments on the one
    /// that is open, and fails the run.
    /// </summary>
    [TestMethod(DisplayName = "The nightly report opens an issue for every result but success, and comments on an open one")]
    [DataRow("""{"main":{"result":"success"}}""", "", 0, "")]
    [DataRow("""{"main":{"result":"failure"}}""", "", 1, "issue create")]
    [DataRow("""{"main":{"result":"cancelled"}}""", "", 1, "issue create")]
    [DataRow("""{"main":{"result":"skipped"}}""", "", 1, "issue create")]
    [DataRow("""{"main":{"result":"failure"}}""", "7", 1, "issue comment 7")]
    public void The_nightly_report_opens_an_issue_for_every_result_but_success(string needs, string openIssue, int expected, string call)
    {
        var folder = Directory.CreateTempSubdirectory("schemakit-nightly-").FullName;

        try
        {
            // A `gh` of the test's own, defined ahead of the step's lines: `issue list` answers
            // with the open issue's number, every other call is written down.
            const string gh = """
                gh() { if [[ "$1 $2" == "issue list" ]]; then printf '%s' "$OPEN_ISSUE"; else echo "$@" >> "$GH_CALLS"; fi; }

                """;

            var calls = Path.Combine(folder, "calls.txt");
            var result = WorkflowFile.Load(NightlyWorkflow).RunStep(
                ReportJob, "Open or update the nightly failure issue", folder,
                new Dictionary<string, string>
                {
                    ["NEEDS"] = needs,
                    ["OPEN_ISSUE"] = openIssue,
                    ["GH_CALLS"] = calls,
                    ["RUN_URL"] = "https://example.invalid/run",
                },
                before: gh);

            var made = File.Exists(calls) ? File.ReadAllText(calls) : "";

            Assert.AreEqual(expected, result.ExitCode, $"needs = {needs}\n{result}");
            if (call.Length == 0)
                Assert.AreEqual("", made, "Every part succeeded, and the report called gh.");
            else
                StringAssert.StartsWith(made, call, $"needs = {needs}\n{result}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
