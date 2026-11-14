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

    public static string NoExtensions(string extension)
        => $"A declared object depends on the database extension '{extension}', and the dialect in use cannot install one. " +
           "A provider's SchemaKit package brings the dialect that can; without one, nothing declares an object that needs an extension.";

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

    public static string DeclaredOnDerivedTypeOfSharedTable(string entity, string declaration, string owner)
        => $"{entity} declares {declaration}, but shares its table with its base type {owner}. An object belongs to the table, " +
           $"not to the rows of {entity} in it: it would hold among the rows of every type in the table. Declare it on {owner}, " +
           "which says what it does.";

    public static string DeclaredOnTypeWithTablesBelow(string entity, string declaration, IReadOnlyList<string> derived)
        => $"{entity} declares {declaration}, but is mapped with TPC and its derived types have tables of their own " +
           $"({string.Join(", ", derived)}). An object belongs to one table: theirs would not get it, and their rows would not be " +
           "held to it, without an error. This version of SchemaKit does not carry a declaration to every table of a hierarchy " +
           "(https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit/issues/118). Declare it on each type that has no " +
           "derived types, under a name of its own.";

    public static string UnknownEpoch(string entity, string declaration, int epoch, int known)
        => $"{declaration} on {entity} was written under the resolution epoch {epoch}; this version of SchemaKit resolves " +
           $"epoch {known}. Resolving it by other rules could describe another object than the database holds. Update the package.";

    // ---- translation -----------------------------------------------------------------------------

    private const string WriteItAsSql = "Write that part of the declaration as SQL.";

    public static string LambdaTakesOneParameter(System.Linq.Expressions.LambdaExpression lambda)
        => $"The lambda '{lambda}' takes {lambda.Parameters.Count} parameters. A declaration's lambda takes one: the entity it is declared on.";

    public static string ExpressionNotTranslated(System.Linq.Expressions.Expression node)
        => $"The expression '{node}' has no translation in a declaration. Translated are property paths, ToLower, ToUpper, " +
           "Trim, TrimStart and TrimEnd without arguments, Substring, Replace, string.Length, string concatenation (+), " +
           $"null coalescing (??) and constants. {WriteItAsSql}";

    public static string PredicateNotTranslated(System.Linq.Expressions.Expression node)
        => $"The expression '{node}' has no translation in a declaration's predicate. Translated are comparisons (==, !=, <, " +
           "<=, >, >=; == null and != null become IS NULL and IS NOT NULL), &&, ||, !, boolean properties, and on the " +
           $"operands what an expression translates. {WriteItAsSql}";

    public static string EnumNotTranslated(System.Linq.Expressions.Expression node)
        => $"The expression '{node}' compares an enum. How an enum is stored depends on the property's value conversion, " +
           $"which is not known where the lambda is written: the comparison could be against a number on a text column. {WriteItAsSql}";

    public static string ConversionNotTranslated(System.Linq.Expressions.Expression node, Type from, Type to)
        => $"The expression '{node}' converts {TypeName(from)} to {TypeName(to)}. A declaration has no spelling for a conversion: " +
           "it would be left out, and the database would read the value as it is stored, so (int)x.Amount == 2 would hold " +
           "for 2 alone where the lambda holds for 2.5 too. Left out are only the conversions that keep every value on every " +
           "database: to and from Nullable<T>, to object or an interface, and from an integer type to a wider one. Compare " +
           "the property with a value of its own type (x.Amount == 2m, x.Ratio > 0.5f); where the conversion is what is " +
           $"meant: {WriteItAsSql}";

    private static string TypeName(Type type)
        => Nullable.GetUnderlyingType(type) is { } underlying ? underlying.Name + "?" : type.Name;

    public static string MemberOfScalarNotTranslated(System.Linq.Expressions.Expression member, string name, Type owner)
        => $"The expression '{member}' reads {name} of a {owner.Name}, which is not a property the model maps and has no " +
           $"translation. {WriteItAsSql}";

    public static string LiteralNotTranslated(Type type)
        => $"A value of type '{type.Name}' cannot be written as a SQL literal in a declaration: only strings, booleans and " +
           $"numbers have a spelling that does not depend on the database's settings. {WriteItAsSql}";

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

    public static string LegacyReaderNotDerived(Type reader)
        => $"'{reader.FullName}' does not derive from LegacyDeclarationReader<TDeclaration>. A reader of another format's " +
           "annotation derives from it.";

    public static string LegacyReaderReturnedNull(Type reader)
        => $"'{reader.FullName}'.Read returned null, or a list that holds one. An annotation reads into its declarations, or Read throws.";

    public static string LegacyAnnotationIsNotText(string entity, string annotation, Type? found)
        => $"{entity} carries the annotation '{annotation}' with a value of type {found?.Name ?? "null"}, and it is read as text. " +
           "Its objects would be left out of every migration, so it is not skipped.";

    public static string LegacyAnnotationInTheModel(string entity, string annotation)
        => $"{entity} carries the annotation '{annotation}' in the context's own model. It is the format of an earlier " +
           "package, which this version reads from a model snapshot only: nothing renders it from the model, and beside a " +
           "declaration of the same object it would be a second one. Remove the annotation and declare the object.";

    public static string UnreadLegacyAnnotation(string entity, string what, string annotation, string advice)
        => $"The model snapshot holds {what} of {entity} as EFCore.ComplexIndexes 5.x stored them ('{annotation}'), and nothing " +
           "registered with this context reads that. They are in the database: passed over, the next migration would create " +
           $"them a second time or never drop them, without an error. {advice}";

    public static string NoContributorForOwner(string entity, string owner)
        => $"{entity} carries a declaration of '{owner}', and no contributor with that owner is registered. The " +
           "declaration would be left out of every migration without an error. Add the package that declares it to " +
           "the context's options.";

    public static string RebuildBeforeTables(Type contributor, QualifiedName name)
        => $"'{contributor.FullName}'.Plan answered Rebuild for '{name}', and its placement is BeforeTables: tables are " +
           "made of its objects, so one cannot be dropped and created again around them. Answer InPlace, or change the object under another name.";

    public static string UnknownOperation(Type operation)
        => $"The migration holds an operation of type '{operation.FullName}', which SchemaKit does not know. What it does " +
           "to the schema objects this model declares cannot be told, and one of them could be dropped or left broken " +
           "without an error. Report the operation type to SchemaKit, or move it to a migration of its own.";

    public static string RebuildAroundBeforeTables(Type contributor, QualifiedName name)
        => $"'{contributor.FullName}'.ImpactOf answered RebuildAround for '{name}', and its placement is BeforeTables: " +
           "tables are made of its objects, so one cannot be dropped and created again around an operation on a table.";

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

    public static string UnreplayedLegacyOperation(string operation, string annotation, string rendered, string issue)
        => $"The migration holds {operation} with the annotation '{annotation}' of EFCore.ComplexIndexes 5.x. Only that " +
           $"package's SQL generator read it, and wrote {rendered}. This version of SchemaKit cannot replay the " +
           "operation, and passed on as it is the migration would apply and build another object, or fail on a name " +
           "nobody wrote. Apply the migration with EFCore.ComplexIndexes 5.x, or replace the operation in the migration " +
           $"with migrationBuilder.Sql(…) of the statement 5.x wrote for it. {issue}";
}
