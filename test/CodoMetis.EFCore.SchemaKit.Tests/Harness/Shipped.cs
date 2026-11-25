using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using CodoMetis.EFCore.SchemaKit.Translation.Internal;

namespace CodoMetis.EFCore.SchemaKit.Tests.Harness;

/// <summary>
/// The spelling the translator's tests translate with: the PostgreSQL package's, as it ships. The
/// kernel has none of its own, and a copy kept here would be what the tests hold instead.
/// </summary>
internal static class Shipped
{
    public static SqlSpelling Spelling => PgSchema.Spelling;

    /// <summary>Every member rendered from its target: the kernel's of <c>Nullable&lt;T&gt;</c>, and the spelling's.</summary>
    public static IEnumerable<(Type DeclaringType, string Name)> ValueMembers
        => Spelling.Members.Keys.Select(member => (member.DeclaringType!, member.Name))
                   .Concat(LambdaTranslator.NullableMembers.Keys.Select(name => (typeof(Nullable<>), name)));
}
