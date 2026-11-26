namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>
/// What an index and an exclusion constraint cannot hold of a JSON document: a member whose
/// cast from text is not immutable.
/// </summary>
/// <remarks>
/// <para>
/// Npgsql's queries read a date or a time out of a document as
/// <c>CAST(doc -&gt;&gt; 'At' AS timestamp with time zone)</c>. That cast depends on the session
/// (<c>DateStyle</c>, <c>TimeZone</c>, <c>lc_monetary</c>), and PostgreSQL takes it neither as a
/// key nor in a condition (42P17). The same member written as text is taken, and serves no query
/// EF Core writes. So it is refused where the model is resolved, with the member named: the
/// migration would fail when it is applied, and a text fallback would be an index nobody uses.
/// </para>
/// <para>
/// The list is the server's, asked on 16 and 18 for every type in it and for the types beside it
/// that are taken (<c>The_casts_an_index_refuses_are_the_servers</c>). It is the index's and the
/// constraint's rule and not the dialect's: a trigger may read such a member.
/// </para>
/// </remarks>
internal static class PgJsonMembers
{
    private static readonly HashSet<string> NotImmutable = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "time", "time without time zone", "time with time zone", "timetz",
        "timestamp", "timestamp without time zone", "timestamp with time zone", "timestamptz",
        "interval", "money",
    };

    /// <summary>The types whose cast from text an index refuses, for the test that asks the server.</summary>
    public static IReadOnlyCollection<string> Refused => NotImmutable;

    /// <summary>Whether a cast from text to this store type is one an index refuses.</summary>
    public static bool IsRefused(string storeType)
    {
        // A precision is no part of the rule: timestamp(3) with time zone is the same cast.
        var open = storeType.IndexOf('(', StringComparison.Ordinal);
        var close = storeType.IndexOf(')', StringComparison.Ordinal);
        var type = open >= 0 && close > open ? string.Concat(storeType.AsSpan(0, open).TrimEnd(), storeType.AsSpan(close + 1)) : storeType;
        return NotImmutable.Contains(type.Trim());
    }

    /// <summary>Refuses a member of a JSON document that the object reads through such a cast.</summary>
    /// <param name="resolver">The resolver of the declaration's table.</param>
    /// <param name="paths">Every property path the object reads, in its keys and in its condition.</param>
    /// <param name="what">The object, for the message: <c>The index 'ix'</c>.</param>
    public static void RequireImmutable(SchemaResolver resolver, IEnumerable<string> paths, string what)
    {
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            // A column of such a type is no cast, and no member: only what the resolver answers
            // with an expression is one.
            if (resolver.Column(path).IsColumn) continue;

            var storeType = resolver.StoreType(path);
            if (IsRefused(storeType))
                throw new InvalidOperationException(PgStrings.JsonMemberCastNotImmutable(what, path, storeType));
        }
    }

    /// <summary>The paths a key or a condition reads: its own, or those of its template.</summary>
    public static IEnumerable<string> PathsOf(KeyTarget target) => target switch
    {
        KeyTarget.Column column     => [column.Path],
        KeyTarget.Template template => PathsOf(template.Sql),
    };

    /// <inheritdoc cref="PathsOf(KeyTarget)" />
    public static IEnumerable<string> PathsOf(SqlTemplate template) => template.Segments.OfType<SqlSegment.Column>().Select(segment => segment.Path);
}
