using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Resolution.Internal;

/// <summary>One table a declaration lands on.</summary>
/// <param name="Table">The table.</param>
/// <param name="TableType">The entity type the table is the own table of.</param>
/// <param name="IsCopy">Whether the table is a derived type's: the object there is a copy of the one the declaring type's own table has, or would have.</param>
internal sealed record Landing(StoreObjectIdentifier Table, IReadOnlyEntityType TableType, bool IsCopy);

/// <summary>
/// The tables a declaration on an entity type lands on (issue 118). One function, asked for both
/// sides of a diff: a model and its snapshot record the same facts, and an answer that differed
/// between them would be a migration nobody asked for, or one that is missing.
/// </summary>
/// <remarks>
/// <para>
/// A type has one table, its own, unless it is mapped with TPC: then each type derived from it
/// that has a table repeats its columns there, and an object declared on it is one object in each
/// of those tables. That is where EF Core puts an index declared on such a type, and where 5.x
/// put its copies from 5.5.0 on. A TPC root without a table lands on its derived types' alone.
/// </para>
/// <para>
/// An entity split over tables has more than one table too, but a declaration is in one of them
/// alone: the one that has a column for everything it reads. Which that is depends on the
/// declaration, so the planner asks each of <see cref="Fragments" /> in turn.
/// </para>
/// <para>
/// A derived type of a shared table is refused by the planner, in the consumer's own model.
/// </para>
/// </remarks>
internal static class Landings
{
    /// <summary>The tables, the declaring type's own first and the derived types' by their names. Empty when there is none.</summary>
    public static IReadOnlyList<Landing> Of(IReadOnlyEntityType entityType)
    {
        List<Landing> landings = [];
        if (StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) is { } own)
            landings.Add(new Landing(own, entityType, IsCopy: false));

        if (entityType.GetMappingStrategy() != RelationalAnnotationNames.TpcMappingStrategy)
            return landings;

        foreach (var derived in entityType.GetDerivedTypes().OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            if (StoreObjectIdentifier.Create(derived, StoreObjectType.Table) is { } table)
                landings.Add(new Landing(table, derived, IsCopy: true));
        }

        return landings;
    }

    /// <summary>
    /// The tables an entity type is split over, its own first, or its own alone when it is not
    /// split. Every one of them has the key's columns, and each other column is in one.
    /// </summary>
    public static IReadOnlyList<StoreObjectIdentifier> Fragments(IReadOnlyEntityType entityType)
    {
        List<StoreObjectIdentifier> tables = [];
        if (StoreObjectIdentifier.Create(entityType, StoreObjectType.Table) is { } own)
            tables.Add(own);

        tables.AddRange(entityType.GetMappingFragments(StoreObjectType.Table).Select(fragment => fragment.StoreObject).OrderBy(table => table.Name, StringComparer.Ordinal));
        return tables;
    }
}
