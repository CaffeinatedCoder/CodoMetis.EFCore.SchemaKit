using System.Text;
using System.Text.RegularExpressions;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>
/// The jobs of a GitHub Actions workflow under <c>.github/workflows/</c>, read well enough for the
/// convention tests that hold them to a rule: which jobs exist, what each one needs, its keys, its
/// steps and the script of one of them. Ported from <c>EFCore.ComplexIndexes</c>.
/// </summary>
/// <remarks>
/// <para>
/// No YAML parser. It reads the layout every workflow of this repository has: jobs as keys
/// indented by two spaces under a top-level <c>jobs:</c>, their own keys at four, their steps at
/// six and a step's script at ten.
/// </para>
/// <para>
/// A reader that does not see a job hides it from every rule: the job runs, fails, and nothing here
/// knows it is there. So every line at two spaces under <c>jobs:</c> that is no comment is a job's
/// key, whatever stands behind it: a comment (<c>extra: # a lane added later</c>), quotes round the
/// name, or the job's whole body on the same line. The first two are read; the last, and any line
/// there that is no key at all, fails the test that loads the file, since nothing below could be
/// read. The port's first version found a job with a regular expression that wanted the line to
/// end at the colon, as the one in <c>EFCore.ComplexIndexes</c> does, and passed every test with
/// such a job left out of the gate.
/// </para>
/// </remarks>
internal sealed class WorkflowFile
{
    /// <summary>A line at two spaces that is no comment: a job's key, and what stands behind its colon.</summary>
    private static readonly Regex JobKey =
        new(@"^  (?<name>""[^""]+""|'[^']+'|[A-Za-z0-9_][\w.-]*)[ \t]*:(?<rest>.*)$", RegexOptions.Compiled);

    private static readonly Regex InlineNeeds =
        new(@"(?m)^    needs:[ \t]*\[(?<list>[^\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex ScalarNeeds =
        new(@"(?m)^    needs:[ \t]*(?<job>[A-Za-z_][\w-]*)[ \t]*(?:#.*)?$", RegexOptions.Compiled);

    private static readonly Regex BlockNeeds =
        new(@"(?m)^    needs:[ \t]*(?:#.*)?\n(?<list>(?:^      -[ \t]*[A-Za-z_][\w-]*[ \t]*(?:#.*)?\n)+)", RegexOptions.Compiled);

    // A YAML key at any indentation, and not the word in a comment.
    private static readonly Regex WritePermission = new(@"(?m)^\s*(?<scope>[a-z-]+)\s*:\s*write\b", RegexOptions.Compiled);

    private WorkflowFile(string fileName, string text, string triggers, IReadOnlyDictionary<string, string> jobs)
    {
        FileName = fileName;
        Text = text;
        Triggers = triggers;
        Jobs = jobs;
    }

    public string FileName { get; }

    public string Text { get; }

    /// <summary>Everything above <c>jobs:</c> without its comment lines: the name, <c>on:</c>, the permissions, the defaults.</summary>
    public string Triggers { get; }

    /// <summary>Each job's id, with its body.</summary>
    public IReadOnlyDictionary<string, string> Jobs { get; }

    /// <summary>The file names of every workflow of the repository.</summary>
    public static IReadOnlyList<string> All()
        => [.. Directory.EnumerateFiles(RepositoryLayout.Workflows, "*.yml").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    public static WorkflowFile Load(string fileName)
    {
        var path = Path.Combine(RepositoryLayout.Workflows, fileName);

        Assert.IsTrue(File.Exists(path), $"No .github/workflows/{fileName}.");

        return Parse(fileName, File.ReadAllText(path));
    }

    /// <summary>Reads a workflow from its text. <see cref="Load" /> for a file of the repository.</summary>
    public static WorkflowFile Parse(string fileName, string text)
    {
        text = text.ReplaceLineEndings("\n");

        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^jobs:[ \t]*(?:#.*)?$"));

        Assert.IsTrue(start >= 0, $"{fileName} has no top-level `jobs:` key.");

        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var body = new StringBuilder();

        void Close()
        {
            if (current is not null) jobs[current] = body.ToString();
            body.Clear();
        }

        foreach (var line in lines.Skip(start + 1))
        {
            // The next top-level key ends the jobs.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#') break;

            var atTwoSpaces = line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal) && !char.IsWhiteSpace(line[2]) && line[2] != '#';

            if (!atTwoSpaces)
            {
                body.Append(line).Append('\n');
                continue;
            }

            var key = JobKey.Match(line);

            Assert.IsTrue(
                key.Success,
                $"{fileName}: '{line.Trim()}' stands where a job's key stands and is none this reader can read. "
                + "A job it does not see is a job no rule is asked of.");

            var name = key.Groups["name"].Value.Trim('"', '\'');
            var rest = key.Groups["rest"].Value.Trim();

            Assert.IsTrue(
                rest.Length == 0 || rest.StartsWith('#'),
                $"{fileName}: the job `{name}` has its body behind its key ('{rest}'). This reader reads a job's keys "
                + "from the lines below it, so it could ask no rule of this one; write the job as a block.");

            Close();

            Assert.IsFalse(jobs.ContainsKey(name), $"{fileName} has two jobs `{name}`.");

            current = name;
        }

        Close();

        Assert.AreNotEqual(0, jobs.Count, $"Could not find any jobs under `jobs:` in {fileName}.");

        return new WorkflowFile(fileName, text, WithoutComments(string.Join('\n', lines.Take(start))), jobs);
    }

    /// <summary>The job ids a job lists under <c>needs</c>, in any of YAML's three spellings.</summary>
    public IReadOnlyList<string> Needs(string job)
    {
        var body = Jobs[job];

        if (InlineNeeds.Match(body) is { Success: true } inline)
            return [.. inline.Groups["list"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        if (BlockNeeds.Match(body) is { Success: true } block)
            return [.. block.Groups["list"].Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(line => WithoutTrailingComment(line.TrimStart('-').Trim()))];

        if (ScalarNeeds.Match(body) is { Success: true } scalar)
            return [scalar.Groups["job"].Value];

        return [];
    }

    /// <summary>
    /// A job's own key at four spaces, such as <c>name</c> or <c>if</c>, without a comment behind
    /// its value; null when the job has no such key.
    /// </summary>
    public string? Key(string job, string key)
    {
        var match = Regex.Match(Jobs[job], $@"(?m)^    {Regex.Escape(key)}:[ \t]*(?<value>.*?)[ \t]*$");
        return match.Success ? WithoutTrailingComment(match.Groups["value"].Value) : null;
    }

    /// <summary>A job's body without its comment lines, for a rule about what runs and not about what is said.</summary>
    public string Script(string job) => WithoutComments(Jobs[job]);

    /// <summary>
    /// What the workflow may write, as <c>job: scope</c>, and <c>(the workflow): scope</c> for a
    /// permission every job gets.
    /// </summary>
    public IReadOnlyList<string> WritePermissions()
        => [.. Jobs
            .SelectMany(job => WritePermission.Matches(job.Value).Select(match => $"{job.Key}: {match.Groups["scope"].Value}"))
            .Concat(WritePermission.Matches(Triggers).Select(match => $"(the workflow): {match.Groups["scope"].Value}"))
            .Order(StringComparer.Ordinal)];

    /// <summary>The steps of a job in their order: each by its name, or by the action it uses when it has none.</summary>
    public IReadOnlyList<string> Steps(string job)
        => [.. Regex.Matches(Jobs[job], @"(?m)^      - (?:name|uses):[ \t]*(?<step>.*?)[ \t]*$").Select(match => WithoutTrailingComment(match.Groups["step"].Value))];

    /// <summary>A step of a job, found by its name: everything from its <c>- name:</c> line to the next step.</summary>
    public string Step(string job, string name)
    {
        var match = Regex.Match(Jobs[job], $@"(?ms)^      - name: {Regex.Escape(name)}[ \t]*\n.*?(?=^      - |\z)");

        Assert.IsTrue(match.Success, $"The `{job}` job of {FileName} has no step named '{name}'.");

        return match.Value;
    }

    /// <summary>
    /// The lines of a step's <c>run: |</c> block as bash gets them, without the block's indentation.
    /// A test that runs them runs what the workflow runs: a rule matched against the text would
    /// pass with the lines commented out, or rewritten into something that never matches.
    /// </summary>
    public string Run(string job, string name)
    {
        var lines = Step(job, name).Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == "        run: |");

        Assert.IsTrue(start >= 0, $"The step '{name}' of the `{job}` job of {FileName} has no `run: |` block.");

        var block = lines.Skip(start + 1)
            .TakeWhile(line => line.Trim().Length == 0 || line.StartsWith("          ", StringComparison.Ordinal))
            .Select(line => line.Length > 10 ? line[10..] : "")
            .ToList();

        Assert.IsTrue(block.Any(line => line.Length > 0), $"The `run: |` block of the step '{name}' in {FileName} is empty.");

        return string.Join('\n', block).TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Whether a step's script runs with <c>pipefail</c>, by the shell it is given. A step, a job or
    /// the workflow that says <c>shell: bash</c> gets <c>bash --noprofile --norc -eo pipefail</c>;
    /// with no shell stated a runner on Linux runs <c>bash -e</c>, in which a pipe's status is its
    /// last command's. The nearest of the three decides, and a workflow that is called takes
    /// nothing from its caller.
    /// </summary>
    public bool Pipefail(string job, string name)
    {
        var shell = Regex.Match(Step(job, name), @"(?m)^        shell:[ \t]*(?<shell>\S+)") is { Success: true } ofStep ? ofStep.Groups["shell"].Value
            : Regex.Match(Jobs[job], @"(?m)^    defaults:[ \t]*\n      run:[ \t]*\n(?:        .*\n)*?        shell:[ \t]*(?<shell>\S+)") is { Success: true } ofJob ? ofJob.Groups["shell"].Value
            : Regex.Match(Triggers, @"(?m)^defaults:[ \t]*\n  run:[ \t]*\n(?:    .*\n)*?    shell:[ \t]*(?<shell>\S+)") is { Success: true } ofWorkflow ? ofWorkflow.Groups["shell"].Value
            : null;

        Assert.IsTrue(
            shell is null or "bash",
            $"The step '{name}' of the `{job}` job of {FileName} runs with the shell '{shell}', and these tests run a step with bash.");

        return shell is not null;
    }

    /// <summary>
    /// Runs a step's script as a runner runs it: with bash, with <c>pipefail</c> only where the
    /// step has it, in the given folder and with the given variables. <paramref name="before" />
    /// stands ahead of the step's lines, for a command the test puts in a tool's place.
    /// </summary>
    public Bash.Result RunStep(string job, string name, string folder, IReadOnlyDictionary<string, string> environment, string before = "")
        => Bash.Run(before + Run(job, name), folder, environment, Pipefail(job, name));

    private static string WithoutComments(string text)
        => string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>A plain value without the comment behind it. A quoted value is left as it is: a <c>#</c> in it is text.</summary>
    private static string WithoutTrailingComment(string value)
        => value.Length > 0 && value[0] is '"' or '\''
            ? value
            : Regex.Replace(value, @"(?:^|[ \t]+)#.*$", "");
}
