using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit;

// The pipeline, one type per stage:
//
//   declaration (ISchemaObject: paths, typed options)        what the model says
//     → descriptor (ISchemaDescriptor: identifiers, SQL)      Resolve, through SchemaResolver
//     → statements (SchemaSql)                                Create / Drop / Plan
//     → MigrationOperation (SqlOperation)                     the kernel, never a contributor
//
// Create receives only the descriptor, which holds resolved identifiers and fragments, so a
// contributor cannot render a path the resolver did not see; and SchemaSql accepts only values that
// went through the dialect, so it cannot render a name the dialect did not see.

/// <summary>
/// A resolved declaration: what the database will hold, and what the differ compares. Two equal
/// descriptors are the same object; two that are equal but for the name are a rename.
/// </summary>
/// <remarks>
/// Implement it as a record whose members all compare by value (<see cref="ValueList{T}" /> for a
/// list, <see cref="Operational{T}" /> for what must not be compared). With reference equality the
/// differ would drop and create the object on every diff.
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public interface ISchemaDescriptor
{
    /// <summary>Where the object lives: in a table, in a schema, or in the database as a whole.</summary>
    SchemaScope Scope { get; }

    /// <summary>
    /// The object's name. The schema part is set for a kind the database names within a schema and
    /// null for one it names within its table.
    /// </summary>
    QualifiedName Name { get; }

    /// <summary>
    /// Everything the object depends on. EF Core's operations are checked against this list and
    /// <see cref="ISchemaContributor.ImpactOf" /> is asked about each match, so a dependency left
    /// out is an operation that can remove or break the object unnoticed.
    /// </summary>
    ValueList<Dependency> Dependencies { get; }

    /// <summary>The same object under another name, for telling a rename from a change.</summary>
    ISchemaDescriptor WithName(QualifiedName name);
}

/// <summary>
/// What adds one kind of schema object to migrations. The kernel holds a list of these and routes
/// each declaration by <see cref="ISchemaObject.Owner" />; it never looks into a contributor's types,
/// and a contributor never sees another's.
/// </summary>
/// <remarks>
/// Derive from <see cref="SchemaContributor{TDeclaration, TDescriptor}" />, which is this interface
/// typed over one declaration union and one descriptor. A class that implements the interface
/// itself is refused when it is registered: the base class is where an answer is checked before
/// the kernel acts on it. Every member is mandatory on purpose: an
/// optional hook is one a contributor compiled against an older contract does not have, and the
/// kernel would take its absence for "nothing to do".
/// </remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public interface ISchemaContributor
{
    /// <summary>
    /// The owner of the declarations this contributor renders: the middle part of their annotation
    /// keys, <c>CodoMetis:{Owner}:{hash}</c>. Permanent once a snapshot holds it.
    /// </summary>
    string Owner { get; }

    /// <summary>Where this contributor's operations go relative to EF Core's own.</summary>
    SchemaPlacement Placement { get; }

    /// <summary>Turns a declaration into what the database will hold, for one side of a diff.</summary>
    ISchemaDescriptor Resolve(ISchemaObject declaration, SchemaResolver resolver);

    /// <summary>Writes the statements that create the object.</summary>
    void Create(ISchemaDescriptor descriptor, SchemaSql sql);

    /// <summary>Writes the statements that drop the object. A drop names the object and never says <c>IF EXISTS</c>.</summary>
    void Drop(ISchemaDescriptor descriptor, SchemaSql sql);

    /// <summary>
    /// Decides what it takes to get from <paramref name="from" /> to <paramref name="to" />, two
    /// descriptors of the same object that are not equal. For <see cref="SchemaChange.InPlace" /> it
    /// writes the statements into <paramref name="sql" />; for the other answers it writes none.
    /// </summary>
    SchemaChange Plan(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql);

    /// <summary>
    /// What <paramref name="change" />, an operation of EF Core's on something the object depends
    /// on, does to the object in the database.
    /// </summary>
    Impact ImpactOf(ISchemaDescriptor descriptor, BaseChange change);

    /// <summary>The namespaces the object's name occupies.</summary>
    IEnumerable<NameClaim> Claims(ISchemaDescriptor descriptor);
}

/// <summary>
/// What <see cref="SchemaContributor{TDeclaration, TDescriptor}" /> is built from. A record, so a
/// service added to it later breaks no contributor's constructor.
/// </summary>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public sealed record SchemaContributorDependencies;

/// <summary>
/// The base a contributor derives from: typed over its own closed declaration union and its own
/// descriptor, so its switches are exhaustive and a new case is a compile error in each of them.
/// </summary>
/// <typeparam name="TDeclaration">The contributor's declaration union.</typeparam>
/// <typeparam name="TDescriptor">Its descriptor, a record: the differ compares descriptors by value.</typeparam>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public abstract class SchemaContributor<TDeclaration, TDescriptor> : ISchemaContributor
    where TDeclaration : class, ISchemaDeclaration<TDeclaration>
    where TDescriptor : class, ISchemaDescriptor
{
    /// <summary>
    /// Refuses a descriptor type that is not a record. C# cannot constrain a type argument to one,
    /// and a descriptor that compares by reference never equals its counterpart on the other side
    /// of a diff: every migration would drop and create the object.
    /// </summary>
    /// <param name="dependencies">The services a contributor is built from.</param>
    protected SchemaContributor(SchemaContributorDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        Dependencies = dependencies;

        if (typeof(TDescriptor).GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is null)
            throw new InvalidOperationException(SchemaKitStrings.DescriptorIsNotARecord(GetType(), typeof(TDescriptor)));
    }

    /// <summary>The services this contributor was built from.</summary>
    protected SchemaContributorDependencies Dependencies { get; }

    /// <inheritdoc />
    public string Owner => TDeclaration.OwnerId;

    /// <inheritdoc />
    public abstract SchemaPlacement Placement { get; }

    /// <inheritdoc cref="ISchemaContributor.Resolve" />
    protected abstract TDescriptor Resolve(TDeclaration declaration, SchemaResolver resolver);

    /// <inheritdoc cref="ISchemaContributor.Create" />
    protected abstract void Create(TDescriptor descriptor, SchemaSql sql);

    /// <inheritdoc cref="ISchemaContributor.Drop" />
    protected abstract void Drop(TDescriptor descriptor, SchemaSql sql);

    /// <inheritdoc cref="ISchemaContributor.Plan" />
    protected abstract SchemaChange Plan(TDescriptor from, TDescriptor to, SchemaSql sql);

    /// <inheritdoc cref="ISchemaContributor.ImpactOf" />
    protected abstract Impact ImpactOf(TDescriptor descriptor, BaseChange change);

    /// <inheritdoc cref="ISchemaContributor.Claims" />
    protected abstract IEnumerable<NameClaim> Claims(TDescriptor descriptor);

    ISchemaDescriptor ISchemaContributor.Resolve(ISchemaObject declaration, SchemaResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return Resolve(Own<TDeclaration>(declaration), resolver)
               ?? throw new InvalidOperationException(SchemaKitStrings.ResolveReturnedNull(GetType()));
    }

    void ISchemaContributor.Create(ISchemaDescriptor descriptor, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        Create(Own<TDescriptor>(descriptor), sql);
    }

    void ISchemaContributor.Drop(ISchemaDescriptor descriptor, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        Drop(Own<TDescriptor>(descriptor), sql);
    }

    SchemaChange ISchemaContributor.Plan(ISchemaDescriptor from, ISchemaDescriptor to, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // The answer and the statements have to agree, or a change is lost in one of two ways: an
        // InPlace with nothing written changes nothing, and statements beside a Rebuild or a None
        // would be thrown away by a kernel that believed the answer.
        var before = sql.Operations.Count;
        var change = Plan(Own<TDescriptor>(from), Own<TDescriptor>(to), sql)
                     ?? throw new InvalidOperationException(SchemaKitStrings.PlanReturnedNull(GetType()));
        var written = sql.Operations.Count - before;

        if (change is SchemaChange.InPlace && written == 0)
            throw new InvalidOperationException(SchemaKitStrings.InPlaceWithoutStatements(GetType(), to.Name));
        if (change is not SchemaChange.InPlace && written > 0)
            throw new InvalidOperationException(SchemaKitStrings.StatementsWithoutInPlace(GetType(), to.Name, change.GetType().Name));

        return change;
    }

    Impact ISchemaContributor.ImpactOf(ISchemaDescriptor descriptor, BaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ImpactOf(Own<TDescriptor>(descriptor), change)
               ?? throw new InvalidOperationException(SchemaKitStrings.ImpactReturnedNull(GetType()));
    }

    IEnumerable<NameClaim> ISchemaContributor.Claims(ISchemaDescriptor descriptor)
        => Claims(Own<TDescriptor>(descriptor))
           ?? throw new InvalidOperationException(SchemaKitStrings.ClaimsReturnedNull(GetType()));

    private T Own<T>(object? value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return value as T ?? throw new InvalidOperationException(SchemaKitStrings.RoutedToTheWrongContributor(value.GetType(), Owner, typeof(T)));
    }
}

/// <summary>
/// What a contributor's <c>Resolve</c> is handed: the table the declaration is on, the dialect, and
/// a statement builder for composing the descriptor's fragments. The kernel implements it, the same
/// way for both sides of a diff.
/// </summary>
/// <remarks>Turning paths into columns and SQL is added with the resolver itself (issue 43).</remarks>
[Experimental(SchemaKitDiagnosticIds.ContributorContract)]
public abstract class SchemaResolver
{
    // Only the kernel resolves: a resolver of someone else's could answer the two sides of a diff
    // differently, which is the one thing a resolver must never do.
    private protected SchemaResolver()
    {
    }

    /// <summary>The table the declaring entity type is mapped to.</summary>
    public abstract TableRef Table { get; }

    /// <summary>The dialect the descriptor's names and fragments are made with.</summary>
    public abstract ISchemaDialect Dialect { get; }

    /// <summary>For composing the descriptor's fragments. Statements written to it go nowhere.</summary>
    public SchemaSql Sql => field ??= new SchemaSql(Dialect);
}
