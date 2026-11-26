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
        BTree  => "btree",
        Hash   => "hash",
        Gist   => "gist",
        SpGist => "spgist",
        Gin    => "gin",
        Brin   => "brin",
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

        // Each case writes what it has, in one order. A parameter that was not given is left out,
        // which is the one thing a writer may leave out: a null. A flag is always written.
        switch (this)
        {
            case BTree index:
                writer.Array("keys", index.Keys, static (key, value) =>
                {
                    WriteTarget(key, value.Target);
                    key.Boolean("desc", value.Descending);
                    key.OptionalString("nulls", value.Nulls switch
                    {
                        NullOrder.First => "first",
                        NullOrder.Last  => "last",
                        _               => null,
                    });
                    WriteClasses(key, value.Collation, value.OpClass?.Name);
                });
                writer.OptionalObject("unique", index.Unique, static (unique, value) => unique.Boolean("nullsDistinct", value.NullsDistinct));
                writer.Strings("include", index.Include);
                writer.OptionalInt32("fillFactor", index.FillFactor);
                writer.OptionalBoolean("deduplicateItems", index.DeduplicateItems);
                break;

            case Hash index:
                writer.Object("key", key => WriteKey(key, index.Key.Target, index.Key.Collation, index.Key.OpClass?.Name));
                writer.OptionalInt32("fillFactor", index.FillFactor);
                break;

            case Gist index:
                writer.Array("keys", index.Keys, static (key, value) => WriteKey(key, value.Target, value.Collation, value.OpClass?.Name));
                writer.Strings("include", index.Include);
                writer.OptionalInt32("fillFactor", index.FillFactor);
                writer.OptionalString("buffering", index.Buffering?.Token);
                break;

            case SpGist index:
                writer.Object("key", key => WriteKey(key, index.Key.Target, index.Key.Collation, index.Key.OpClass?.Name));
                writer.Strings("include", index.Include);
                writer.OptionalInt32("fillFactor", index.FillFactor);
                break;

            case Gin index:
                writer.Array("keys", index.Keys, static (key, value) => WriteKey(key, value.Target, value.Collation, value.OpClass?.Name));
                writer.OptionalBoolean("fastUpdate", index.FastUpdate);
                writer.OptionalInt32("pendingListLimit", index.PendingListLimit);
                break;

            case Brin index:
                writer.Array("keys", index.Keys, static (key, value) => WriteKey(key, value.Target, value.Collation, value.OpClass?.Name));
                writer.OptionalInt32("pagesPerRange", index.PagesPerRange);
                writer.OptionalBoolean("autosummarize", index.Autosummarize);
                break;
        }

        writer.OptionalTemplate("where", Where?.Template);
    }

    /// <inheritdoc />
    public static PgIndex Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(reader);

        PgIndex index = header.Kind switch
        {
            "btree" => new BTree(reader.Array("keys", static key => new BTreeKey(ReadTarget(key))
            {
                Descending = key.Boolean("desc"),
                Nulls = key.OptionalString("nulls") switch
                {
                    null      => null,
                    "first"   => NullOrder.First,
                    "last"    => NullOrder.Last,
                    var token => throw new InvalidOperationException(PgStrings.UnknownNullOrder(token)),
                },
                Collation = key.OptionalString("collation"),
                OpClass = key.OptionalString("ops") is { } ops ? new BTreeOpClass(ops) : null,
            }))
            {
                Unique = reader.OptionalObject("unique", static unique => new Uniqueness(unique.Boolean("nullsDistinct"))),
                Include = reader.Strings("include"),
                FillFactor = reader.OptionalInt32("fillFactor"),
                DeduplicateItems = reader.OptionalBoolean("deduplicateItems"),
            },
            "hash" => new Hash(reader.Object("key", static key => new HashKey(ReadTarget(key))
            {
                Collation = key.OptionalString("collation"),
                OpClass = key.OptionalString("ops") is { } ops ? new HashOpClass(ops) : null,
            }))
            {
                FillFactor = reader.OptionalInt32("fillFactor"),
            },
            "gist" => new Gist(reader.Array("keys", static key => new GistKey(ReadTarget(key))
            {
                Collation = key.OptionalString("collation"),
                OpClass = key.OptionalString("ops") is { } ops ? new GistOpClass(ops) : null,
            }))
            {
                Include = reader.Strings("include"),
                FillFactor = reader.OptionalInt32("fillFactor"),
                Buffering = reader.OptionalString("buffering") is { } buffering ? GistBuffering.Parse(buffering) : null,
            },
            "spgist" => new SpGist(reader.Object("key", static key => new SpGistKey(ReadTarget(key))
            {
                Collation = key.OptionalString("collation"),
                OpClass = key.OptionalString("ops") is { } ops ? new SpGistOpClass(ops) : null,
            }))
            {
                Include = reader.Strings("include"),
                FillFactor = reader.OptionalInt32("fillFactor"),
            },
            "gin" => new Gin(reader.Array("keys", static key => new GinKey(ReadTarget(key))
            {
                Collation = key.OptionalString("collation"),
                OpClass = key.OptionalString("ops") is { } ops ? new GinOpClass(ops) : null,
            }))
            {
                FastUpdate = reader.OptionalBoolean("fastUpdate"),
                PendingListLimit = reader.OptionalInt32("pendingListLimit"),
            },
            "brin" => new Brin(reader.Array("keys", static key => new BrinKey(ReadTarget(key))
            {
                Collation = key.OptionalString("collation"),
                OpClass = key.OptionalString("ops") is { } ops ? new BrinOpClass(ops) : null,
            }))
            {
                PagesPerRange = reader.OptionalInt32("pagesPerRange"),
                Autosummarize = reader.OptionalBoolean("autosummarize"),
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
    // as for an element of an exclusion constraint.
    private static void WriteTarget(SchemaJsonWriter writer, KeyTarget target)
    {
        writer.OptionalString("column", (target as KeyTarget.Column)?.Path);
        writer.OptionalTemplate("sql", (target as KeyTarget.Template)?.Sql);
    }

    private static void WriteClasses(SchemaJsonWriter writer, string? collation, string? opClass)
    {
        writer.OptionalString("collation", collation);
        writer.OptionalString("ops", opClass);
    }

    private static void WriteKey(SchemaJsonWriter writer, KeyTarget target, string? collation, string? opClass)
    {
        WriteTarget(writer, target);
        WriteClasses(writer, collation, opClass);
    }

    private static KeyTarget ReadTarget(SchemaJsonReader reader)
    {
        var column = reader.OptionalString("column");
        var sql = reader.OptionalTemplate("sql");
        return (column, sql) switch
        {
            ({ } path, null)     => new KeyTarget.Column(path),
            (null, { } template) => new KeyTarget.Template(template),
            _                    => throw new InvalidOperationException(PgStrings.KeyNeedsOneTarget),
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

    // The bounds are PostgreSQL's own, the same on 16 and 18 (PgIndexLiveTests holds each against
    // the server). A value outside them fails when the migration is applied; here it fails where
    // it is written.
    private static int? Within(int? value, int low, int high, string name)
        => value is null || (value >= low && value <= high) ? value : throw new ArgumentOutOfRangeException(name, value, PgStrings.OutOfRange(name, low, high));

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

        /// <summary><c>fillfactor</c>, 10 to 100, or null for PostgreSQL's own.</summary>
        public int? FillFactor
        {
            get;
            init => field = Within(value, 10, 100, nameof(FillFactor));
        }

        /// <summary><c>deduplicate_items</c>, or null for PostgreSQL's own.</summary>
        public bool? DeduplicateItems { get; init; }
    }

    /// <summary>A hash index: one key, for equality alone. It has no order, cannot be unique and carries no other column.</summary>
    /// <param name="Key">The key.</param>
    public sealed record Hash(HashKey Key) : PgIndex
    {
        /// <summary>The key.</summary>
        public HashKey Key
        {
            get;
            init => field = value ?? throw new ArgumentNullException(nameof(value));
        } = Key ?? throw new ArgumentNullException(nameof(Key));

        /// <summary><c>fillfactor</c>, 10 to 100, or null for PostgreSQL's own.</summary>
        public int? FillFactor
        {
            get;
            init => field = Within(value, 10, 100, nameof(FillFactor));
        }
    }

    /// <summary>A GiST index: several keys, and columns it carries. It has no order and cannot be unique.</summary>
    /// <param name="Keys">The keys, in their order. At least one.</param>
    public sealed record Gist(ValueList<GistKey> Keys) : PgIndex
    {
        /// <summary>The keys, in their order. At least one.</summary>
        public ValueList<GistKey> Keys
        {
            get;
            init => field = RequireKeys(value, nameof(Keys));
        } = RequireKeys(Keys, nameof(Keys));

        /// <inheritdoc cref="BTree.Include" />
        public ValueList<string> Include
        {
            get;
            init => field = RequirePaths(value, nameof(Include));
        } = [];

        /// <summary><c>fillfactor</c>, 10 to 100, or null for PostgreSQL's own.</summary>
        public int? FillFactor
        {
            get;
            init => field = Within(value, 10, 100, nameof(FillFactor));
        }

        /// <summary><c>buffering</c>, or null for PostgreSQL's own.</summary>
        public GistBuffering? Buffering { get; init; }
    }

    /// <summary>An SP-GiST index: one key, and columns it carries.</summary>
    /// <param name="Key">The key.</param>
    public sealed record SpGist(SpGistKey Key) : PgIndex
    {
        /// <summary>The key.</summary>
        public SpGistKey Key
        {
            get;
            init => field = value ?? throw new ArgumentNullException(nameof(value));
        } = Key ?? throw new ArgumentNullException(nameof(Key));

        /// <inheritdoc cref="BTree.Include" />
        public ValueList<string> Include
        {
            get;
            init => field = RequirePaths(value, nameof(Include));
        } = [];

        /// <summary><c>fillfactor</c>, 10 to 100, or null for PostgreSQL's own.</summary>
        public int? FillFactor
        {
            get;
            init => field = Within(value, 10, 100, nameof(FillFactor));
        }
    }

    /// <summary>A GIN index: several keys. It has no order, cannot be unique and carries no other column.</summary>
    /// <param name="Keys">The keys, in their order. At least one.</param>
    public sealed record Gin(ValueList<GinKey> Keys) : PgIndex
    {
        /// <summary>The keys, in their order. At least one.</summary>
        public ValueList<GinKey> Keys
        {
            get;
            init => field = RequireKeys(value, nameof(Keys));
        } = RequireKeys(Keys, nameof(Keys));

        /// <summary><c>fastupdate</c>, or null for PostgreSQL's own.</summary>
        public bool? FastUpdate { get; init; }

        /// <summary><c>gin_pending_list_limit</c> in kilobytes, 64 or more, or null for the server's setting.</summary>
        public int? PendingListLimit
        {
            get;
            init => field = Within(value, 64, int.MaxValue, nameof(PendingListLimit));
        }
    }

    /// <summary>A BRIN index: several keys. It has no order, cannot be unique and carries no other column.</summary>
    /// <param name="Keys">The keys, in their order. At least one.</param>
    public sealed record Brin(ValueList<BrinKey> Keys) : PgIndex
    {
        /// <summary>The keys, in their order. At least one.</summary>
        public ValueList<BrinKey> Keys
        {
            get;
            init => field = RequireKeys(value, nameof(Keys));
        } = RequireKeys(Keys, nameof(Keys));

        /// <summary><c>pages_per_range</c>, 1 to 131072, or null for PostgreSQL's own.</summary>
        public int? PagesPerRange
        {
            get;
            init => field = Within(value, 1, 131072, nameof(PagesPerRange));
        }

        /// <summary><c>autosummarize</c>, or null for PostgreSQL's own.</summary>
        public bool? Autosummarize { get; init; }
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

/// <summary>How a GiST index is built: PostgreSQL's <c>buffering</c> parameter.</summary>
public closed record GistBuffering
{
    private sealed record OnCase : GistBuffering;

    private sealed record OffCase : GistBuffering;

    private sealed record AutoCase : GistBuffering;

    /// <summary><c>buffering = on</c></summary>
    public static GistBuffering On { get; } = new OnCase();

    /// <summary><c>buffering = off</c></summary>
    public static GistBuffering Off { get; } = new OffCase();

    /// <summary><c>buffering = auto</c>, which is PostgreSQL's own.</summary>
    public static GistBuffering Auto { get; } = new AutoCase();

    /// <summary>How the case is stored, and written into the statement. In consumers' snapshots: a file format.</summary>
    internal string Token => this switch
    {
        OnCase   => "on",
        OffCase  => "off",
        AutoCase => "auto",
    };

    internal static GistBuffering Parse(string token) => token switch
    {
        "on"   => On,
        "off"  => Off,
        "auto" => Auto,
        _      => throw new InvalidOperationException(PgStrings.UnknownBuffering(token)),
    };

    /// <summary>The case's name, for messages.</summary>
    public sealed override string ToString() => Token;
}
