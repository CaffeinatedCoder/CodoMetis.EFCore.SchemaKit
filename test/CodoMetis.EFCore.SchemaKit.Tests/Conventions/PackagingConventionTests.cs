using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Conventions;

/// <summary>
/// What a package has to carry, read from the packed <c>.nupkg</c> and not from the project file.
/// None of it shows at compile time or in any other test: a package without its README, without
/// symbols, or with the kernel as a lower bound builds, restores and installs.
/// </summary>
/// <remarks>
/// <para>
/// Every project under <c>src/</c> is packed once, in Release, into a temporary folder with its
/// own <c>bin</c> and <c>obj</c>, under a version no release has (<c>….packcheck</c>). Nothing is
/// written to the checkout or to the global packages folder, and nothing is restored from what was
/// packed.
/// </para>
/// <para>
/// With <c>SCHEMAKIT_PACKAGES</c> set to a folder, nothing is packed and the packages in that folder
/// are read instead, at the version <c>Directory.Build.props</c> states. The release workflow sets
/// it to what it is about to publish, so the packages checked are the packages pushed.
/// </para>
/// </remarks>
[TestClass]
public sealed class PackagingConventionTests
{
    private const string Kernel = "CodoMetis.EFCore.SchemaKit";
    private const string Framework = "net11.0";

    // Stated here and not read from Directory.Build.props: read from there, a wrong value would be
    // compared with itself. The repository is also what the trusted-publishing policy names.
    private const string License = "MIT";
    private const string Repository = "https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit";

    /// <summary>The custom debug information Source Link is stored under in a portable PDB.</summary>
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private static readonly Lazy<PackOutput> Output = new(Pack);

    private static string? temporary;

    private sealed record PackOutput(string Folder, string Version);

    /// <summary>The package ids: one per project file under <c>src/</c>, which is named after its id.</summary>
    private static IReadOnlyList<string> PackageIds { get; } =
    [
        .. Directory.EnumerateFiles(RepositoryLayout.Source, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)!,
    ];

    private static XDocument BuildProps => XDocument.Load(Path.Combine(RepositoryLayout.Root, "Directory.Build.props"));

    private static string Declared(string property)
        => BuildProps.Descendants(property).Single().Value;

    [ClassCleanup]
    public static void Cleanup()
    {
        if (temporary is not null && Directory.Exists(temporary))
            Directory.Delete(temporary, recursive: true);
    }

    [TestMethod(DisplayName = "Both shipping projects are found under src/ and each has its package")]
    public void Every_shipping_project_is_packed()
    {
        // The discovery itself: restructure src/ and every test below would pass on an empty list.
        CollectionAssert.AreEqual(new[] { Kernel, $"{Kernel}.PostgreSQL" }, PackageIds.ToList());

        foreach (var id in PackageIds)
            Assert.IsTrue(File.Exists(PackagePath(id, ".nupkg")), $"No {Path.GetFileName(PackagePath(id, ".nupkg"))} in {Output.Value.Folder}.");
    }

    [TestMethod(DisplayName = "A package depends on another SchemaKit package at exactly its own version")]
    public void The_kernel_dependency_is_exact()
    {
        var version = Output.Value.Version;
        var found = 0;

        foreach (var id in PackageIds)
        foreach (var dependency in Dependencies(Nuspec(id)).Where(d => PackageIds.Contains(d.Id)))
        {
            found++;
            Assert.AreEqual(
                $"[{version}]",
                dependency.Version,
                $"{id} depends on {dependency.Id} as '{dependency.Version}'. A version without brackets is a lower bound: "
                + "a newer kernel restores and builds, and the contract this package calls is not there at run time "
                + "(MissingMethodException). See the target ExactKernelVersion in the PostgreSQL package's project file.");
        }

        Assert.AreEqual(1, found, "Expected one dependency between SchemaKit's packages, the PostgreSQL package's on the kernel.");
    }

    [TestMethod(DisplayName = "Each package carries the README its nuspec names")]
    public void Each_package_carries_its_readme()
    {
        foreach (var id in PackageIds)
        {
            var readme = Metadata(Nuspec(id), "readme")?.Value;
            Assert.IsFalse(string.IsNullOrWhiteSpace(readme), $"{id}'s nuspec names no readme (PackageReadmeFile), so nuget.org shows none.");

            using var package = ZipFile.OpenRead(PackagePath(id, ".nupkg"));
            var entry = package.GetEntry(readme!);
            Assert.IsNotNull(entry, $"{id}'s nuspec names the readme '{readme}', and the package holds no such file.");

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            StringAssert.StartsWith(text.TrimStart(), $"# {id}", $"{id}'s README is not its own: it does not open with the package's name.");
        }
    }

    [TestMethod(DisplayName = "Each nuspec states the license, the repository and the commit it was built from")]
    public void Each_nuspec_states_license_repository_and_commit()
    {
        var head = Run("git", "rev-parse HEAD").Trim();

        foreach (var id in PackageIds)
        {
            var nuspec = Nuspec(id);

            var license = Metadata(nuspec, "license");
            Assert.AreEqual("expression", license?.Attribute("type")?.Value, $"{id}: the license is not an SPDX expression.");
            Assert.AreEqual(License, license!.Value, $"{id}: the license.");

            var repository = Metadata(nuspec, "repository");
            Assert.IsNotNull(repository, $"{id}: the nuspec has no repository element (RepositoryUrl).");
            Assert.AreEqual("git", repository.Attribute("type")?.Value, $"{id}: the repository's type.");
            Assert.AreEqual(Repository, repository.Attribute("url")?.Value, $"{id}: the repository's URL.");
            Assert.AreEqual(
                head,
                repository.Attribute("commit")?.Value,
                $"{id}: the nuspec's commit is not the commit that was packed. Source Link fetches the sources of that commit.");
        }
    }

    [TestMethod(DisplayName = "Each package targets net11.0 and nothing else")]
    public void Each_package_targets_one_framework()
    {
        foreach (var id in PackageIds)
        {
            var groups = Nuspec(id).Descendants().Where(e => e.Name.LocalName == "group")
                .Select(g => g.Attribute("targetFramework")?.Value).ToList();
            CollectionAssert.AreEqual(new[] { Framework }, groups, $"{id}: the dependency groups are {string.Join(", ", groups)}.");

            using var package = ZipFile.OpenRead(PackagePath(id, ".nupkg"));
            var folders = package.Entries.Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal))
                .Select(e => e.FullName.Split('/')[1]).Distinct().ToList();
            CollectionAssert.AreEqual(new[] { Framework }, folders, $"{id}: lib/ holds {string.Join(", ", folders)}.");

            Assert.IsNotNull(package.GetEntry($"lib/{Framework}/{id}.dll"), $"{id}: no assembly in lib/{Framework}/.");
            Assert.IsNotNull(package.GetEntry($"lib/{Framework}/{id}.xml"), $"{id}: no XML documentation in lib/{Framework}/.");
        }
    }

    [TestMethod(DisplayName = "Each package has a symbols package whose PDB links the sources of the packed commit")]
    public void Each_package_has_symbols_with_source_link()
    {
        var head = Run("git", "rev-parse HEAD").Trim();
        var onCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        foreach (var id in PackageIds)
        {
            var path = PackagePath(id, ".snupkg");
            Assert.IsTrue(File.Exists(path), $"No {Path.GetFileName(path)} beside the package (IncludeSymbols, SymbolPackageFormat): a debugger gets no symbols for {id}.");

            using var symbols = ZipFile.OpenRead(path);
            var entry = symbols.GetEntry($"lib/{Framework}/{id}.pdb");
            Assert.IsNotNull(entry, $"{Path.GetFileName(path)} holds no lib/{Framework}/{id}.pdb.");

            using var buffer = new MemoryStream();
            using (var stream = entry.Open())
                stream.CopyTo(buffer);
            buffer.Position = 0;

            using var provider = MetadataReaderProvider.FromPortablePdbStream(buffer);
            var pdb = provider.GetMetadataReader();

            var sourceLink = pdb.CustomDebugInformation
                .Select(pdb.GetCustomDebugInformation)
                .Where(information => pdb.GetGuid(information.Kind) == SourceLinkKind)
                .Select(information => Encoding.UTF8.GetString(pdb.GetBlobBytes(information.Value)))
                .SingleOrDefault();

            Assert.IsNotNull(sourceLink, $"{id}: the PDB has no Source Link, so a debugger cannot fetch the sources.");
            StringAssert.Contains(sourceLink, $"/{head}/", $"{id}: Source Link does not point at the packed commit.");

            // CI's build writes every source path under a mapped root, /_/ for the checkout and
            // /_1/ for what is generated outside it: the runner's folders are no part of what was
            // built. On a laptop the paths stay, on purpose (Directory.Build.props).
            if (!onCi) continue;

            var local = pdb.Documents.Select(pdb.GetDocument).Select(document => pdb.GetString(document.Name))
                .Where(name => !Regex.IsMatch(name, @"^/_\d*/"))
                .ToList();
            Assert.AreEqual(0, local.Count, $"{id}: CI is set and these sources keep the build machine's path (ContinuousIntegrationBuild): {string.Join(", ", local.Take(5))}");
        }
    }

    private static string PackagePath(string id, string extension)
        => Path.Combine(Output.Value.Folder, $"{id}.{Output.Value.Version}{extension}");

    private static XDocument Nuspec(string id)
    {
        var path = PackagePath(id, ".nupkg");
        Assert.IsTrue(File.Exists(path), $"No {Path.GetFileName(path)} in {Output.Value.Folder}.");

        using var package = ZipFile.OpenRead(path);
        var entry = package.GetEntry($"{id}.nuspec");
        Assert.IsNotNull(entry, $"{Path.GetFileName(path)} holds no {id}.nuspec.");

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static XElement? Metadata(XDocument nuspec, string name)
        => nuspec.Root!.Elements().Single(e => e.Name.LocalName == "metadata").Elements().SingleOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<(string Id, string Version)> Dependencies(XDocument nuspec)
        => nuspec.Descendants().Where(e => e.Name.LocalName == "dependency")
            .Select(e => (e.Attribute("id")!.Value, e.Attribute("version")!.Value));

    private static PackOutput Pack()
    {
        var declared = Declared("Version");

        if (Environment.GetEnvironmentVariable("SCHEMAKIT_PACKAGES") is { Length: > 0 } given)
        {
            var folder = Path.GetFullPath(given, RepositoryLayout.Root);
            Assert.IsTrue(Directory.Exists(folder), $"SCHEMAKIT_PACKAGES names {folder}, which is no folder.");
            return new PackOutput(folder, declared);
        }

        // A version of its own, so that what is packed here can never be taken for a release.
        var version = declared + (declared.Contains('-') ? ".packcheck" : "-packcheck");

        temporary = Path.Combine(Path.GetTempPath(), "schemakit-pack-" + Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(temporary, "packages");

        foreach (var id in PackageIds)
            Run(
                Dotnet,
                $"pack \"{Path.Combine(RepositoryLayout.Source, id, id + ".csproj")}\" -c Release --disable-build-servers "
                + $"--artifacts-path \"{Path.Combine(temporary, "artifacts")}\" -o \"{packages}\" -p:Version={version}");

        return new PackOutput(packages, version);
    }

    /// <summary>The host that runs this test, so that the pack uses the SDK the tests were built with.</summary>
    private static string Dotnet
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet"
                ? Environment.ProcessPath!
                : "dotnet";

    private static string Run(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = RepositoryLayout.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"`{file} {arguments}` did not end within ten minutes.");
        }

        Assert.AreEqual(0, process.ExitCode, $"`{file} {arguments}` failed:{Environment.NewLine}{output}{Environment.NewLine}{error.Result}");
        return output;
    }
}
