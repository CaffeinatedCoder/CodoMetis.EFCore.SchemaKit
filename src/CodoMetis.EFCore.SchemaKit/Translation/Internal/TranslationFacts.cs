using System.Globalization;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;
using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.Translation.Internal;

/// <summary>
/// What a lambda cannot say of the properties it reads: whether a column can be null, and how a
/// value compared with a column is written. A lambda is translated twice (issue 114): where it is
/// written, from what the C# types say, to refuse what has no SQL; and when its model is
/// finalized, from the model, for the SQL that is stored.
/// </summary>
internal abstract class TranslationFacts
{
    /// <summary>
    /// What the C# types say, for the translation at the call. It answers nothing itself: the
    /// translator falls back to the nullability annotations, and writes a value it has no literal
    /// for as a mark, since the model may have a mapping that does.
    /// </summary>
    public static TranslationFacts OfTheTypes { get; } = new Types();

    /// <summary>Whether the model's answers are used. False where the lambda is written.</summary>
    public abstract bool OfTheModel { get; }

    /// <summary>Whether the column of a property path can be null; null to ask the C# annotations.</summary>
    public abstract bool? CanBeNull(string path);

    /// <summary>
    /// A value as the column of a property path stores it, as a literal; null for a value the
    /// property's type cannot hold, which the translator then writes by its own rule.
    /// </summary>
    /// <exception cref="NotSupportedException">The property has no mapping that writes a literal.</exception>
    public abstract string? Literal(string path, object value);

    /// <summary>
    /// Whether the column of a property path orders its values as C# orders the property's:
    /// false for a property stored through a converter, whose text or number may order otherwise.
    /// </summary>
    public abstract bool KeepsOrder(string path);

    /// <summary>Whether the column of a boolean property is a boolean, so that it can stand as a condition by itself.</summary>
    public abstract bool IsBooleanColumn(string path);

    private sealed class Types : TranslationFacts
    {
        public override bool OfTheModel => false;
        public override bool? CanBeNull(string path) => null;
        public override string? Literal(string path, object value) => null;
        public override bool KeepsOrder(string path) => true;
        public override bool IsBooleanColumn(string path) => true;
    }
}

/// <summary>The facts of one entity type in a model, mutable or finalized.</summary>
/// <param name="entityType">The entity type the declaration is on.</param>
/// <param name="mappings">
/// Where a property's mapping comes from while the model is being finalized, when no property
/// holds one yet (spike F, question 1); null for a finalized model, whose properties do.
/// </param>
internal sealed class ModelFacts(IReadOnlyEntityType entityType, IRelationalTypeMappingSource? mappings) : TranslationFacts
{
    public override bool OfTheModel => true;

    public override bool? CanBeNull(string path)
    {
        var (property, through) = Find(path);
        return property.IsNullable || through.Any(complex => complex.IsNullable);
    }

    public override string? Literal(string path, object value)
    {
        var property = Find(path).Property;
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

        // The compiler hands an enum over as its number, a char as its code, and a narrow integer
        // widened; the mapping wants the property's own type.
        if (value.GetType() != type && !type.IsInstanceOfType(value))
        {
            try
            {
                value = type.IsEnum ? Enum.ToObject(type, value)
                    : type == typeof(char) && value is int code ? code is >= char.MinValue and <= char.MaxValue ? (char)code : throw new OverflowException()
                    : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException or ArgumentException)
            {
                return null;
            }
        }

        var mapping = Mapping(property) as RelationalTypeMapping
                      ?? throw new NotSupportedException(SchemaKitStrings.NoMappingForLiteral(entityType.ShortName(), path));

        // A mapping refuses what its type cannot hold in its own words and with its own exception:
        // Npgsql a DateTime that is not UTC for a timestamp with time zone.
        try
        {
            return mapping.GenerateSqlLiteral(value);
        }
        catch (Exception e) when (e is not NotSupportedException)
        {
            throw new NotSupportedException(SchemaKitStrings.LiteralRefusedByMapping(entityType.ShortName(), path, e.Message), e);
        }
    }

    // A converter between two numbers, or from an enum to its number, stores what orders as the
    // property does: Npgsql stores an sbyte as a smallint that way. One to text does not.
    public override bool KeepsOrder(string path)
    {
        var property = Find(path).Property;
        return Mapping(property)?.Converter is not { } converter || (IsNumber(converter.ModelClrType) && IsNumber(converter.ProviderClrType));

        static bool IsNumber(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsEnum || (type.IsPrimitive && type != typeof(bool) && type != typeof(char)) || type == typeof(decimal);
        }
    }

    public override bool IsBooleanColumn(string path) => Mapping(Find(path).Property)?.Converter is null;

    /// <summary>Refuses a path that names no property of the entity type, by the resolver's own walk.</summary>
    public void Require(string path) => PropertyPaths.Walk(entityType, path, Mapping);

    private CoreTypeMapping? Mapping(IReadOnlyProperty property)
        => property.FindTypeMapping() ?? mappings?.FindMapping((IProperty)property);

    private (IReadOnlyProperty Property, IReadOnlyList<IReadOnlyComplexProperty> Through) Find(string path)
        => PropertyPaths.Find(entityType, path, Mapping);
}
