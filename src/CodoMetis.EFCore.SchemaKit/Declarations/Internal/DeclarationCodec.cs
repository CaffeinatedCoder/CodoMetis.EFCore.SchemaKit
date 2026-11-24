using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit.Declarations.Internal;

/// <summary>
/// The stored form of a declaration: its annotation key and its annotation value. Both end up in
/// consumers' model snapshots, so everything here is permanent from 11.0.0.
/// </summary>
/// <remarks>
/// <para>
/// <b>The value</b> is one JSON object, written by hand in a fixed order: <c>$kind</c>, <c>v</c>,
/// <c>r</c>, <c>name</c> when there is one, then the declaration's own properties in the order it
/// writes them. <c>v</c> is the format version. The 5.x blobs are the format before 2, which
/// 11.0.0-preview.1 wrote with SQL as one string; 3 holds SQL as segments. A reader that meets
/// another version refuses it.
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
    public const int FormatVersion = 3;

    // Strings do not go through this encoder: CanonicalJson writes them, by a rule that is the same
    // on every runtime and that keeps an operator readable in a snapshot ("&&", not "\u0026\u0026").
    // What is left to the encoder are property names, which are ASCII letters, digits and "_", and
    // "$kind". The relaxed encoder writes each of those as itself.
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string KeyPrefix(string owner) => $"{Prefix}{owner}:";

    /// <summary>The owner in a declaration's annotation key, or null when the annotation is not a declaration.</summary>
    public static string? OwnerOf(string annotation)
    {
        if (!annotation.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        // CodoMetis:{Owner}:{hash}, and an owner holds no colon.
        var parts = annotation.Split(':');
        return parts.Length == 3 && parts[1].Length > 0 && parts[2].Length > 0 ? parts[1] : null;
    }

    /// <summary>
    /// Whether the declaration holds a lambda that is not bound yet. Its stored form does not exist
    /// until its model is finalized; its name, if it has one, is its key already.
    /// </summary>
    public static bool IsUnbound(ISchemaObject declaration)
    {
        Validate(declaration);
        TemplateWriting templates = new() { AsWritten = true };
        Json(declaration, forIdentity: false, templates);
        return templates.SawUnbound;
    }

    /// <param name="declaration">The declaration.</param>
    /// <param name="bind">What makes segments of a lambda, for a declaration that is not bound yet; null for one that is.</param>
    public static string Key(ISchemaObject declaration, Func<UnboundLambda, SqlTemplate>? bind = null)
    {
        Validate(declaration);
        var identity = declaration.Name is { } name
            ? $"name\n{declaration.Kind}\n{Fold(name, declaration.NameComparison)}"
            : $"content\n{declaration.Kind}\n{Json(declaration, forIdentity: true, new TemplateWriting { Bind = bind })}";

        return KeyPrefix(declaration.Owner) + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    /// <param name="declaration">The declaration.</param>
    /// <param name="bind">What makes segments of a lambda, for a declaration that is not bound yet; null for one that is.</param>
    public static string Value(ISchemaObject declaration, Func<UnboundLambda, SqlTemplate>? bind = null)
    {
        Validate(declaration);
        return Json(declaration, forIdentity: false, new TemplateWriting { Bind = bind });
    }

    /// <summary>What a message calls the declaration: its owner, its kind, and its name or its content, a lambda as C# prints it.</summary>
    public static string Describe(ISchemaObject declaration)
        => declaration.Name is { } name
            ? $"{declaration.Owner} {declaration.Kind} '{name}'"
            : $"{declaration.Owner} {declaration.Kind} {Json(declaration, forIdentity: true, new TemplateWriting { AsWritten = true })}";

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

            // Before anything is read: a property that is there twice has two values, and the reader
            // would take the second without a word.
            if (Repeated(root, key) is var (path, name))
                throw new InvalidOperationException(SchemaKitStrings.DeclarationPropertyTwice(path, name));

            // The version first: what the other properties mean depends on it.
            // The kind before the number: TryGetInt32 throws on anything that is not a number, with
            // a message of its own that names no annotation.
            int? version = root.TryGetProperty(DeclarationCodecNames.Version, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number)
                ? number
                : null;
            if (version != FormatVersion)
                throw new InvalidOperationException(SchemaKitStrings.UnknownFormatVersion(key, v.ValueKind == JsonValueKind.Undefined ? "none" : v.GetRawText(), version, FormatVersion));

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

    /// <summary>The first property an object of the value holds twice, with the path of that object.</summary>
    private static (string Path, string Name)? Repeated(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> seen = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) return (path, property.Name);
                    if (Repeated(property.Value, $"{path}.{property.Name}") is { } nested) return nested;
                }

                return null;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (Repeated(item, $"{path}[{index++}]") is { } nested) return nested;
                }

                return null;

            default:
                return null;
        }
    }

    private static void Validate(ISchemaObject declaration)
    {
        var owner = declaration.Owner;
        if (string.IsNullOrEmpty(owner) || owner.Any(c => c == ':' || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException(SchemaKitStrings.InvalidOwner(declaration.GetType(), owner));
        if (string.IsNullOrEmpty(declaration.Kind))
            throw new InvalidOperationException(SchemaKitStrings.DeclarationWithoutKind(declaration.GetType()));

        // A named declaration's identity is its kind and its name with a line break between them.
        // A kind that brings one would give two declarations one key: kind "index\nx" named "y",
        // and kind "index" named "x\ny".
        if (declaration.Kind.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException(SchemaKitStrings.InvalidKind(declaration.GetType(), declaration.Kind));
        if (declaration.Name is { Length: 0 })
            throw new InvalidOperationException(SchemaKitStrings.EmptyDeclarationName(declaration.Owner, declaration.Kind));
        if (declaration.Epoch < 1)
            throw new InvalidOperationException(SchemaKitStrings.InvalidEpoch(declaration.Owner, declaration.Kind, declaration.Epoch));
    }

    private static string Json(ISchemaObject declaration, bool forIdentity, TemplateWriting templates)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
        {
            json.WriteStartObject();
            if (!forIdentity)
            {
                json.WriteCanonicalString(DeclarationCodecNames.Kind, declaration.Kind);
                json.WriteNumber(DeclarationCodecNames.Version, FormatVersion);
                json.WriteNumber(DeclarationCodecNames.Epoch, declaration.Epoch);
                if (declaration.Name is { } name) json.WriteCanonicalString(DeclarationCodecNames.Name, name);
            }

            declaration.Write(new SchemaJsonWriter(json, forIdentity, templates));
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
