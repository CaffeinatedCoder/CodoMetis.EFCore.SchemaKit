using System.Text;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

/// <summary>
/// PostgreSQL's dialect. It differs from the kernel's in one rule: a name's length is counted in
/// bytes.
/// </summary>
/// <remarks>
/// PostgreSQL keeps 63 bytes of an identifier and shortens a longer one without an error. A name
/// of 40 two-byte characters is 40 characters and 80 bytes: counted in characters it passes, the
/// server creates the object under the first 31 of them, and the next diff looks for a name that
/// is not there.
/// </remarks>
internal sealed class NpgsqlSchemaDialect(SchemaDialectDependencies dependencies, int maxIdentifierLength)
    : SchemaDialect(dependencies, maxIdentifierLength > 0 ? maxIdentifierLength : DefaultMaxIdentifierLength)
{
    /// <summary>NAMEDATALEN - 1 of a stock server, for a model that states no limit.</summary>
    public const int DefaultMaxIdentifierLength = 63;

    protected override int Measure(string name) => Encoding.UTF8.GetByteCount(name);
}
