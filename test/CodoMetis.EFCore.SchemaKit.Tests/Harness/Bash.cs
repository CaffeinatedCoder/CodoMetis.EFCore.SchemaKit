using System.Diagnostics;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>
/// Runs a script the way a workflow step with <c>shell: bash</c> runs it, <c>bash -eo pipefail</c>,
/// in a folder and with the variables the test gives it. For the convention tests that run a
/// step's own lines.
/// </summary>
internal static class Bash
{
    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public override string ToString() => $"exit {ExitCode}\n{Output}\n{Error}";
    }

    public static Result Run(string script, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-eo");
        start.ArgumentList.Add("pipefail");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);

        // What a runner sets and a step may read is given by the test, never taken from the machine.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GITHUB_", StringComparison.Ordinal)).ToList())
            start.Environment.Remove(name);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start bash.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"The script did not end within ten minutes:\n{script}");
        }

        return new Result(process.ExitCode, output, error.Result);
    }
}
