using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// An immutable list that compares by its elements. A declaration or a descriptor that holds one
/// keeps the value equality its record gives it; with an array or a <see cref="List{T}" /> two equal
/// descriptors would compare unequal and the differ would rebuild the object on every diff.
/// </summary>
/// <typeparam name="T">The element type. Its own equality decides the list's.</typeparam>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly ImmutableArray<T> _items;

    internal ValueList(ImmutableArray<T> items) => _items = items;

    /// <summary>The list with no elements.</summary>
    public static ValueList<T> Empty { get; } = new([]);

    /// <inheritdoc />
    public int Count => _items.Length;

    /// <inheritdoc />
    public T this[int index] => _items[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Whether both lists hold equal elements in the same order.</summary>
    public bool Equals(ValueList<T>? other) => other is not null && _items.SequenceEqual(other._items);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ValueList<T>);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }

    /// <summary>The elements, for messages.</summary>
    public override string ToString() => $"[{string.Join(", ", _items)}]";
}

/// <summary>Builds a <see cref="ValueList{T}" />; a collection expression calls it.</summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public static class ValueList
{
    /// <summary>A list of the given elements, in their order.</summary>
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => new([.. items]);
}

/// <summary>
/// A descriptor field that changes how DDL runs and not what it builds, such as
/// <c>CONCURRENTLY</c>. It is carried to rendering and never compared, so changing it never rebuilds
/// the object.
/// </summary>
/// <typeparam name="T">The option's type.</typeparam>
/// <param name="Value">The option.</param>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public readonly record struct Operational<T>(T Value)
{
    /// <summary>Always true: two operational values never make two descriptors differ.</summary>
    public bool Equals(Operational<T> other) => true;

    /// <inheritdoc />
    public override int GetHashCode() => 0;
}
