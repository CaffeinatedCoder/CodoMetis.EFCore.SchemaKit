using System.Text;
namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>An index as the database holds it.</summary>
/// <param name="Table">The table.</param>
/// <param name="Name">The index's name, with the table's schema: an index is a relation of the schema its table is in.</param>
/// <param name="Method">The access method.</param>
/// <param name="IsUnique">Whether no two rows the index covers may share a key.</param>
/// <param name="NullsNotDistinct">Whether two rows with a null in the key share it. Of a unique index only.</param>
/// <param name="Keys">Each key as it is written: <c>"column"</c> or <c>(expression)</c>, then its collation, its operator class and its order, each where it is stated and not the default.</param>
/// <param name="Include">The columns carried without being keys.</param>
/// <param name="Parameters">The storage parameters that were given, each as <c>name = value</c>.</param>
/// <param name="Where">The condition, or the empty fragment.</param>
/// <param name="Extension">
/// The extension the index cannot be created without, or null. Not compared: it says how the
/// index comes to be, and an index that is there needs nothing done for it.
/// </param>
/// <param name="Concurrently">
/// Whether the index is built with <c>CONCURRENTLY</c>. Not compared: an index built either way
/// is the same index.
/// </param>
internal sealed record PgIndexDescriptor(
    TableRef Table,
    QualifiedName Name,
    SqlName Method,
    bool IsUnique,
    bool NullsNotDistinct,
    ValueList<SqlFragment> Keys,
    ValueList<SqlFragment> Include,
    ValueList<SqlFragment> Parameters,
    SqlFragment Where,
    Operational<SqlName?> Extension,
    Operational<bool> Concurrently) : ISchemaDescriptor
{
    public SchemaScope Scope => new SchemaScope.Table(Table);

    public ValueList<Dependency> Dependencies =>
    [
        .. Keys.Concat(Include).Append(Where).SelectMany(part => part.Dependencies).Distinct(),
        .. Extension.Value is { } extension ? new Dependency[] { new Dependency.Extension(extension) } : [],
    ];

    public ISchemaDescriptor WithName(QualifiedName name) => this with { Name = name };
}

/// <summary>
/// Renders <see cref="PgIndex" />: a plain <c>CREATE INDEX</c>, a drop by the qualified name, a
/// rename in place.
/// </summary>
internal sealed class PgIndexContributor(SchemaContributorDependencies dependencies)
    : SchemaContributor<PgIndex, PgIndexDescriptor>(dependencies)
{
    // Ahead of the exclusion constraints, as EF Core writes its indexes ahead of nothing that
    // needs them: neither kind reads the other.
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    // What every method's key has, and what a B-tree key has besides.
    private readonly record struct Key(KeyTarget Target, string? Collation, string? OpClass, bool Descending = false, NullOrder? Nulls = null);

    private static List<Key> KeysOf(PgIndex declaration) => declaration switch
    {
        PgIndex.BTree index  => [.. index.Keys.Select(key => new Key(key.Target, key.Collation, key.OpClass?.Name, key.Descending, key.Nulls))],
        PgIndex.Hash index   => [new Key(index.Key.Target, index.Key.Collation, index.Key.OpClass?.Name)],
        PgIndex.Gist index   => [.. index.Keys.Select(key => new Key(key.Target, key.Collation, key.OpClass?.Name))],
        PgIndex.SpGist index => [new Key(index.Key.Target, index.Key.Collation, index.Key.OpClass?.Name)],
        PgIndex.Gin index    => [.. index.Keys.Select(key => new Key(key.Target, key.Collation, key.OpClass?.Name))],
        PgIndex.Brin index   => [.. index.Keys.Select(key => new Key(key.Target, key.Collation, key.OpClass?.Name))],
    };

    private static ValueList<string> IncludeOf(PgIndex declaration) => declaration switch
    {
        PgIndex.BTree index  => index.Include,
        PgIndex.Gist index   => index.Include,
        PgIndex.SpGist index => index.Include,
        PgIndex.Hash or PgIndex.Gin or PgIndex.Brin => [],
    };

    // Each parameter under PostgreSQL's own name, in one order, and only where it was given: a
    // parameter that is not stated is the server's to decide, and the catalog does not hold it.
    private static IEnumerable<SqlFragment> ParametersOf(PgIndex declaration, SchemaSql sql)
    {
        SqlFragment? Number(SqlName name, int? value) => value is { } number ? sql.Fragment($"{name} = {number}") : null;
        SqlFragment? Flag(SqlName name, bool? value) => value is { } flag ? flag ? sql.Fragment($"{name} = on") : sql.Fragment($"{name} = off") : null;
        SqlName Name(string name) => sql.Dialect.Name(name);

        SqlFragment?[] parameters = declaration switch
        {
            PgIndex.BTree index  => [Number(Name("fillfactor"), index.FillFactor), Flag(Name("deduplicate_items"), index.DeduplicateItems)],
            PgIndex.Hash index   => [Number(Name("fillfactor"), index.FillFactor)],
            PgIndex.Gist index   =>
            [
                Number(Name("fillfactor"), index.FillFactor),
                index.Buffering is { } buffering ? sql.Fragment($"{Name("buffering")} = {Name(buffering.Token)}") : null,
            ],
            PgIndex.SpGist index => [Number(Name("fillfactor"), index.FillFactor)],
            PgIndex.Gin index    => [Flag(Name("fastupdate"), index.FastUpdate), Number(Name("gin_pending_list_limit"), index.PendingListLimit)],
            PgIndex.Brin index   => [Number(Name("pages_per_range"), index.PagesPerRange), Flag(Name("autosummarize"), index.Autosummarize)],
        };

        return parameters.OfType<SqlFragment>();
    }

    protected override PgIndexDescriptor Resolve(PgIndex declaration, SchemaResolver resolver)
    {
        var sql = resolver.Sql;
        var keys = KeysOf(declaration);

        // The keys as the resolver returns them, before anything is written around them: a default
        // name is built from what each one carries for it, which is not its SQL.
        var targets = keys
            .Select(key => key.Target switch
            {
                KeyTarget.Column column     => resolver.Column(column.Path),
                KeyTarget.Template template => resolver.Template(template.Sql),
            })
            .ToList();

        var name = resolver.Name(declaration.Name, "IX", targets);

        PgJsonMembers.RequireImmutable(
            resolver,
            [.. keys.SelectMany(key => PgJsonMembers.PathsOf(key.Target)), .. declaration.Where is { } condition ? PgJsonMembers.PathsOf(condition.Template) : []],
            PgStrings.TheIndex(name.Name));

        foreach (var key in keys)
        {
            // An operator class of PostgreSQL's own on a column whose type does not take it: the
            // server would refuse the index when the migration is applied, so it is refused here,
            // where the migration is scaffolded. A class or a type this package does not know is
            // the server's to judge.
            if (key is { Target: KeyTarget.Column column, OpClass: { } named }
                && resolver.StoreType(column.Path) is var storeType
                && PgOperatorClasses.Refuses(declaration.Kind, named, storeType))
                throw new InvalidOperationException(PgStrings.OperatorClassNotForType(name.Name, named, declaration.Kind, column.Path, storeType));
        }

        return new PgIndexDescriptor(
            resolver.Table,
            resolver.Table.Qualify(name),
            resolver.Dialect.Name(declaration.Kind),
            declaration is PgIndex.BTree { Unique: not null },
            declaration is PgIndex.BTree { Unique.NullsDistinct: false },
            [.. keys.Select((key, index) =>
            {
                // A column stands bare and everything else in parentheses of its own, which is what
                // PostgreSQL's grammar asks of a key. What the declaration held does not say which:
                // a path into a JSON document is a column there and an expression here.
                var target = targets[index].IsColumn ? targets[index] : sql.Fragment($"({targets[index]})");

                if (key.Collation is { } collation) target = sql.Fragment($"{target} COLLATE {resolver.Dialect.Name(collation)}");
                if (key.OpClass is { } opClass) target = sql.Fragment($"{target} {resolver.Dialect.Name(opClass)}");

                // What PostgreSQL does unasked is not written: an ascending key has its nulls
                // last and a descending one first, and the catalog says neither. Written out,
                // a key that says so would be another index than one that does not, and the
                // database holds one index for both.
                return (key.Descending, key.Nulls) switch
                {
                    (false, NullOrder.First) => sql.Fragment($"{target} NULLS FIRST"),
                    (false, _)               => target,
                    (true, NullOrder.Last)   => sql.Fragment($"{target} DESC NULLS LAST"),
                    (true, _)                => sql.Fragment($"{target} DESC"),
                };
            })],
            [.. IncludeOf(declaration).Select(path => resolver.Column(path) is { IsColumn: true } column
                ? column.As(ColumnRole.Included)
                : throw new InvalidOperationException(PgStrings.IncludeNeedsAColumn(name.Name, path)))],
            [.. ParametersOf(declaration, sql)],
            declaration.Where is { } where ? resolver.Template(where.Template).As(ColumnRole.Condition) : SqlFragment.Empty,
            new Operational<SqlName?>(NeedsBtreeGist(declaration, resolver) ? resolver.Dialect.Name(PgExclusionContributor.BtreeGist) : null),
            new Operational<bool>(declaration.Concurrently));
    }

    // The rule of an exclusion constraint, for what an index can say: under GiST a column whose
    // type has no GiST operator class of its own needs btree_gist, unless the key names a class,
    // which may be anyone's. An expression's type is not known here, so it is the model's to
    // declare the extension for one.
    private static bool NeedsBtreeGist(PgIndex declaration, SchemaResolver resolver)
        => declaration is PgIndex.Gist gist
           && gist.Keys.Any(key => key is { Target: KeyTarget.Column column, OpClass: null }
                                   && !PgExclusionContributor.HasGistOperatorClass(resolver.StoreType(column.Path)));

    // No IF NOT EXISTS: a name that is taken fails (42P07) and nothing is passed over. The index
    // is created in its table's schema, so the statement names it without one; PostgreSQL has no
    // syntax for another. B-tree is what PostgreSQL takes when no method is named, and so no
    // method is named for it.
    protected override void Create(PgIndexDescriptor descriptor, SchemaSql sql)
    {
        var unique = descriptor.IsUnique ? sql.Fragment($"UNIQUE ") : SqlFragment.Empty;
        var method = descriptor.Method.Name == "btree" ? SqlFragment.Empty : sql.Fragment($" USING {descriptor.Method}");
        var include = descriptor.Include.Count > 0 ? sql.Fragment($" INCLUDE ({sql.List(descriptor.Include)})") : SqlFragment.Empty;
        var nulls = descriptor.NullsNotDistinct ? sql.Fragment($" NULLS NOT DISTINCT") : SqlFragment.Empty;
        var with = descriptor.Parameters.Count > 0 ? sql.Fragment($" WITH ({sql.List(descriptor.Parameters)})") : SqlFragment.Empty;
        var where = descriptor.Where.IsEmpty ? SqlFragment.Empty : sql.Fragment($" WHERE {descriptor.Where}");

        // CONCURRENTLY cannot run in a transaction, so the statement is written outside the
        // migration's. It is how the index is built and no part of the descriptor's equality:
        // an index built either way is the same index.
        var concurrently = descriptor.Concurrently.Value ? sql.Fragment($"CONCURRENTLY ") : SqlFragment.Empty;

        sql.Statement(
            $"CREATE {unique}INDEX {concurrently}{descriptor.Name.Name} ON {descriptor.Table}{method} ({sql.List(descriptor.Keys)}){include}{nulls}{with}{where}",
            suppressTransaction: descriptor.Concurrently.Value);
    }

    // By its qualified name: without the schema PostgreSQL takes the first index of that name on
    // the search path, which may be another schema's. No IF EXISTS: an index that is gone already
    // is a database that differs from the snapshot.
    protected override void Drop(PgIndexDescriptor descriptor, SchemaSql sql)
        => sql.Statement($"DROP INDEX {descriptor.Name}");

    // A rename, and a rebuild for everything else (decided with issue 27). PostgreSQL can set a
    // storage parameter in place, which leaves the pages that are there as they were: the index
    // would not be the one a new database gets.
    protected override SchemaChange Plan(PgIndexDescriptor from, PgIndexDescriptor to, SchemaSql sql)
    {
        if (from.WithName(to.Name).Equals(to))
        {
            // The new name without a schema: a rename leaves the index in the schema it is in.
            sql.Statement($"ALTER INDEX {from.Name} RENAME TO {to.Name.Name}");
            return new SchemaChange.InPlace();
        }

        if (!to.Concurrently.Value) return new SchemaChange.Rebuild();

        // Built concurrently, a rebuild is no drop and create (issue 213). The build runs outside
        // the migration's transaction, so a drop written ahead of it is committed before it
        // begins: until it ends the table has no such index, a unique one checks nothing, and
        // when the build fails the index that was there is gone. So the new index is built
        // beside the old one, and the old one goes only once the new one is there. The drop and
        // the rename that follow are in one transaction: no moment has neither index, and none
        // has the new one under another name than the model's.
        //
        // The two have one name here: an index that changes its name and what it is at once is
        // no pair to the planner but one index dropped and another created, in that order.
        var beside = to with { Name = to.Name with { Name = Beside(to.Name.Name, sql.Dialect) } };
        Create(beside, sql);
        Drop(from, sql);
        sql.Statement($"ALTER INDEX {beside.Name} RENAME TO {to.Name.Name}");

        return new SchemaChange.InPlace();
    }

    /// <summary>What PostgreSQL itself puts behind the name of an index it builds beside another, in <c>REINDEX CONCURRENTLY</c>.</summary>
    internal const string BesideSuffix = "_ccnew";

    // The name the new index has while the old one is still there. PostgreSQL's own suffix for
    // the same thing: a build that failed leaves an invalid index of that name, which is what
    // its documentation tells an operator to look for and drop. A name too long to take the
    // suffix is cut, at a character, to the bytes PostgreSQL keeps; two indexes whose names then
    // come out alike fail at the second build (42P07) and replace nothing, and so does a name
    // something else holds. A model that states a shorter limit of its own is refused by the
    // dialect here, with the name.
    private static Identifier Beside(Identifier name, ISchemaDialect dialect)
    {
        var limit = NpgsqlSchemaDialect.DefaultMaxIdentifierLength - BesideSuffix.Length;
        var kept = name.Name;
        while (Encoding.UTF8.GetByteCount(kept) > limit)
            kept = kept[..^(char.IsLowSurrogate(kept[^1]) ? 2 : 1)];

        return dialect.Identifier(kept + BesideSuffix);
    }

    // What PostgreSQL does is the package's one table's to say, not this contributor's.
    protected override Impact ImpactOf(PgIndexDescriptor descriptor, BaseChange change)
        => PgImpact.Of(PgObjectKind.Index, descriptor.Dependencies, change);

    // An index is a relation: its name is unique within its schema, among tables, views,
    // sequences and every other index.
    protected override IEnumerable<NameClaim> Claims(PgIndexDescriptor descriptor)
        => [new NameClaim.Relation(descriptor.Name.Schema, descriptor.Name.Name)];
}

/// <summary>
/// Which of PostgreSQL's own operator classes a column's type does not take. It answers only
/// where both are known: a class an extension brings, and a type the model names that is not on
/// the list (a domain, an enum, an extension's type), are the server's to judge.
/// </summary>
/// <remarks>
/// Each answer is held against the server: <c>PgIndexLiveTests</c> creates an index for every
/// pair of a class and a type listed here, on PostgreSQL 16 and 18, and compares.
/// </remarks>
internal static class PgOperatorClasses
{
    private enum Family
    {
        Text,
        Varchar,
        Character,
        Jsonb,
        TsVector,
        Network,
        Range,
        Multirange,
        Array,
        Other,
    }

    // The store types Npgsql gives the CLR types it maps, by their names without a length.
    private static readonly Dictionary<string, Family> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = Family.Text,
        ["character varying"] = Family.Varchar,
        ["varchar"] = Family.Varchar,
        ["character"] = Family.Character,
        ["bpchar"] = Family.Character,
        ["jsonb"] = Family.Jsonb,
        ["tsvector"] = Family.TsVector,
        ["inet"] = Family.Network,
        ["cidr"] = Family.Network,
        ["int4range"] = Family.Range,
        ["int8range"] = Family.Range,
        ["numrange"] = Family.Range,
        ["daterange"] = Family.Range,
        ["tsrange"] = Family.Range,
        ["tstzrange"] = Family.Range,
        ["int4multirange"] = Family.Multirange,
        ["int8multirange"] = Family.Multirange,
        ["nummultirange"] = Family.Multirange,
        ["datemultirange"] = Family.Multirange,
        ["tsmultirange"] = Family.Multirange,
        ["tstzmultirange"] = Family.Multirange,
        ["smallint"] = Family.Other,
        ["integer"] = Family.Other,
        ["bigint"] = Family.Other,
        ["boolean"] = Family.Other,
        ["numeric"] = Family.Other,
        ["real"] = Family.Other,
        ["double precision"] = Family.Other,
        ["uuid"] = Family.Other,
        ["date"] = Family.Other,
        ["time without time zone"] = Family.Other,
        ["timestamp without time zone"] = Family.Other,
        ["timestamp with time zone"] = Family.Other,
        ["interval"] = Family.Other,
        ["bytea"] = Family.Other,
        ["json"] = Family.Other,
    };

    private static readonly Dictionary<(string Method, string Class), Family[]> Classes = new()
    {
        [("btree", "text_pattern_ops")] = [Family.Text, Family.Varchar],
        [("btree", "varchar_pattern_ops")] = [Family.Text, Family.Varchar],
        // Measured: the server takes this one for text too, and text_pattern_ops for no character(n).
        [("btree", "bpchar_pattern_ops")] = [Family.Text, Family.Varchar, Family.Character],
        [("gin", "jsonb_ops")] = [Family.Jsonb],
        [("gin", "jsonb_path_ops")] = [Family.Jsonb],
        [("gin", "array_ops")] = [Family.Array],
        [("gin", "tsvector_ops")] = [Family.TsVector],
        [("gist", "range_ops")] = [Family.Range],
        [("gist", "multirange_ops")] = [Family.Multirange],
        [("gist", "inet_ops")] = [Family.Network],
        [("spgist", "range_ops")] = [Family.Range],
        [("spgist", "text_ops")] = [Family.Text, Family.Varchar],
        [("spgist", "inet_ops")] = [Family.Network],
    };

    /// <summary>The classes the table answers for, for the test that asks the server about each.</summary>
    public static IEnumerable<(string Method, string Class)> Known => Classes.Keys;

    /// <summary>The types the table answers for, besides an array of any of them.</summary>
    public static IEnumerable<string> KnownTypes => Types.Keys;

    public static bool Refuses(string method, string opClass, string storeType)
    {
        if (!Classes.TryGetValue((method, opClass), out var takes)) return false;

        var type = storeType.Trim();
        if (type.EndsWith("[]", StringComparison.Ordinal)) return !takes.Contains(Family.Array);

        // "character varying(40)", "numeric(18,2)", "timestamp(3) with time zone".
        var open = type.IndexOf('(');
        if (open >= 0)
        {
            var close = type.IndexOf(')', open);
            if (close < 0) return false;
            type = string.Concat(type.AsSpan(0, open).TrimEnd(), type.AsSpan(close + 1));
        }

        return Types.TryGetValue(type, out var family) && !takes.Contains(family);
    }
}
