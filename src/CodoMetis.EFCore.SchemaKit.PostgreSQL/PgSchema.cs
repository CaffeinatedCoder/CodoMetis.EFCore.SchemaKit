using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// The factory for PostgreSQL declarations on <typeparamref name="TEntity" />: every method is a
/// function from typed lambdas and options to a value. It holds nothing; <c>DeclarePostgres</c>
/// hands one in, and <see cref="PgSchema.For{TEntity}" /> gives one outside a configuration.
/// </summary>
/// <remarks>
/// A lambda is translated where it is written, so one the translator has no SQL for fails in
/// <c>OnModelCreating</c>. Other packages add methods for their own types as extension members.
/// </remarks>
/// <typeparam name="TEntity">The entity type being configured. Every lambda is over it.</typeparam>
public readonly struct PgSchema<TEntity>
    where TEntity : class
{
    /// <summary>
    /// A key of an index, for where a key needs more than a lambda says: an order, or its place
    /// in a list of several.
    /// </summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgKey<TValue> Key<TValue>(Expression<Func<TEntity, TValue>> key)
        => new(KeyTarget.Of(key, PgSchema.Spelling));

    /// <summary>A B-tree index on one key.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="deduplicateItems"><c>deduplicate_items</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.BTree BTree<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool? deduplicateItems = null,
        bool concurrently = false,
        string? name = null)
        => BTree([Key(key)], where, include, fillFactor, deduplicateItems, concurrently, name);

    /// <summary>A B-tree index on several keys, or on keys with an order.</summary>
    /// <param name="keys">The keys in their order, as <c>[pg.Key(…), pg.Key(…).Desc()]</c>.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="deduplicateItems"><c>deduplicate_items</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.BTree BTree(
        ValueList<BTreeKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool? deduplicateItems = null,
        bool concurrently = false,
        string? name = null)
        => new(keys)
        {
            Where = Indexes.Condition(where),
            Include = Indexes.Paths(include),
            FillFactor = fillFactor,
            DeduplicateItems = deduplicateItems,
            Concurrently = concurrently,
            Name = name,
        };

    /// <summary>
    /// A unique index on one key: no two rows it covers have the same value. It is a B-tree
    /// index, since no other access method of PostgreSQL can be unique.
    /// </summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="nullsDistinct">
    /// Whether two rows with a null in the key are two keys, which is PostgreSQL's default. With
    /// false the second of them is refused (<c>NULLS NOT DISTINCT</c>).
    /// </param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.BTree Unique<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        bool nullsDistinct = true,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => Unique([Key(key)], where, include, nullsDistinct, fillFactor, concurrently, name);

    /// <summary>A unique index on several keys: no two rows it covers agree in all of them.</summary>
    /// <param name="keys">The keys in their order, as <c>[pg.Key(…), pg.Key(…)]</c>.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="nullsDistinct">
    /// Whether two rows with a null in a key are two keys, which is PostgreSQL's default. With
    /// false the second of them is refused (<c>NULLS NOT DISTINCT</c>).
    /// </param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.BTree Unique(
        ValueList<BTreeKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        bool nullsDistinct = true,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => BTree(keys, where, include, fillFactor, concurrently: concurrently, name: name) with { Unique = new Uniqueness(nullsDistinct) };

    /// <summary>A hash index: for equality on one key, and nothing else.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.Hash Hash<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => Hash(Key(key), where, fillFactor, concurrently, name);

    /// <summary>A hash index on a key with an operator class or a collation.</summary>
    /// <param name="key">The key, as <c>pg.Key(…)</c> makes it.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.Hash Hash(
        HashKey key,
        Expression<Func<TEntity, bool>>? where = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => new(key) { Where = Indexes.Condition(where), FillFactor = fillFactor, Concurrently = concurrently, Name = name };

    /// <summary>A GiST index on one key.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="buffering"><c>buffering</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.Gist Gist<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        GistBuffering? buffering = null,
        bool concurrently = false,
        string? name = null)
        => Gist([Key(key)], where, include, fillFactor, buffering, concurrently, name);

    /// <summary>A GiST index on several keys, or on keys with an operator class.</summary>
    /// <remarks>
    /// A key on a scalar column needs the <c>btree_gist</c> extension, as an element of an
    /// exclusion constraint does: the migration installs it, unless the key names an operator
    /// class, the model declares the extension or calls
    /// <see cref="PostgresDeclarationExtensions.SuppressBtreeGistInjection" />.
    /// </remarks>
    /// <param name="keys">The keys in their order, as <c>[pg.Key(…), pg.Key(…)]</c>.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="buffering"><c>buffering</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.Gist Gist(
        ValueList<GistKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        GistBuffering? buffering = null,
        bool concurrently = false,
        string? name = null)
        => new(keys)
        {
            Where = Indexes.Condition(where),
            Include = Indexes.Paths(include),
            FillFactor = fillFactor,
            Buffering = buffering,
            Concurrently = concurrently,
            Name = name,
        };

    /// <summary>An SP-GiST index, which has one key.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.SpGist SpGist<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => SpGist(Key(key), where, include, fillFactor, concurrently, name);

    /// <summary>An SP-GiST index on a key with an operator class.</summary>
    /// <param name="key">The key, as <c>pg.Key(…)</c> makes it.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="include">Properties whose columns the index carries without their being keys.</param>
    /// <param name="fillFactor"><c>fillfactor</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.SpGist SpGist(
        SpGistKey key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => new(key) { Where = Indexes.Condition(where), Include = Indexes.Paths(include), FillFactor = fillFactor, Concurrently = concurrently, Name = name };

    /// <summary>A GIN index on one key: an array, a <c>jsonb</c> document, a <c>tsvector</c>.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="ops">The operator class, such as <see cref="GinOpClass.JsonbPathOps" />; the default of the key's type when null.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="fastUpdate"><c>fastupdate</c>; PostgreSQL's own when null.</param>
    /// <param name="pendingListLimit"><c>gin_pending_list_limit</c> in kilobytes; the server's setting when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.Gin Gin<TValue>(
        Expression<Func<TEntity, TValue>> key,
        GinOpClass? ops = null,
        Expression<Func<TEntity, bool>>? where = null,
        bool? fastUpdate = null,
        int? pendingListLimit = null,
        bool concurrently = false,
        string? name = null)
        => Gin([ops is null ? Key(key) : Key(key).Ops(ops)], where, fastUpdate, pendingListLimit, concurrently, name);

    /// <summary>A GIN index on several keys.</summary>
    /// <param name="keys">The keys in their order, as <c>[pg.Key(…), pg.Key(…).Ops(…)]</c>.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="fastUpdate"><c>fastupdate</c>; PostgreSQL's own when null.</param>
    /// <param name="pendingListLimit"><c>gin_pending_list_limit</c> in kilobytes; the server's setting when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.Gin Gin(
        ValueList<GinKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        bool? fastUpdate = null,
        int? pendingListLimit = null,
        bool concurrently = false,
        string? name = null)
        => new(keys) { Where = Indexes.Condition(where), FastUpdate = fastUpdate, PendingListLimit = pendingListLimit, Concurrently = concurrently, Name = name };

    /// <summary>A BRIN index on one key: for a large table whose rows are stored in the key's order, more or less.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="pagesPerRange"><c>pages_per_range</c>; PostgreSQL's own when null.</param>
    /// <param name="autosummarize"><c>autosummarize</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    /// <typeparam name="TValue">The type of the key's values.</typeparam>
    public PgIndex.Brin Brin<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        int? pagesPerRange = null,
        bool? autosummarize = null,
        bool concurrently = false,
        string? name = null)
        => Brin([Key(key)], where, pagesPerRange, autosummarize, concurrently, name);

    /// <summary>A BRIN index on several keys.</summary>
    /// <param name="keys">The keys in their order, as <c>[pg.Key(…), pg.Key(…)]</c>.</param>
    /// <param name="where">The rows the index covers; all of them when null.</param>
    /// <param name="pagesPerRange"><c>pages_per_range</c>; PostgreSQL's own when null.</param>
    /// <param name="autosummarize"><c>autosummarize</c>; PostgreSQL's own when null.</param>
    /// <param name="concurrently">
    /// True to build the index without locking the table against writes
    /// (<c>CREATE INDEX CONCURRENTLY</c>), outside the migration's transaction.
    /// </param>
    /// <param name="name">The index's name; <c>IX_{table}_{keys}</c> when null.</param>
    public PgIndex.Brin Brin(
        ValueList<BrinKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        int? pagesPerRange = null,
        bool? autosummarize = null,
        bool concurrently = false,
        string? name = null)
        => new(keys) { Where = Indexes.Condition(where), PagesPerRange = pagesPerRange, Autosummarize = autosummarize, Concurrently = concurrently, Name = name };

    /// <summary>
    /// Starts the elements of an exclusion constraint with one compared by <c>=</c>. What it
    /// returns is not yet a constraint's elements: it becomes that with the first element compared
    /// by another operator.
    /// </summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <typeparam name="TValue">The operand's type.</typeparam>
    public ExclusionDraft<TEntity> Equal<TValue>(Expression<Func<TEntity, TValue>> key)
        => new([Exclusions.Element(key, ExclusionOperator.Equal)]);

    /// <summary>Starts the elements of an exclusion constraint with a range compared by <c>&amp;&amp;</c>.</summary>
    /// <param name="range">A range property.</param>
    /// <typeparam name="T">The range's element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>>> range)
        => new([Exclusions.Element(range, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, NpgsqlRange{T}}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>?>> range)
        => new([Exclusions.Element(range, ExclusionOperator.Overlaps)]);

    /// <summary>Starts the elements of an exclusion constraint with a multirange compared by <c>&amp;&amp;</c>.</summary>
    /// <remarks>
    /// A multirange is an array or a list of ranges on the CLR side, which Npgsql maps to a
    /// multirange column. A plain array does not compile here: GiST has no operator class for it,
    /// and PostgreSQL would refuse the constraint when the migration is applied (#104).
    /// </remarks>
    /// <param name="multirange">A multirange property.</param>
    /// <typeparam name="T">The ranges' element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>[]>> multirange)
        => new([Exclusions.Element(multirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, NpgsqlRange{T}[]}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<NpgsqlRange<T>>>> multirange)
        => new([Exclusions.Element(multirange, ExclusionOperator.Overlaps)]);

    /// <summary>
    /// An exclusion constraint backed by a GiST index: no two rows may satisfy every element's
    /// operator at once.
    /// </summary>
    /// <remarks>
    /// An element on a scalar column, such as one compared by <c>=</c>, needs the
    /// <c>btree_gist</c> extension. The migration that adds the constraint installs it
    /// (<c>CREATE EXTENSION IF NOT EXISTS</c>), unless the model declares it with
    /// <c>HasPostgresExtension("btree_gist")</c> or calls
    /// <see cref="PostgresDeclarationExtensions.SuppressBtreeGistInjection" />.
    /// </remarks>
    /// <param name="elements">The elements, as <c>pg.Equal(…).Overlaps(…)</c> builds them.</param>
    /// <param name="where">The rows the constraint covers; all of them when null.</param>
    /// <param name="deferral">When it is checked; at each statement when null.</param>
    /// <param name="name">The constraint's name; <c>EX_{table}_{elements}</c> when null.</param>
    public PgExclusion.Gist Exclusion(
        ExclusionElements<TEntity> elements,
        Expression<Func<TEntity, bool>>? where = null,
        Deferral? deferral = null,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return new PgExclusion.Gist(elements.Elements)
        {
            Where = where is null ? null : Predicate.Of(where, PgSchema.Spelling),
            Deferral = deferral ?? Deferral.NotDeferrable,
            Name = name,
        };
    }
}

/// <summary>The factory outside a configuration: for declarations kept in a field, shared, or tested on their own.</summary>
public static class PgSchema
{
    /// <summary>The factory for <typeparamref name="TEntity" />.</summary>
    /// <typeparam name="TEntity">The entity type the declarations are for.</typeparam>
    public static PgSchema<TEntity> For<TEntity>()
        where TEntity : class
        => default;

    /// <summary>
    /// A condition as a value, spelled for PostgreSQL where it is written: for one that several
    /// declarations share, or that a convention adds to each of them with <c>AndWhere</c>.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TEntity" /> may be an interface, so that one condition serves every
    /// entity type that implements it. Its paths are resolved against each entity type it is
    /// declared on. A comparison that can be NULL is written as EF Core writes it in a query, so
    /// the condition keeps the rows C# keeps.
    /// </remarks>
    /// <param name="condition">The condition.</param>
    /// <typeparam name="TEntity">The entity type, or an interface of it.</typeparam>
    /// <exception cref="NotSupportedException">The lambda holds something the translator has no SQL for.</exception>
    public static Predicate Where<TEntity>(Expression<Func<TEntity, bool>> condition)
        => Predicate.Of(condition, Spelling);

    /// <summary>
    /// How this package spells a lambda: as Npgsql's query translation does, character for
    /// character. For a package that adds PostgreSQL declarations of its own and takes lambdas
    /// for them; a consumer writes a lambda into a factory and never names a spelling.
    /// </summary>
    [Experimental("SK9001")]
    public static SqlSpelling Spelling => NpgsqlSpelling.Instance;
}

/// <summary>
/// The elements of an exclusion constraint while every one of them is compared by <c>=</c>. It
/// converts to nothing and no factory takes it: such a constraint would be a unique constraint.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class ExclusionDraft<TEntity>
    where TEntity : class
{
    internal ExclusionDraft(ValueList<ExclusionElement> elements) => Elements = elements;

    /// <summary>The elements so far, in the order they were written.</summary>
    public ValueList<ExclusionElement> Elements { get; }

    /// <inheritdoc cref="PgSchema{TEntity}.Equal" />
    public ExclusionDraft<TEntity> Equal<TValue>(Expression<Func<TEntity, TValue>> key)
        => new([.. Elements, Exclusions.Element(key, ExclusionOperator.Equal)]);

    /// <summary>Adds a range compared by <c>&amp;&amp;</c>, which makes these a constraint's elements.</summary>
    /// <param name="range">A range property.</param>
    /// <typeparam name="T">The range's element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>>> range)
        => new([.. Elements, Exclusions.Element(range, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, NpgsqlRange{T}}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>?>> range)
        => new([.. Elements, Exclusions.Element(range, ExclusionOperator.Overlaps)]);

    /// <summary>Adds a multirange compared by <c>&amp;&amp;</c>, which makes these a constraint's elements.</summary>
    /// <remarks>
    /// A multirange is an array or a list of ranges on the CLR side, which Npgsql maps to a
    /// multirange column. A plain array does not compile here: GiST has no operator class for it,
    /// and PostgreSQL would refuse the constraint when the migration is applied (#104).
    /// </remarks>
    /// <param name="multirange">A multirange property.</param>
    /// <typeparam name="T">The ranges' element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>[]>> multirange)
        => new([.. Elements, Exclusions.Element(multirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, NpgsqlRange{T}[]}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<NpgsqlRange<T>>>> multirange)
        => new([.. Elements, Exclusions.Element(multirange, ExclusionOperator.Overlaps)]);

    /// <summary>
    /// Adds an element compared by any operator. Unchecked: the compiler does not know whether the
    /// operand's type has the operator, and the database says so when the migration is applied.
    /// </summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <param name="operator">The operator's characters, such as <c>-|-</c>. Not <c>=</c>: that is <see cref="Equal" />.</param>
    /// <typeparam name="TValue">The operand's type.</typeparam>
    public ExclusionElements<TEntity> WithUncheckedOperator<TValue>(Expression<Func<TEntity, TValue>> key, string @operator)
        => new([.. Elements, Exclusions.Unchecked(key, @operator)]);
}

/// <summary>
/// The elements of an exclusion constraint, of which at least one is compared by something other
/// than <c>=</c>: what <see cref="PgSchema{TEntity}.Exclusion" /> takes.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class ExclusionElements<TEntity>
    where TEntity : class
{
    internal ExclusionElements(ValueList<ExclusionElement> elements) => Elements = elements;

    /// <summary>The elements, in the order they were written.</summary>
    public ValueList<ExclusionElement> Elements { get; }

    /// <summary>Adds an element compared by <c>=</c>.</summary>
    /// <param name="key">A property, or an expression over the entity.</param>
    /// <typeparam name="TValue">The operand's type.</typeparam>
    public ExclusionElements<TEntity> Equal<TValue>(Expression<Func<TEntity, TValue>> key)
        => new([.. Elements, Exclusions.Element(key, ExclusionOperator.Equal)]);

    /// <summary>Adds a range compared by <c>&amp;&amp;</c>.</summary>
    /// <param name="range">A range property.</param>
    /// <typeparam name="T">The range's element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>>> range)
        => new([.. Elements, Exclusions.Element(range, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, NpgsqlRange{T}}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>?>> range)
        => new([.. Elements, Exclusions.Element(range, ExclusionOperator.Overlaps)]);

    /// <summary>Adds a multirange compared by <c>&amp;&amp;</c>.</summary>
    /// <remarks>
    /// A multirange is an array or a list of ranges on the CLR side, which Npgsql maps to a
    /// multirange column. A plain array does not compile here: GiST has no operator class for it,
    /// and PostgreSQL would refuse the constraint when the migration is applied (#104).
    /// </remarks>
    /// <param name="multirange">A multirange property.</param>
    /// <typeparam name="T">The ranges' element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>[]>> multirange)
        => new([.. Elements, Exclusions.Element(multirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, NpgsqlRange{T}[]}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<NpgsqlRange<T>>>> multirange)
        => new([.. Elements, Exclusions.Element(multirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="ExclusionDraft{TEntity}.WithUncheckedOperator" />
    public ExclusionElements<TEntity> WithUncheckedOperator<TValue>(Expression<Func<TEntity, TValue>> key, string @operator)
        => new([.. Elements, Exclusions.Unchecked(key, @operator)]);
}

internal static class Indexes
{
    public static Predicate? Condition<TEntity>(Expression<Func<TEntity, bool>>? where)
        => where is null ? null : Predicate.Of(where, PgSchema.Spelling);

    /// <summary>
    /// The paths of the properties a list of lambdas names. A lambda over <c>object</c> boxes a
    /// value type, which the translator takes off as it does every conversion that keeps the
    /// value; anything else than a property is refused, since an index carries columns and no
    /// expressions.
    /// </summary>
    public static ValueList<string> Paths<TEntity>(IEnumerable<Expression<Func<TEntity, object?>>>? include)
    {
        if (include is null) return [];

        return [.. include.Select(lambda =>
        {
            ArgumentNullException.ThrowIfNull(lambda, nameof(include));
            return KeyTarget.Of(lambda, PgSchema.Spelling) is KeyTarget.Column column
                ? column.Path
                : throw new ArgumentException(Internal.PgStrings.IncludeTakesAProperty(lambda.ToString()), nameof(include));
        })];
    }
}

internal static class Exclusions
{
    public static ExclusionElement Element(LambdaExpression key, ExclusionOperator @operator)
        => new(KeyTarget.Of(key, PgSchema.Spelling), @operator);

    /// <summary>
    /// The unchecked form must still leave the stage honest: with <c>=</c> it would turn a draft of
    /// equality elements into a constraint's elements, which is what the stages exist to prevent.
    /// </summary>
    public static ExclusionElement Unchecked(LambdaExpression key, string @operator)
    {
        var op = new ExclusionOperator(@operator);
        return op != ExclusionOperator.Equal
            ? Element(key, op)
            : throw new ArgumentException(Internal.PgStrings.UncheckedEquality, nameof(@operator));
    }
}

/// <summary>Where PostgreSQL declarations meet EF Core's builders.</summary>
public static class PostgresDeclarationExtensions
{
    /// <summary>
    /// Says that <c>btree_gist</c> is installed some other way, so that no migration of this model
    /// writes <c>CREATE EXTENSION</c> for it: for a database whose extensions the migrating role
    /// may not create, or that keeps them in a schema of their own.
    /// </summary>
    /// <remarks>
    /// A constraint that needs the extension then fails to apply on a database that lacks it.
    /// </remarks>
    /// <param name="modelBuilder">The model's builder.</param>
    /// <returns>The same builder.</returns>
    public static ModelBuilder SuppressBtreeGistInjection(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Model.SetAnnotation(Internal.NpgsqlSchemaDialect.SuppressExtensions, true);
        return modelBuilder;
    }

    /// <summary>
    /// Declares PostgreSQL objects on the entity type: one call, a list of values.
    /// </summary>
    /// <remarks>
    /// Each declaration is stored as its own annotation. A declaration never replaces another: two
    /// with one name, or two without a name that say the same, are refused. The context's options
    /// need <c>UseNpgsql(…, o => o.UseSchemaKit())</c>; without it this call throws.
    /// </remarks>
    /// <param name="builder">The entity type's builder.</param>
    /// <param name="declare">Builds the declarations from the factory it is handed.</param>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The same builder.</returns>
    public static EntityTypeBuilder<TEntity> DeclarePostgres<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Func<PgSchema<TEntity>, IEnumerable<IPostgresObject>> declare)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(declare);
        builder.Metadata.Declare(declare(default));
        return builder;
    }
}
