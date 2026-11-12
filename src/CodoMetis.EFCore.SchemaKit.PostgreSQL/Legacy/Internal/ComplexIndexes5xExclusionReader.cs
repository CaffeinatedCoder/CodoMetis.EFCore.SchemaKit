using System.Text.Json;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Legacy.Internal;

/// <summary>
/// Reads the exclusion constraints <c>EFCore.ComplexIndexes.PostgreSQL</c> 5.x stored on an entity
/// type: the annotation <c>CustomExclusion:Constraints</c>, a JSON list written by
/// <c>HasExclusionConstraint</c>.
/// </summary>
/// <remarks>
/// <para>
/// The format is 5.x's and is finished: <c>parts</c> (each a <c>path</c> or an <c>expr</c>, and
/// its <c>op</c>), <c>method</c>, <c>filter</c>, <c>name</c>, <c>deferrable</c>,
/// <c>initiallyDeferred</c>. 5.x left out what was null and a deferral that was false. This file
/// changes only when a 5.x snapshot is found that it reads wrongly.
/// </para>
/// <para>
/// A constraint is read into the <see cref="PgExclusion" /> that declares the same object, and
/// resolved by the rules a declaration is resolved by, not by 5.x's. For what is read here the two
/// agree on the object: a path is its property's column, a filter's <c>{Path}</c> is that column,
/// and a default name is <c>EX_{table}_{columns}</c> with an expression's letters and digits.
/// They differ in how a name is written (5.x quoted every one), which is not a difference in the
/// database. The fixture scaffolded by the published 5.6.0 holds that: its snapshot against the
/// same model declared here is no operations, and the catalog is the same
/// (<c>LegacyHandoverTests</c>).
/// </para>
/// <para>
/// Where the rules do not agree, or <see cref="PgExclusion" /> cannot say what 5.x stored, the
/// constraint is refused by name. It is in the database: left out, it would be created a second
/// time by the next migration, and never dropped by one that removes it from the model.
/// </para>
/// </remarks>
internal sealed class ComplexIndexes5xExclusionReader : LegacyDeclarationReader<PgExclusion>
{
    /// <summary>5.x's <c>NpgsqlExclusionAnnotations.Constraints</c>.</summary>
    public const string AnnotationName = "CustomExclusion:Constraints";

    public override string Annotation => AnnotationName;

    protected override IEnumerable<PgExclusion> Read(IReadOnlyEntityType entityType, string value)
    {
        // 5.x read an empty annotation as no constraints, and so it is here.
        if (value.Length == 0) return [];

        var entity = entityType.ShortName();

        // From its third rendering version on, 5.x gave every table of a TPC hierarchy its copy of
        // a constraint declared on the root, under names it derived per table. A declaration is
        // resolved for its own table only (#43), so read as one constraint the copies would be
        // left in the database with nothing that knows them.
        if (entityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy && entityType.GetDirectlyDerivedTypes().Any())
            throw new InvalidOperationException(PgStrings.LegacyExclusionOnTpcRoot(entity));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, exception.Message), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, "it is not a list"));

            return [.. document.RootElement.EnumerateArray().Select(constraint => ReadConstraint(entity, constraint))];
        }
    }

    private static PgExclusion ReadConstraint(string entity, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, "a constraint is not an object"));

        string? method = null, filter = null, name = null;
        bool deferrable = false, initiallyDeferred = false;
        List<ExclusionElement>? elements = null;

        // Every property is one 5.x wrote, or the constraint is not read: a property passed over
        // is a part of the constraint that is in the database and not in what is compared.
        foreach (var property in json.EnumerateObject())
        {
            switch (property.Name)
            {
                case "parts" when property.Value.ValueKind == JsonValueKind.Array:
                    elements = [.. property.Value.EnumerateArray().Select(part => ReadPart(entity, part))];
                    break;
                case "method":
                    method = Text(entity, property);
                    break;
                case "filter":
                    filter = Text(entity, property);
                    break;
                case "name":
                    name = Text(entity, property);
                    break;
                case "deferrable":
                    deferrable = Flag(entity, property);
                    break;
                case "initiallyDeferred":
                    initiallyDeferred = Flag(entity, property);
                    break;
                default:
                    throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, $"a constraint holds '{property.Name}'"));
            }
        }

        if (elements is not { Count: > 0 })
            throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, "a constraint has no parts"));

        var described = name ?? string.Join(", ", elements.Select(element => element.Target switch
        {
            KeyTarget.Column column     => column.Path,
            KeyTarget.Template template => template.Sql,
        }));

        // The record refuses it too, as a declaration nobody should write. Here it is a constraint
        // that exists, so the message says how to get off it.
        if (elements.TrueForAll(element => element.Operator == ExclusionOperator.Equal))
            throw new InvalidOperationException(PgStrings.LegacyExclusionOfEqualityOnly(entity, described));

        // 5.x wrote the method after USING as it was given, and PostgreSQL folds its case.
        PgExclusion constraint = (method ?? "gist").ToLowerInvariant() switch
        {
            "gist"                            => new PgExclusion.Gist([.. elements]),
            "spgist" when elements.Count == 1 => new PgExclusion.SpGist(elements[0]),
            _                                 => throw new InvalidOperationException(PgStrings.LegacyExclusionMethod(entity, described, method!)),
        };

        return constraint with
        {
            // The epoch whose rules were measured against 5.x's, whatever a new declaration carries by then.
            Epoch = 1,
            Name = name,
            Where = filter is null ? null : new Predicate(filter),

            // 5.x wrote INITIALLY DEFERRED only for a deferrable constraint.
            Deferral = (deferrable, initiallyDeferred) switch
            {
                (true, true)  => Deferral.InitiallyDeferred,
                (true, false) => Deferral.InitiallyImmediate,
                _             => Deferral.NotDeferrable,
            },
        };
    }

    private static ExclusionElement ReadPart(string entity, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, "a part is not an object"));

        string? path = null, expression = null, op = null;
        foreach (var property in json.EnumerateObject())
        {
            switch (property.Name)
            {
                case "path":
                    path = Text(entity, property);
                    break;
                case "expr":
                    expression = Text(entity, property);
                    break;
                case "op":
                    op = Text(entity, property);
                    break;
                default:
                    throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, $"a part holds '{property.Name}'"));
            }
        }

        // 5.x emitted an expression as it was written, in parentheses. It holds no placeholder: a
        // brace outside a literal is not SQL, so no constraint in a database has one.
        KeyTarget target = (path, expression) switch
        {
            ({ Length: > 0 }, null) => new KeyTarget.Column(path),
            (null, { Length: > 0 }) => new KeyTarget.Template(expression),
            _                       => throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, "a part needs exactly one of 'path' and 'expr'")),
        };

        if (string.IsNullOrWhiteSpace(op))
            throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, "a part has no 'op'"));

        return new ExclusionElement(target, new ExclusionOperator(op));
    }

    // 5.x left a null out and never wrote one, so a null that is there was not written by it.
    private static string Text(string entity, JsonProperty property)
        => property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()!
            : throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, $"'{property.Name}' is not text"));

    private static bool Flag(string entity, JsonProperty property) => property.Value.ValueKind switch
    {
        JsonValueKind.True  => true,
        JsonValueKind.False => false,
        _                   => throw new InvalidOperationException(PgStrings.LegacyExclusionUnreadable(entity, $"'{property.Name}' is neither true nor false")),
    };
}
