using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// Holds the build workflow's <c>CI</c> gate to the one rule the protection of <c>main</c> rests
/// on: it sees every lane, and it passes only when each of them succeeded. Ported from
/// <c>EFCore.ComplexIndexes</c>.
/// </summary>
/// <remarks>
/// <c>CI</c> is the one check to require and no lane is required by name, because GitHub reports a
/// skipped job as Success even on a required check. That moves the guard into the workflow, where
/// nothing else notices it decay: a lane left out of the gate's <c>needs</c> runs, fails, and the
/// pull request merges; and a gate without <c>if: always()</c> is itself skipped when a lane
/// fails, which the protection reads as Success. <c>release.yml</c> and <c>nightly.yml</c> call
/// this workflow and rely on the same gate.
/// </remarks>
[TestClass]
public sealed class WorkflowGateConventionTests
{
    /// <summary>The build workflow, which every push and pull request runs.</summary>
    private const string BuildWorkflow = "dotnet.yml";

    /// <summary>The gate's job id.</summary>
    private const string GateJob = "ci";

    /// <summary>The gate's name: the context to require on <c>main</c>.</summary>
    private const string GateContext = "CI";

    private static WorkflowFile Build => WorkflowFile.Load(BuildWorkflow);

    [TestMethod(DisplayName = "The build workflow's gate reports under the context that is required")]
    public void The_gate_reports_under_the_required_context()
    {
        var workflow = Build;

        Assert.IsTrue(workflow.Jobs.ContainsKey(GateJob), $"{BuildWorkflow} has no `{GateJob}` job, the gate.");

        Assert.AreEqual(
            GateContext, workflow.Key(GateJob, "name"),
            $"The `{GateJob}` job no longer reports as '{GateContext}'. That context is the one required on main; "
            + "a renamed gate leaves it required and never reported, which hangs every pull request instead of failing it.");

        var others = workflow.Jobs.Keys.Where(job => job != GateJob && workflow.Key(job, "name") == GateContext).ToList();

        Assert.AreEqual(0, others.Count, $"{string.Join(", ", others)} reports as '{GateContext}' too, so the required context no longer names the gate alone.");
    }

    [TestMethod(DisplayName = "The gate runs whatever happened to the lanes")]
    public void The_gate_runs_whatever_happened_to_the_lanes()
    {
        var condition = Build.Key(GateJob, "if");

        Assert.IsTrue(
            condition is "always()" or "${{ always() }}",
            $"The `{GateJob}` job's condition is '{condition ?? "(none)"}', not always(). Without it the gate is "
            + "skipped when a lane fails, and GitHub reports a skipped required check as Success.");
    }

    [TestMethod(DisplayName = "Every lane of the build workflow is in the gate's needs")]
    public void Every_lane_is_in_the_gates_needs()
    {
        var workflow = Build;
        var needs = workflow.Needs(GateJob);
        var lanes = workflow.Jobs.Keys.Where(job => job != GateJob).ToList();
        var missing = lanes.Where(job => !needs.Contains(job)).ToList();

        Assert.AreNotEqual(0, lanes.Count, $"{BuildWorkflow} has no job but the gate, so the gate passes on nothing.");
        Assert.AreEqual(
            0, missing.Count,
            $"{string.Join(", ", missing)} is not in the `{GateJob}` job's needs. Only '{GateContext}' is required, "
            + "so a lane the gate does not need can fail and the pull request still merges.");
    }

    /// <summary>
    /// The gate reads <c>needs</c> whole, so a lane added there is checked without a second list
    /// to keep in step, and it takes <c>success</c> alone: skipped and cancelled are what a
    /// required check cannot tell from a pass, which is the reason the gate exists.
    /// </summary>
    [TestMethod(DisplayName = "The gate fails on any result but success, for every job it needs")]
    public void The_gate_fails_on_any_result_but_success()
    {
        var script = Build.Script(GateJob);

        StringAssert.Contains(
            script, "toJSON(needs)",
            $"The `{GateJob}` job does not read the results from `toJSON(needs)`, so a lane added to its needs "
            + "would not be checked without a second edit.");

        StringAssert.Contains(
            script, "!= \"success\"",
            $"The `{GateJob}` job does not fail on every result other than success. A skipped or a cancelled lane must fail it.");
    }

    /// <summary>
    /// The gate's script, run with the results a runner hands it: the two tests above match its
    /// text, and a comparison that is written and never reached matches too.
    /// </summary>
    [TestMethod(DisplayName = "The gate's script passes on success alone, and not on an empty needs")]
    [DataRow("""{"test":{"result":"success"},"probes":{"result":"success"}}""", 0)]
    [DataRow("""{"test":{"result":"success"},"probes":{"result":"failure"}}""", 1)]
    [DataRow("""{"test":{"result":"success"},"probes":{"result":"skipped"}}""", 1)]
    [DataRow("""{"test":{"result":"cancelled"},"probes":{"result":"success"}}""", 1)]
    [DataRow("{}", 1)]
    public void The_gates_script_passes_on_success_alone(string needs, int expected)
    {
        var folder = Directory.CreateTempSubdirectory("schemakit-gate-").FullName;

        try
        {
            var result = Bash.Run(
                Build.Run(GateJob, "Every lane succeeded"),
                folder,
                new Dictionary<string, string> { ["NEEDS"] = needs, ["GITHUB_STEP_SUMMARY"] = Path.Combine(folder, "summary.md") });

            Assert.AreEqual(expected, result.ExitCode, $"needs = {needs}\n{result}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
