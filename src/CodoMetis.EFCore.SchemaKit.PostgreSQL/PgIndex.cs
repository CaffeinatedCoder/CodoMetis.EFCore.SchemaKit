using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// A PostgreSQL index. One case per access method, each with the options PostgreSQL has for that
/// method and no other. A value: compare it, keep it in a field, change it with <c>with</c>.
/// </summary>
/// <remarks>
/// <para>
/// A key is a column or an expression, and so the index may be one EF Core's own <c>HasIndex</c>
/// could declare as well. Declared here it is created, renamed and dropped by SchemaKit's
/// statements, and EF Core's index metadata does not hold it.
/// </para>
/// <para>
/// Stored as one annotation, <c>CodoMetis:Pg.Index:{hash}</c>. The owner, the kinds and the
/// property names written below are in consumers' snapshots: a file format.
/// </para>
/// </remarks>
public closed record PgIndex : IPostgresObject, ISchemaDeclaration<PgIndex>
{
    /// <summary>The owner of every index: the middle part of its annotation key.</summary>
    public static string OwnerId => "Pg.Index";

    /// <inheritdoc />
    public string Owner => OwnerId;

    /// <summary>The index's name, or null for <c>IX_{table}_{keys}</c>.</summary>
    public string? Name { get; init; }

    /// <inheritdoc />
    public int Epoch { get; init; } = 1;

    /// <summary>The rows the index covers, or null for all of them.</summary>
    public Predicate? Where { get; init; }

    /// <inheritdoc />
    public string Kind => this switch
    {
        BTree => "btree",
    };

    /// <summary>
    /// The same index over fewer rows: its condition and <paramref name="predicate" />. One that
    /// is already the condition, or its last conjunct, is not added again.
    /// </summary>
    /// <param name="predicate">The condition to add.</param>
    public PgIndex AndWhere(Predicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return this with { Where = Where?.And(predicate) ?? predicate };
    }

    /// <inheritdoc />
    public void Write(SchemaJsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _ = this switch
        {
            BTree btree => WriteBTree(btree),
        };
        writer.OptionalTemplate("where", Where?.Template);

        bool WriteBTree(BTree btree)
        {
            writer.Array("keys", btree.Keys, WriteKey);
            writer.OptionalObject("unique", btree.Unique, static (unique, value) => unique.Boolean("nullsDistinct", value.NullsDistinct));
            writer.Strings("include", btree.Include);
            return true;
        }
    }

    /// <inheritdoc />
    public static PgIndex Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(reader);

        PgIndex index = header.Kind switch
        {
            "btree" => new BTree(reader.Array("keys", ReadKey))
            {
                Unique = reader.OptionalObject("unique", static unique => new Uniqueness(unique.Boolean("nullsDistinct"))),
                Include = reader.Strings("include"),
            },
            var kind => throw new InvalidOperationException(PgStrings.UnknownKind(OwnerId, kind)),
        };

        return index with
        {
            Name = header.Name,
            Epoch = header.Epoch,
            Where = reader.OptionalTemplate("where") is { } where ? new Predicate(where) : null,
        };
    }

    // A key's target is written under the name of its case, and exactly one of the two is there,
    // as for an element of an exclusion constraint. The direction is always written: left out when
    // it is false, a reader would have to have a default, and a default is how a stored index
    // comes out of a snapshot as another one.
    private static void WriteKey(SchemaJsonWriter writer, BTreeKey key)
    {
        writer.OptionalString("column", (key.Target as KeyTarget.Column)?.Path);
        writer.OptionalTemplate("sql", (key.Target as KeyTarget.Template)?.Sql);
        writer.Boolean("desc", key.Descending);
        writer.OptionalString("nulls", key.Nulls switch
        {
            NullOrder.First => "first",
            NullOrder.Last  => "last",
            _               => null,
        });
    }

    private static BTreeKey ReadKey(SchemaJsonReader reader)
    {
        var column = reader.OptionalString("column");
        var sql = reader.OptionalTemplate("sql");
        KeyTarget target = (column, sql) switch
        {
            ({ } path, null)     => new KeyTarget.Column(path),
            (null, { } template) => new KeyTarget.Template(template),
            _                    => throw new InvalidOperationException(PgStrings.KeyNeedsOneTarget),
        };

        return new BTreeKey(target)
        {
            Descending = reader.Boolean("desc"),
            Nulls = reader.OptionalString("nulls") switch
            {
                null    => null,
                "first" => NullOrder.First,
                "last"  => NullOrder.Last,
                var token => throw new InvalidOperationException(PgStrings.UnknownNullOrder(token)),
            },
        };
    }

    private static ValueList<TKey> RequireKeys<TKey>(ValueList<TKey> keys, string name)
    {
        ArgumentNullException.ThrowIfNull(keys, name);
        if (keys.Count == 0) throw new ArgumentException(PgStrings.IndexNeedsAKey, name);
        if (keys.Any(key => key is null)) throw new ArgumentException(PgStrings.IndexNeedsAKey, name);
        return keys;
    }

    private static ValueList<string> RequirePaths(ValueList<string> paths, string name)
    {
        ArgumentNullException.ThrowIfNull(paths, name);
        return paths.All(path => !string.IsNullOrWhiteSpace(path)) ? paths : throw new ArgumentException(PgStrings.IncludeNeedsAPath, name);
    }

    /// <summary>
    /// A B-tree index, PostgreSQL's default: the one access method with an order, and the only
    /// one that can be unique.
    /// </summary>
    /// <param name="Keys">The keys, in their order. At least one.</param>
    public sealed record BTree(ValueList<BTreeKey> Keys) : PgIndex
    {
        /// <summary>The keys, in their order. At least one.</summary>
        public ValueList<BTreeKey> Keys
        {
            get;
            init => field = RequireKeys(value, nameof(Keys));
        } = RequireKeys(Keys, nameof(Keys));

        /// <summary>
        /// Null for an index that is not unique. <c>NULLS NOT DISTINCT</c> is inside, so there is
        /// no way to say it of an index that is not unique.
        /// </summary>
        public Uniqueness? Unique { get; init; }

        /// <summary>
        /// The properties whose columns the index carries without their being keys
        /// (<c>INCLUDE</c>), by their paths.
        /// </summary>
        public ValueList<string> Include
        {
            get;
            init => field = RequirePaths(value, nameof(Include));
        } = [];
    }
}

/// <summary>That an index is unique, and how it counts rows whose key holds a null.</summary>
/// <param name="NullsDistinct">
/// True, PostgreSQL's default: two rows with a null in the key are not the same key, so both are
/// kept. False writes <c>NULLS NOT DISTINCT</c>: the second is refused.
/// </param>
public sealed record Uniqueness(bool NullsDistinct = true);

/// <summary>Where a B-tree key puts the rows whose value is null.</summary>
public enum NullOrder
{
    /// <summary><c>NULLS FIRST</c>, which is where a descending key puts them anyway.</summary>
    First,

    /// <summary><c>NULLS LAST</c>, which is where an ascending key puts them anyway.</summary>
    Last,
}

/// <summary>A key of a B-tree index: what is indexed, and in which order.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record BTreeKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Target ?? throw new ArgumentNullException(nameof(Target));

    /// <summary>Whether the key is in descending order.</summary>
    public bool Descending { get; init; }

    /// <summary>Where nulls are put, or null for where the direction puts them.</summary>
    public NullOrder? Nulls
    {
        get;
        init => field = value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>The same key in descending order.</summary>
    public BTreeKey Desc() => this with { Descending = true };

    /// <summary>The same key with nulls before every other value.</summary>
    public BTreeKey NullsFirst() => this with { Nulls = NullOrder.First };

    /// <summary>The same key with nulls after every other value.</summary>
    public BTreeKey NullsLast() => this with { Nulls = NullOrder.Last };
}

/// <summary>
/// A key as <see cref="PgSchema{TEntity}.Key" /> makes it: what is indexed, with the type of its
/// values, before anything says which access method it is for. An order makes it a B-tree key.
/// </summary>
/// <typeparam name="TValue">The type of the key's values.</typeparam>
public sealed class PgKey<TValue>
{
    internal PgKey(KeyTarget target) => Target = target;

    /// <summary>A column or an expression.</summary>
    public KeyTarget Target { get; }

    /// <summary>A B-tree key in descending order. No other access method has an order.</summary>
    public BTreeKey Desc() => new BTreeKey(Target).Desc();

    /// <summary>A B-tree key with nulls before every other value.</summary>
    public BTreeKey NullsFirst() => new BTreeKey(Target).NullsFirst();

    /// <summary>A B-tree key with nulls after every other value.</summary>
    public BTreeKey NullsLast() => new BTreeKey(Target).NullsLast();

    /// <summary>The key as a B-tree key, ascending.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator BTreeKey(PgKey<TValue> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new BTreeKey(key.Target);
    }
}
