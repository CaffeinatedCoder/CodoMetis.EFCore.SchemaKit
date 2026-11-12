namespace CodoMetis.EFCore.SchemaKit;

// What a consumer reads when a model is refused for what it declares. Like the read model these
// carry no experimental mark: they are caught and read, not implemented.

/// <summary>One finding about what a model declares, with an id that stays the same across versions.</summary>
public closed record SchemaDiagnostic
{
    /// <summary>The finding's id, such as <c>SK1001</c>.</summary>
    public string Id => this switch
    {
        NameCollision => "SK1001",
    };

    /// <summary>The finding in words, starting with its id.</summary>
    public string Message => this switch
    {
        NameCollision collision =>
            $"{collision.Id}: {collision.First} and {collision.Second} both claim the name '{collision.Name}' among {collision.Namespace}. " +
            "The database keeps a name once there: give one of them another name.",
    };

    /// <summary>
    /// Two objects of the model take one name where the database keeps a name once. At least one of
    /// them is declared with SchemaKit; the other may be EF Core's own, such as a primary key.
    /// </summary>
    /// <param name="Name">The name both take.</param>
    /// <param name="Namespace">Where the name is taken: <c>the relations of schema 'public'</c>, <c>the constraints of table 'bookings'</c>.</param>
    /// <param name="First">The first object, as the model declares it.</param>
    /// <param name="Second">The second.</param>
    public sealed record NameCollision(string Name, string Namespace, string First, string Second) : SchemaDiagnostic;

    /// <inheritdoc />
    public sealed override string ToString() => Message;
}

/// <summary>
/// The model a migration leads to declares something the database would refuse. Every finding of
/// the model is here at once, so they are fixed in one pass.
/// </summary>
public sealed class SchemaValidationException : InvalidOperationException
{
    internal SchemaValidationException(IReadOnlyList<SchemaDiagnostic> diagnostics)
        : base(Text(diagnostics))
        => Diagnostics = diagnostics;

    /// <summary>The findings, in the order of the model.</summary>
    public IReadOnlyList<SchemaDiagnostic> Diagnostics { get; }

    private static string Text(IReadOnlyList<SchemaDiagnostic> diagnostics)
        => diagnostics.Count == 1
            ? diagnostics[0].Message
            : $"The model declares {diagnostics.Count} things the database would refuse:{Environment.NewLine}"
              + string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.Message));
}
