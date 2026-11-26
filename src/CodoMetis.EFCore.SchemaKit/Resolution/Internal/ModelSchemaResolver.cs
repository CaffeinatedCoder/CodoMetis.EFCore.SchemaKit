using System.Reflection;
using System.Text;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.Resolution.Internal;

/// <summary>
/// The refusal of a path whose property has no column in the table a declaration is resolved in.
/// It is the <see cref="InvalidOperationException" /> the contract says, and carries its path:
/// the planner asks a table on purpose where an entity is split over tables, and has to tell
/// this refusal from every other one.
/// </summary>
internal static class MissingColumn
{
    private const string Key = "CodoMetis.SchemaKit.PathWithoutColumn";

    public static InvalidOperationException Refusal(string message, string path)
    {
        var refusal = new InvalidOperationException(message);
        refusal.Data[Key] = path;
        return refusal;
    }

    /// <summary>The path, when the exception is this refusal; else null.</summary>
    public static string? PathOf(Exception exception) => exception.Data[Key] as string;
}

/// <summary>
/// The kernel's resolver: one declaration's paths and templates, turned into the SQL of one table.
/// </summary>
/// <remarks>
/// <para>
/// It reads metadata only: properties, complex properties and the column a property has in the
/// table. That is what a configured model and a model snapshot have in common, and the reason both
/// sides of a diff get the same answer from the same code. A snapshot rebuilds every type as a
/// property bag and keeps no converter, so the one rule that asks a CLR type has a second form for
/// it (<see cref="PropertyPaths" />).
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
    private readonly ISchemaRenames? _renames;
    private readonly TableRef _ownTable;
    private readonly bool _suffixed;
    private readonly IRelationalTypeMappingSource _mappings;

    /// <param name="entityType">The entity type the declaration is on.</param>
    /// <param name="table">The table, as the entity type's own model names it.</param>
    /// <param name="dialect">The dialect.</param>
    /// <param name="mappings">The provider's type mappings, for a member of a JSON document in a model that holds none yet.</param>
    /// <param name="renames">
    /// For the source side of a diff whose objects follow EF Core's renames: the table and its
    /// columns are answered as the target model names them. Null to resolve a model as it is.
    /// </param>
    /// <param name="suffixed">
    /// For a copy of a declaration on a table of a derived type, when its name is taken beyond the
    /// table: a name the declaration states gets the table's name behind it.
    /// </param>
    public ModelSchemaResolver(
        IReadOnlyEntityType entityType,
        StoreObjectIdentifier table,
        ISchemaDialect dialect,
        IRelationalTypeMappingSource mappings,
        ISchemaRenames? renames = null,
        bool suffixed = false)
    {
        _mappings = mappings;
        _entityType = entityType;
        _storeObject = table;
        _renames = renames;
        _suffixed = suffixed;
        Dialect = dialect;
        _ownTable = new TableRef(table.Schema is null ? null : dialect.Identifier(table.Schema), dialect.Identifier(table.Name));
        Table = renames?.Forward(_ownTable) ?? _ownTable;
    }

    public override TableRef Table { get; }

    // A default name is built from what the model itself calls its table and its columns. Under a
    // rename the object's SQL takes the new names and its name does not, so that it differs from
    // the target's in the name alone: a rename of the object, not another object.
    private protected override TableRef NamedAfter => _ownTable;

    // One declaration on a TPC type is an object in each table of the hierarchy below it, and a
    // name that is taken in the schema can be only one of them. The declaring type's own table
    // keeps the name, and a derived type's copy has its table's name behind it, as 5.x named it.
    private protected override string Declared(string name) => _suffixed ? $"{name}_{_ownTable.Name.Name}" : name;

    public override ISchemaDialect Dialect { get; }

    public override SqlFragment Column(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!IsPropertyPath(path))
            throw new InvalidOperationException(SchemaKitStrings.NotAPropertyPath(_entityType.ShortName(), path));

        return Resolve(path, ColumnRole.Key).Fragment;
    }

    public override string StoreType(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!IsPropertyPath(path))
            throw new InvalidOperationException(SchemaKitStrings.NotAPropertyPath(_entityType.ShortName(), path));

        var walked = PropertyPaths.Walk(_entityType, path);
        if (walked.Document is not null) return JsonStoreType(walked, path);

        // Asked of the declared property in this table, as its column is. A snapshot holds the type
        // as an annotation and a configured model computes it from the mapping: one answer.
        return walked.Property!.GetColumnType(_storeObject)
               ?? throw MissingColumn.Refusal(SchemaKitStrings.PathHasNoColumn(_entityType.ShortName(), path, _ownTable), path);
    }

    public override SqlFragment Template(SqlTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        StringBuilder sql = new();
        StringBuilder token = new();
        List<Dependency> reads = [];
        var byHand = false;

        foreach (var segment in template.Segments)
        {
            switch (segment)
            {
                case SqlSegment.Text text:
                    sql.Append(text.Sql);
                    token.Append(text.Sql);
                    break;

                case SqlSegment.Raw raw:
                    sql.Append(raw.Sql);
                    token.Append(raw.Sql);
                    byHand = true;
                    break;

                case SqlSegment.Column path:
                    var (fragment, own) = Resolve(path.Path, ColumnRole.Expression);

                    // A member of a JSON document is an expression, and what stands around a
                    // placeholder was written for a column: in parentheses it is one operand
                    // whatever the operators beside it bind to.
                    sql.Append(fragment.IsColumn ? fragment.Text : $"({fragment.Text})");
                    token.Append(own);
                    foreach (var read in fragment.Dependencies)
                    {
                        if (!reads.Contains(read)) reads.Add(read);
                    }

                    break;
            }
        }

        // A column named in SQL written by hand is invisible here, and no scan of SQL can prove
        // there is none. So such SQL is taken to read every column, beside the ones it names by a path.
        if (byHand) reads.Add(new Dependency.WholeTable(Table));

        return new SqlFragment(sql.ToString(), [.. reads], NameTokens.Of(token.ToString()));
    }

    /// <summary>
    /// A path as SQL, and what a default name takes for it. A property with a column is that
    /// column; a complex property mapped to JSON is the column that holds the document; anything
    /// inside a document is what the dialect writes for a member, and reads the document's column.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="role">How a plain column is read: by itself, or within a template.</param>
    private (SqlFragment Fragment, string Own) Resolve(string path, ColumnRole role)
    {
        var walked = PropertyPaths.Walk(_entityType, path);
        if (walked.Document is null)
        {
            var own = walked.Property!.GetColumnName(_storeObject)
                      ?? throw MissingColumn.Refusal(SchemaKitStrings.PathHasNoColumn(_entityType.ShortName(), path, _ownTable), path);
            var column = Forwarded(own);
            return (new SqlFragment(Dialect.Delimit(column), [new Dependency.Column(Table, column, role)], own, isColumn: true), own);
        }

        var ownContainer = walked.Document.ComplexType.GetContainerColumnName()
                           ?? throw MissingColumn.Refusal(SchemaKitStrings.PathHasNoColumn(_entityType.ShortName(), path, _ownTable), path);
        var container = Forwarded(ownContainer);

        // The document itself: a column like any other, of the document's type.
        if (walked.JsonNames.Count == 0)
            return (new SqlFragment(Dialect.Delimit(container), [new Dependency.Column(Table, container, role)], ownContainer, isColumn: true), ownContainer);

        var member = new JsonMember(container, [.. walked.JsonNames], KindOf(walked), JsonStoreType(walked, path), MappingOf(walked, path));
        var written = Dialect.JsonMember(member, Sql);

        // The document's column and the names, never the SQL: a member rendered differently is
        // still the same name. It is read as an expression wherever it stands, since that is what
        // it is to the database: a document's column that is dropped takes an index with it, and
        // is held by a constraint, as any expression's column is.
        var token = ownContainer + string.Concat(walked.JsonNames);
        return (new SqlFragment(written.Text, [new Dependency.Column(Table, container, ColumnRole.Expression)], token), token);
    }

    private Identifier Forwarded(string own)
    {
        var column = Dialect.Identifier(own);
        return _renames?.Forward(Table, column) ?? column;
    }

    private static JsonMemberKind KindOf(PropertyPaths.Walked walked) => walked.Property switch
    {
        { IsPrimitiveCollection: true } => JsonMemberKind.Collection,
        not null                        => JsonMemberKind.Scalar,
        null                            => walked.Through[^1].IsCollection ? JsonMemberKind.Collection : JsonMemberKind.Document,
    };

    // A member's own type for a scalar, as its mapping states it. A nested document and a
    // collection are of the document's type: what is taken out of one is one.
    private string JsonStoreType(PropertyPaths.Walked walked, string path)
        => walked.Property is { IsPrimitiveCollection: false }
            ? MappingOf(walked, path)!.StoreType
            : walked.Document!.ComplexType.GetContainerColumnType() ?? MappingOf(walked, path)?.StoreType
              ?? throw new InvalidOperationException(SchemaKitStrings.JsonMemberWithoutAMapping(_entityType.ShortName(), path));

    private RelationalTypeMapping? MappingOf(PropertyPaths.Walked walked, string path)
    {
        if (walked.Property is not { } property) return null;

        // A snapshot keeps a member's type and its facets and not a column type that was stated
        // for it: the context's own model would cast to one type and its snapshot to another, in
        // every diff. Only a configured model can hold the annotation, so only it is refused.
        if (property.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value is string stated)
            throw new InvalidOperationException(SchemaKitStrings.JsonMemberStatesItsColumnType(_entityType.ShortName(), path, stated));

        // A member has no column of its own whose type a model could state, so its type is its
        // mapping's. A model that was never initialized, a snapshot as it is compiled, holds no
        // mapping yet, and the provider's mapping source is asked for the one it would get.
        return property.FindRelationalTypeMapping() ?? _mappings.FindMapping((IProperty)property)
               ?? throw new InvalidOperationException(SchemaKitStrings.JsonMemberWithoutAMapping(_entityType.ShortName(), path));
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

/// <summary>
/// A property path against an entity type: what the resolver resolves a column from, and what the
/// binder reads a column's facts from. One walk, so that the two cannot disagree on what a path names.
/// </summary>
internal static class PropertyPaths
{
    /// <summary>
    /// What a path names: a scalar property, or a complex property mapped to a JSON document, and
    /// the way there.
    /// </summary>
    /// <param name="Property">The scalar property the path ends at; null when it ends at a document or a collection.</param>
    /// <param name="Through">The complex properties on the path, the last one included when the path ends at one.</param>
    /// <param name="Document">The complex property mapped to a JSON column that the path enters or ends at; null for a path outside any.</param>
    /// <param name="JsonNames">The names below the document, as the JSON has them; empty when the path is the document itself.</param>
    public sealed record Walked(
        IReadOnlyProperty? Property,
        IReadOnlyList<IReadOnlyComplexProperty> Through,
        IReadOnlyComplexProperty? Document,
        IReadOnlyList<string> JsonNames);

    /// <summary>
    /// Walks the path through complex properties to a scalar property. The property that is found is
    /// the declared one, and its column is asked of it: a column looked up by name in the table
    /// could be another property's.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="path">The property names from the entity type down, separated by dots.</param>
    /// <param name="mapping">Where a property's mapping comes from; null to ask the property.</param>
    /// <returns>The property, and the complex properties the path goes through.</returns>
    public static (IReadOnlyProperty Property, IReadOnlyList<IReadOnlyComplexProperty> Through) Find(
        IReadOnlyEntityType entityType, string path, Func<IReadOnlyProperty, CoreTypeMapping?>? mapping = null)
    {
        var walked = Walk(entityType, path, mapping);
        return walked.Property is { } property
            ? (property, walked.Through)
            : throw new InvalidOperationException(SchemaKitStrings.PathEndsOnComplexProperty(entityType.ShortName(), path));
    }

    /// <summary>
    /// The same walk, for a path that may go into a JSON document or end at one. A complex
    /// property mapped to a JSON column starts a document, and what is below it is named as the
    /// JSON names it.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="path">The property names from the entity type down, separated by dots.</param>
    /// <param name="mapping">Where a property's mapping comes from; null to ask the property.</param>
    public static Walked Walk(IReadOnlyEntityType entityType, string path, Func<IReadOnlyProperty, CoreTypeMapping?>? mapping = null)
    {
        var segments = path.Split('.');
        IReadOnlyTypeBase current = entityType;
        List<IReadOnlyComplexProperty> through = [];
        IReadOnlyComplexProperty? document = null;
        List<string> names = [];

        Walked Scalar(IReadOnlyProperty property)
        {
            if (document is not null) names.Add(property.GetJsonPropertyName() ?? property.Name);
            return new Walked(property, through, document, names);
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var complex = current.FindComplexProperty(segment);

            if (i == segments.Length - 1)
            {
                if (current.FindProperty(segment) is { } property) return Scalar(property);

                // A document as a whole, or a document or a collection inside one. A complex
                // property that is spread over columns of its own has no one column to be.
                if (complex is not null && (document is not null || IsJson(complex)))
                {
                    through.Add(complex);
                    if (document is null) document = complex;
                    else names.Add(complex.GetJsonPropertyName() ?? complex.Name);
                    return new Walked(null, through, document, names);
                }

                throw new InvalidOperationException(complex is not null
                    ? SchemaKitStrings.PathEndsOnComplexProperty(entityType.ShortName(), path)
                    : SchemaKitStrings.PathNotFound(entityType.ShortName(), path, segment, current.ShortName()));
            }

            if (complex is not null)
            {
                // An element of a collection has no one value in a row.
                if (complex.IsCollection)
                    throw new InvalidOperationException(SchemaKitStrings.PathThroughCollection(entityType.ShortName(), path, segment));

                if (document is not null) names.Add(complex.GetJsonPropertyName() ?? complex.Name);
                else if (IsJson(complex)) document = complex;

                through.Add(complex);
                current = complex.ComplexType;
                continue;
            }

            if (i == segments.Length - 2 && current.FindProperty(segment) is { } scalar && ConvertedMember(current, scalar, segments[i + 1], mapping))
                return Scalar(scalar);

            throw new InvalidOperationException(current.FindProperty(segment) is not null
                ? SchemaKitStrings.PathPastScalar(entityType.ShortName(), path, segment, segments[i + 1])
                : SchemaKitStrings.PathNotFound(entityType.ShortName(), path, segment, current.ShortName()));
        }

        throw new InvalidOperationException(SchemaKitStrings.NotAPropertyPath(entityType.ShortName(), path));

        static bool IsJson(IReadOnlyComplexProperty complex) => complex.IsCollection || complex.ComplexType.GetContainerColumnName() is not null;
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
    private static bool ConvertedMember(IReadOnlyTypeBase declaring, IReadOnlyProperty property, string member, Func<IReadOnlyProperty, CoreTypeMapping?>? mapping)
    {
        var converter = (mapping?.Invoke(property) ?? property.FindTypeMapping())?.Converter ?? property.GetValueConverter();
        if (converter is null)
            return declaring.IsPropertyBag && property.IsIndexerProperty();

        const BindingFlags Members = BindingFlags.Public | BindingFlags.Instance;
        var memberType = property.ClrType.GetProperty(member, Members)?.PropertyType
                         ?? property.ClrType.GetField(member, Members)?.FieldType;

        return memberType is not null && Unwrap(memberType) == Unwrap(converter.ProviderClrType);

        static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;
    }
}

/// <summary>
/// The renames EF Core applies between the two models of a diff. The source side is resolved under
/// them when its objects follow: the database renames an index's column along, so the object the
/// source model describes is, after EF Core's operations, the one under the new names.
/// </summary>
internal interface ISchemaRenames
{
    /// <summary>A table of the source model as the target model names it.</summary>
    TableRef Forward(TableRef table);

    /// <summary>A column, its table named as the target has it, as the target model names the column.</summary>
    Identifier Forward(TableRef table, Identifier column);
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
internal sealed class SchemaResolverFactory(ISchemaDialect dialect, IRelationalTypeMappingSource mappings)
{
    /// <summary>The resolver for a declaration of an entity type, in the table the type is mapped to.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="declaration">The declaration.</param>
    /// <param name="renames">For the source side of a diff, EF Core's renames to resolve under; null for a model as it is.</param>
    public SchemaResolver Create(IReadOnlyEntityType entityType, ISchemaObject declaration, ISchemaRenames? renames = null)
        => Create(
            entityType,
            StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException(SchemaKitStrings.DeclaredOnUnmappedType(entityType.ShortName(), Describe(declaration))),
            declaration,
            renames);

    /// <summary>The resolver for a declaration of an entity type, in one of the tables it lands on.</summary>
    /// <param name="entityType">The entity type the declaration is on.</param>
    /// <param name="table">The table.</param>
    /// <param name="declaration">The declaration.</param>
    /// <param name="renames">For the source side of a diff, EF Core's renames to resolve under; null for a model as it is.</param>
    /// <param name="suffixed">Whether a name the declaration states gets the table's name behind it.</param>
    public SchemaResolver Create(IReadOnlyEntityType entityType, StoreObjectIdentifier table, ISchemaObject declaration, ISchemaRenames? renames = null, bool suffixed = false)
    {
        // An epoch this version does not know is how a later version resolves. Resolving it as
        // epoch 1 would give the source side of a diff another answer than the version that wrote it.
        if (declaration.Epoch != ResolutionEpoch.Current)
            throw new InvalidOperationException(SchemaKitStrings.UnknownEpoch(entityType.ShortName(), Describe(declaration), declaration.Epoch, ResolutionEpoch.Current));

        return new ModelSchemaResolver(entityType, table, dialect, mappings, renames, suffixed);
    }

    private static string Describe(ISchemaObject declaration)
        => declaration.Name is { } name ? $"{declaration.Owner} {declaration.Kind} '{name}'" : $"{declaration.Owner} {declaration.Kind}";
}
