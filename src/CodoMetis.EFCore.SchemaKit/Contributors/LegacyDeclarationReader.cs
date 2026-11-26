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

    bool ILegacyDeclarationSource.OnProperties => false;

    IReadOnlyList<ISchemaObject> ILegacyDeclarationSource.Read(IReadOnlyEntityType entityType, string? path, IReadOnlyProperty? property, object? value)
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
/// Reads the declarations an earlier package stored on a property: an annotation of the property
/// that marks it, with whatever other annotations of that property describe the object.
/// <c>EFCore.ComplexIndexes</c> 5.x stored an index declared on a property that way.
/// </summary>
/// <remarks>
/// It is asked for every property of an entity type that carries the annotation, the properties
/// of its complex types included, and for no other model than
/// <see cref="LegacyDeclarationReader{TDeclaration}" /> is asked for. The same rules hold: what it
/// returns is a declaration like any other, and what the declaration cannot say is thrown.
/// </remarks>
/// <typeparam name="TDeclaration">The declaration the annotation is read into.</typeparam>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public abstract class LegacyPropertyDeclarationReader<TDeclaration> : ILegacyDeclarationSource
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
{
    /// <summary>The name of the annotation on a property that this reader reads.</summary>
    public abstract string Annotation { get; }

    /// <summary>The declarations the property's annotations hold.</summary>
    /// <param name="entityType">The entity type the property belongs to, directly or through complex properties.</param>
    /// <param name="path">The property's path from the entity type, as a declaration names it: <c>Address.City</c>.</param>
    /// <param name="property">The property that carries the annotation.</param>
    /// <param name="value">The annotation's value.</param>
    /// <exception cref="InvalidOperationException">The annotations hold something <typeparamref name="TDeclaration" /> cannot say.</exception>
    protected abstract IEnumerable<TDeclaration> Read(IReadOnlyEntityType entityType, string path, IReadOnlyProperty property, object? value);

    string ILegacyDeclarationSource.Owner => TDeclaration.OwnerId;

    bool ILegacyDeclarationSource.OnProperties => true;

    IReadOnlyList<ISchemaObject> ILegacyDeclarationSource.Read(IReadOnlyEntityType entityType, string? path, IReadOnlyProperty? property, object? value)
    {
        var declarations = Read(entityType, path!, property!, value)
                           ?? throw new InvalidOperationException(SchemaKitStrings.LegacyReaderReturnedNull(GetType()));
        return [.. declarations.Select(declaration
            => declaration ?? throw new InvalidOperationException(SchemaKitStrings.LegacyReaderReturnedNull(GetType())))];
    }
}

/// <summary>
/// How the kernel asks a reader without knowing its declaration's type. Only
/// <see cref="LegacyDeclarationReader{TDeclaration}" /> and
/// <see cref="LegacyPropertyDeclarationReader{TDeclaration}" /> implement it.
/// </summary>
internal interface ILegacyDeclarationSource
{
    string Annotation { get; }

    string Owner { get; }

    /// <summary>Whether the annotation sits on a property, and not on the entity type.</summary>
    bool OnProperties { get; }

    /// <param name="entityType">The entity type.</param>
    /// <param name="path">The property's path, for a reader of properties; null otherwise.</param>
    /// <param name="property">The property, for a reader of properties; null otherwise.</param>
    /// <param name="value">The annotation's value.</param>
    IReadOnlyList<ISchemaObject> Read(IReadOnlyEntityType entityType, string? path, IReadOnlyProperty? property, object? value);

    /// <summary>
    /// Every property of the entity type that is declared on it and carries the annotation, with
    /// its path, the properties of its complex types included. Declared on it: a property a
    /// derived type inherits is its base type's, and is read there.
    /// </summary>
    static IEnumerable<(string Path, IReadOnlyProperty Property, object? Value)> Marked(IReadOnlyEntityType entityType, string annotation)
    {
        foreach (var property in entityType.GetDeclaredProperties())
        {
            if (property.FindAnnotation(annotation) is { } found) yield return (property.Name, property, found.Value);
        }

        foreach (var complex in entityType.GetDeclaredComplexProperties())
        {
            foreach (var marked in Marked(complex.ComplexType, $"{complex.Name}.", annotation)) yield return marked;
        }
    }

    private static IEnumerable<(string Path, IReadOnlyProperty Property, object? Value)> Marked(IReadOnlyTypeBase type, string prefix, string annotation)
    {
        foreach (var property in type.GetProperties())
        {
            if (property.FindAnnotation(annotation) is { } found) yield return (prefix + property.Name, property, found.Value);
        }

        foreach (var complex in type.GetComplexProperties())
        {
            foreach (var marked in Marked(complex.ComplexType, $"{prefix}{complex.Name}.", annotation)) yield return marked;
        }
    }
}
