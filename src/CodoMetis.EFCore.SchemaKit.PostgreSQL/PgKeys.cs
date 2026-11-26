using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

// A key is typed by the access method it is for. What only one method has, an order or that
// method's operator classes, is on that method's key and nowhere else, so a key with a direction
// does not go into a GIN index and a GIN operator class does not reach a B-tree key: neither
// compiles. PgKey<TValue> is the key before that is decided, and converts to each.

internal static class Keys
{
    // A record's `with` goes through init and not through the constructor, so the rule is in both.
    public static KeyTarget Target(KeyTarget? target, string name) => target ?? throw new ArgumentNullException(name);

    public static string? Collation(string? collation)
        => collation is null || !string.IsNullOrWhiteSpace(collation) ? collation : throw new ArgumentException(PgStrings.CollationNeedsAName, nameof(collation));

    public static string ClassName(string name)
        => !string.IsNullOrWhiteSpace(name) ? name : throw new ArgumentException(PgStrings.OperatorClassNeedsAName, nameof(name));
}

/// <summary>A key of a B-tree index: what is indexed, and in which order.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record BTreeKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = Keys.Target(value, nameof(Target));
    } = Keys.Target(Target, nameof(Target));

    /// <summary>Whether the key is in descending order.</summary>
    public bool Descending { get; init; }

    /// <summary>Where nulls are put, or null for where the direction puts them.</summary>
    public NullOrder? Nulls
    {
        get;
        init => field = value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>The collation the key is compared by, or null for the column's own.</summary>
    public string? Collation
    {
        get;
        init => field = Keys.Collation(value);
    }

    /// <summary>The operator class, or null for the default one of the key's type.</summary>
    public BTreeOpClass? OpClass { get; init; }

    /// <summary>The same key in descending order.</summary>
    public BTreeKey Desc() => this with { Descending = true };

    /// <summary>The same key with nulls before every other value.</summary>
    public BTreeKey NullsFirst() => this with { Nulls = NullOrder.First };

    /// <summary>The same key with nulls after every other value.</summary>
    public BTreeKey NullsLast() => this with { Nulls = NullOrder.Last };

    /// <summary>The same key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public BTreeKey Ops(BTreeOpClass opClass) => this with { OpClass = opClass ?? throw new ArgumentNullException(nameof(opClass)) };
}

/// <summary>The key of a hash index.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record HashKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = Keys.Target(value, nameof(Target));
    } = Keys.Target(Target, nameof(Target));

    /// <inheritdoc cref="BTreeKey.Collation" />
    public string? Collation
    {
        get;
        init => field = Keys.Collation(value);
    }

    /// <inheritdoc cref="BTreeKey.OpClass" />
    public HashOpClass? OpClass { get; init; }
}

/// <summary>A key of a GiST index.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record GistKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = Keys.Target(value, nameof(Target));
    } = Keys.Target(Target, nameof(Target));

    /// <inheritdoc cref="BTreeKey.Collation" />
    public string? Collation
    {
        get;
        init => field = Keys.Collation(value);
    }

    /// <inheritdoc cref="BTreeKey.OpClass" />
    public GistOpClass? OpClass { get; init; }
}

/// <summary>The key of an SP-GiST index.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record SpGistKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = Keys.Target(value, nameof(Target));
    } = Keys.Target(Target, nameof(Target));

    /// <inheritdoc cref="BTreeKey.Collation" />
    public string? Collation
    {
        get;
        init => field = Keys.Collation(value);
    }

    /// <inheritdoc cref="BTreeKey.OpClass" />
    public SpGistOpClass? OpClass { get; init; }
}

/// <summary>A key of a GIN index.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record GinKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = Keys.Target(value, nameof(Target));
    } = Keys.Target(Target, nameof(Target));

    /// <inheritdoc cref="BTreeKey.Collation" />
    public string? Collation
    {
        get;
        init => field = Keys.Collation(value);
    }

    /// <inheritdoc cref="BTreeKey.OpClass" />
    public GinOpClass? OpClass { get; init; }
}

/// <summary>A key of a BRIN index.</summary>
/// <param name="Target">A column or an expression.</param>
public sealed record BrinKey(KeyTarget Target)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = Keys.Target(value, nameof(Target));
    } = Keys.Target(Target, nameof(Target));

    /// <inheritdoc cref="BTreeKey.Collation" />
    public string? Collation
    {
        get;
        init => field = Keys.Collation(value);
    }

    /// <inheritdoc cref="BTreeKey.OpClass" />
    public BrinOpClass? OpClass { get; init; }
}

/// <summary>
/// A key as <see cref="PgSchema{TEntity}.Key" /> makes it: what is indexed, with the type of its
/// values, before anything says which access method it is for. It converts to the key of each
/// method; an order makes it a B-tree key, and an operator class the key of that class's method.
/// </summary>
/// <typeparam name="TValue">The type of the key's values.</typeparam>
public sealed class PgKey<TValue>
{
    internal PgKey(KeyTarget target, string? collation = null)
    {
        Target = target;
        Collation = Keys.Collation(collation);
    }

    /// <summary>A column or an expression.</summary>
    public KeyTarget Target { get; }

    /// <summary>The collation the key is compared by, or null for the column's own.</summary>
    public string? Collation { get; }

    private BTreeKey BTree => new(Target) { Collation = Collation };

    /// <summary>A B-tree key in descending order. No other access method has an order.</summary>
    public BTreeKey Desc() => BTree.Desc();

    /// <summary>A B-tree key with nulls before every other value.</summary>
    public BTreeKey NullsFirst() => BTree.NullsFirst();

    /// <summary>A B-tree key with nulls after every other value.</summary>
    public BTreeKey NullsLast() => BTree.NullsLast();

    /// <summary>A B-tree key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public BTreeKey Ops(BTreeOpClass opClass) => BTree.Ops(opClass);

    /// <summary>A hash key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public HashKey Ops(HashOpClass opClass) => new(Target) { Collation = Collation, OpClass = opClass ?? throw new ArgumentNullException(nameof(opClass)) };

    /// <summary>A GiST key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public GistKey Ops(GistOpClass opClass) => new(Target) { Collation = Collation, OpClass = opClass ?? throw new ArgumentNullException(nameof(opClass)) };

    /// <summary>An SP-GiST key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public SpGistKey Ops(SpGistOpClass opClass) => new(Target) { Collation = Collation, OpClass = opClass ?? throw new ArgumentNullException(nameof(opClass)) };

    /// <summary>A GIN key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public GinKey Ops(GinOpClass opClass) => new(Target) { Collation = Collation, OpClass = opClass ?? throw new ArgumentNullException(nameof(opClass)) };

    /// <summary>A BRIN key under an operator class.</summary>
    /// <param name="opClass">The operator class.</param>
    public BrinKey Ops(BrinOpClass opClass) => new(Target) { Collation = Collation, OpClass = opClass ?? throw new ArgumentNullException(nameof(opClass)) };

    /// <summary>The key as a B-tree key, ascending.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator BTreeKey(PgKey<TValue> key) => Checked(key).BTree;

    /// <summary>The key as a hash key.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator HashKey(PgKey<TValue> key) => new(Checked(key).Target) { Collation = key.Collation };

    /// <summary>The key as a GiST key.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator GistKey(PgKey<TValue> key) => new(Checked(key).Target) { Collation = key.Collation };

    /// <summary>The key as an SP-GiST key.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator SpGistKey(PgKey<TValue> key) => new(Checked(key).Target) { Collation = key.Collation };

    /// <summary>The key as a GIN key.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator GinKey(PgKey<TValue> key) => new(Checked(key).Target) { Collation = key.Collation };

    /// <summary>The key as a BRIN key.</summary>
    /// <param name="key">The key.</param>
    public static implicit operator BrinKey(PgKey<TValue> key) => new(Checked(key).Target) { Collation = key.Collation };

    private static PgKey<TValue> Checked(PgKey<TValue> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key;
    }
}

/// <summary>What a key of text has and no other key: a collation.</summary>
public static class PgKeyExtensions
{
    /// <summary>
    /// The same key compared by a collation. Only a key of text has the method: PostgreSQL
    /// refuses <c>COLLATE</c> on a type that has no collations, such as an integer.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="collation">The collation's name, such as <c>C</c> or <c>en-US-x-icu</c>.</param>
    public static PgKey<string> Collate(this PgKey<string> key, string collation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(collation);
        return new PgKey<string>(key.Target, collation);
    }

    /// <inheritdoc cref="Collate(PgKey{string}, string)" />
    public static PgKey<char> Collate(this PgKey<char> key, string collation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(collation);
        return new PgKey<char>(key.Target, collation);
    }
}

// An operator class is typed by its access method. Each takes any name, for the classes an
// extension brings (pg_trgm's gin_trgm_ops); the ones named here are PostgreSQL's own.

/// <summary>An operator class of B-tree.</summary>
/// <param name="Name">The class's name.</param>
public sealed record BTreeOpClass(string Name)
{
    /// <summary>The class's name.</summary>
    public string Name
    {
        get;
        init => field = Keys.ClassName(value);
    } = Keys.ClassName(Name);

    /// <summary><c>text_pattern_ops</c>: text compared character by character, for <c>LIKE 'abc%'</c> under a collation that is not <c>C</c>.</summary>
    public static BTreeOpClass TextPatternOps { get; } = new("text_pattern_ops");

    /// <summary><c>varchar_pattern_ops</c>: the same for <c>character varying</c>.</summary>
    public static BTreeOpClass VarcharPatternOps { get; } = new("varchar_pattern_ops");

    /// <summary><c>bpchar_pattern_ops</c>: the same for <c>character(n)</c>, which is the one of the three that takes it.</summary>
    public static BTreeOpClass BpcharPatternOps { get; } = new("bpchar_pattern_ops");
}

/// <summary>An operator class of hash. PostgreSQL's own are the defaults of their types, so none is named here.</summary>
/// <param name="Name">The class's name.</param>
public sealed record HashOpClass(string Name)
{
    /// <summary>The class's name.</summary>
    public string Name
    {
        get;
        init => field = Keys.ClassName(value);
    } = Keys.ClassName(Name);
}

/// <summary>An operator class of GiST.</summary>
/// <param name="Name">The class's name.</param>
public sealed record GistOpClass(string Name)
{
    /// <summary>The class's name.</summary>
    public string Name
    {
        get;
        init => field = Keys.ClassName(value);
    } = Keys.ClassName(Name);

    /// <summary><c>range_ops</c>, the default for a range.</summary>
    public static GistOpClass RangeOps { get; } = new("range_ops");

    /// <summary><c>multirange_ops</c>, the default for a multirange.</summary>
    public static GistOpClass MultirangeOps { get; } = new("multirange_ops");

    /// <summary><c>inet_ops</c>, which <c>inet</c> and <c>cidr</c> have and do not use by default.</summary>
    public static GistOpClass InetOps { get; } = new("inet_ops");
}

/// <summary>An operator class of SP-GiST.</summary>
/// <param name="Name">The class's name.</param>
public sealed record SpGistOpClass(string Name)
{
    /// <summary>The class's name.</summary>
    public string Name
    {
        get;
        init => field = Keys.ClassName(value);
    } = Keys.ClassName(Name);

    /// <summary><c>range_ops</c>, the default for a range.</summary>
    public static SpGistOpClass RangeOps { get; } = new("range_ops");

    /// <summary><c>text_ops</c>, the default for text.</summary>
    public static SpGistOpClass TextOps { get; } = new("text_ops");

    /// <summary><c>inet_ops</c>, the default for <c>inet</c> and <c>cidr</c>.</summary>
    public static SpGistOpClass InetOps { get; } = new("inet_ops");
}

/// <summary>An operator class of GIN.</summary>
/// <param name="Name">The class's name.</param>
public sealed record GinOpClass(string Name)
{
    /// <summary>The class's name.</summary>
    public string Name
    {
        get;
        init => field = Keys.ClassName(value);
    } = Keys.ClassName(Name);

    /// <summary><c>jsonb_ops</c>, the default for <c>jsonb</c>.</summary>
    public static GinOpClass JsonbOps { get; } = new("jsonb_ops");

    /// <summary><c>jsonb_path_ops</c>: smaller and faster for containment (<c>@&gt;</c>), and for nothing else.</summary>
    public static GinOpClass JsonbPathOps { get; } = new("jsonb_path_ops");

    /// <summary><c>array_ops</c>, the default for an array.</summary>
    public static GinOpClass ArrayOps { get; } = new("array_ops");

    /// <summary><c>tsvector_ops</c>, the default for <c>tsvector</c>.</summary>
    public static GinOpClass TsVectorOps { get; } = new("tsvector_ops");
}

/// <summary>An operator class of BRIN. PostgreSQL has several per type (<c>int4_minmax_ops</c>, <c>int4_bloom_ops</c>), so none is named here.</summary>
/// <param name="Name">The class's name.</param>
public sealed record BrinOpClass(string Name)
{
    /// <summary>The class's name.</summary>
    public string Name
    {
        get;
        init => field = Keys.ClassName(value);
    } = Keys.ClassName(Name);
}
