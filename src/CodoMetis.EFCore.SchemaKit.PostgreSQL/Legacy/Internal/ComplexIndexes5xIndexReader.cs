using System.Globalization;
using System.Text;
using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Legacy.Internal;

/// <summary>
/// Reads the indexes <c>EFCore.ComplexIndexes</c> 5.x stored on an entity type: the annotation
/// <c>CustomIndex:CompositeIndexes</c>, a JSON list written by <c>HasComplexIndex</c> on an entity
/// type, <c>HasComplexCompositeIndex</c> and <c>HasExpressionIndex</c>.
/// </summary>
/// <remarks>
/// <para>
/// The format is 5.x's and is finished: <c>paths</c>, or <c>parts</c> (each a <c>path</c>, an
/// <c>expr</c> or a <c>tmpl</c>, with <c>desc</c> and <c>nulls</c>), <c>unique</c>,
/// <c>filter</c>, <c>name</c>, and <c>props</c> with Npgsql's options. This file changes only
/// when a 5.x snapshot is found that it reads wrongly.
/// </para>
/// <para>
/// What is read is in <see cref="ComplexIndexes5xIndexes" />, which the reader of an index
/// declared on a property shares.
/// </para>
/// </remarks>
internal sealed class ComplexIndexes5xIndexReader : LegacyDeclarationReader<PgIndex>
{
    /// <summary>5.x's <c>ComplexIndexAnnotations.CompositeIndexes</c>.</summary>
    public const string AnnotationName = "CustomIndex:CompositeIndexes";

    public override string Annotation => AnnotationName;

    protected override IEnumerable<PgIndex> Read(IReadOnlyEntityType entityType, string value)
    {
        // 5.x read an empty annotation as no indexes, and so it is here.
        if (value.Length == 0) return [];

        var entity = entityType.ShortName();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, exception.Message), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, "it is not a list"));

            return [.. document.RootElement.EnumerateArray().Select(index => ReadIndex(entityType, index))];
        }
    }

    private static PgIndex ReadIndex(IReadOnlyEntityType entityType, JsonElement json)
    {
        var entity = entityType.ShortName();
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, "an index is not an object"));

        List<ComplexIndexes5xIndexes.Part>? paths = null, parts = null;
        string? filter = null, name = null;
        var unique = false;
        Dictionary<string, object?> options = new(StringComparer.Ordinal);

        // Every property is one 5.x wrote, or the index is not read: a property passed over is a
        // part of the index that is in the database and not in what is compared.
        foreach (var property in json.EnumerateObject())
        {
            switch (property.Name)
            {
                case "paths" when property.Value.ValueKind == JsonValueKind.Array:
                    paths = [.. property.Value.EnumerateArray().Select(path => path.ValueKind == JsonValueKind.String
                        ? new ComplexIndexes5xIndexes.Part(path.GetString(), null, null, false, null)
                        : throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, "a path is not text")))];
                    break;
                case "parts" when property.Value.ValueKind == JsonValueKind.Array:
                    parts = [.. property.Value.EnumerateArray().Select(part => ReadPart(entity, part))];
                    break;
                case "unique" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    unique = property.Value.GetBoolean();
                    break;
                case "filter" when property.Value.ValueKind == JsonValueKind.String:
                    filter = property.Value.GetString();
                    break;
                case "name" when property.Value.ValueKind == JsonValueKind.String:
                    name = property.Value.GetString();
                    break;
                case "props" when property.Value.ValueKind == JsonValueKind.Object:
                    foreach (var option in property.Value.EnumerateObject())
                        options[option.Name] = Value(entity, option);
                    break;
                default:
                    throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, $"an index holds '{property.Name}' in a form 5.x did not write"));
            }
        }

        // 5.x's own rule: parts, when they are there, are the index, and paths are not looked at.
        return ComplexIndexes5xIndexes.Build(entityType, parts ?? paths ?? [], unique, filter, name, options);
    }

    private static ComplexIndexes5xIndexes.Part ReadPart(string entity, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, "a part is not an object"));

        string? path = null, expression = null, template = null, nulls = null;
        var descending = false;
        foreach (var property in json.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path" when property.Value.ValueKind == JsonValueKind.String:
                    path = property.Value.GetString();
                    break;
                case "expr" when property.Value.ValueKind == JsonValueKind.String:
                    expression = property.Value.GetString();
                    break;
                case "tmpl" when property.Value.ValueKind == JsonValueKind.String:
                    template = property.Value.GetString();
                    break;
                case "desc" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    descending = property.Value.GetBoolean();
                    break;
                case "nulls" when property.Value.ValueKind == JsonValueKind.String:
                    nulls = property.Value.GetString();
                    break;
                default:
                    throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, $"a part holds '{property.Name}' in a form 5.x did not write"));
            }
        }

        return new ComplexIndexes5xIndexes.Part(path, expression, template, descending, nulls);
    }

    // As 5.x read its own options back: text and flags as they are, a number as a number, a list
    // as a list of texts with an empty text where an entry is null.
    private static object? Value(string entity, JsonProperty option) => option.Value.ValueKind switch
    {
        JsonValueKind.String => option.Value.GetString(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        // Each as its own type: one conditional over both would make every number a double.
        JsonValueKind.Number => option.Value.TryGetInt64(out var whole) ? (object)whole : option.Value.GetDouble(),
        JsonValueKind.Array  => option.Value.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString()! : "").ToArray(),
        _                    => throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, $"the option '{option.Name}' is neither text, a flag, a number nor a list")),
    };
}

/// <summary>
/// Reads an index 5.x stored on a property: the marker <c>CustomIndex:IsIndexed</c>, written by
/// <c>HasComplexIndex</c> on a property of a complex type, with the annotations beside it that
/// describe the index.
/// </summary>
internal sealed class ComplexIndexes5xPropertyIndexReader : LegacyPropertyDeclarationReader<PgIndex>
{
    /// <summary>5.x's <c>ComplexIndexAnnotations.IsIndexed</c>.</summary>
    public const string AnnotationName = "CustomIndex:IsIndexed";

    public override string Annotation => AnnotationName;

    protected override IEnumerable<PgIndex> Read(IReadOnlyEntityType entityType, string path, IReadOnlyProperty property, object? value)
    {
        // 5.x read the marker with "is true": anything else declares nothing.
        if (value is not true) return [];

        // Beside the marker: whether it is unique, its condition and its name, and Npgsql's options
        // under their own names. 5.x carried the options on its list and passed every other
        // annotation of the property over.
        Dictionary<string, object?> options = new(StringComparer.Ordinal);
        foreach (var annotation in property.GetAnnotations())
        {
            if (ComplexIndexes5xIndexes.IsOption(annotation.Name)) options[annotation.Name] = annotation.Value;
        }

        return
        [
            ComplexIndexes5xIndexes.Build(
                entityType,
                [new ComplexIndexes5xIndexes.Part(path, null, null, false, null)],
                property.FindAnnotation("CustomIndex:IsUnique")?.Value is true,
                property.FindAnnotation("CustomIndex:Filter")?.Value as string,
                property.FindAnnotation("CustomIndex:Name")?.Value as string,
                options),
        ];
    }
}

/// <summary>
/// One index as 5.x stored it, as the <see cref="PgIndex" /> that declares the same object.
/// </summary>
/// <remarks>
/// <para>
/// The index is resolved by the rules a declaration is resolved by, not by 5.x's. For what is read
/// here the two agree on the object: a path is its property's column or its member of a JSON
/// document, a filter's <c>{Path}</c> likewise, and a default name is <c>IX_{table}_{parts}</c>
/// with a column's name, an expression's letters and digits and a member's container and names.
/// That is not argued, it is measured: the fixture scaffolded by the published 5.6.0 diffs to
/// nothing against the same model declared here, and the script 5.x's own generator wrote leaves
/// the catalog <c>EnsureCreated()</c> builds (<c>LegacyIndexHandoverTests</c>).
/// </para>
/// <para>
/// Where the rules do not agree, or <see cref="PgIndex" /> cannot say what 5.x stored, the index
/// is refused by name. It is in the database: left out, it would be created a second time by the
/// next migration, and never dropped by one that removes it from the model.
/// </para>
/// </remarks>
internal static class ComplexIndexes5xIndexes
{
    /// <summary>A part as 5.x stored it: exactly one of a path, SQL as written, and a template of a lambda.</summary>
    public sealed record Part(string? Path, string? Expression, string? Template, bool Descending, string? Nulls);

    private const string RenderingVersionAnnotation = "CustomIndex:RenderingVersion";

    private const string Method = "Npgsql:IndexMethod";
    private const string Operators = "Npgsql:IndexOperators";
    private const string Collation = "Npgsql:IndexCollation";
    private const string Include = "Npgsql:IndexInclude";
    private const string NullsDistinct = "Npgsql:NullsDistinct";
    private const string Concurrently = "Npgsql:CreatedConcurrently";
    private const string StorageParameter = "Npgsql:StorageParameter:";

    /// <summary>Whether an annotation of a property is one of the options 5.x carried from it to its index.</summary>
    public static bool IsOption(string annotation)
        => annotation is Method or Operators or Collation or Include or NullsDistinct or Concurrently
           || annotation.StartsWith(StorageParameter, StringComparison.Ordinal);

    // The second rendering version (5.4.0) wrote a member of a JSON document as Npgsql's queries
    // do, and named an expression's index by what it reads. The third (5.5.0) gave every table
    // of a TPC hierarchy its copy. A snapshot without the annotation is of the first.
    private const int AsTheQueriesWrite = 2;

    private const int CopiesOnEveryTable = 3;

    private static int RenderingVersion(IReadOnlyModel model) => model.FindAnnotation(RenderingVersionAnnotation)?.Value is int version ? version : 1;

    public static PgIndex Build(
        IReadOnlyEntityType entityType,
        IReadOnlyList<Part> parts,
        bool unique,
        string? filter,
        string? name,
        IReadOnlyDictionary<string, object?> options)
    {
        var entity = entityType.ShortName();
        if (parts.Count == 0)
            throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, "an index has no parts"));

        List<KeyTarget> targets = [.. parts.Select(part => Target(entity, part))];
        var index = name ?? string.Join(", ", targets.Select(target => target switch
        {
            KeyTarget.Column column     => column.Path,
            KeyTarget.Template template => template.Sql.ToString(),
        }));

        InvalidOperationException CannotSay(string what, string wayOff) => new(PgStrings.LegacyIndexCannotSay(entity, index, what, wayOff));

        var version = RenderingVersion(entityType.Model);

        // Before its third rendering version 5.x had the index in the declaring type's table
        // alone. Read as a declaration it would say the derived types' tables hold copies.
        if (entityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy
            && entityType.GetDirectlyDerivedTypes().Any()
            && version < CopiesOnEveryTable)
            throw CannotSay(
                "is on a type of a TPC hierarchy with derived types, in a snapshot scaffolded before 5.5.0, where the index is in that type's table alone",
                PgStrings.MoveTo56First);

        // Every path the index reads: its keys', and those its condition names.
        var condition = filter is null ? null : SqlTemplate.Raw(filter);
        List<string> read =
        [
            .. targets.SelectMany(PgJsonMembers.PathsOf),
            .. condition is null ? [] : PgJsonMembers.PathsOf(condition),
        ];
        var members = read.Select(path => (Path: path, Member: MemberOf(entityType, path))).Where(member => member.Member is not null).ToList();

        if (version < AsTheQueriesWrite)
        {
            // The first rendering wrote a member as text, step by step, which is another
            // expression than the one a path resolves to here, and named an expression's index
            // by its SQL as it stood. Read as declarations, both would diff to nothing while the
            // database holds other expressions and other names (decided with #27).
            if (members.Count > 0)
                throw CannotSay($"reads '{members[0].Path}', a member of a JSON document, in a snapshot scaffolded before 5.4.0", PgStrings.MoveTo56First);
            if (name is null && targets.Any(target => target is KeyTarget.Template))
                throw CannotSay("is on an expression and has no name of its own, in a snapshot scaffolded before 5.4.0", PgStrings.MoveTo56First);
        }

        // A date or a time out of a document: 5.x built the index on the member's text, since the
        // cast a query uses is not one an index takes. A path here is that cast, which is refused.
        foreach (var (path, member) in members)
        {
            if (member is { } property && IsDateOrTime(property.ClrType))
                throw CannotSay(
                    $"reads '{path}', a date or a time in a JSON document, which 5.x indexed as the member's text",
                    "No declaration of a path builds that index, and no query EF Core writes uses it. Remove it from the model and add a " +
                    "migration while the project is on 5.x; declare it afterwards with the expression written as SQL, if it is still wanted.");
        }

        var method = options.TryGetValue(Method, out var stated) ? (stated as string ?? "").ToLowerInvariant() : "btree";
        var operators = Texts(entity, options, Operators);
        var collations = Texts(entity, options, Collation);
        var nullsDistinct = options.TryGetValue(NullsDistinct, out var distinct) ? distinct as bool? ?? throw Unreadable(entity, NullsDistinct) : (bool?)null;
        var concurrently = options.TryGetValue(Concurrently, out var concurrent) && (concurrent as bool? ?? throw Unreadable(entity, Concurrently));

        foreach (var option in options.Keys)
        {
            if (!IsOption(option))
                throw CannotSay($"carries the option '{option}', which is none of Npgsql's that 5.x wrote for an index", "");
        }

        // What only a B-tree has.
        if (method != "btree")
        {
            if (parts.Any(part => part.Descending || part.Nulls is not null))
                throw CannotSay($"states an order on a key of a {method} index, which has none", "");
            if (unique)
                throw CannotSay($"is unique and a {method} index, which PostgreSQL does not build", "");
        }

        // PostgreSQL keeps NULLS NOT DISTINCT on an index that is not unique, and no declaration says it.
        if (nullsDistinct is false && !unique)
            throw CannotSay("says NULLS NOT DISTINCT and is not unique, which PostgreSQL keeps and no declaration here can say", "");

        string? At(string[] list, int i) => i < list.Length && list[i].Length > 0 ? list[i] : null;

        var include = options.TryGetValue(Include, out var included)
            ? (included as string[] ?? throw Unreadable(entity, Include)).Select(entry => IncludedPath(entityType, entry) ?? throw CannotSay(
                $"includes '{entry}', which is neither a property of {entity} nor the column of one", "")).ToArray()
            : [];

        Dictionary<string, object?> parameters = options
            .Where(option => option.Key.StartsWith(StorageParameter, StringComparison.Ordinal))
            .ToDictionary(option => option.Key[StorageParameter.Length..], option => option.Value, StringComparer.Ordinal);

        int? Number(string parameter)
        {
            if (!parameters.Remove(parameter, out var value)) return null;
            return value switch
            {
                int number                                                                                => number,
                long number when number is >= int.MinValue and <= int.MaxValue                            => (int)number,
                string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
                _ => throw CannotSay($"sets the storage parameter '{parameter}' to '{value}', which is no whole number", ""),
            };
        }

        bool? Flag(string parameter)
        {
            if (!parameters.Remove(parameter, out var value)) return null;
            return value switch
            {
                bool flag                                                                     => flag,
                string text when text.ToLowerInvariant() is "on" or "true" or "yes" or "1"    => true,
                string text when text.ToLowerInvariant() is "off" or "false" or "no" or "0"   => false,
                _ => throw CannotSay($"sets the storage parameter '{parameter}' to '{value}', which is neither on nor off", ""),
            };
        }

        void Requires(bool allowed, string what)
        {
            if (!allowed) throw CannotSay($"{what}, which a {method} index does not take", "");
        }

        PgIndex declared;
        switch (method)
        {
            case "btree":
                declared = new PgIndex.BTree([.. targets.Select((target, i) => new BTreeKey(target)
                {
                    Descending = parts[i].Descending,
                    Nulls = parts[i].Nulls switch
                    {
                        null    => null,
                        "First" => NullOrder.First,
                        "Last"  => NullOrder.Last,
                        var other => throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, $"a part puts its nulls '{other}'")),
                    },
                    Collation = At(collations, i),
                    OpClass = At(operators, i) is { } ops ? new BTreeOpClass(ops) : null,
                })])
                {
                    Unique = unique ? new Uniqueness(nullsDistinct ?? true) : null,
                    Include = [.. include],
                    FillFactor = Number("fillfactor"),
                    DeduplicateItems = Flag("deduplicate_items"),
                };
                break;

            case "hash":
                Requires(targets.Count == 1, "has several keys");
                Requires(include.Length == 0, "includes columns");
                declared = new PgIndex.Hash(new HashKey(targets[0]) { Collation = At(collations, 0), OpClass = At(operators, 0) is { } hashOps ? new HashOpClass(hashOps) : null })
                {
                    FillFactor = Number("fillfactor"),
                };
                break;

            case "gist":
                declared = new PgIndex.Gist([.. targets.Select((target, i) => new GistKey(target)
                {
                    Collation = At(collations, i),
                    OpClass = At(operators, i) is { } ops ? new GistOpClass(ops) : null,
                })])
                {
                    Include = [.. include],
                    FillFactor = Number("fillfactor"),
                    Buffering = parameters.Remove("buffering", out var buffering)
                        ? (buffering switch { bool on => on ? "on" : "off", string text => text.ToLowerInvariant(), _ => "" }) switch
                        {
                            "on"   => GistBuffering.On,
                            "off"  => GistBuffering.Off,
                            "auto" => GistBuffering.Auto,
                            _      => throw CannotSay($"sets the storage parameter 'buffering' to '{buffering}'", ""),
                        }
                        : null,
                };
                break;

            case "spgist":
                Requires(targets.Count == 1, "has several keys");
                declared = new PgIndex.SpGist(new SpGistKey(targets[0]) { Collation = At(collations, 0), OpClass = At(operators, 0) is { } spOps ? new SpGistOpClass(spOps) : null })
                {
                    Include = [.. include],
                    FillFactor = Number("fillfactor"),
                };
                break;

            case "gin":
                Requires(include.Length == 0, "includes columns");
                declared = new PgIndex.Gin([.. targets.Select((target, i) => new GinKey(target)
                {
                    Collation = At(collations, i),
                    OpClass = At(operators, i) is { } ops ? new GinOpClass(ops) : null,
                })])
                {
                    FastUpdate = Flag("fastupdate"),
                    PendingListLimit = Number("gin_pending_list_limit"),
                };
                break;

            case "brin":
                Requires(include.Length == 0, "includes columns");
                declared = new PgIndex.Brin([.. targets.Select((target, i) => new BrinKey(target)
                {
                    Collation = At(collations, i),
                    OpClass = At(operators, i) is { } ops ? new BrinOpClass(ops) : null,
                })])
                {
                    PagesPerRange = Number("pages_per_range"),
                    Autosummarize = Flag("autosummarize"),
                };
                break;

            default:
                throw CannotSay($"uses the access method '{method}'", "");
        }

        // A parameter the method has and this package has a place for was taken above.
        if (parameters.Count > 0)
            throw CannotSay($"sets the storage parameter '{parameters.Keys.Order(StringComparer.Ordinal).First()}', which a {method} index does not have", "");

        // A class or a collation behind the last key belongs to no key.
        if (operators.Skip(targets.Count).Any(entry => entry.Length > 0) || collations.Skip(targets.Count).Any(entry => entry.Length > 0))
            throw CannotSay("names more operator classes or collations than it has keys", "");

        return declared with
        {
            // The epoch whose rules were measured against 5.x's, whatever a new declaration carries by then.
            Epoch = 1,
            Name = name,

            // Whether 5.x translated the filter from a lambda or took it as text, its format does not
            // say. As text it may name a column outside a placeholder, so that is what it is read as.
            Where = condition is null ? null : new Predicate(condition),
            Concurrently = concurrently,
        };
    }

    private static KeyTarget Target(string entity, Part part) => part switch
    {
        { Path: { Length: > 0 } path, Expression: null, Template: null } => new KeyTarget.Column(path),

        // SQL as it was written: 5.x emitted it in parentheses and resolved nothing in it.
        { Path: null, Expression: { Length: > 0 } sql, Template: null } => new KeyTarget.Template(SqlTemplate.Of([new SqlSegment.Raw(sql)])),

        // The translation of a lambda: 5.x's own text around its placeholders. Inside a string
        // literal it wrote a brace twice, for a second grammar this package does not have.
        { Path: null, Expression: null, Template: { Length: > 0 } template } => new KeyTarget.Template(SqlTemplate.FromPlaceholders(WithoutDoubledBraces(template))),

        _ => throw new InvalidOperationException(PgStrings.LegacyIndexUnreadable(entity, "a part needs exactly one of 'path', 'expr' and 'tmpl'")),
    };

    /// <summary>A template of 5.x with the braces of its string literals written once.</summary>
    internal static string WithoutDoubledBraces(string template)
    {
        StringBuilder text = new(template.Length);
        var quoted = false;
        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if (ch == '\'') quoted = !quoted;
            text.Append(ch);
            if (quoted && ch is '{' or '}' && i + 1 < template.Length && template[i + 1] == ch) i++;
        }

        return text.ToString();
    }

    private static InvalidOperationException Unreadable(string entity, string option)
        => new(PgStrings.LegacyIndexUnreadable(entity, $"the option '{option}' is not of the type 5.x wrote"));

    private static string[] Texts(string entity, IReadOnlyDictionary<string, object?> options, string option)
        => options.TryGetValue(option, out var value)
            ? (value as IEnumerable<string?> ?? throw Unreadable(entity, option)).Select(entry => entry ?? "").ToArray()
            : [];

    /// <summary>The property a path names when it is inside a JSON document, or null: a path outside one, or one this walk does not follow.</summary>
    internal static IReadOnlyProperty? MemberOf(IReadOnlyEntityType entityType, string path)
    {
        var segments = path.Split('.');
        IReadOnlyTypeBase current = entityType;
        var json = false;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current.FindComplexProperty(segments[i]) is not { } complex) return null;

            json |= complex.IsCollection || complex.ComplexType.GetContainerColumnName() is not null;
            current = complex.ComplexType;
        }

        return json ? current.FindProperty(segments[^1]) : null;
    }

    private static bool IsDateOrTime(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(DateTime) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(DateTimeOffset);
    }

    /// <summary>
    /// An entry of 5.x's include list as a property path. 5.x took an entry for a path first and
    /// for a column's name otherwise, and a declaration takes paths alone.
    /// </summary>
    private static string? IncludedPath(IReadOnlyEntityType entityType, string entry)
    {
        if (Names(entityType, entry)) return entry;

        // The column as the property has it in the entity type's table: asked without the table,
        // a property of a complex type answers with its own name.
        var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
        return Columns(entityType, prefix: "", table).Where(column => string.Equals(column.Column, entry, StringComparison.Ordinal)).Select(column => column.Path).FirstOrDefault();

        static bool Names(IReadOnlyEntityType entityType, string path)
        {
            var segments = path.Split('.');
            IReadOnlyTypeBase current = entityType;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (current.FindComplexProperty(segments[i]) is not { } complex) return false;
                current = complex.ComplexType;
            }

            return current.FindProperty(segments[^1]) is not null || current.FindComplexProperty(segments[^1]) is not null;
        }

        static IEnumerable<(string Path, string? Column)> Columns(IReadOnlyTypeBase type, string prefix, StoreObjectIdentifier? table)
        {
            foreach (var property in type.GetProperties())
                yield return (prefix + property.Name, table is { } named ? property.GetColumnName(named) : property.GetColumnName());

            foreach (var complex in type.GetComplexProperties())
            {
                if (complex.IsCollection || complex.ComplexType.GetContainerColumnName() is { }) yield return (prefix + complex.Name, complex.ComplexType.GetContainerColumnName());
                else foreach (var column in Columns(complex.ComplexType, $"{prefix}{complex.Name}.", table)) yield return column;
            }
        }
    }
}
