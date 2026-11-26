using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Operations;

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

    // A collation or an operator class is shortened like any identifier: 64 bytes become 63 with
    // a notice and no error, and the index takes the collation of the shortened name where
    // there is one (issue 210). Held to the server's own limit and not to the model's: a model
    // may state a shorter one for its own names, and PostgreSQL's names are not the model's.
    public override SqlName Name(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Measure(name) is var length && length > DefaultMaxIdentifierLength)
            throw new InvalidOperationException(PgStrings.NameTooLong(name, length, DefaultMaxIdentifierLength));

        return base.Name(name);
    }

    // A table without a schema, in a model without a default schema, is created where the
    // search_path's first schema is, which on a stock database is public. A search_path that
    // points elsewhere makes a database the model does not describe: its ToTable("x", "public")
    // and its ToTable("x") are then two tables, where the model says one schema (#100).
    public override Identifier? DefaultSchema => field ??= Identifier("public");

    // A member of a JSON document, written as Npgsql's queries write it (its VisitJsonScalar and
    // GenerateJsonPath), case for case and in their order: PostgreSQL uses an expression index
    // only for a query whose expression is the index's own. A value that is JSON itself, a
    // collection or a nested document, is taken out as jsonb; a text is the text; bytea is
    // decoded; everything else is the text cast to the member's store type. Each row is compared
    // with ToQueryString() in PgJsonMemberTests.
    public override SqlFragment JsonMember(JsonMember member, SchemaSql sql)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(sql);

        if (member.Kind != JsonMemberKind.Scalar || member.Mapping?.StoreTypeNameBase is "jsonb" or "json")
            return Extraction(member, sql, asText: false);

        var text = Extraction(member, sql, asText: true);
        return member.Mapping switch
        {
            null or StringTypeMapping                  => text,
            { StoreTypeNameBase: "bytea" }             => sql.Fragment($"decode({text}, {Literal("base64")})"),
            _                                          => sql.Fragment($"CAST({text} AS {TypeName(member.StoreType)})"),
        };
    }

    // One step is -> or ->>; more are #> or #>> with the path as an array literal when every
    // name is ASCII letters and digits, and as ARRAY[...]::text[] otherwise.
    private SqlFragment Extraction(JsonMember member, SchemaSql sql, bool asText)
    {
        if (member.Path.Count == 1)
        {
            var name = Literal(member.Path[0]);
            return asText ? sql.Fragment($"{member.Container} ->> {name}") : sql.Fragment($"{member.Container} -> {name}");
        }

        var path = member.Path.All(name => name.All(char.IsAsciiLetterOrDigit))
            ? Literal("{" + string.Join(",", member.Path) + "}")
            : sql.Fragment($"ARRAY[{sql.List(member.Path.Select(Literal))}]::text[]");

        return asText ? sql.Fragment($"{member.Container} #>> {path}") : sql.Fragment($"{member.Container} #> {path}");
    }

    // Npgsql writes every statement of an idempotent script into a DO block, and PostgreSQL runs
    // nothing there that cannot run in a transaction (25001). psql goes on after the error and
    // the next block records the migration, so the database counts it as applied without the
    // object (#155, spike D). A plain script and the migrator's commands carry the statement.
    public override string? WhyNotOutsideTransaction(Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions options)
        => options.HasFlag(Microsoft.EntityFrameworkCore.Migrations.MigrationsSqlGenerationOptions.Idempotent)
            ? PgStrings.NotInAnIdempotentScript
            : null;

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

    private const string TsVectorConfig = "Npgsql:TsVectorConfig";

    private const string TsVectorProperties = "Npgsql:TsVectorProperties";

    // What Npgsql's generator does otherwise than the kernel says for EF Core Relational's
    // operations, and its own operation types (issue 116). Each rule is held against the generator
    // itself in NpgsqlOperationEffectTests: a patch of the provider that changes the SQL fails there.
    /// <inheritdoc />
    public override OperationEffect EffectOf(MigrationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        switch (operation)
        {
            // Npgsql computes a generated tsvector column from these two annotations, and writes a
            // change of either as DROP COLUMN and ADD COLUMN. PostgreSQL drops an index and an
            // exclusion constraint with a column without a word.
            case AlterColumnOperation alter
                when !Equals(alter[TsVectorConfig], alter.OldColumn[TsVectorConfig])
                     || !(alter[TsVectorProperties] as string[] ?? []).SequenceEqual(alter.OldColumn[TsVectorProperties] as string[] ?? [], StringComparer.Ordinal):
                return new OperationEffect.Changes([new BaseChange.ColumnDropped(TableOf(alter.Schema, alter.Table), Identifier(alter.Name))]);

            // The generator writes ALTER TABLE … SET SCHEMA only for a schema that is named. For a
            // table moved back to the default schema, which the operation says as no schema, it
            // writes nothing, and the table stays where it was (issue 105; measured on 11.0.0).
            // A table that was in the default schema under its name is where it is to be.
            case RenameTableOperation { Schema: { } schema, NewSchema: null } rename when !Identifier(schema).Equals(DefaultSchema):
                return new OperationEffect.NotWritten(
                    [TableOf(rename.Schema, rename.Name), TableOf(null, rename.NewName ?? rename.Name)],
                    PgStrings.MoveToTheDefaultSchemaNotWritten(schema, DefaultSchema!.Name));

            // Databases are no object a declaration is attached to.
            case NpgsqlCreateDatabaseOperation or NpgsqlDropDatabaseOperation:
                return new OperationEffect.Changes([]);

            default:
                return base.EffectOf(operation);
        }
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
