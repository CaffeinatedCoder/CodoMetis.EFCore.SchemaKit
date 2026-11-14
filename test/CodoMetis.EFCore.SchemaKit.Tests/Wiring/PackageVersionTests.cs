using System.Reflection;
using System.Reflection.Emit;

namespace CodoMetis.EFCore.SchemaKit.Tests.Wiring;

/// <summary>
/// A SchemaKit package beside a kernel of another version (#113). NuGet lets it through with a
/// warning when the consumer references the kernel directly, and without the check the pair fails
/// with a <see cref="MissingMethodException" /> at whichever member differs first.
/// </summary>
[TestClass]
public sealed class PackageVersionTests
{
    private static readonly Assembly Kernel = typeof(SchemaKitPackages).Assembly;

    private static string KernelVersion
    {
        get
        {
            var version = Kernel.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            return version.Contains('+') ? version[..version.IndexOf('+')] : version;
        }
    }

    /// <summary>An assembly that says it was built as the version, as a package's does.</summary>
    private static Assembly Package(string name, string? informationalVersion)
    {
        var attributes = informationalVersion is null
            ? Array.Empty<CustomAttributeBuilder>()
            : [new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informationalVersion])];
        return AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name) { Version = new Version(11, 0, 0, 0) }, AssemblyBuilderAccess.Run, attributes);
    }

    [TestMethod]
    public void A_package_of_the_kernels_version_is_taken_whatever_commit_either_was_built_from()
    {
        SchemaKitPackages.RequireKernel(Package("Some.Package", KernelVersion));
        SchemaKitPackages.RequireKernel(Package("Some.Package", KernelVersion + "+0123456789abcdef"));
    }

    [TestMethod]
    public void A_package_of_another_version_is_refused_with_both_versions_and_both_names()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => SchemaKitPackages.RequireKernel(Package("Some.Package", "11.0.0-preview.0+0123456789abcdef")));

        StringAssert.Contains(error.Message, $"Some.Package 11.0.0-preview.0 is loaded beside CodoMetis.EFCore.SchemaKit {KernelVersion}.");
        StringAssert.Contains(error.Message, "Reference Some.Package and CodoMetis.EFCore.SchemaKit at one version");
    }

    /// <summary>
    /// The assembly version is 11.0.0.0 for every preview, so a package that states no
    /// informational version must not pass on it.
    /// </summary>
    [TestMethod]
    public void The_assembly_version_alone_does_not_pass()
    {
        Assert.AreEqual(new Version(11, 0, 0, 0), Kernel.GetName().Version, "If this moves with the package version, the check may read it.");

        Assert.ThrowsExactly<InvalidOperationException>(() => SchemaKitPackages.RequireKernel(Package("Some.Package", null)));
    }

    /// <summary>The kernel's own version has a suffix to tell apart: without one the test above would prove nothing about previews.</summary>
    [TestMethod]
    public void The_kernel_states_its_package_version()
        => StringAssert.Matches(KernelVersion, new System.Text.RegularExpressions.Regex(@"^11\.\d+\.\d+(-[0-9A-Za-z.]+)?$"));
}
