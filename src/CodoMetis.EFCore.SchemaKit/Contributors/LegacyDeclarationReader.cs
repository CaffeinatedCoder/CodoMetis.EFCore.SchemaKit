using System.Diagnostics.CodeAnalysis;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// Reads the declarations a model snapshot holds in another format: an annotation an earlier
/// package wrote, such as <c>EFCore.ComplexIndexes</c> 5.x. What it returns is resolved, compared
/// and rendered like a declaration of <typeparamref name="TDeclaration" />, by that declaration's
/// contributor.
/// </summary>
/// <remarks>
/// <para>
/// A reader is asked for the model a migration starts from, which is the snapshot, and never for
/// the model it leads to: there the annotation is refused, since nothing writes it any more and a
/// declaration made twice, once in each format, would be two objects.
/// </para>
/// <para>
/// A reader translates and decides nothing. What the old format could say and
/// <typeparamref name="TDeclaration" /> cannot is thrown, with what to do about it: a constraint
/// left out here is one the next migration would create a second time, or never drop.
/// </para>
/// <para>
/// It is created without the dialect and reads metadata only: it runs before anything is resolved.
/// </para>
/// </remarks>
/// <typeparam name="TDeclaration">The declaration the annotation is read into.</typeparam>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public abstract class LegacyDeclarationReader<TDeclaration> : ILegacyDeclarationSource
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
{
    /// <summary>The name of the annotation on an entity type that this reader reads.</summary>
    public abstract string Annotation { get; }

    /// <summary>The declarations the annotation holds, in the order it holds them.</summary>
    /// <param name="entityType">The entity type that carries the annotation.</param>
    /// <param name="value">The annotation's value.</param>
    /// <exception cref="InvalidOperationException">The value holds something <typeparamref name="TDeclaration" /> cannot say.</exception>
    protected abstract IEnumerable<TDeclaration> Read(IReadOnlyEntityType entityType, string value);

    string ILegacyDeclarationSource.Owner => TDeclaration.OwnerId;

    IReadOnlyList<ISchemaObject> ILegacyDeclarationSource.Read(IReadOnlyEntityType entityType, object? value)
    {
        // An annotation of another format is text in every format there has been. A value of
        // another type is not this reader's, and skipping it would leave its objects out.
        if (value is not string text)
            throw new InvalidOperationException(SchemaKitStrings.LegacyAnnotationIsNotText(entityType.ShortName(), Annotation, value?.GetType()));

        var declarations = Read(entityType, text)
                           ?? throw new InvalidOperationException(SchemaKitStrings.LegacyReaderReturnedNull(GetType()));
        return [.. declarations.Select(declaration
            => declaration ?? throw new InvalidOperationException(SchemaKitStrings.LegacyReaderReturnedNull(GetType())))];
    }
}

/// <summary>
/// How the kernel asks a reader without knowing its declaration's type. Only
/// <see cref="LegacyDeclarationReader{TDeclaration}" /> implements it.
/// </summary>
internal interface ILegacyDeclarationSource
{
    string Annotation { get; }

    string Owner { get; }

    IReadOnlyList<ISchemaObject> Read(IReadOnlyEntityType entityType, object? value);
}
