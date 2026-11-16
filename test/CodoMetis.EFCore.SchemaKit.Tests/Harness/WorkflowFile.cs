using System.Text.RegularExpressions;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>
/// The jobs of a GitHub Actions workflow under <c>.github/workflows/</c>, read well enough for the
/// convention tests that hold them to a rule: which jobs exist, what each one needs, its keys and
/// the script of one of its steps. Ported from <c>EFCore.ComplexIndexes</c>.
/// </summary>
/// <remarks>
/// No YAML parser. It reads the layout every workflow of this repository has: jobs as keys
/// indented by two spaces under a top-level <c>jobs:</c>, their own keys at four, their steps at
/// six and a step's script at ten. A comment between two jobs falls into the body of the job
/// before it, which does no harm as long as a caller matches YAML keys and not words.
/// </remarks>
internal sealed class WorkflowFile
{
    private static readonly Regex JobBlock =
        new(@"(?ms)^  (?<name>[A-Za-z_][\w-]*):\s*\n(?<body>.*?)(?=^  [A-Za-z_][\w-]*:\s*\n|\z)", RegexOptions.Compiled);

    private static readonly Regex InlineNeeds =
        new(@"(?m)^    needs:[ \t]*\[(?<list>[^\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex ScalarNeeds =
        new(@"(?m)^    needs:[ \t]*(?<job>[A-Za-z_][\w-]*)[ \t]*(?:#.*)?$", RegexOptions.Compiled);

    private static readonly Regex BlockNeeds =
        new(@"(?m)^    needs:[ \t]*\n(?<list>(?:^      -[ \t]*[A-Za-z_][\w-]*[ \t]*\n)+)", RegexOptions.Compiled);

    private WorkflowFile(string fileName, string text, string triggers, IReadOnlyDictionary<string, string> jobs)
    {
        FileName = fileName;
        Text = text;
        Triggers = triggers;
        Jobs = jobs;
    }

    public string FileName { get; }

    public string Text { get; }

    /// <summary>Everything above <c>jobs:</c> without its comment lines: the name, <c>on:</c>, the permissions.</summary>
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

        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = Regex.Match(text, @"(?m)^jobs:\s*$");

        Assert.IsTrue(start.Success, $"{fileName} has no top-level `jobs:` key.");

        var jobs = JobBlock.Matches(text[(start.Index + start.Length)..])
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["body"].Value, StringComparer.Ordinal);

        Assert.AreNotEqual(0, jobs.Count, $"Could not find any jobs under `jobs:` in {fileName}.");

        return new WorkflowFile(fileName, text, WithoutComments(text[..start.Index]), jobs);
    }

    /// <summary>The job ids a job lists under <c>needs</c>, in any of YAML's three spellings.</summary>
    public IReadOnlyList<string> Needs(string job)
    {
        var body = Jobs[job];

        if (InlineNeeds.Match(body) is { Success: true } inline)
            return [.. inline.Groups["list"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        if (BlockNeeds.Match(body) is { Success: true } block)
            return [.. block.Groups["list"].Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimStart('-').Trim())];

        if (ScalarNeeds.Match(body) is { Success: true } scalar)
            return [scalar.Groups["job"].Value];

        return [];
    }

    /// <summary>A job's own key at four spaces, such as <c>name</c> or <c>if</c>; null when it has none.</summary>
    public string? Key(string job, string key)
    {
        var match = Regex.Match(Jobs[job], $@"(?m)^    {Regex.Escape(key)}:[ \t]*(?<value>.*?)[ \t]*$");
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>A job's body without its comment lines, for a rule about what runs and not about what is said.</summary>
    public string Script(string job) => WithoutComments(Jobs[job]);

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

    private static string WithoutComments(string text)
        => string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
}
