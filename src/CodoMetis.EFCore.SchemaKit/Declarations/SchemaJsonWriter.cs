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

    private readonly TemplateWriting _templates;

    internal SchemaJsonWriter(Utf8JsonWriter json, bool forIdentity, TemplateWriting templates)
        : this(json, new HashSet<string>(StringComparer.Ordinal), root: true, forIdentity, silent: false, templates)
    {
    }

    private SchemaJsonWriter(Utf8JsonWriter json, HashSet<string> names, bool root, bool forIdentity, bool silent, TemplateWriting templates)
    {
        _json = json;
        _names = names;
        _root = root;
        _forIdentity = forIdentity;
        _silent = silent;
        _templates = templates;
    }

    /// <summary>
    /// Writes what changes how DDL runs and not what it builds, such as <c>CONCURRENTLY</c>. It is
    /// stored like everything else and left out of the declaration's identity, so two declarations
    /// that differ only here are one object.
    /// </summary>
    public SchemaJsonWriter Operational => new(_json, _names, _root, _forIdentity, silent: _silent || _forIdentity, _templates);

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

    /// <summary>SQL as a declaration holds it: a list in which a string is text a package wrote, <c>{"col":…}</c> a column by its property path and <c>{"raw":…}</c> SQL written by hand.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The template.</param>
    public void Template(string name, SqlTemplate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Claim(name)) WriteTemplate(name, value);
    }

    /// <summary>SQL as a declaration holds it, or nothing when it is null.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The template, or null.</param>
    public void OptionalTemplate(string name, SqlTemplate? value)
    {
        if (Claim(name) && value is not null) WriteTemplate(name, value);
    }

    private void WriteTemplate(string name, SqlTemplate value)
    {
        if (!value.IsBound)
        {
            // Asked of the stored value, where an operational property is written too.
            _templates.SawUnbound = true;

            // What a message shows of a declaration that is not bound yet. It is never stored.
            if (_templates.Bind is null && _templates.AsWritten)
            {
                _json.WriteCanonicalString(name, value.ToString());
                return;
            }

            value = value.Bind(_templates.Bind ?? throw new InvalidOperationException(SchemaKitStrings.DeclarationNotBound(value.ToString())));
        }

        _json.WriteStartArray(name);
        foreach (var segment in value.Segments)
        {
            switch (segment)
            {
                case SqlSegment.Text text:
                    _json.WriteCanonicalStringValue(name, text.Sql);
                    break;
                case SqlSegment.Column column:
                    _json.WriteStartObject();
                    _json.WriteCanonicalString(DeclarationCodecNames.Column, column.Path);
                    _json.WriteEndObject();
                    break;
                case SqlSegment.Raw raw:
                    _json.WriteStartObject();
                    _json.WriteCanonicalString(DeclarationCodecNames.Raw, raw.Sql);
                    _json.WriteEndObject();
                    break;
            }
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

    private SchemaJsonWriter Nested() => new(_json, new HashSet<string>(StringComparer.Ordinal), root: false, _forIdentity, _silent, _templates);

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
/// <summary>What a writer does with a template that holds a lambda which is not bound yet.</summary>
internal sealed class TemplateWriting
{
    /// <summary>Makes segments of a lambda. With it, a declaration is written as it is stored.</summary>
    public Func<Translation.Internal.UnboundLambda, SqlTemplate>? Bind { get; init; }

    /// <summary>Without <see cref="Bind" />: write the lambda as C# prints it, for a message, where the writer otherwise refuses.</summary>
    public bool AsWritten { get; init; }

    /// <summary>Whether the declaration that was written holds such a template, in any property.</summary>
    public bool SawUnbound { get; set; }
}

internal static class DeclarationCodecNames
{
    public const string Kind = "$kind";
    public const string Version = "v";
    public const string Epoch = "r";
    public const string Name = "name";

    /// <summary>In a template, the property of a segment that is a column: its property path.</summary>
    public const string Column = "col";

    /// <summary>In a template, the property of a segment that is SQL written by hand.</summary>
    public const string Raw = "raw";

    public static bool IsReserved(string name) => name is Kind or Version or Epoch or Name;
}
