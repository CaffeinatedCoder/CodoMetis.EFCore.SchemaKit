using System.Text.RegularExpressions;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Documentation;

/// <summary>The Markdown this repository writes for its readers, and what is in it.</summary>
public static partial class Markdown
{
    public static string Root { get; } = FindRoot();

    /// <summary>The pages a reader is sent to: the root pages, <c>docs/</c>, and each package's README.</summary>
    public static IEnumerable<string> Pages()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            yield return file;

        var docs = Path.Combine(Root, "docs");
        if (Directory.Exists(docs))
            foreach (var file in Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                yield return file;

        foreach (var readme in PackageReadmes())
            yield return readme;
    }

    /// <summary>The README packed into each package under <c>src/</c>, which nuget.org shows.</summary>
    public static IEnumerable<string> PackageReadmes()
        => Directory.EnumerateDirectories(Path.Combine(Root, "src"))
            .Order(StringComparer.Ordinal)
            .Select(project => Path.Combine(project, "README.md"));

    public static string Relative(string path) => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A fenced code block: its info string, the line its code starts on, and its code.</summary>
    public sealed record CodeBlock(string File, int Line, string Info, string Code)
    {
        public override string ToString() => $"{Relative(File)}:{Line}";
    }

    public static IEnumerable<CodeBlock> CodeBlocks(string file)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("```", StringComparison.Ordinal)) continue;

            var info = lines[i][3..].Trim();
            var start = i + 1;
            var end = start;
            while (end < lines.Length && !lines[end].StartsWith("```", StringComparison.Ordinal)) end++;

            yield return new CodeBlock(file, start + 1, info, string.Join('\n', lines[start..end]));
            i = end;
        }
    }

    /// <summary>Every inline link target, <c>[text](target)</c>, outside code blocks and code spans.</summary>
    public static IEnumerable<string> LinkTargets(string file)
    {
        var text = FencedBlock().Replace(File.ReadAllText(file), "");
        text = CodeSpan().Replace(text, "");
        return InlineLink().Matches(text).Select(match => match.Groups[1].Value);
    }

    /// <summary>
    /// GitHub's anchors for the headings of a page: lower case, inline Markdown taken off, every
    /// character but letters, digits, spaces, hyphens and underscores removed, spaces to hyphens.
    /// A repeated heading gets a numbered suffix on GitHub, which is not modelled: a link to one
    /// fails here rather than pass on a guess.
    /// </summary>
    public static HashSet<string> Anchors(string file)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var text = FencedBlock().Replace(File.ReadAllText(file), "");

        foreach (Match heading in Heading().Matches(text))
        {
            var title = InlineLink().Replace(heading.Groups[1].Value, "$1");
            title = title.Replace("`", "").Replace("*", "");
            anchors.Add(new string([.. title.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
                .Select(c => c == ' ' ? '-' : c)]));
        }

        return anchors;
    }

    [GeneratedRegex(@"\[(?:[^\]]*)\]\(([^)\s]+)\)")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^```.*?^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

    [GeneratedRegex(@"`[^`\n]+`")]
    private static partial Regex CodeSpan();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                return directory.FullName;

        throw new InvalidOperationException($"No repository root (global.json beside src/) above {AppContext.BaseDirectory}.");
    }
}
