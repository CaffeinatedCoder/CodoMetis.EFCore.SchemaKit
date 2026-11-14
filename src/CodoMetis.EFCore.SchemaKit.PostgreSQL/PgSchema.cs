using System.Linq.Expressions;
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

    /// <summary>Starts the elements of an exclusion constraint with an array or a multirange compared by <c>&amp;&amp;</c>.</summary>
    /// <param name="arrayOrMultirange">An array or multirange property.</param>
    /// <typeparam name="T">The element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, T[]>> arrayOrMultirange)
        => new([Exclusions.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, T[]}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<T>>> arrayOrMultirange)
        => new([Exclusions.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

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
            Where = where is null ? null : Predicate.Of(where, SqlSpelling.PostgreSql),
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
        => Predicate.Of(condition, SqlSpelling.PostgreSql);
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

    /// <summary>Adds an array or a multirange compared by <c>&amp;&amp;</c>, which makes these a constraint's elements.</summary>
    /// <param name="arrayOrMultirange">An array or multirange property.</param>
    /// <typeparam name="T">The element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, T[]>> arrayOrMultirange)
        => new([.. Elements, Exclusions.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, T[]}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<T>>> arrayOrMultirange)
        => new([.. Elements, Exclusions.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

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

    /// <summary>Adds an array or a multirange compared by <c>&amp;&amp;</c>.</summary>
    /// <param name="arrayOrMultirange">An array or multirange property.</param>
    /// <typeparam name="T">The element type.</typeparam>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, T[]>> arrayOrMultirange)
        => new([.. Elements, Exclusions.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="Overlaps{T}(Expression{Func{TEntity, T[]}})" />
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<T>>> arrayOrMultirange)
        => new([.. Elements, Exclusions.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    /// <inheritdoc cref="ExclusionDraft{TEntity}.WithUncheckedOperator" />
    public ExclusionElements<TEntity> WithUncheckedOperator<TValue>(Expression<Func<TEntity, TValue>> key, string @operator)
        => new([.. Elements, Exclusions.Unchecked(key, @operator)]);
}

internal static class Exclusions
{
    public static ExclusionElement Element(LambdaExpression key, ExclusionOperator @operator)
        => new(KeyTarget.Of(key, SqlSpelling.PostgreSql), @operator);

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
