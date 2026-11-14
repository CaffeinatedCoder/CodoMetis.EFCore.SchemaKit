using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>What a SchemaKit package asks of the kernel it finds itself beside.</summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public static class SchemaKitPackages
{
    /// <summary>
    /// Refuses a package that was not built with this kernel. A package of this repository depends
    /// on the kernel at exactly its own version, and a consumer that references another kernel
    /// directly gets a warning from NuGet (NU1608) and a build: the first call into a member that
    /// differs between the two then fails with a <see cref="MissingMethodException" /> that names
    /// neither package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A package calls it first in its registration, from a method that calls nothing else of the
    /// kernel besides it: the runtime binds a method's calls when it compiles the method, so a
    /// registration that calls a missing member fails before its first statement runs.
    /// </para>
    /// <para>
    /// This signature is what makes a mixed pair a message, so it does not change: it has been here
    /// since the first preview, and a version that changed it would be refused by the runtime and
    /// not by this method.
    /// </para>
    /// </remarks>
    /// <param name="package">The calling package's assembly.</param>
    /// <exception cref="InvalidOperationException">The two were built as different versions.</exception>
    public static void RequireKernel(Assembly package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var kernel = typeof(SchemaKitPackages).Assembly;
        var (theirs, ours) = (VersionOf(package), VersionOf(kernel));
        if (!string.Equals(theirs, ours, StringComparison.Ordinal))
            throw new InvalidOperationException(SchemaKitStrings.MixedVersions(
                package.GetName().Name ?? package.ToString(), theirs, kernel.GetName().Name!, ours));
    }

    // The informational version is the package version, with the commit after a '+' when Source
    // Link stamps it. The assembly version is 11.0.0.0 for every preview and says nothing here.
    private static string VersionOf(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "";
        var commit = version.IndexOf('+', StringComparison.Ordinal);
        return commit < 0 ? version : version[..commit];
    }
}
