namespace CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

/// <summary>
/// The messages of the wiring guards. Each names what is wrong, what would have happened without
/// the refusal, and the call that fixes it.
/// </summary>
internal static class SchemaKitStrings
{
    public static string NotWired(string declaring)
        => $"{declaring} declares a SchemaKit object, but UseSchemaKit() is not on this context's options. " +
           "Without it the declaration would be left out of migrations and of EnsureCreated() without an error. " +
           "Add UseSchemaKit() where the context's options are built, including in an IDesignTimeDbContextFactory.";

    public static string DisplacedByReplaceService(Type service, Type replacement)
        => $"ReplaceService<{service.Name}, {replacement.Name}>() was called after UseSchemaKit() and displaced it: " +
           "EF Core keeps one replacement per service. SchemaKit declarations would be left out of migrations without an error. " +
           "Call UseSchemaKit() last; it keeps an earlier replacement as its inner service.";

    public static string NotOutermost(Type service, Type actual)
        => $"{service.Name} is '{actual.FullName}', not SchemaKit's. SchemaKit declarations would be left out of " +
           "migrations without an error. Register the other package before SchemaKit, which keeps it as its inner " +
           "service: before UseSchemaKit() on the options, or before AddEntityFrameworkSchemaKit() in a service " +
           "collection passed to UseInternalServiceProvider().";

    public const string InternalServiceProviderWithoutServices =
        "UseSchemaKit() is on the options, but the service provider passed to UseInternalServiceProvider() has no " +
        "SchemaKit services. Call AddEntityFrameworkSchemaKit() on its collection, after the provider's AddEntityFramework…().";

    public static string NoInnerRegistration(Type service)
        => $"No {service.Name} is registered for SchemaKit to wrap. Call UseSchemaKit() on options that configure a " +
           "relational provider, or AddEntityFrameworkSchemaKit() after the provider's AddEntityFramework…().";

    public static string IdentifierTooLong(string name, int length, int limit)
        => $"The name '{name}' is {length} long and this database keeps {limit}. A longer name is refused or, on " +
           "PostgreSQL, shortened without an error, so that two names can become one. Give the object a shorter name.";

    public static string NotAnOperator(string token)
        => $"'{token}' is not an operator: an operator is one to 63 of + - * / < > = ~ ! @ # % ^ & | ` ? and contains no comment start.";

    public const string NoStringMapping = "The provider maps no string type, so SchemaKit cannot write a string literal.";

    public const string EmptyStatement = "A statement with no text was added.";

    public static string StatementHasTerminator(string statement, string terminator)
        => $"The statement already ends with '{terminator}': {statement} SchemaKit appends the provider's statement " +
           "terminator to every statement; remove it from the text.";

    public static string DropIfExists(string statement)
        => $"The statement drops with IF EXISTS: {statement} SchemaKit never does: a drop that cannot fail hides an " +
           "object that is already gone, which means the database and the model snapshot disagree. Drop it by name.";

    public static string LegacyPackageReferenced(string assembly, string designTimeServices)
        => $"'{assembly}' still references the EFCore.ComplexIndexes packages: it carries a design-time services " +
           $"reference to '{designTimeServices}'. At design time that would put the 5.x differ in front of SchemaKit's, " +
           "and SchemaKit declarations would be left out of migrations without an error. Remove the " +
           "EFCore.ComplexIndexes, EFCore.ComplexIndexes.PostgreSQL and EFCore.ComplexIndexes.SqlServer package " +
           "references; SchemaKit reads their snapshots and migrations.";
}
