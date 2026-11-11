using System.Globalization;
using System.Text;
using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit.Declarations.Internal;

/// <summary>
/// How a string is written into a stored declaration: by one fixed rule, and by none of the
/// runtime's encoders.
/// </summary>
/// <remarks>
/// <para>
/// A key is a hash over this text and sits in consumers' snapshots, so the text for one
/// declaration must be the same on every runtime, for good. The encoders of
/// <c>System.Text.Encodings.Web</c> escape every code point their runtime does not know as
/// assigned, and each Unicode version assigns more: 22 characters were escaped by .NET 9 and
/// written as themselves by .NET 10. Text written through one of them moves a key with the runtime.
/// </para>
/// <para>
/// <b>The rule.</b> <c>"</c> and <c>\</c> are escaped with a backslash. U+0008, U+0009, U+000A,
/// U+000C and U+000D are <c>\b</c>, <c>\t</c>, <c>\n</c>, <c>\f</c> and <c>\r</c>; every other
/// character below U+0020 is <c>\u</c> and four upper-case hex digits, and so are U+0085, U+2028
/// and U+2029. Everything else is written as itself. The rule reads no table.
/// </para>
/// <para>
/// The first part is what JSON requires. The last three are C#'s line terminators besides CR and
/// LF: EF Core writes an annotation's value into a string literal in the model snapshot and does
/// not escape them, so a value holding one would be a snapshot that does not compile.
/// <c>SnapshotRoundTripTests</c> holds that with a compiler.
/// </para>
/// <para>
/// A string that is not well-formed UTF-16 is refused: it has no UTF-8 form, and writing U+FFFD
/// in its place would store another string than the one declared.
/// </para>
/// </remarks>
internal static class CanonicalJson
{
    /// <summary>The JSON string literal for <paramref name="value" />, quotes included.</summary>
    /// <param name="value">The string.</param>
    /// <param name="property">The property it is written for, for the message.</param>
    public static string Quote(string value, string property)
    {
        var text = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':  text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\t': text.Append("\\t"); break;
                case '\n': text.Append("\\n"); break;
                case '\f': text.Append("\\f"); break;
                case '\r': text.Append("\\r"); break;
                case < ' ' or '\u0085' or '\u2028' or '\u2029':
                    text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (!char.IsSurrogate(c))
                    {
                        text.Append(c);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        text.Append(c).Append(value[++i]);
                    }
                    else
                    {
                        throw new InvalidOperationException(SchemaKitStrings.TextIsNotUnicode(property, i));
                    }
                    break;
            }
        }

        return text.Append('"').ToString();
    }

    /// <summary>Writes a string property.</summary>
    public static void WriteCanonicalString(this Utf8JsonWriter json, string name, string value)
    {
        json.WritePropertyName(name);
        json.WriteRawValue(Quote(value, name), skipInputValidation: true);
    }

    /// <summary>Writes a string as an element of the array <paramref name="name" />.</summary>
    public static void WriteCanonicalStringValue(this Utf8JsonWriter json, string name, string value)
        => json.WriteRawValue(Quote(value, name), skipInputValidation: true);

    /// <summary>
    /// Whether a property name is one the writer takes: ASCII letters, digits and <c>_</c>. A name
    /// goes through <see cref="Utf8JsonWriter" />'s encoder, which has no raw form for one, and
    /// these are the characters no encoder escapes.
    /// </summary>
    public static bool IsPropertyName(string name)
    {
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }

        return name.Length > 0;
    }
}
