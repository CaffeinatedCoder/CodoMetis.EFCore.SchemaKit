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

    public static string UnknownKind(string owner, string kind)
        => $"{owner} has no kind '{kind}'. The declaration was written by a newer version of CodoMetis.EFCore.SchemaKit.PostgreSQL.";

    public static string UnknownDeferral(string token)
        => $"'{token}' is no deferral of an exclusion constraint. The declaration was written by a newer version of CodoMetis.EFCore.SchemaKit.PostgreSQL.";

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
