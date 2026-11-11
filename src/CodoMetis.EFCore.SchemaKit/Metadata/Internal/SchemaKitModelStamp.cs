using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Metadata.Internal;

/// <summary>
/// The mark <c>UseSchemaKit()</c> leaves on a model when it is created. A declaration API calls
/// <see cref="Require" /> before it stores anything, so a model that declares a SchemaKit object
/// without the wiring fails in <c>OnModelCreating</c>, on every path, instead of scaffolding and
/// applying without the object.
/// </summary>
/// <remarks>
/// The annotation is written into model snapshots, as <c>Relational:MaxIdentifierLength</c> is, so
/// its name and value are permanent from 11.0.0 and the value never changes with the package
/// version: a changing value would rewrite every consumer's snapshot on upgrade.
/// </remarks>
internal static class SchemaKitModelStamp
{
    public const string Annotation = "CodoMetis:SchemaKit";
    public const string Value = "1";

    /// <param name="model">The model being configured.</param>
    /// <param name="declaring">What declares, for the message: an entity type's display name.</param>
    public static void Require(IReadOnlyModel model, string declaring)
    {
        if (model.FindAnnotation(Annotation) is null)
            throw new InvalidOperationException(SchemaKitStrings.NotWired(declaring));
    }
}
