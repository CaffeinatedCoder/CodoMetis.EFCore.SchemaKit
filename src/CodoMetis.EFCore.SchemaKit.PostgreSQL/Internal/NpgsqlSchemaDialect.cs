using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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

    /// <summary>
    /// The model annotation that says the extensions are installed some other way. 5.x's key, so
    /// that a model which set it there still says it here.
    /// </summary>
    public const string SuppressExtensions = "CustomTemporal:SuppressAutoExtension";

    protected override int Measure(string name) => Encoding.UTF8.GetByteCount(name);

    public override void EnsureExtension(SqlName extension, IReadOnlyModel model, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sql);

        if (model.FindAnnotation(SuppressExtensions)?.Value is true) return;

        // Declared with HasPostgresExtension, it is Npgsql's to create, and Npgsql does.
        if (model.GetPostgresExtensions().Any(declared => string.Equals(declared.Name, extension.Name, StringComparison.Ordinal))) return;

        // IF NOT EXISTS, unlike everything SchemaKit creates: the extension is not the model's
        // object. Another migration, another context or the operator may have installed it, and
        // none of them would be wrong.
        sql.Statement($"CREATE EXTENSION IF NOT EXISTS {extension}");
    }

    // An index is a relation: its name is taken in the schema, among tables, sequences and views.
    // A primary key and a unique constraint are named within their table and are built on an index
    // of the same name, so they take both. A foreign key and a check constraint have no index.
    public override IEnumerable<NameClaim> Claims(SchemaModelObject kind, TableRef table, Identifier name)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(name);

        return kind switch
        {
            SchemaModelObject.Index => [new NameClaim.Relation(table.Schema, name)],
            SchemaModelObject.PrimaryKey or SchemaModelObject.UniqueConstraint =>
                [new NameClaim.Constraint(table, name), new NameClaim.Relation(table.Schema, name)],
            _ => base.Claims(kind, table, name),
        };
    }
}
