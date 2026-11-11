namespace CodoMetis.EFCore.SchemaKit;

/// <summary>The ids of the compiler diagnostics SchemaKit's attributes raise.</summary>
internal static class SchemaKitDiagnosticIds
{
    /// <summary>
    /// The contributor contract: the types a package uses to add a schema object. They are public so
    /// that such a package can be written, and experimental until the contract freezes at rc.1, so
    /// using them takes a deliberate <c>#pragma warning disable SK9001</c>.
    /// </summary>
    public const string ContributorContract = "SK9001";
}
