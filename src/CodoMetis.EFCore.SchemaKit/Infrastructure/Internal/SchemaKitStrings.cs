namespace CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

/// <summary>
/// SchemaKit's messages. Each names what is wrong, what would have happened without the refusal,
/// and what fixes it.
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

    // ---- declarations ---------------------------------------------------------------------------

    public static string DeclaredTwice(string entityType, string first, string second)
        => $"'{entityType}' declares the same schema object twice: {first} and {second}. One would replace the other " +
           "without an error and the database would hold one object where the model declares two. Give them different " +
           "names, or remove one.";

    public static string AlreadyDeclared(string entityType, string declaration)
        => $"'{entityType}' already declares {declaration}. Declare() never replaces a declaration: the first would be " +
           "gone without an error. Use Redeclare() to change the one that is there.";

    public static string RedeclaredOntoOneKey(string entityType, string first, string second)
        => $"Redeclare() on '{entityType}' would turn two declarations into one: {first} and {second}. Nothing was changed.";

    public static string AmendReturnedNull(string entityType)
        => $"The function passed to Redeclare() on '{entityType}' returned null. Return the declaration itself to leave it as it is.";

    public static string NameIsAmbiguous(string entityType, string owner, string name)
        => $"'{entityType}' has more than one {owner} declaration named '{name}'. Read them with GetDeclarations() and pick by kind.";

    public static string InvalidOwner(Type type, string? owner)
        => $"'{type.FullName}' has the owner '{owner}'. An owner is the middle part of an annotation key, " +
           "CodoMetis:{Owner}:{hash}: it is not empty and holds no colon and no white space.";

    public static string DeclarationWithoutKind(Type type)
        => $"A declaration of type '{type.FullName}' has no kind. The kind names the case of the union in the model snapshot.";

    public static string InvalidKind(Type type, string kind)
        => $"A declaration of type '{type.FullName}' has the kind '{kind}'. A kind holds no white space and no control " +
           "character: a declaration's key is over its kind and its name, with a line break between the two.";

    public static string EmptyDeclarationName(string owner, string kind)
        => $"A {owner} {kind} declaration has an empty name. Leave the name null for a default name.";

    public static string InvalidEpoch(string owner, string kind, int epoch)
        => $"A {owner} {kind} declaration has the resolution epoch {epoch}. Epochs start at 1.";

    public static string ReservedPropertyName(string name)
        => $"A declaration wrote a property named '{name}'. '$kind', 'v', 'r' and 'name' are written by SchemaKit.";

    public static string InvalidPropertyName(string name)
        => $"A declaration wrote a property named '{name}'. A property name is made of ASCII letters, digits and '_': " +
           "those are written the same by every runtime, and the declaration's key is a hash over what is written.";

    public static string TextIsNotUnicode(string property, int index)
        => $"The text written for the property '{property}' is not well-formed UTF-16: the character at index {index} is half of a " +
           "surrogate pair. It cannot be stored as it is, and replacing it would store another declaration.";

    public static string PropertyWrittenTwice(string name)
        => $"A declaration wrote the property '{name}' twice in one object. A reader would see one of the two values.";

    public static string DeclarationNotJson(string key)
        => $"The annotation '{key}' is not a SchemaKit declaration: its value is not a JSON object. The model snapshot " +
           "was edited, or written by something else.";

    /// <param name="key">The annotation.</param>
    /// <param name="found">The version as it is stored, or "none".</param>
    /// <param name="version">The version when it is a whole number.</param>
    /// <param name="known">The version this package reads.</param>
    public static string UnknownFormatVersion(string key, string found, int? version, int known)
        => $"The annotation '{key}' has the format version {found}; this version of SchemaKit reads {known}. " + version switch
        {
            null => "A format version is a whole number: the snapshot was edited by hand or is damaged.",
            var higher when higher > known => "The snapshot was written by a newer SchemaKit: reading it as the older format would resolve it wrongly. Update the package.",
            _ => "That is an older format, which this version does not read: reading it as the newer one would resolve it wrongly.",
        };

    public static string DeclarationPropertyTwice(string path, string name)
        => $"The declaration '{path}' has the property '{name}' twice. SchemaKit writes a property once, and taking " +
           "either value would be a guess: the model snapshot was edited by hand or is damaged.";

    public static string DeclarationPropertyMissing(string path, string name)
        => $"The declaration '{path}' has no '{name}'. SchemaKit does not assume a value for a property that was not " +
           "written; the snapshot was edited, or written by another version of the package that owns the declaration.";

    public static string DeclarationPropertyMalformed(string path, string name, string expected)
        => $"The declaration '{path}' has a '{name}' that is not {expected}.";

    public static string DeclarationPropertyNotRead(string path, string name)
        => $"The declaration '{path}' has a property '{name}' that the package owning it does not read. It was written " +
           "by a newer version of that package; dropping it would change the object without an error. Update the package.";

    public static string ReadReturnedNull(Type declaration, string key)
        => $"{declaration.Name}.Read returned null for the annotation '{key}'.";

    public static string ReadLostTheHeader(Type declaration, string key)
        => $"{declaration.Name}.Read built a declaration for the annotation '{key}' whose kind, name or epoch is not the " +
           "one that was stored. Stored again, it would be another declaration.";

    public static string ReadChangedTheDeclaration(Type declaration, string key, string stored, string rewritten)
        => $"{declaration.Name}.Read built a declaration for the annotation '{key}' that is not the one that was stored. " +
           $"Stored: {stored}. What it read writes: {rewritten}. Read must keep every property it takes from the reader.";

    // ---- resolution ------------------------------------------------------------------------------

    public static string NotAPropertyPath(string entity, string path)
        => $"'{path}' on {entity} is not a property path. A path is property names separated by dots, such as 'Address.City'.";

    public static string PathNotFound(string entity, string path, string segment, string type)
        => $"The path '{path}' on {entity} cannot be resolved: '{type}' has no property '{segment}'. A path goes through " +
           "complex properties to a mapped property; a navigation is another table and cannot be part of one.";

    public static string PathEndsOnComplexProperty(string entity, string path)
        => $"The path '{path}' on {entity} ends on a complex property, which is several columns. Name one of its properties.";

    public static string PathPastScalar(string entity, string path, string property, string member)
        => $"The path '{path}' on {entity} goes past the property '{property}' to '{member}'. That resolves to the property's " +
           $"column only when '{property}' has a value converter that stores the type of '{member}': otherwise the object " +
           "would be built on the whole column while the declaration names a part of it.";

    public static string PathIntoJson(string entity, string path, string segment)
        => $"The path '{path}' on {entity} goes into '{segment}', which is stored as a JSON document. A member of a document " +
           "has to be written as the provider's queries write it, which this version of SchemaKit does not do yet.";

    public static string PathHasNoColumn(string entity, string path, TableRef table)
        => $"The path '{path}' on {entity} has no column in the table '{table}'.";

    public static string DeclaredOnUnmappedType(string entity, string declaration)
        => $"{entity} declares {declaration}, but is not mapped to a table. The declaration would produce nothing, without an error.";

    public static string UnknownEpoch(string entity, string declaration, int epoch, int known)
        => $"{declaration} on {entity} was written under the resolution epoch {epoch}; this version of SchemaKit resolves " +
           $"epoch {known}. Resolving it by other rules could describe another object than the database holds. Update the package.";

    // ---- contributors ---------------------------------------------------------------------------

    public static string DescriptorIsNotARecord(Type contributor, Type descriptor)
        => $"'{contributor.FullName}' has the descriptor '{descriptor.FullName}', which is not a record. Descriptors are " +
           "compared by value: with reference equality every migration would drop and create the object again.";

    public static string RoutedToTheWrongContributor(Type value, string owner, Type expected)
        => $"A '{value.FullName}' was handed to the {owner} contributor, which takes '{expected.FullName}'.";

    public static string ContributorNotDerived(Type contributor)
        => $"'{contributor.FullName}' implements ISchemaContributor without deriving from SchemaContributor<TDeclaration, " +
           "TDescriptor>. The base class checks a contributor's answers before SchemaKit acts on them; derive from it.";

    public static string ResolveReturnedNull(Type contributor)
        => $"'{contributor.FullName}'.Resolve returned null. A declaration resolves to a descriptor, or Resolve throws.";

    public static string ClaimsReturnedNull(Type contributor)
        => $"'{contributor.FullName}'.Claims returned null. Answer the names the object occupies, or an empty list for none.";

    public static string PlanReturnedNull(Type contributor)
        => $"'{contributor.FullName}'.Plan returned null. Answer None, InPlace or Rebuild.";

    public static string ImpactReturnedNull(Type contributor)
        => $"'{contributor.FullName}'.ImpactOf returned null. Answer Unaffected, Follows, Lost or RebuildAround.";

    public static string InPlaceWithoutStatements(Type contributor, QualifiedName name)
        => $"'{contributor.FullName}'.Plan answered InPlace for '{name}' and wrote no statement, so the change would not " +
           "reach the database. Write the statements, or answer None or Rebuild.";

    public static string StatementsWithoutInPlace(Type contributor, QualifiedName name, string answer)
        => $"'{contributor.FullName}'.Plan answered {answer} for '{name}' and wrote statements, which that answer does " +
           "not run. Answer InPlace, or write none.";

    public static string ContributorWithoutPlacement(Type contributor)
        => $"'{contributor.FullName}' has no placement. Answer SchemaPlacement.BeforeTables or SchemaPlacement.AfterTables(order).";

    public static string TwoContributorsForOneOwner(string owner, Type first, Type second)
        => $"Two contributors have the owner '{owner}': '{first.FullName}' and '{second.FullName}'. A declaration is " +
           "rendered by the one contributor of its owner; with two, which one rendered would depend on the order of registration.";
}
