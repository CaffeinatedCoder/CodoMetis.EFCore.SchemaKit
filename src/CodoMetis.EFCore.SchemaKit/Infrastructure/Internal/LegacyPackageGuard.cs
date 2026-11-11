using System.Reflection;
using Microsoft.EntityFrameworkCore.Design;

namespace CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

/// <summary>
/// Refuses an assembly that still references the EFCore.ComplexIndexes packages.
/// </summary>
/// <remarks>
/// Those packages inject a <see cref="DesignTimeServicesReferenceAttribute" /> into the consuming
/// assembly through a <c>.targets</c> file. EF Core adds referenced design-time services after the
/// context's own, so the 5.x differ would scaffold the migration and every SchemaKit declaration
/// would be missing from it, with the snapshot hiding that from every later diff. The attribute is
/// the thing that does the damage, so it is what this looks for, not a loaded assembly.
/// </remarks>
internal static class LegacyPackageGuard
{
    private const string LegacyAssemblyPrefix = "EFCore.ComplexIndexes";

    public static void ThrowIfReferenced(params Assembly?[] assemblies)
    {
        foreach (var assembly in assemblies.OfType<Assembly>().Distinct())
            if (Find(assembly.GetCustomAttributes<DesignTimeServicesReferenceAttribute>()) is { } reference)
                throw new InvalidOperationException(
                    SchemaKitStrings.LegacyPackageReferenced(assembly.GetName().Name ?? assembly.ToString(), reference));
    }

    /// <summary>The first reference whose type lives in an EFCore.ComplexIndexes assembly, or null.</summary>
    public static string? Find(IEnumerable<DesignTimeServicesReferenceAttribute> references)
        => references.Select(r => r.TypeName).FirstOrDefault(IsLegacy);

    // An assembly-qualified name: "Namespace.Type, Assembly[, Version=…]". The namespace alone does
    // not decide it: a consumer's own type may sit in a namespace of that name.
    private static bool IsLegacy(string typeName)
    {
        var parts = typeName.Split(',');
        return parts.Length > 1 && parts[1].Trim().StartsWith(LegacyAssemblyPrefix, StringComparison.Ordinal);
    }
}
