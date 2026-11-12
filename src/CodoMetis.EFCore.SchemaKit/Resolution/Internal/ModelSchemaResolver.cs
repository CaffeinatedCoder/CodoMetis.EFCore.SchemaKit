using System.Reflection;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Resolution.Internal;

/// <summary>
/// The kernel's resolver: one declaration's paths and templates, turned into the SQL of one table.
/// </summary>
/// <remarks>
/// <para>
/// It reads metadata only: properties, complex properties and the column a property has in the
/// table. That is what a configured model and a model snapshot have in common, and the reason both
/// sides of a diff get the same answer from the same code. A snapshot rebuilds every type as a
/// property bag and keeps no converter, so the one rule that asks a CLR type has a second form for
/// it (<see cref="ConvertedMember" />).
/// </para>
/// <para>
/// How a path becomes SQL is the resolution epoch <see cref="ResolutionEpoch.Current" />. A change
/// to what any method here returns for an input it accepted before is a new epoch, not a fix.
/// </para>
/// </remarks>
internal sealed class ModelSchemaResolver : SchemaResolver
{
    private readonly IReadOnlyEntityType _entityType;
    private readonly StoreObjectIdentifier _storeObject;

    public ModelSchemaResolver(IReadOnlyEntityType entityType, StoreObjectIdentifier table, ISchemaDialect dialect)
    {
        _entityType = entityType;
        _storeObject = table;
        Dialect = dialect;
        Table = new TableRef(table.Schema is null ? null : dialect.Identifier(table.Schema), dialect.Identifier(table.Name));
    }

    public override TableRef Table { get; }

    public override ISchemaDialect Dialect { get; }

    public override SqlFragment Column(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!IsPropertyPath(path))
            throw new InvalidOperationException(SchemaKitStrings.NotAPropertyPath(_entityType.ShortName(), path));

        var column = ResolveColumn(path);
        return new SqlFragment(Dialect.Delimit(column), [new Dependency.Column(Table, column)], column.Name);
    }

    public override SqlFragment Template(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        StringBuilder sql = new(template.Length + 16);
        StringBuilder token = new(template.Length);
        List<Dependency> reads = [];
        var inLiteral = false;

        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];

            // A doubled quote inside a literal toggles twice, which leaves the state where it was.
            if (ch == '\'') inLiteral = !inLiteral;

            if (ch == '{' && !inLiteral)
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i + 1 && IsPropertyPath(template.AsSpan(i + 1, end - i - 1)))
                {
                    var column = ResolveColumn(template[(i + 1)..end]);
                    sql.Append(Dialect.Delimit(column));
                    token.Append(column.Name);
                    Dependency read = new Dependency.Column(Table, column);
                    if (!reads.Contains(read)) reads.Add(read);
                    i = end;
                    continue;
                }
            }

            sql.Append(ch);
            token.Append(ch);
        }

        // A column named outside a placeholder is invisible here, and no scan of SQL can prove there
        // is none. So SQL that names no column through a placeholder is taken to read all of them.
        if (reads.Count == 0) reads.Add(new Dependency.WholeTable(Table));

        return new SqlFragment(sql.ToString(), [.. reads], NameTokens.Of(token.ToString()));
    }

    private Identifier ResolveColumn(string path)
    {
        var property = FindProperty(path);
        var column = property.GetColumnName(_storeObject)
                     ?? throw new InvalidOperationException(SchemaKitStrings.PathHasNoColumn(_entityType.ShortName(), path, Table));
        return Dialect.Identifier(column);
    }

    /// <summary>
    /// Walks the path through complex properties to a scalar property. The property that is found is
    /// the declared one, and its column is asked of it: a column looked up by name in the table
    /// could be another property's.
    /// </summary>
    private IReadOnlyProperty FindProperty(string path)
    {
        var segments = path.Split('.');
        IReadOnlyTypeBase current = _entityType;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (i == segments.Length - 1)
            {
                if (current.FindProperty(segment) is { } property) return property;

                throw new InvalidOperationException(current.FindComplexProperty(segment) is not null
                    ? SchemaKitStrings.PathEndsOnComplexProperty(_entityType.ShortName(), path)
                    : SchemaKitStrings.PathNotFound(_entityType.ShortName(), path, segment, current.ShortName()));
            }

            if (current.FindComplexProperty(segment) is { } complex)
            {
                // What a member of a JSON document is in SQL is the provider's to say, and it has to
                // say it as its queries do. Until a provider package does, there is no answer here
                // that an index could be trusted with.
                if (complex.IsCollection || complex.ComplexType.GetContainerColumnName() is not null)
                    throw new InvalidOperationException(SchemaKitStrings.PathIntoJson(_entityType.ShortName(), path, segment));

                current = complex.ComplexType;
                continue;
            }

            if (i == segments.Length - 2 && current.FindProperty(segment) is { } scalar && ConvertedMember(current, scalar, segments[i + 1]))
                return scalar;

            throw new InvalidOperationException(current.FindProperty(segment) is not null
                ? SchemaKitStrings.PathPastScalar(_entityType.ShortName(), path, segment, segments[i + 1])
                : SchemaKitStrings.PathNotFound(_entityType.ShortName(), path, segment, current.ShortName()));
        }

        throw new InvalidOperationException(SchemaKitStrings.NotAPropertyPath(_entityType.ShortName(), path));
    }

    /// <summary>
    /// Whether <c>Property.Member</c> is the property's whole column: the property has a converter
    /// and the member has the type the converter stores, as <c>Email.Value</c> does with a converter
    /// to <see cref="string" />. Without the type check <c>CreatedAt.Year</c> would name the whole
    /// timestamp.
    /// </summary>
    /// <remarks>
    /// A snapshot has neither the CLR type nor the converter: it keeps the property as an indexer
    /// property of the stored type. There the member is taken as it is, since the path was checked
    /// against the configured model when the snapshot was written. Refusing it would let the first
    /// <c>migrations add</c> pass and fail every diff after it.
    /// </remarks>
    private static bool ConvertedMember(IReadOnlyTypeBase declaring, IReadOnlyProperty property, string member)
    {
        var converter = property.FindTypeMapping()?.Converter ?? property.GetValueConverter();
        if (converter is null)
            return declaring.IsPropertyBag && property.IsIndexerProperty();

        const BindingFlags Members = BindingFlags.Public | BindingFlags.Instance;
        var memberType = property.ClrType.GetProperty(member, Members)?.PropertyType
                         ?? property.ClrType.GetField(member, Members)?.FieldType;

        return memberType is not null && Unwrap(memberType) == Unwrap(converter.ProviderClrType);

        static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;
    }

    /// <summary>Dotted segments, each a letter or an underscore followed by letters, digits and underscores.</summary>
    internal static bool IsPropertyPath(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;

        var start = true;
        foreach (var ch in text)
        {
            if (ch == '.')
            {
                if (start) return false;
                start = true;
                continue;
            }

            if (!(char.IsLetter(ch) || ch == '_' || (!start && char.IsDigit(ch)))) return false;
            start = false;
        }

        return !start;
    }
}

/// <summary>What a default name takes from SQL that is not a column.</summary>
internal static class NameTokens
{
    /// <summary>The letters and digits of the text, or <c>expr</c> when it has none.</summary>
    public static string Of(string text)
        => string.Concat(text.Where(char.IsLetterOrDigit)) is { Length: > 0 } letters ? letters : "expr";
}

/// <summary>The resolution epochs this version of SchemaKit resolves under.</summary>
internal static class ResolutionEpoch
{
    /// <summary>The epoch a declaration written by this version carries.</summary>
    public const int Current = 1;
}

/// <summary>
/// Makes the resolver a contributor's <c>Resolve</c> is handed, the same way for the model and for
/// the snapshot.
/// </summary>
/// <remarks>
/// A diff-time service: it takes the dialect, which reads the design-time model, so nothing that is
/// resolved while a model is being built may take it in its constructor.
/// </remarks>
internal sealed class SchemaResolverFactory(ISchemaDialect dialect)
{
    /// <summary>The resolver for a declaration of an entity type, in the table the type is mapped to.</summary>
    public SchemaResolver Create(IReadOnlyEntityType entityType, ISchemaObject declaration)
        => Create(
            entityType,
            StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException(SchemaKitStrings.DeclaredOnUnmappedType(entityType.ShortName(), Describe(declaration))),
            declaration);

    /// <summary>The resolver for a declaration of an entity type, in one of the type's tables.</summary>
    public SchemaResolver Create(IReadOnlyEntityType entityType, StoreObjectIdentifier table, ISchemaObject declaration)
    {
        // An epoch this version does not know is how a later version resolves. Resolving it as
        // epoch 1 would give the source side of a diff another answer than the version that wrote it.
        if (declaration.Epoch != ResolutionEpoch.Current)
            throw new InvalidOperationException(SchemaKitStrings.UnknownEpoch(entityType.ShortName(), Describe(declaration), declaration.Epoch, ResolutionEpoch.Current));

        return new ModelSchemaResolver(entityType, table, dialect);
    }

    private static string Describe(ISchemaObject declaration)
        => declaration.Name is { } name ? $"{declaration.Owner} {declaration.Kind} '{name}'" : $"{declaration.Owner} {declaration.Kind}";
}
