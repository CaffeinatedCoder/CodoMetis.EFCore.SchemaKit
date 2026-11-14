using System.Runtime.CompilerServices;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Metadata.Internal;

/// <summary>
/// The mark <c>UseSchemaKit()</c> leaves on a model when it is created. A declaration API calls
/// <see cref="Require(IReadOnlyModel, string, string, Type)" /> before it stores anything, so a model that declares a SchemaKit object
/// without the wiring fails in <c>OnModelCreating</c>, on every path, instead of scaffolding and
/// applying without the object.
/// </summary>
/// <remarks>
/// The annotation is written into model snapshots, as <c>Relational:MaxIdentifierLength</c> is, so
/// its name and value are permanent from 11.0.0 and the value never changes with the package
/// version: a changing value would rewrite every consumer's snapshot on upgrade.
/// <para>
/// Which owners the context can render is kept beside the model and not on it (#113). It differs
/// with the packages a context registers, so as an annotation it would be written into the snapshot
/// and move with them. It is needed only while the model is configured, and a snapshot's model,
/// which declares nothing, has none.
/// </para>
/// </remarks>
internal static class SchemaKitModelStamp
{
    public const string Annotation = "CodoMetis:SchemaKit";
    public const string Value = "1";

    private static readonly ConditionalWeakTable<object, HashSet<string>> Owners = new();

    /// <summary>Stamps a model, with the owners the context has a contributor for.</summary>
    public static void Stamp(IMutableModel model, IEnumerable<string> owners)
    {
        model.SetAnnotation(Annotation, Value);
        Owners.AddOrUpdate(model, new HashSet<string>(owners, StringComparer.Ordinal));
    }

    /// <param name="model">The model being configured.</param>
    /// <param name="declaring">What declares, for the message: an entity type's display name.</param>
    public static void Require(IReadOnlyModel model, string declaring)
    {
        if (model.FindAnnotation(Annotation) is null)
            throw new InvalidOperationException(SchemaKitStrings.NotWired(declaring));
    }

    /// <summary>
    /// Requires the stamp and a contributor for the declaration's owner. The kernel's
    /// <c>UseSchemaKit()</c> alone stamps a model and renders nothing: without this the declaration
    /// was stored and the refusal came at the first diff.
    /// </summary>
    /// <param name="model">The model being configured.</param>
    /// <param name="declaring">What declares, for the message: an entity type's display name.</param>
    /// <param name="owner">The declaration's owner.</param>
    /// <param name="declaration">The declaration's type, whose assembly is the package to register.</param>
    public static void Require(IReadOnlyModel model, string declaring, string owner, Type declaration)
    {
        Require(model, declaring);
        if (!Owners.TryGetValue(model, out var owners) || !owners.Contains(owner))
            throw new InvalidOperationException(SchemaKitStrings.NotWiredForOwner(
                declaring, owner, declaration, declaration.Assembly.GetName().Name ?? declaration.Assembly.ToString()));
    }
}
