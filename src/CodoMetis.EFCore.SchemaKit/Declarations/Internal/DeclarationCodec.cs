using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit.Declarations.Internal;

/// <summary>
/// The stored form of a declaration: its annotation key and its annotation value. Both end up in
/// consumers' model snapshots, so everything here is permanent from 11.0.0.
/// </summary>
/// <remarks>
/// <para>
/// <b>The value</b> is one JSON object, written by hand in a fixed order: <c>$kind</c>, <c>v</c>,
/// <c>r</c>, <c>name</c> when there is one, then the declaration's own properties in the order it
/// writes them. <c>v</c> is the format version; 2 is the first, the 5.x blobs being the format
/// before it. A reader that meets another version refuses it.
/// </para>
/// <para>
/// <b>The key</b> is <c>CodoMetis:{Owner}:{hash}</c>, the first 16 hex digits of SHA-256 over the
/// declaration's identity: kind and name for a named declaration, kind and every property that is
/// not operational for one without a name. A declaration never replaces another: the store refuses
/// a key that is taken.
/// </para>
/// </remarks>
internal static class DeclarationCodec
{
    public const string Prefix = "CodoMetis:";

    /// <summary>The format this version writes and the only one it reads here. The 5.x readers are their own.</summary>
    public const int FormatVersion = 2;

    // The relaxed encoder keeps an operator readable in a snapshot ("&&", not "&&"). The
    // value is never put into HTML, and it escapes what JSON itself requires.
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string KeyPrefix(string owner) => $"{Prefix}{owner}:";

    public static string Key(ISchemaObject declaration)
    {
        Validate(declaration);
        var identity = declaration.Name is { } name
            ? $"name\n{declaration.Kind}\n{Fold(name, declaration.NameComparison)}"
            : $"content\n{declaration.Kind}\n{Json(declaration, forIdentity: true)}";

        return KeyPrefix(declaration.Owner) + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    public static string Value(ISchemaObject declaration)
    {
        Validate(declaration);
        return Json(declaration, forIdentity: false);
    }

    /// <summary>What a message calls the declaration: its owner, its kind, and its name or its content.</summary>
    public static string Describe(ISchemaObject declaration)
        => declaration.Name is { } name
            ? $"{declaration.Owner} {declaration.Kind} '{name}'"
            : $"{declaration.Owner} {declaration.Kind} {Json(declaration, forIdentity: true)}";

    public static bool SameName(string left, string right, SchemaNameComparison comparison)
        => string.Equals(Fold(left, comparison), Fold(right, comparison), StringComparison.Ordinal);

    public static TDeclaration Read<TDeclaration>(string key, string value)
        where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(SchemaKitStrings.DeclarationNotJson(key), e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(SchemaKitStrings.DeclarationNotJson(key));

            // The version first: what the other properties mean depends on it.
            if (!root.TryGetProperty(DeclarationCodecNames.Version, out var v) || !v.TryGetInt32(out var version) || version != FormatVersion)
                throw new InvalidOperationException(SchemaKitStrings.UnknownFormatVersion(key, v.ValueKind == JsonValueKind.Undefined ? "none" : v.GetRawText(), FormatVersion));

            var reader = new SchemaJsonReader(root, key);
            reader.Skip(DeclarationCodecNames.Version);
            var header = new SchemaDeclarationHeader(
                reader.String(DeclarationCodecNames.Kind),
                reader.OptionalString(DeclarationCodecNames.Name),
                reader.Int32(DeclarationCodecNames.Epoch));

            var declaration = TDeclaration.Read(header, reader)
                              ?? throw new InvalidOperationException(SchemaKitStrings.ReadReturnedNull(typeof(TDeclaration), key));
            reader.ThrowIfUnread();

            // The declaration is what the differ resolves and what Redeclare writes back. One that
            // does not carry what its header said would move to another key, or resolve under
            // another epoch, the next time it is stored.
            if (declaration.Kind != header.Kind || declaration.Name != header.Name || declaration.Epoch != header.Epoch)
                throw new InvalidOperationException(SchemaKitStrings.ReadLostTheHeader(typeof(TDeclaration), key));

            // Every other property the same way: a Read that takes a property and then drops it, or
            // puts a default in its place, has satisfied the reader and built another declaration.
            // Writing what was read must give what was stored. The comparison is of the JSON and
            // not of the text, so it does not depend on how a character is escaped.
            var rewritten = Value(declaration);
            using var again = JsonDocument.Parse(rewritten);
            if (!JsonElement.DeepEquals(again.RootElement, root))
                throw new InvalidOperationException(SchemaKitStrings.ReadChangedTheDeclaration(typeof(TDeclaration), key, value, rewritten));

            return declaration;
        }
    }

    private static void Validate(ISchemaObject declaration)
    {
        var owner = declaration.Owner;
        if (string.IsNullOrEmpty(owner) || owner.Any(c => c == ':' || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException(SchemaKitStrings.InvalidOwner(declaration.GetType(), owner));
        if (string.IsNullOrEmpty(declaration.Kind))
            throw new InvalidOperationException(SchemaKitStrings.DeclarationWithoutKind(declaration.GetType()));
        if (declaration.Name is { Length: 0 })
            throw new InvalidOperationException(SchemaKitStrings.EmptyDeclarationName(declaration.Owner, declaration.Kind));
        if (declaration.Epoch < 1)
            throw new InvalidOperationException(SchemaKitStrings.InvalidEpoch(declaration.Owner, declaration.Kind, declaration.Epoch));
    }

    private static string Json(ISchemaObject declaration, bool forIdentity)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
        {
            json.WriteStartObject();
            if (!forIdentity)
            {
                json.WriteString(DeclarationCodecNames.Kind, declaration.Kind);
                json.WriteNumber(DeclarationCodecNames.Version, FormatVersion);
                json.WriteNumber(DeclarationCodecNames.Epoch, declaration.Epoch);
                if (declaration.Name is { } name) json.WriteString(DeclarationCodecNames.Name, name);
            }

            declaration.Write(new SchemaJsonWriter(json, forIdentity));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Fold(string name, SchemaNameComparison comparison) => comparison switch
    {
        SchemaNameComparison.IgnoreCase => name.ToUpperInvariant(),
        _                               => name,
    };
}
