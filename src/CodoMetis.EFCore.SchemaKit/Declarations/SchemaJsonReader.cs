using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// Reads a declaration's properties back from its annotation: one method per method of
/// <see cref="SchemaJsonWriter" />, under the same name.
/// </summary>
/// <remarks>
/// A property read with a method that is not <c>Optional…</c> has to be there, and one read with an
/// <c>Optional…</c> method is null when it is not. The reader has no default of its own, so a value
/// cannot change on the way through a snapshot. A property nothing read is refused when the
/// declaration has been rebuilt: it is something a newer version wrote and this one would drop.
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed class SchemaJsonReader
{
    private readonly JsonElement _object;
    private readonly string _path;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);
    private readonly List<SchemaJsonReader> _nested = [];

    internal SchemaJsonReader(JsonElement @object, string path)
    {
        _object = @object;
        _path = path;
    }

    /// <summary>A string that was written.</summary>
    public string String(string name) => OptionalString(name) ?? throw Missing(name);

    /// <summary>A string, or null when none was written.</summary>
    public string? OptionalString(string name)
        => Find(name, JsonValueKind.String) is { } value ? value.GetString() : null;

    /// <summary>A boolean that was written.</summary>
    public bool Boolean(string name) => OptionalBoolean(name) ?? throw Missing(name);

    /// <summary>A boolean, or null when none was written.</summary>
    public bool? OptionalBoolean(string name)
        => Find(name, JsonValueKind.True, JsonValueKind.False) is { } value ? value.GetBoolean() : null;

    /// <summary>A whole number that was written.</summary>
    public int Int32(string name) => OptionalInt32(name) ?? throw Missing(name);

    /// <summary>A whole number, or null when none was written.</summary>
    public int? OptionalInt32(string name)
    {
        if (Find(name, JsonValueKind.Number) is not { } value) return null;
        return value.TryGetInt32(out var number) ? number : throw Malformed(name, "a whole number");
    }

    /// <summary>A list of strings that was written.</summary>
    public ValueList<string> Strings(string name)
    {
        var array = Find(name, JsonValueKind.Array) ?? throw Missing(name);
        return [.. array.EnumerateArray().Select(item
            => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Malformed(name, "a list of strings"))];
    }

    /// <summary>A nested object that was written.</summary>
    public T Object<T>(string name, Func<SchemaJsonReader, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var value = Find(name, JsonValueKind.Object) ?? throw Missing(name);
        return read(Nested(value, $"{_path}.{name}"));
    }

    /// <summary>A nested object, or null when none was written.</summary>
    public T? OptionalObject<T>(string name, Func<SchemaJsonReader, T> read)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        return Find(name, JsonValueKind.Object) is { } value ? read(Nested(value, $"{_path}.{name}")) : null;
    }

    /// <summary>A list of objects that was written, in its order.</summary>
    public ValueList<T> Array<T>(string name, Func<SchemaJsonReader, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var array = Find(name, JsonValueKind.Array) ?? throw Missing(name);
        return [.. array.EnumerateArray().Select((item, index)
            => item.ValueKind == JsonValueKind.Object
                ? read(Nested(item, $"{_path}.{name}[{index}]"))
                : throw Malformed(name, "a list of objects"))];
    }

    /// <summary>Marks a property as read without reading it: the kernel's own, which it has read itself.</summary>
    internal void Skip(string name) => _read.Add(name);

    /// <summary>Throws when a property was written that nothing read, here or in a nested object.</summary>
    internal void ThrowIfUnread()
    {
        foreach (var property in _object.EnumerateObject())
        {
            if (!_read.Contains(property.Name))
                throw new InvalidOperationException(SchemaKitStrings.DeclarationPropertyNotRead(_path, property.Name));
        }

        foreach (var nested in _nested) nested.ThrowIfUnread();
    }

    private SchemaJsonReader Nested(JsonElement value, string path)
    {
        var reader = new SchemaJsonReader(value, path);
        _nested.Add(reader);
        return reader;
    }

    private JsonElement? Find(string name, params ReadOnlySpan<JsonValueKind> kinds)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _read.Add(name);
        if (!_object.TryGetProperty(name, out var value)) return null;

        foreach (var kind in kinds)
        {
            if (value.ValueKind == kind) return value;
        }

        throw Malformed(name, kinds[0] switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a whole number",
            JsonValueKind.Array  => "a list",
            JsonValueKind.Object => "an object",
            _                    => "true or false",
        });
    }

    private InvalidOperationException Missing(string name)
        => new(SchemaKitStrings.DeclarationPropertyMissing(_path, name));

    private InvalidOperationException Malformed(string name, string expected)
        => new(SchemaKitStrings.DeclarationPropertyMalformed(_path, name, expected));
}
