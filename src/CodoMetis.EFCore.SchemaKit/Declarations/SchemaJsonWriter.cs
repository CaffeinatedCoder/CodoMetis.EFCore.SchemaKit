using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>
/// Writes a declaration's properties into its annotation, in the order they are written.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is left out but a null.</b> There is no way to omit a value because it is the
/// default: a writer that drops <c>false</c> and a reader whose default is <c>true</c> turn a
/// <c>NULLS NOT DISTINCT</c> index into a plain unique one on the way through a snapshot, and the
/// next diff rebuilds it. A property is written, or it is null and absent, and
/// <see cref="SchemaJsonReader" /> reads an absent property as null and as nothing else.
/// </para>
/// <para>
/// A property name is written once per object, and is made of ASCII letters, digits and <c>_</c>.
/// <c>$kind</c>, <c>v</c>, <c>r</c> and <c>name</c> are the kernel's.
/// </para>
/// <para>
/// <b>A string is written by one fixed rule,</b> the same on every runtime: <c>"</c>, <c>\</c> and
/// the characters below U+0020 are escaped, with C#'s three other line terminators, and everything
/// else is written as itself. A declaration's key is a hash over this text. A string that is not
/// well-formed UTF-16 is refused.
/// </para>
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed class SchemaJsonWriter
{
    private readonly Utf8JsonWriter _json;
    private readonly HashSet<string> _names;
    private readonly bool _root;
    private readonly bool _forIdentity;
    private readonly bool _silent;

    internal SchemaJsonWriter(Utf8JsonWriter json, bool forIdentity)
        : this(json, new HashSet<string>(StringComparer.Ordinal), root: true, forIdentity, silent: false)
    {
    }

    private SchemaJsonWriter(Utf8JsonWriter json, HashSet<string> names, bool root, bool forIdentity, bool silent)
    {
        _json = json;
        _names = names;
        _root = root;
        _forIdentity = forIdentity;
        _silent = silent;
    }

    /// <summary>
    /// Writes what changes how DDL runs and not what it builds, such as <c>CONCURRENTLY</c>. It is
    /// stored like everything else and left out of the declaration's identity, so two declarations
    /// that differ only here are one object.
    /// </summary>
    public SchemaJsonWriter Operational => new(_json, _names, _root, _forIdentity, silent: _silent || _forIdentity);

    /// <summary>A string.</summary>
    public void String(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Claim(name)) _json.WriteCanonicalString(name, value);
    }

    /// <summary>A string, or nothing when it is null.</summary>
    public void OptionalString(string name, string? value)
    {
        if (Claim(name) && value is not null) _json.WriteCanonicalString(name, value);
    }

    /// <summary>A boolean. Written whether it is true or false.</summary>
    public void Boolean(string name, bool value)
    {
        if (Claim(name)) _json.WriteBoolean(name, value);
    }

    /// <summary>A boolean, or nothing when it is null.</summary>
    public void OptionalBoolean(string name, bool? value)
    {
        if (Claim(name) && value is { } v) _json.WriteBoolean(name, v);
    }

    /// <summary>A whole number. Written whether it is zero or not.</summary>
    public void Int32(string name, int value)
    {
        if (Claim(name)) _json.WriteNumber(name, value);
    }

    /// <summary>A whole number, or nothing when it is null.</summary>
    public void OptionalInt32(string name, int? value)
    {
        if (Claim(name) && value is { } v) _json.WriteNumber(name, v);
    }

    /// <summary>A list of strings. Written when it is empty too.</summary>
    public void Strings(string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Claim(name)) return;

        _json.WriteStartArray(name);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(values));
            _json.WriteCanonicalStringValue(name, value);
        }
        _json.WriteEndArray();
    }

    /// <summary>A nested object.</summary>
    public void Object(string name, Action<SchemaJsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!Claim(name)) return;

        _json.WriteStartObject(name);
        write(Nested());
        _json.WriteEndObject();
    }

    /// <summary>A nested object, or nothing when <paramref name="value" /> is null.</summary>
    public void OptionalObject<T>(string name, T? value, Action<SchemaJsonWriter, T> write)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!Claim(name) || value is null) return;

        _json.WriteStartObject(name);
        write(Nested(), value);
        _json.WriteEndObject();
    }

    /// <summary>A list of objects, in their order. Written when it is empty too.</summary>
    public void Array<T>(string name, IEnumerable<T> items, Action<SchemaJsonWriter, T> write)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(write);
        if (!Claim(name)) return;

        _json.WriteStartArray(name);
        foreach (var item in items)
        {
            _json.WriteStartObject();
            write(Nested(), item);
            _json.WriteEndObject();
        }
        _json.WriteEndArray();
    }

    private SchemaJsonWriter Nested() => new(_json, new HashSet<string>(StringComparer.Ordinal), root: false, _forIdentity, _silent);

    /// <summary>Checks the name and says whether the property is written at all.</summary>
    private bool Claim(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!CanonicalJson.IsPropertyName(name))
            throw new InvalidOperationException(SchemaKitStrings.InvalidPropertyName(name));
        if (_root && DeclarationCodecNames.IsReserved(name))
            throw new InvalidOperationException(SchemaKitStrings.ReservedPropertyName(name));
        if (!_names.Add(name))
            throw new InvalidOperationException(SchemaKitStrings.PropertyWrittenTwice(name));

        return !_silent;
    }
}

/// <summary>The property names of a stored declaration that are the kernel's.</summary>
internal static class DeclarationCodecNames
{
    public const string Kind = "$kind";
    public const string Version = "v";
    public const string Epoch = "r";
    public const string Name = "name";

    public static bool IsReserved(string name) => name is Kind or Version or Epoch or Name;
}
