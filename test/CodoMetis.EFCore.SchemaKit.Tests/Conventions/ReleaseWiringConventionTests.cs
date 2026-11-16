using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// The parts of the release path that fail without a word, or on the day of the release and no
/// earlier. Ported from <c>EFCore.ComplexIndexes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Trusted Publishing matches three names, the workflow's <em>file name</em>, the environment and
/// the repository, and none of them is in the build. Rename the workflow or the environment and
/// nothing here breaks: the next tag fails to publish, with an authentication error that does not
/// mention the rename. The split into jobs is the same kind of thing: nothing stops a later edit
/// from handing the unattended <c>verify</c> job an <c>id-token</c>, or one job both the token and
/// write access to the repository, and the workflow would go on passing.
/// </para>
/// <para>
/// Where a rule is about what a step does, the step's own lines are run
/// (<see cref="WorkflowFile.Run" />): the tag check, the release's notes, the SBOMs. A release is
/// the maintainer's to trigger, so nothing else runs them before the day they are needed.
/// </para>
/// </remarks>
[TestClass]
public sealed class ReleaseWiringConventionTests
{
    /// <summary>The workflow file name the trusted-publishing policy on nuget.org names.</summary>
    private const string ReleaseWorkflow = "release.yml";

    /// <summary>The build workflow, which the release workflow calls for its lanes.</summary>
    private const string BuildWorkflow = "dotnet.yml";

    /// <summary>The environment the policy expects, which is also the approval.</summary>
    private const string PublishEnvironment = "nuget";

    private const string VerifyJob = "verify";

    /// <summary>The one job that may mint an OIDC token.</summary>
    private const string PublishJob = "publish";

    /// <summary>The one job that may write to the repository.</summary>
    private const string ReleaseJob = "release";

    private const string Repository = "https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit";

    // The functions that, in a job's `if:`, take the place of the implicit success() of its needs.
    private static readonly Regex StatusFunction = new(@"\b(always|failure|cancelled)\s*\(\s*\)", RegexOptions.Compiled);

    // A YAML key at any indentation, and not the word in a comment: verify's comment says
    // "id-token" to explain why it asks for none.
    private static readonly Regex WritePermission = new(@"(?m)^\s*(?<scope>[a-z-]+)\s*:\s*write\b", RegexOptions.Compiled);

    private static readonly Regex IdTokenKey = new(@"(?m)^\s*id-token\s*:", RegexOptions.Compiled);

    private static WorkflowFile Release => WorkflowFile.Load(ReleaseWorkflow);

    [TestMethod(DisplayName = "The release workflow is at the file name the trusted-publishing policy names")]
    public void The_release_workflow_is_at_the_policys_file_name()
    {
        Assert.IsTrue(
            File.Exists(Path.Combine(RepositoryLayout.Workflows, ReleaseWorkflow)),
            $"No .github/workflows/{ReleaseWorkflow}. The policy on nuget.org names the workflow by its file name, so a "
            + "renamed or moved file stops publishing, with an authentication error that does not mention the rename.");
    }

    [TestMethod(DisplayName = "The publish job waits for the environment the policy expects")]
    public void The_publish_job_waits_for_the_policys_environment()
    {
        var release = Release;

        Assert.IsTrue(release.Jobs.ContainsKey(PublishJob), $"{ReleaseWorkflow} has no `{PublishJob}` job.");

        Assert.AreEqual(
            PublishEnvironment, release.Key(PublishJob, "environment"),
            $"The `{PublishJob}` job does not run in the '{PublishEnvironment}' environment. The OIDC token carries the "
            + "environment as a claim, so the policy does not match a token without it; and the environment is the approval.");
    }

    /// <summary>
    /// The split exists so that a reviewer is asked only after everything else has passed, and so
    /// that nothing that runs unattended can mint a token that publishes.
    /// </summary>
    [TestMethod(DisplayName = "Only the publish job may mint an OIDC token")]
    public void Only_the_publish_job_may_mint_an_oidc_token()
    {
        var release = Release;

        Assert.IsTrue(
            release.Jobs.TryGetValue(PublishJob, out var publish) && Regex.IsMatch(publish, @"(?m)^\s*id-token\s*:\s*write\b"),
            $"The `{PublishJob}` job does not ask for id-token: write, so the OIDC login cannot work.");

        var others = release.Jobs.Where(job => job.Key != PublishJob && IdTokenKey.IsMatch(job.Value)).Select(job => job.Key).ToList();

        Assert.AreEqual(
            0, others.Count,
            $"{string.Join(", ", others)} asks for an id-token permission. Only `{PublishJob}` may: every other job runs "
            + "unattended or with write access, and none of them may be able to mint a token that publishes.");

        Assert.IsFalse(
            IdTokenKey.IsMatch(release.Triggers),
            $"{ReleaseWorkflow} asks for an id-token permission for the whole workflow, which gives it to every job.");
    }

    /// <summary>
    /// The token and the write access to the repository are in two jobs on purpose: a step that
    /// went wrong in the job that publishes cannot rewrite the repository, and one in the job that
    /// creates the release cannot publish.
    /// </summary>
    [TestMethod(DisplayName = "The workflow's only write permissions are publish's token and release's contents")]
    public void The_only_write_permissions_are_the_token_and_the_release()
    {
        var release = Release;

        var granted = release.Jobs
            .SelectMany(job => WritePermission.Matches(job.Value).Select(match => $"{job.Key}: {match.Groups["scope"].Value}"))
            .Concat(WritePermission.Matches(release.Triggers).Select(match => $"(the workflow): {match.Groups["scope"].Value}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(
            new[] { $"{PublishJob}: id-token", $"{ReleaseJob}: contents" }, granted,
            $"{ReleaseWorkflow} grants write as: {string.Join("; ", granted)}. `{PublishJob}` holds the token and nothing "
            + $"else, `{ReleaseJob}` writes the release and holds no token, and every other job reads.");

        StringAssert.Matches(
            release.Triggers, new Regex(@"(?m)^permissions:\s*\n  contents:\s*read\s*$"),
            $"{ReleaseWorkflow}'s own permissions are not `contents: read`, so a job that states none gets the repository's default.");
    }

    /// <summary>
    /// <c>publish</c> runs only after every lane of <c>dotnet.yml</c> passed on the tagged commit.
    /// </summary>
    /// <remarks>
    /// A job calls <c>dotnet.yml</c>, whose <c>CI</c> gate fails on any lane that did not succeed
    /// (<see cref="WorkflowGateConventionTests" />), and <c>publish</c> depends on that job through
    /// <c>needs</c>. A status function in an <c>if:</c> on that path (<c>always()</c>,
    /// <c>failure()</c>, <c>cancelled()</c>) would take the place of the implicit
    /// <c>success()</c> and let <c>publish</c> run after a lane failed, so none may stand there.
    /// </remarks>
    [TestMethod(DisplayName = "The release workflow cannot publish without the whole of CI")]
    public void The_release_cannot_publish_without_the_whole_of_ci()
    {
        Assert.IsTrue(
            Regex.IsMatch(WorkflowFile.Load(BuildWorkflow).Triggers, @"(?m)^  workflow_call:"),
            $"{BuildWorkflow} has no `workflow_call` trigger, so the release workflow cannot run its lanes.");

        var release = Release;
        var ciJobs = release.Jobs.Keys.Where(job => release.Key(job, "uses") == $"./.github/workflows/{BuildWorkflow}").ToList();

        Assert.AreNotEqual(
            0, ciJobs.Count,
            $"No job of {ReleaseWorkflow} calls ./.github/workflows/{BuildWorkflow}, so a release runs none of the lanes a "
            + "pull request runs, and nothing stops a tag on a commit whose CI failed.");

        Assert.IsTrue(release.Jobs.ContainsKey(PublishJob), $"{ReleaseWorkflow} has no `{PublishJob}` job.");

        var path = new HashSet<string>(StringComparer.Ordinal) { PublishJob };
        var pending = new Queue<string>(release.Needs(PublishJob));

        while (pending.TryDequeue(out var job))
            if (path.Add(job) && release.Jobs.ContainsKey(job))
                foreach (var need in release.Needs(job))
                    pending.Enqueue(need);

        Assert.IsTrue(
            ciJobs.Any(path.Contains),
            $"`{PublishJob}` does not depend on {string.Join(", ", ciJobs)} through `needs`, so it can publish before, "
            + "or without, every lane passing.");

        Assert.IsTrue(
            path.Contains(VerifyJob),
            $"`{PublishJob}` does not depend on `{VerifyJob}`, which packs and checks what it pushes.");

        var overriding = path
            .Where(job => release.Jobs.ContainsKey(job) && release.Key(job, "if") is { } condition && StatusFunction.IsMatch(condition))
            .ToList();

        Assert.AreEqual(
            0, overriding.Count,
            $"{string.Join(", ", overriding)} has a status function in its `if:`, which takes the place of the implicit "
            + $"success() and lets `{PublishJob}` run after a lane failed.");
    }

    /// <summary>
    /// <c>publish</c> says which events it runs for: a push, which is a tag <c>v*</c>, and a manual
    /// run that says it is no dry run.
    /// </summary>
    /// <remarks>
    /// Written the other way round, as what is excluded (<c>github.event_name != 'workflow_dispatch'
    /// || !inputs.dry_run</c>), the condition is true for every event the workflow does not know
    /// yet: add a <c>schedule</c> or a <c>release</c> trigger to <c>on:</c> for another purpose and
    /// it publishes. So each alternative names its event, nothing is negated, and the one push
    /// the workflow listens to is a tag.
    /// </remarks>
    [TestMethod(DisplayName = "The publish job names the events it runs for, and excludes none")]
    public void The_publish_job_names_the_events_it_runs_for()
    {
        var release = Release;
        var condition = release.Key(PublishJob, "if");

        Assert.IsNotNull(condition, $"The `{PublishJob}` job has no `if:`, so it runs for every event {ReleaseWorkflow} is triggered by, a dry run included.");

        var expression = Regex.Replace(condition, @"^\$\{\{\s*|\s*\}\}$", "");

        Assert.IsFalse(
            expression.Contains('!'),
            $"The `{PublishJob}` job's condition, '{expression}', negates something. A condition that says what is "
            + "excluded publishes for every event added to `on:` later; say which events publish.");

        var alternatives = expression.Split("||", StringSplitOptions.TrimEntries);
        var events = new List<string>();

        foreach (var alternative in alternatives)
        {
            var named = Regex.Match(alternative, @"github\.event_name\s*==\s*'(?<event>[a-z_]+)'");

            Assert.IsTrue(
                named.Success,
                $"'{alternative}', an alternative of the `{PublishJob}` job's condition, names no event "
                + "(github.event_name == '…'), so it holds for any.");

            events.Add(named.Groups["event"].Value);

            if (named.Groups["event"].Value == "workflow_dispatch")
                StringAssert.Matches(
                    alternative, new Regex(@"&&\s*inputs\.dry_run\s*==\s*false"),
                    $"A manual run publishes without saying that it is no dry run: '{alternative}'.");
        }

        CollectionAssert.AreEquivalent(
            new[] { "push", "workflow_dispatch" }, events,
            $"The `{PublishJob}` job runs for {string.Join(", ", events)}. A tag and a manual run that is no dry run publish; nothing else does.");

        // `push` is every push the workflow listens to, so that has to be tags `v*` and nothing else.
        var push = Regex.Match(release.Triggers, @"(?m)^  push:\s*\n(?<body>(?:    .*\n)+)");

        Assert.IsTrue(push.Success, $"{ReleaseWorkflow} has no `push:` trigger.");
        Assert.AreEqual(
            "tags:[\"v*\"]", push.Groups["body"].Value.Replace(" ", "").Trim(),
            $"{ReleaseWorkflow}'s push trigger is not tags `v*` alone. `{PublishJob}` runs for a push, so a branch there publishes from a branch.");

        StringAssert.Matches(
            release.Triggers, new Regex(@"(?m)^      dry_run:\s*\n(?:        .*\n)*?        default:\s*true\s*$"),
            "The `dry_run` input of a manual run does not default to true, so a run started without a thought publishes.");
    }

    /// <summary>
    /// The tag is checked against the version and does not set it: a mistyped tag stops before
    /// anything is packed, and no version is published that the build did not state.
    /// </summary>
    [TestMethod(DisplayName = "A tag that is not the version in Directory.Build.props stops the release")]
    [DataRow("v11.0.0-preview.1", "11.0.0-preview.1", 0)]
    [DataRow("v11.0.0", "11.0.0-preview.1", 1)]
    [DataRow("v11.0.0-preview.1", "11.0.0-preview.2", 1)]
    public void A_tag_that_is_not_the_version_stops_the_release(string tag, string version, int expected)
    {
        const string step = "The tag is the package version";
        var release = Release;

        StringAssert.Matches(
            release.Step(VerifyJob, step), new Regex(@"(?m)^        if: startsWith\(github\.ref, 'refs/tags/v'\)\s*$"),
            $"The step '{step}' does not run for every tag `v*`.");

        StringAssert.Contains(
            release.Run(VerifyJob, "Resolve version"), "-getProperty:Version",
            "The version is not MSBuild's evaluation of the Version property, so it is not what the build stamps onto the packages.");

        var result = InScratch(folder => Bash.Run(
            release.Run(VerifyJob, step), folder,
            new Dictionary<string, string> { ["GITHUB_REF_NAME"] = tag, ["VERSION"] = version }));

        Assert.AreEqual(expected, result.ExitCode, $"tag {tag}, version {version}\n{result}");
    }

    /// <summary>
    /// The release's notes are the version's section of the changelog, with the links into this
    /// repository at the tag: the changelog links <c>blob/main</c>, and <c>main</c> moves on after
    /// the tag while the notes stay as published.
    /// </summary>
    /// <remarks>
    /// The step's own lines are run against a changelog of the test's: matching the text would pass
    /// with a rewrite that never matches, and the bash of a contributor's Mac (3.2) and of the
    /// runner (5.x) read a quoted replacement differently.
    /// </remarks>
    [TestMethod(DisplayName = "The release's notes are the version's section, with its links at the tag")]
    public void The_releases_notes_are_the_versions_section_with_its_links_at_the_tag()
    {
        var (result, notes) = Notes("11.0.0", "refs/tags/v11.0.0", SeededChangelog("2026-11-20"));

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        Assert.AreEqual(
            string.Join("\n",
                $"The introduction, with [the guide]({Repository}/blob/v11.0.0/docs/moving-from-complexindexes-5x.md#conditions).",
                "",
                "### Fixed",
                "",
                $"- Docs: [the folder]({Repository}/tree/v11.0.0/docs/), [the issue]({Repository}/issues/35), [main twice]({Repository}/blob/v11.0.0/README.md) ({Repository}/blob/v11.0.0/CHANGELOG.md).",
                $"- Docs: [another repository]({Repository}.Extra/blob/main/README.md) and [another branch]({Repository}/blob/release/11.x/README.md) stay as written."),
            notes?.TrimEnd('\n'),
            $"The notes are written by {ReleaseWorkflow}'s step 'Release notes from the changelog'.");
    }

    /// <summary>
    /// <c>— unreleased</c> in the heading stops a tag (CHANGELOG.md says so above its first
    /// section), and so does a version with no section: the release would go out with notes that
    /// say it is not out, or with none. A run from a branch with no section goes on, so that a dry
    /// run can exercise the rest.
    /// </summary>
    [TestMethod(DisplayName = "A tag whose version is unreleased in the changelog, or not in it, stops before anything is packed")]
    public void A_tag_whose_version_has_no_dated_section_stops()
    {
        var (unreleased, _) = Notes("11.0.0", "refs/tags/v11.0.0", SeededChangelog("unreleased"));

        Assert.AreEqual(1, unreleased.ExitCode, $"A tag for a version whose heading says 'unreleased' went on:\n{unreleased}");
        StringAssert.Contains(unreleased.Output, "is not '## [11.0.0] — yyyy-mm-dd'", unreleased.ToString());

        var (missing, _) = Notes("11.0.2", "refs/tags/v11.0.2", SeededChangelog("2026-11-20"));

        Assert.AreEqual(1, missing.ExitCode, $"A tag for a version the changelog does not have went on:\n{missing}");
        StringAssert.Contains(missing.Output, "has no section '## [11.0.2] — yyyy-mm-dd'", missing.ToString());

        var (dryRun, notes) = Notes("11.0.2", "refs/heads/main", SeededChangelog("2026-11-20"));

        Assert.AreEqual(0, dryRun.ExitCode, $"A run from a branch stopped at a version the changelog does not have yet:\n{dryRun}");
        StringAssert.Contains(dryRun.Output, "::warning::", dryRun.ToString());
        StringAssert.Contains(notes, "dry run", "A run without a section wrote notes that do not say so.");
    }

    /// <summary>
    /// The GitHub release keeps a version with a suffix and a version older than the current
    /// Latest off Latest.
    /// </summary>
    /// <remarks>
    /// GitHub marks each new release Latest unless told otherwise, and Latest is what the
    /// repository's page shows. Every release before <c>11.0.0</c> is a preview, and one marked
    /// only <c>--prerelease</c> relies on the API to refuse it Latest, so it says
    /// <c>--latest=false</c> as well. Only script lines are read: the comment above them explains
    /// both rules, and a test that matched it would pass with the code deleted. Each flag is looked
    /// for on its own branch, so moving <c>--latest=false</c> out of the guard for an older
    /// version, which would keep every stable release off Latest, fails too.
    /// </remarks>
    [TestMethod(DisplayName = "The GitHub release keeps previews and an older version off Latest")]
    public void The_github_release_keeps_previews_and_an_older_version_off_latest()
    {
        var script = Release.Run(ReleaseJob, "Create the release if it does not exist")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        int IndexOf(Predicate<string> predicate, int from = 0) => script.FindIndex(Math.Max(from, 0), predicate);

        var preview = IndexOf(line => line.StartsWith("if ", StringComparison.Ordinal) && line.Contains("\"$VERSION\" == *-*", StringComparison.Ordinal));

        Assert.IsTrue(
            preview >= 0,
            "The step no longer tells a version with a suffix (x.y.z-preview.N) apart, so a preview's tag would be published as a full release.");

        var stable = IndexOf(line => line == "else", preview);

        Assert.IsTrue(stable > preview, "The step's check for a prerelease has no `else` branch for a stable tag.");

        var previewFlags = IndexOf(line => line.Contains("flags+=(--prerelease --latest=false)", StringComparison.Ordinal), preview);

        Assert.IsTrue(previewFlags > preview && previewFlags < stable, "The step does not publish a preview as a prerelease that is not Latest.");

        var olderGuard = IndexOf(
            line => line.StartsWith("if ", StringComparison.Ordinal)
                    && line.Contains("sort -V", StringComparison.Ordinal)
                    && line.Contains("!= \"$VERSION\"", StringComparison.Ordinal),
            stable);
        var olderFlags = olderGuard < 0 ? -1 : IndexOf(line => line == "flags+=(--latest=false)", olderGuard);
        var guardEnd = olderGuard < 0 ? -1 : IndexOf(line => line == "fi", olderGuard);

        Assert.IsTrue(
            olderGuard > stable && olderFlags > olderGuard && olderFlags < guardEnd,
            "The step no longer compares a stable version with the current Latest by `sort -V` and passes --latest=false "
            + "for an older one only, so a tag for an older version would take Latest (or no stable release ever would).");

        Assert.IsTrue(
            script.Any(line => line.StartsWith("latest=\"$(gh release view --json tagName", StringComparison.Ordinal)),
            "The step does not read the current Latest release with a `gh release view` that names no tag.");

        Assert.IsTrue(
            IndexOf(line => line.StartsWith("gh release create", StringComparison.Ordinal) && line.Contains("\"${flags[@]}\"", StringComparison.Ordinal), guardEnd) > guardEnd,
            "The step's `gh release create` does not pass the flags it computed.");
    }

    /// <summary>
    /// <c>verify</c> runs the pack checks on the packages it is about to hand to <c>publish</c>, by
    /// a filter on the test class's name. A filter that matches nothing passes; the step reads the
    /// count back, but only on the day of the release, so the name is held here.
    /// </summary>
    [TestMethod(DisplayName = "The pack checks of the release are the tests of PackagingConventionTests, on the packed folder")]
    public void The_pack_checks_of_the_release_name_the_test_class()
    {
        const string step = "Pack checks";
        var release = Release;

        StringAssert.Contains(
            release.Run(VerifyJob, step), $"--filter \"ClassName~{nameof(PackagingConventionTests)}\"",
            $"The step '{step}' does not filter on {nameof(PackagingConventionTests)}, the class that reads the packages.");

        StringAssert.Matches(
            release.Step(VerifyJob, step), new Regex(@"(?m)^          SCHEMAKIT_PACKAGES:\s*\$\{\{ github\.workspace \}\}/dist\s*$"),
            $"The step '{step}' does not hand the tests the folder that was packed, so they pack a second time and check that.");
    }

    /// <summary>
    /// <c>verify</c> runs <c>dotnet cyclonedx</c> after <c>dotnet tool restore</c>, which needs the
    /// tool in the manifest, at one version; what it writes has to be in the artifact, or the
    /// <c>release</c> job finds nothing to attach, after the packages are on nuget.org.
    /// </summary>
    [TestMethod(DisplayName = "The SBOM tool is pinned in the tool manifest, and the SBOMs travel to the release")]
    public void The_sbom_tool_is_pinned_and_the_sboms_travel_to_the_release()
    {
        var manifest = Path.Combine(RepositoryLayout.Root, ".config", "dotnet-tools.json");

        Assert.IsTrue(File.Exists(manifest), "No tool manifest at .config/dotnet-tools.json, so `dotnet tool restore` restores nothing.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));

        Assert.IsTrue(
            document.RootElement.GetProperty("tools").TryGetProperty("cyclonedx", out var tool)
            && Regex.IsMatch(tool.GetProperty("version").GetString() ?? "", $"^{ReleaseVersion.Pattern}$"),
            $"The tool manifest does not pin cyclonedx to one version, and {ReleaseWorkflow} runs `dotnet cyclonedx` after `dotnet tool restore`.");

        var release = Release;
        var generate = release.Run(VerifyJob, "Generate SBOMs").Split('\n').ToList();
        var restore = generate.FindIndex(line => line.Trim() == "dotnet tool restore");
        var run = generate.FindIndex(line => line.TrimStart().StartsWith("dotnet cyclonedx ", StringComparison.Ordinal));

        Assert.IsTrue(restore >= 0 && run > restore, "The step 'Generate SBOMs' does not restore the tools before it runs `dotnet cyclonedx`.");

        StringAssert.Matches(
            release.Step(VerifyJob, "Upload packages"), new Regex(@"(?m)^            dist/\*\.cdx\.json\s*$"),
            "The artifact of `verify` does not hold dist/*.cdx.json, so the SBOMs never reach the `release` job.");

        StringAssert.Matches(
            release.Run(ReleaseJob, "Attach the SBOMs"), new Regex(@"(?m)^gh release upload ""\$GITHUB_REF_NAME"" ""\$\{sboms\[@\]\}"""),
            "The `release` job does not attach the SBOMs to the release.");
    }

    /// <summary>
    /// An SBOM says what a consumer of the package takes on. A reference with
    /// <c>PrivateAssets="all"</c> never reaches a consumer, so it is named in the step's
    /// <c>--exclude-filter</c>; and a package that is named there is hidden with everything it
    /// brings, so it is one no project references in any other way.
    /// </summary>
    /// <remarks>
    /// No project under <c>src/</c> has such a reference today, and the step has no filter. In
    /// <c>EFCore.ComplexIndexes</c> the one for <c>Microsoft.EntityFrameworkCore.Design</c> kept
    /// about 45 MSBuild and Roslyn components out of an SBOM whose nuspec declared one dependency.
    /// </remarks>
    [TestMethod(DisplayName = "The SBOMs leave out what never reaches a consumer, and nothing else")]
    public void The_sboms_leave_out_what_never_reaches_a_consumer_and_nothing_else()
    {
        var references = RepositoryLayout.ShippingProjects()
            .SelectMany(project => XDocument.Load(project).Descendants("PackageReference"))
            .Where(reference => reference.Attribute("Include") is not null)
            .Select(reference => (Id: reference.Attribute("Include")!.Value, BuildOnly: IsPrivate(reference)))
            .ToList();

        // The premise: the projects and their references are found.
        Assert.AreNotEqual(0, references.Count, "No package reference was found under src/, so none was checked.");

        var buildOnly = references.Where(reference => reference.BuildOnly).Select(reference => reference.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shipped = references.Where(reference => !reference.BuildOnly).Select(reference => reference.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = Regex.Matches(Release.Run(VerifyJob, "Generate SBOMs"), @"--exclude-filter\s+(\S+)")
            .SelectMany(match => match.Groups[1].Value.Trim('"', '\'').Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(entry => entry.Split('@')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var listed = buildOnly.Except(filtered, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

        Assert.AreEqual(
            0, listed.Count,
            $"{string.Join(", ", listed)} is referenced with PrivateAssets=all under src/ and is not in the --exclude-filter of "
            + $"{ReleaseWorkflow}'s step 'Generate SBOMs', so the SBOM lists it and everything it brings as what a consumer takes on.");

        var hidden = filtered.Where(id => !buildOnly.Contains(id) || shipped.Contains(id)).Order(StringComparer.Ordinal).ToList();

        Assert.AreEqual(
            0, hidden.Count,
            $"{string.Join(", ", hidden)} is in the --exclude-filter of the step 'Generate SBOMs' and is no reference with "
            + "PrivateAssets=all in every project that has it, so the SBOM leaves out a dependency a consumer does take on.");
    }

    /// <summary>
    /// The step's own lines, run into a folder of the test's: one SBOM per package, which names the
    /// package at the version and what its project references.
    /// </summary>
    /// <remarks>
    /// In <c>EFCore.ComplexIndexes</c> a lane of the build workflow generates the SBOMs on every
    /// pull request, so that a step that is broken shows before the day of the release. Here this
    /// test does it. It restores the tool from nuget.org the first time; without a network and
    /// without <c>CI</c> it is inconclusive, as the tests that need a server are.
    /// </remarks>
    [TestMethod(DisplayName = "The SBOM step writes one SBOM per package, naming it and what it references")]
    [DoNotParallelize]
    public void The_sbom_step_writes_one_sbom_per_package()
    {
        var version = RepositoryLayout.ShippedVersion.ToString();
        var output = Directory.CreateTempSubdirectory("schemakit-sbom-").FullName;

        try
        {
            var result = Bash.Run(
                Release.Run(VerifyJob, "Generate SBOMs"), RepositoryLayout.Root,
                new Dictionary<string, string> { ["VERSION"] = version, ["DIST"] = output, ["DOTNET_NOLOGO"] = "true" });

            // The loop writes "==> id" before each package: without one, the tool was not restored.
            if (result.ExitCode != 0 && !result.Output.Contains("==> ", StringComparison.Ordinal)
                && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Inconclusive($"`dotnet tool restore` failed, and CI is not set: is nuget.org reachable?\n{result}");

            Assert.AreEqual(0, result.ExitCode, result.ToString());

            var projects = RepositoryLayout.ShippingProjects();
            var ids = projects.Select(Path.GetFileNameWithoutExtension).ToList();

            CollectionAssert.AreEquivalent(
                ids.Select(id => $"{id}.{version}.cdx.json").ToList(),
                Directory.EnumerateFiles(output).Select(Path.GetFileName).ToList(),
                "One SBOM per project under src/, named as the release attaches it.");

            foreach (var project in projects)
            {
                var id = Path.GetFileNameWithoutExtension(project);
                using var sbom = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, $"{id}.{version}.cdx.json")));
                var subject = sbom.RootElement.GetProperty("metadata").GetProperty("component");

                Assert.AreEqual("library", subject.GetProperty("type").GetString(), $"{id}: the SBOM's subject is no library.");
                Assert.AreEqual(id, subject.GetProperty("name").GetString(), $"{id}: the SBOM's subject.");
                Assert.AreEqual(version, subject.GetProperty("version").GetString(), $"{id}: the SBOM's subject is not at the version that ships.");
                Assert.AreEqual($"pkg:nuget/{id}@{version}", subject.GetProperty("purl").GetString(), $"{id}: the SBOM's subject is not the package on nuget.org.");

                var components = sbom.RootElement.GetProperty("components").EnumerateArray()
                    .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("version").GetString(), StringComparer.OrdinalIgnoreCase);
                var csproj = XDocument.Load(project);

                // What the nuspec depends on: each package reference, and a referenced project of
                // this repository as the package it is, at this version.
                foreach (var reference in csproj.Descendants("PackageReference").Where(r => !IsPrivate(r)).Select(r => r.Attribute("Include")!.Value))
                    Assert.IsTrue(components.ContainsKey(reference), $"{id}: the SBOM does not list {reference}, which the project references.");

                foreach (var reference in csproj.Descendants("ProjectReference").Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/'))))
                    Assert.AreEqual(
                        version, components.GetValueOrDefault(reference),
                        $"{id}: the SBOM does not list {reference} at {version}, which the package depends on at exactly that version.");
            }
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>
    /// Publishing goes through <c>release.yml</c> and Trusted Publishing, and nowhere else. A second
    /// path, a script or a workflow with an API key, is the one that leaves a check out.
    /// </summary>
    [TestMethod(DisplayName = "There is one publishing path, and it holds no API key")]
    public void There_is_one_publishing_path()
    {
        var pushing = new List<string>();

        foreach (var fileName in WorkflowFile.All())
        {
            var workflow = WorkflowFile.Load(fileName);
            pushing.AddRange(workflow.Jobs.Keys
                .Where(job => Regex.IsMatch(workflow.Script(job), @"\bnuget\s+push\b"))
                .Select(job => $"{fileName}: {job}"));
        }

        CollectionAssert.AreEqual(
            new[] { $"{ReleaseWorkflow}: {PublishJob}" }, pushing,
            $"`nuget push` is run by: {string.Join("; ", pushing)}. Only the `{PublishJob}` job of {ReleaseWorkflow} publishes.");

        var scripts = RepositoryLayout.Files().Where(path => path.EndsWith(".sh", StringComparison.Ordinal)).ToList();

        Assert.AreNotEqual(0, scripts.Count, "No script was found in the repository, so none was read.");

        var publishing = scripts
            .Where(script => File.ReadLines(script).Any(line => !line.TrimStart().StartsWith('#') && Regex.IsMatch(line, @"\bnuget\s+push\b")))
            .Select(RepositoryLayout.Relative)
            .ToList();

        Assert.AreEqual(
            0, publishing.Count,
            $"{string.Join(", ", publishing)} pushes to a NuGet source. Publishing goes through {ReleaseWorkflow}; a script "
            + "beside it is the path that leaves a check out.");

        var secrets = Regex.Matches(Release.Text, @"secrets\.(?<name>[A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups["name"].Value)
            .Distinct()
            .ToList();

        CollectionAssert.AreEqual(
            new[] { "NUGET_USER" }, secrets,
            $"{ReleaseWorkflow} reads the secrets {string.Join(", ", secrets)}. The key that publishes is issued for one hour "
            + "by the OIDC login; the one secret is the profile name, which is no credential.");
    }

    private static bool IsPrivate(XElement reference)
        => string.Equals(reference.Element("PrivateAssets")?.Value ?? reference.Attribute("PrivateAssets")?.Value, "all", StringComparison.OrdinalIgnoreCase);

    /// <summary>A changelog with the section of 11.0.0 between a newer and an older one.</summary>
    private static string SeededChangelog(string date) => string.Join("\n",
        "# Changelog",
        "",
        "## [11.0.1] — 2026-12-01",
        "",
        "### Fixed",
        "",
        $"- Docs: not this one ({Repository}/blob/main/docs/moving-from-complexindexes-5x.md).",
        "",
        $"## [11.0.0] — {date}",
        "",
        $"The introduction, with [the guide]({Repository}/blob/main/docs/moving-from-complexindexes-5x.md#conditions).",
        "",
        "### Fixed",
        "",
        $"- Docs: [the folder]({Repository}/tree/main/docs/), [the issue]({Repository}/issues/35), [main twice]({Repository}/blob/main/README.md) ({Repository}/blob/main/CHANGELOG.md).",
        $"- Docs: [another repository]({Repository}.Extra/blob/main/README.md) and [another branch]({Repository}/blob/release/11.x/README.md) stay as written.",
        "",
        "## [11.0.0-preview.1] — 2026-11-17",
        "",
        "### Added",
        "",
        "- Exclusion constraints: nor this one.",
        "");

    /// <summary>Runs the step that writes the release's notes in a folder that holds the given changelog.</summary>
    private static (Bash.Result Result, string? Notes) Notes(string version, string gitRef, string changelog)
        => InScratch(folder =>
        {
            File.WriteAllText(Path.Combine(folder, "CHANGELOG.md"), changelog);

            var result = Bash.Run(
                Release.Run(VerifyJob, "Release notes from the changelog"), folder,
                new Dictionary<string, string>
                {
                    ["VERSION"] = version,
                    ["REPO_URL"] = Repository,
                    ["GITHUB_REF"] = gitRef,
                    ["GITHUB_STEP_SUMMARY"] = Path.Combine(folder, "summary.md"),
                });

            var notes = Path.Combine(folder, "dist", "release-notes.md");
            return (result, File.Exists(notes) ? File.ReadAllText(notes) : null);
        });

    private static T InScratch<T>(Func<string, T> run)
    {
        var folder = Directory.CreateTempSubdirectory("schemakit-release-").FullName;

        try
        {
            return run(folder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
