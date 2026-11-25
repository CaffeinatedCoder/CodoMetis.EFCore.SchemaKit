namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>The kinds of object this package renders, as far as the table below has rows for them.</summary>
internal enum PgObjectKind
{
    ExclusionConstraint,
    Index,
}

/// <summary>
/// What PostgreSQL does to an object when EF Core changes what the object depends on: one table
/// for the package, read by every contributor's <c>ImpactOf</c> (issue 117).
/// </summary>
/// <remarks>
/// <para>
/// A row is a measurement, and its test is the live server: <c>PgExclusionLiveTests</c> and
/// <c>PgIndexLiveTests</c> have one test for each way an object reads a column. A row is added with its test, not from the
/// documentation. The rows of one change are read in their order and the first that fits
/// answers, because PostgreSQL's answer for a column read in two ways is not the stronger of the
/// two: a column that is an element by itself takes the constraint with it, whatever else of the
/// constraint reads it too.
/// </para>
/// <para>
/// A change no row answers is <see cref="Impact.RebuildAround" />: dropped before EF Core's
/// operations and created after them. That is a rebuild that may not have been needed, never an
/// object that is gone without a word.
/// </para>
/// </remarks>
internal static class PgImpact
{
    // Role: for a change of a column, how the object reads it; null for any way, and for a change of the table.
    private sealed record Row(PgObjectKind Kind, Type Change, ColumnRole? Role, Impact Impact);

    private static readonly Row[] Rows =
    [
        // ALTER TABLE … RENAME COLUMN, RENAME TO and SET SCHEMA rewrite what reads the column or
        // the table.
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.ColumnRenamed), null, new Impact.Follows()),
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.TableRenamed), null, new Impact.Follows()),
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.TableMoved), null, new Impact.Follows()),

        // A type change rebuilds the index under a new OID; by name and definition it is the same.
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.ColumnAltered), null, new Impact.Unaffected()),

        // With the column as an element by itself the constraint goes with it, in silence,
        // whatever else reads the column. With the column in an element's expression or in the
        // condition only, the drop is refused while the constraint is there (2BP01). Measured on
        // 16 and 18 (issue 102).
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.ColumnDropped), ColumnRole.Key, new Impact.Lost()),
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.ColumnDropped), ColumnRole.Expression, new Impact.RebuildAround()),
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.ColumnDropped), ColumnRole.Condition, new Impact.RebuildAround()),

        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.TableDropped), null, new Impact.Lost()),

        // Not something PostgreSQL does; were a table created again, nothing attached to it would be on the new one.
        new(PgObjectKind.ExclusionConstraint, typeof(BaseChange.TableRebuilt), null, new Impact.Lost()),

        // An index follows the renames as a constraint does.
        new(PgObjectKind.Index, typeof(BaseChange.ColumnRenamed), null, new Impact.Follows()),
        new(PgObjectKind.Index, typeof(BaseChange.TableRenamed), null, new Impact.Follows()),
        new(PgObjectKind.Index, typeof(BaseChange.TableMoved), null, new Impact.Follows()),

        // PostgreSQL builds the index again under the altered column, by the definition it has.
        new(PgObjectKind.Index, typeof(BaseChange.ColumnAltered), null, new Impact.Unaffected()),

        // An index goes with any column it reads, in silence: a key, a column of a key's
        // expression, a column of the condition, an included one. Nothing is refused, unlike
        // under an exclusion constraint. A column that only SQL written by hand may read has no
        // row and is rebuilt around.
        new(PgObjectKind.Index, typeof(BaseChange.ColumnDropped), ColumnRole.Key, new Impact.Lost()),
        new(PgObjectKind.Index, typeof(BaseChange.ColumnDropped), ColumnRole.Expression, new Impact.Lost()),
        new(PgObjectKind.Index, typeof(BaseChange.ColumnDropped), ColumnRole.Condition, new Impact.Lost()),
        new(PgObjectKind.Index, typeof(BaseChange.ColumnDropped), ColumnRole.Included, new Impact.Lost()),

        new(PgObjectKind.Index, typeof(BaseChange.TableDropped), null, new Impact.Lost()),
        new(PgObjectKind.Index, typeof(BaseChange.TableRebuilt), null, new Impact.Lost()),
    ];

    /// <summary>The table's answer for an object of this kind that reads these things.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="reads">The object's dependencies, as its descriptor lists them.</param>
    /// <param name="change">The change.</param>
    public static Impact Of(PgObjectKind kind, IEnumerable<Dependency> reads, BaseChange change)
    {
        // The change names its table as the target has it and the object as the source has it, so
        // a column is found by its name: an object of this package is on one table.
        var column = change switch
        {
            BaseChange.ColumnRenamed renamed => renamed.From,
            BaseChange.ColumnAltered altered => altered.Column,
            BaseChange.ColumnDropped dropped => dropped.Column,
            _                                => null,
        };

        var roles = column is null ? [] : reads.OfType<Dependency.Column>().Where(read => read.Name.Equals(column)).Select(read => read.Role).ToHashSet();

        foreach (var row in Rows)
        {
            if (row.Kind == kind && row.Change == change.GetType() && (row.Role is not { } role || roles.Contains(role)))
                return row.Impact;
        }

        // Among the rest: SQL written by hand, which names no column and may read this one or not.
        return new Impact.RebuildAround();
    }
}
