using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.Migrations.Internal;

/// <summary>
/// The names a model's objects take, and the ones taken twice (design, section 9.8; #44).
/// </summary>
/// <remarks>
/// <para>
/// A name is taken in a namespace, and which one is the database's rule: a PostgreSQL index is a
/// relation of its schema, a constraint is named within its table, and an exclusion constraint
/// takes both, since its index is a relation. A contributor says it for its objects
/// (<c>Claims</c>), the dialect for EF Core's (<see cref="ISchemaDialect.Claims" />). 5.x compared
/// each kind with itself, and a constraint named like another kind's object replaced it or failed
/// at apply.
/// </para>
/// <para>
/// Two of EF Core's own objects that collide are EF Core's to report, and are not reported here.
/// </para>
/// </remarks>
internal static class NameClaims
{
    /// <summary>A name taken, by whom, and how that party's names compare.</summary>
    private sealed record Taken(NameClaim Claim, string By, object Party, bool Ours, SchemaNameComparison Comparison);

    /// <summary>Throws with every collision between the declared objects, and between one of them and one of EF Core's.</summary>
    public static void Require(
        IReadOnlyList<(ISchemaContributor Contributor, ISchemaDescriptor Descriptor, IReadOnlyEntityType EntityType, ISchemaObject Declaration)> declared,
        IRelationalModel model,
        ISchemaDialect dialect)
    {
        List<Taken> taken = [];
        foreach (var (contributor, descriptor, entityType, declaration) in declared)
        {
            var by = $"{declaration.Owner} {declaration.Kind} '{descriptor.Name}' on {entityType.ShortName()}";
            foreach (var claim in contributor.Claims(descriptor).Distinct())
                taken.Add(new Taken(claim, by, descriptor, Ours: true, declaration.NameComparison));
        }

        var ours = taken.Count;
        taken.AddRange(OfTheModel(model, dialect));

        List<SchemaDiagnostic> collisions = [];
        HashSet<(object, object)> reported = [];
        for (var i = 0; i < ours; i++)
        {
            for (var j = i + 1; j < taken.Count; j++)
            {
                var (first, second) = (taken[i], taken[j]);
                var comparison = first.Comparison == SchemaNameComparison.IgnoreCase || second.Comparison == SchemaNameComparison.IgnoreCase
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (!Same(first.Claim, second.Claim, comparison, dialect.DefaultSchema)) continue;

                // Two objects that collide in two namespaces, as two exclusion constraints of one
                // name do, are one finding.
                if (!reported.Add((first.Party, second.Party))) continue;

                collisions.Add(new SchemaDiagnostic.NameCollision(NameOf(first.Claim), Describe(first.Claim), first.By, second.By));
            }
        }

        if (collisions.Count > 0) throw new SchemaValidationException(collisions);
    }

    /// <summary>What EF Core's own objects take, each asked of the dialect.</summary>
    private static IEnumerable<Taken> OfTheModel(IRelationalModel model, ISchemaDialect dialect)
    {
        foreach (var table in model.Tables)
        {
            var reference = new TableRef(table.Schema is null ? null : new Identifier(table.Schema), new Identifier(table.Name));

            foreach (var taken in Of(SchemaModelObject.Table, reference, table.Name, $"the table '{reference}'", table)) yield return taken;

            foreach (var index in table.Indexes)
                foreach (var taken in Of(SchemaModelObject.Index, reference, index.Name, $"the index '{index.Name}' on '{reference}'", index)) yield return taken;

            foreach (var constraint in table.UniqueConstraints)
            {
                var (kind, what) = constraint.GetIsPrimaryKey()
                    ? (SchemaModelObject.PrimaryKey, "primary key")
                    : (SchemaModelObject.UniqueConstraint, "unique constraint");
                foreach (var taken in Of(kind, reference, constraint.Name, $"the {what} '{constraint.Name}' of '{reference}'", constraint)) yield return taken;
            }

            foreach (var foreignKey in table.ForeignKeyConstraints)
                foreach (var taken in Of(SchemaModelObject.ForeignKey, reference, foreignKey.Name, $"the foreign key '{foreignKey.Name}' of '{reference}'", foreignKey)) yield return taken;

            foreach (var check in table.CheckConstraints)
            {
                if (check.Name is not { } name) continue;
                foreach (var taken in Of(SchemaModelObject.CheckConstraint, reference, name, $"the check constraint '{name}' of '{reference}'", check)) yield return taken;
            }
        }

        foreach (var sequence in model.Sequences)
        {
            var reference = new TableRef(sequence.Schema is null ? null : new Identifier(sequence.Schema), new Identifier(sequence.Name));
            foreach (var taken in Of(SchemaModelObject.Sequence, reference, sequence.Name, $"the sequence '{reference}'", sequence)) yield return taken;
        }

        // The names are EF Core's and are in the model already: they are compared, not written, so
        // they are taken as they are and not measured against the identifier limit.
        IEnumerable<Taken> Of(SchemaModelObject kind, TableRef table, string name, string by, object party)
            => dialect.Claims(kind, table, new Identifier(name)).Select(claim => new Taken(claim, by, party, Ours: false, SchemaNameComparison.Ordinal));
    }

    // A table without a schema of its own is in the model's default schema, and the relational
    // model says so already. A model without one leaves the schema to the database, and the
    // dialect says which that is (#100): PostgreSQL's public. A dialect that does not know says
    // null, and then a table without a schema shares a namespace only with another without one.
    private static bool Same(NameClaim first, NameClaim second, StringComparison comparison, Identifier? defaultSchema)
    {
        return (first, second) switch
        {
            (NameClaim.Relation a, NameClaim.Relation b)     => SameSchema(a.Schema, b.Schema) && SameName(a.Name, b.Name),
            (NameClaim.TableIndex a, NameClaim.TableIndex b) => SameTable(a.Table, b.Table) && SameName(a.Name, b.Name),
            (NameClaim.Constraint a, NameClaim.Constraint b) => SameTable(a.Table, b.Table) && SameName(a.Name, b.Name),
            (NameClaim.Trigger a, NameClaim.Trigger b)       => SameTable(a.Table, b.Table) && SameName(a.Name, b.Name),

            // Two namespaces: an index of a table and a constraint of it may share a name where the database says so.
            _ => false,
        };

        bool SameName(Identifier a, Identifier b) => string.Equals(a.Name, b.Name, comparison);
        bool SameSchema(Identifier? a, Identifier? b) => string.Equals((a ?? defaultSchema)?.Name, (b ?? defaultSchema)?.Name, comparison);
        bool SameTable(TableRef a, TableRef b) => SameSchema(a.Schema, b.Schema) && SameName(a.Name, b.Name);
    }

    private static string NameOf(NameClaim claim) => claim switch
    {
        NameClaim.Relation relation => relation.Name.Name,
        NameClaim.TableIndex index  => index.Name.Name,
        NameClaim.Constraint named  => named.Name.Name,
        NameClaim.Trigger trigger   => trigger.Name.Name,
    };

    private static string Describe(NameClaim claim) => claim switch
    {
        NameClaim.Relation relation => relation.Schema?.Name is { } schema
            ? $"the relations of schema '{schema}' (tables, indexes, sequences, and the indexes behind constraints)"
            : "the relations of the default schema (tables, indexes, sequences, and the indexes behind constraints)",
        NameClaim.TableIndex index => $"the indexes of table '{index.Table}'",
        NameClaim.Constraint named => $"the constraints of table '{named.Table}'",
        NameClaim.Trigger trigger  => $"the triggers of table '{trigger.Table}'",
    };
}
