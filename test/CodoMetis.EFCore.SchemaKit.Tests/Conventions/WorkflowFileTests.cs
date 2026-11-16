using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// <see cref="WorkflowFile" /> is what every rule about a workflow reads through. A job it does not
/// see is a job no rule is asked of, and the tests that ask stay green: its first version missed a
/// job whose key had a comment behind it, and with it a lane left out of the gate.
/// </summary>
[TestClass]
public sealed class WorkflowFileTests
{
    private const string Workflow = """
        name: Sample

        on:
          push:

        permissions:
          contents: read

        # A comment above the jobs.
        jobs:
          first:
            name: First
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6.1.0
              - name: Build
                run: |
                  echo one | cat

                  echo two

          # A comment between two jobs, at the jobs' own indentation.
          second: # a lane added later
            name: Second # as it shows
            runs-on: ubuntu-latest
            needs: first # the one before
            if: always() # whatever happened
            continue-on-error: true
            steps:
              - name: Told its shell
                shell: bash
                run: |
                  echo three

          "third":
            name: "Third #3"
            runs-on: ubuntu-latest
            needs:
              - first
              - second # both
            permissions:
              issues: write
            steps:
              - run: exit 1

          gate:
            needs: [ first, second, third ]
            steps:
              - run: exit 0
        """;

    private static WorkflowFile Sample(string text = Workflow) => WorkflowFile.Parse("sample.yml", text);

    [TestMethod(DisplayName = "A job is seen with a comment behind its key, and with its key in quotes")]
    public void A_job_is_seen_with_a_comment_behind_its_key_and_with_its_key_in_quotes()
        => CollectionAssert.AreEqual(new[] { "first", "second", "third", "gate" }, Sample().Jobs.Keys.ToList());

    [TestMethod(DisplayName = "A job written on its key's line fails the reader, and is not passed over")]
    public void A_job_written_on_its_keys_line_fails_the_reader()
    {
        var refusal = Assert.ThrowsExactly<AssertFailedException>(
            () => Sample(Workflow + "\n  inline: { runs-on: ubuntu-latest, steps: [ { run: exit 1 } ] }\n"));

        StringAssert.Contains(refusal.Message, "the job `inline` has its body behind its key");
    }

    [TestMethod(DisplayName = "A line where a job's key stands and that is none fails the reader")]
    public void A_line_that_is_no_key_fails_the_reader()
    {
        var refusal = Assert.ThrowsExactly<AssertFailedException>(() => Sample(Workflow + "\n  ? complex\n"));

        StringAssert.Contains(refusal.Message, "stands where a job's key stands");
    }

    [TestMethod(DisplayName = "A job's key is read without the comment behind it, and a quoted value as it is")]
    public void A_jobs_key_is_read_without_the_comment_behind_it()
    {
        var workflow = Sample();

        Assert.AreEqual("Second", workflow.Key("second", "name"));
        Assert.AreEqual("always()", workflow.Key("second", "if"));
        Assert.AreEqual("true", workflow.Key("second", "continue-on-error"));
        Assert.AreEqual("\"Third #3\"", workflow.Key("third", "name"));
        Assert.IsNull(workflow.Key("first", "if"));
    }

    [TestMethod(DisplayName = "Needs are read in each of YAML's spellings, with a comment behind them")]
    public void Needs_are_read_in_each_spelling()
    {
        var workflow = Sample();

        CollectionAssert.AreEqual(Array.Empty<string>(), workflow.Needs("first").ToList());
        CollectionAssert.AreEqual(new[] { "first" }, workflow.Needs("second").ToList());
        CollectionAssert.AreEqual(new[] { "first", "second" }, workflow.Needs("third").ToList());
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, workflow.Needs("gate").ToList());
    }

    [TestMethod(DisplayName = "A step's script is its run block, and its steps stand in their order")]
    public void A_steps_script_is_its_run_block()
    {
        var workflow = Sample();

        Assert.AreEqual("echo one | cat\n\necho two\n", workflow.Run("first", "Build"));
        CollectionAssert.AreEqual(new[] { "actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803", "Build" }, workflow.Steps("first").ToList());
        CollectionAssert.AreEqual(new[] { "third: issues" }, workflow.WritePermissions().ToList());
    }

    /// <summary>
    /// A runner gives a step <c>pipefail</c> only when the step, its job or the workflow says
    /// <c>shell: bash</c>. A test that ran every step with it would run a script the runner does not.
    /// </summary>
    [TestMethod(DisplayName = "A step runs with pipefail only where it is given the shell bash")]
    public void A_step_runs_with_pipefail_only_where_it_is_given_the_shell_bash()
    {
        var workflow = Sample();

        Assert.IsFalse(workflow.Pipefail("first", "Build"), "No shell is stated for the step, its job or the workflow.");
        Assert.IsTrue(workflow.Pipefail("second", "Told its shell"), "The step states its shell.");

        var withDefault = Sample(Workflow.Replace("# A comment above the jobs.", "defaults:\n  run:\n    shell: bash\n", StringComparison.Ordinal));

        Assert.IsTrue(withDefault.Pipefail("first", "Build"), "The workflow states the shell for every step.");

        // The same script, the two ways: the pipe's first command fails, and only pipefail says so.
        const string script = "false | cat\n";
        var folder = Directory.CreateTempSubdirectory("schemakit-shell-").FullName;

        try
        {
            Assert.AreEqual(0, Bash.Run(script, folder, new Dictionary<string, string>(), pipefail: false).ExitCode);
            Assert.AreEqual(1, Bash.Run(script, folder, new Dictionary<string, string>(), pipefail: true).ExitCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod(DisplayName = "Every workflow of the repository is read, and each of its jobs has a body")]
    public void Every_workflow_of_the_repository_is_read()
    {
        var files = WorkflowFile.All();

        CollectionAssert.IsSubsetOf(new[] { "dotnet.yml", "nightly.yml", "pr-hygiene.yml", "release.yml" }, files.ToList());

        foreach (var fileName in files)
        foreach (var (job, body) in WorkflowFile.Load(fileName).Jobs)
            Assert.IsTrue(
                body.Contains("    runs-on:", StringComparison.Ordinal) || body.Contains("    uses:", StringComparison.Ordinal),
                $"{fileName}: the job `{job}` was read with no `runs-on` and no `uses`, so its body went to another job.");
    }
}
