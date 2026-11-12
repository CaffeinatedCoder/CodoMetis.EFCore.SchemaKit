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
}
