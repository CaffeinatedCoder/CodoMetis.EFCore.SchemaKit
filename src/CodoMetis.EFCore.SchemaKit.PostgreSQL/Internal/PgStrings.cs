namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>The package's messages, in one place.</summary>
internal static class PgStrings
{
    public const string ExclusionOfEqualityOnly =
        "An exclusion constraint needs an element that compares with something other than '='. One of equality elements only "
        + "is a unique constraint: declare that instead.";

    public const string ElementNeedsOneTarget =
        "A stored exclusion element has to hold exactly one of 'column' and 'sql'.";

    public const string OperatorNeedsCharacters = "An exclusion operator needs its characters, such as '&&'.";

    public const string UncheckedEquality =
        "WithUncheckedOperator takes any operator but '='. For an element compared by '=', call Equal.";

    public const string KeyNeedsOneTarget =
        "A stored index key has to hold exactly one of 'column' and 'sql'.";

    public const string IndexNeedsAKey = "An index needs at least one key, and no key of it is null.";

    public const string IncludeNeedsAPath = "An included column is a property's path, such as 'Name' or 'Address.City'.";

    public static string IncludeTakesAProperty(string lambda)
        => $"include: takes properties, and '{lambda}' is more than one: an index carries columns, not expressions. "
           + "An expression can be a key.";

    public const string CollationNeedsAName = "A collation needs its name, such as 'C' or 'en-US-x-icu'.";

    public const string OperatorClassNeedsAName = "An operator class needs its name, such as 'jsonb_path_ops'.";

    public static string OutOfRange(string parameter, int low, int high)
        => high == int.MaxValue
            ? $"PostgreSQL takes {parameter} from {low} on."
            : $"PostgreSQL takes {parameter} from {low} to {high}.";

    public static string UnknownBuffering(string token)
        => $"'{token}' is no buffering of a GiST index. The declaration was written by a newer version of CodoMetis.EFCore.SchemaKit.PostgreSQL.";

    public static string NameTooLong(string name, int length, int limit)
        => $"The name '{name}' is {length} bytes long and PostgreSQL keeps {limit}. It shortens a longer name without an error, " +
           "and a collation or an operator class of the shortened name would be taken in its place.";

    public const string NotInAnIdempotentScript =
        "An idempotent script holds every statement in a DO block, where PostgreSQL runs nothing that cannot run in a transaction " +
        "(25001): psql would go on after the error and record the migration without the object. Write the script without " +
        "--idempotent, or apply the migration with a bundle or with 'dotnet ef database update'.";

    public static string OperatorClassNotForType(string index, string opClass, string method, string path, string storeType)
        => $"The index '{index}' gives its key '{path}' the operator class '{opClass}' of {method}, which PostgreSQL has for other types than "
           + $"the column's, '{storeType}': the server would refuse the index when the migration is applied. Leave the class out for the "
           + "default one of the column's type, or name one that takes the type.";

    public static string UnknownNullOrder(string token)
        => $"'{token}' is no place for the nulls of a key. The declaration was written by a newer version of CodoMetis.EFCore.SchemaKit.PostgreSQL.";

    public static string UnknownKind(string owner, string kind)
        => $"{owner} has no kind '{kind}'. The declaration was written by a newer version of CodoMetis.EFCore.SchemaKit.PostgreSQL.";

    public static string UnknownDeferral(string token)
        => $"'{token}' is no deferral of an exclusion constraint. The declaration was written by a newer version of CodoMetis.EFCore.SchemaKit.PostgreSQL.";

    public static string MoveToTheDefaultSchemaNotWritten(string from, string defaultSchema)
        => $"Npgsql writes ALTER TABLE … SET SCHEMA only for a schema the model names, and none for a table that moves from '{from}' "
           + $"back to the default schema. Name the schema in the model, ToTable(\"…\", \"{defaultSchema}\"), and the move is written.";

    private const string Legacy = "EFCore.ComplexIndexes 5.x";

    public static string LegacyExclusionUnreadable(string entity, string why)
        => $"The model snapshot holds the exclusion constraints of {entity} as {Legacy} stored them, and they cannot be read: {why}. "
           + "They are in the database, so they are not left out. If the snapshot was not edited by hand, this is a defect of "
           + "CodoMetis.EFCore.SchemaKit.PostgreSQL: please report it with the annotation 'CustomExclusion:Constraints'.";

    public static string LegacyExclusionOfEqualityOnly(string entity, string constraint)
        => $"The model snapshot holds an exclusion constraint of {entity} ({constraint}) from {Legacy} whose elements are all "
           + "compared with '='. That is a unique constraint, and PgExclusion does not declare one. Before moving to SchemaKit, "
           + "replace it with a unique index in the 5.x project and apply that migration.";

    public static string LegacyExclusionMethod(string entity, string constraint, string method)
        => $"The model snapshot holds an exclusion constraint of {entity} ({constraint}) from {Legacy} that uses the access method "
           + $"'{method}'. PgExclusion declares GiST constraints, and SP-GiST constraints of one element. Before moving to "
           + "SchemaKit, remove the constraint in the 5.x project and apply that migration; afterwards keep it by hand-written SQL.";

    public static string LegacyExclusionOnTpcRootBeforeCopies(string entity)
        => $"The model snapshot holds exclusion constraints from {Legacy} on {entity}, a type of a TPC hierarchy with derived types, "
           + "and was scaffolded before 5.5.0: the constraints are in the table of that type alone. SchemaKit creates such a "
           + "constraint in every table of the hierarchy, as 5.x does from 5.5.0 on, and cannot say from this snapshot which tables "
           + "have it. Before moving to SchemaKit, update the project to EFCore.ComplexIndexes 5.6, add a migration, which creates "
           + "the constraints in the derived types' tables, and apply it.";
}
