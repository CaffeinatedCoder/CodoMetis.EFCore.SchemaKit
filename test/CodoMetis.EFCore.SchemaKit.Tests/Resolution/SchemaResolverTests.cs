using CodoMetis.EFCore.SchemaKit.Resolution.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodoMetis.EFCore.SchemaKit.Tests.Resolution;

public sealed record EmailAddress(string Value)
{
    /// <summary>A member the converter does not store: it has another type than the column.</summary>
    public int Length => Value.Length;
}

public sealed class Geo
{
    public double Lat { get; set; }
}

public sealed class Address
{
    public string City { get; set; } = "";
    public string Zip { get; set; } = "";
    public Geo Geo { get; set; } = new();
}

public sealed class Prefs
{
    public string Theme { get; set; } = "";
}

public sealed class Team
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
}

public sealed class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public EmailAddress Email { get; set; } = new("");
    public DateTime CreatedAt { get; set; }
    public Address Address { get; set; } = new();
    public Prefs Prefs { get; set; } = new();
    public Team? Team { get; set; }
}

public sealed class Report
{
    public string Title { get; set; } = "";
}

public sealed class ResolverContext(DbContextOptions<ResolverContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Person>(person =>
        {
            person.ToTable("people", "app");
            person.Property(p => p.Name).HasColumnName("full_name");
            person.Property(p => p.Email).HasConversion(e => e.Value, v => new EmailAddress(v));
            person.ComplexProperty(p => p.Address, address =>
            {
                address.Property(a => a.City).HasColumnName("town");
                address.ComplexProperty(a => a.Geo);
            });
            person.ComplexProperty(p => p.Prefs, prefs => prefs.ToJson());
        });
        modelBuilder.Entity<Report>().HasNoKey().ToView("reports");
    }
}

/// <summary>
/// The resolver: a declaration's paths and templates as the SQL of one table. Every rule is asked
/// of the configured model and of its compiled snapshot, because a diff resolves both and an answer
/// that differs between them is a migration nobody asked for, or one that is missing.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SchemaResolverTests
{
    private static readonly TestGuard Declaration = new TestGuard.Trigger("Name");

    private sealed record Sides(ResolverContext Context, SchemaResolver Model, SchemaResolver Snapshot, IReadOnlyEntityType SnapshotEntity) : IDisposable
    {
        public IEnumerable<(string Side, SchemaResolver Resolver)> Both => [("model", Model), ("snapshot", Snapshot)];

        public ISchemaDialect Dialect => Model.Dialect;

        /// <summary>The name as the provider writes it, asked of the provider and not of SchemaKit.</summary>
        public string D(string name) => Context.GetService<ISqlGenerationHelper>().DelimitIdentifier(name);

        public Dependency Column(string name) => new Dependency.Column(Model.Table, Dialect.Identifier(name));

        public void Dispose() => Context.Dispose();
    }

    private static Sides Resolve(Provider provider)
    {
        var context = new ResolverContext(new DbContextOptionsBuilder<ResolverContext>().UseProvider(provider).UseSchemaKit().Options);
        var factory = context.GetService<SchemaResolverFactory>();
        var model = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Person))!;
        var snapshot = Snapshots.Of(context).Model.FindEntityType(typeof(Person).FullName!)!;
        return new Sides(context, factory.Create(model, Declaration), factory.Create(snapshot, Declaration), snapshot);
    }

    private static string Refused(Action action) => Assert.ThrowsExactly<InvalidOperationException>(action).Message;

    // ---- paths -------------------------------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_path_resolves_to_the_declared_propertys_column_on_the_model_and_on_its_snapshot(Provider provider)
    {
        using var sides = Resolve(provider);
        (string Path, string Column)[] paths =
        [
            ("Id", "Id"),
            ("Name", "full_name"),
            ("CreatedAt", "CreatedAt"),
            ("Email", "Email"),
            ("Email.Value", "Email"),
            ("Address.City", "town"),
            ("Address.Zip", "Address_Zip"),
            ("Address.Geo.Lat", "Address_Geo_Lat"),
        ];

        Assert.AreEqual("app.people", sides.Model.Table.ToString());
        Assert.AreEqual(sides.Model.Table, sides.Snapshot.Table);

        foreach (var (path, column) in paths)
        {
            foreach (var (side, resolver) in sides.Both)
            {
                var fragment = resolver.Column(path);

                Assert.AreEqual(sides.D(column), fragment.ToString(), $"{path} on the {side}");
                CollectionAssert.AreEqual(new[] { sides.Column(column) }, fragment.Dependencies.ToList(), $"{path} on the {side}");
            }
        }
    }

    /// <summary>
    /// The premise of the property-bag rule, so that the test above cannot pass for another reason:
    /// the snapshot has no CLR type and no converter to ask.
    /// </summary>
    [TestMethod]
    public void A_snapshot_keeps_a_converted_property_as_an_indexer_property_of_the_stored_type()
    {
        using var sides = Resolve(Provider.Npgsql);
        var email = sides.SnapshotEntity.FindProperty("Email")!;

        Assert.IsTrue(sides.SnapshotEntity.IsPropertyBag);
        Assert.IsTrue(email.IsIndexerProperty());
        Assert.AreEqual(typeof(string), email.ClrType);
        Assert.IsNull(email.GetValueConverter());
    }

    /// <summary>
    /// One segment past a scalar property is the property's column only when a converter stores the
    /// member's type. <c>CreatedAt.Year</c> and <c>Name.Length</c> name a part of a column, and an
    /// object built on the whole column would not be what was declared.
    /// </summary>
    [TestMethod]
    [DataRow("CreatedAt.Year", "goes past the property 'CreatedAt' to 'Year'")]
    [DataRow("Name.Length", "goes past the property 'Name' to 'Length'")]
    [DataRow("Email.Missing", "goes past the property 'Email' to 'Missing'")]
    [DataRow("Email.Length", "goes past the property 'Email' to 'Length'")]
    [DataRow("Email.Value.Length", "goes past the property 'Email' to 'Value'")]
    public void A_member_of_a_scalar_property_is_refused_unless_a_converter_stores_it(string path, string message)
    {
        using var sides = Resolve(Provider.Npgsql);

        StringAssert.Contains(Refused(() => sides.Model.Column(path)), message);
        StringAssert.Contains(Refused(() => sides.Model.Template($"{{{path}}} > 0")), message);
    }

    [TestMethod]
    [DataRow("Nope", "'Person' has no property 'Nope'")]
    [DataRow("Address.Nope", "has no property 'Nope'")]
    [DataRow("Team.Title", "'Person' has no property 'Team'")]
    [DataRow("Address", "ends on a complex property")]
    [DataRow("Address.Geo", "ends on a complex property")]
    [DataRow("Prefs.Theme", "goes into 'Prefs', which is stored as a JSON document")]
    public void A_path_that_names_no_column_is_refused_on_both_sides(string path, string message)
    {
        using var sides = Resolve(Provider.Npgsql);

        foreach (var (side, resolver) in sides.Both)
        {
            StringAssert.Contains(Refused(() => resolver.Column(path)), message, side);
            StringAssert.Contains(Refused(() => resolver.Column(path)), $"'{path}' on Person", side);
            StringAssert.Contains(Refused(() => resolver.Template($"{{{path}}} IS NULL")), message, side);
        }
    }

    [TestMethod]
    [DataRow("a b"), DataRow("Name."), DataRow(".Name"), DataRow("Address..City"), DataRow("1Name"), DataRow("Name;--")]
    public void Text_that_is_not_a_property_path_is_not_looked_up(string path)
    {
        using var sides = Resolve(Provider.Sqlite);

        StringAssert.Contains(Refused(() => sides.Model.Column(path)), "is not a property path");
    }

    [TestMethod]
    public void A_property_with_no_column_in_the_table_is_refused()
    {
        using var sides = Resolve(Provider.Npgsql);
        var factory = sides.Context.GetService<SchemaResolverFactory>();
        var entity = sides.Context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Person))!;

        var elsewhere = factory.Create(entity, StoreObjectIdentifier.Table("archive", "app"), Declaration);

        StringAssert.Contains(Refused(() => elsewhere.Column("Name")), "'Name' on Person has no column in the table 'app.archive'");
    }

    // ---- templates ---------------------------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_template_has_its_placeholders_replaced_outside_literals_and_nowhere_else(Provider provider)
    {
        using var sides = Resolve(provider);
        const string Template = "lower({Email.Value}) <> 'it''s {Name}' AND {Address.City} IS NOT NULL AND tags = '{{a,b}}' AND {Name} > {Email} {not a path} {} {Name";
        var expected = $"lower({sides.D("Email")}) <> 'it''s {{Name}}' AND {sides.D("town")} IS NOT NULL AND tags = '{{{{a,b}}}}' AND {sides.D("full_name")} > {sides.D("Email")} {{not a path}} {{}} {{Name";

        foreach (var (side, resolver) in sides.Both)
        {
            var fragment = resolver.Template(Template);

            Assert.AreEqual(expected, fragment.ToString(), side);
            CollectionAssert.AreEqual(
                new[] { sides.Column("Email"), sides.Column("town"), sides.Column("full_name") },
                fragment.Dependencies.ToList(),
                $"Each column once, in the order it is first read, on the {side}.");
        }
    }

    /// <summary>
    /// A column named outside a placeholder cannot be seen, and no scan of SQL can prove there is
    /// none. SQL without a placeholder is therefore taken to read every column of its table.
    /// </summary>
    [TestMethod]
    public void A_template_without_a_placeholder_depends_on_the_whole_table()
    {
        using var sides = Resolve(Provider.Npgsql);

        foreach (var (side, resolver) in sides.Both)
        {
            var fragment = resolver.Template("deleted_at IS NULL AND note <> '{Name}'");

            Assert.AreEqual("deleted_at IS NULL AND note <> '{Name}'", fragment.ToString(), side);
            CollectionAssert.AreEqual(new Dependency[] { new Dependency.WholeTable(resolver.Table) }, fragment.Dependencies.ToList(), side);
        }
    }

    // ---- names -------------------------------------------------------------------------------------------

    [TestMethod, DataRow(Provider.Npgsql), DataRow(Provider.SqlServer), DataRow(Provider.Sqlite)]
    public void A_default_name_is_made_of_the_prefix_the_table_and_each_parts_token(Provider provider)
    {
        using var sides = Resolve(provider);

        foreach (var (side, resolver) in sides.Both)
        {
            SqlFragment[] parts =
            [
                resolver.Column("Name"),
                resolver.Column("Address.City"),
                resolver.Template("lower({Email.Value}) || '-x'"),
                resolver.Template("<> ''"),
            ];

            // A column gives its name as it is, underscore included, and never its delimiters; a
            // template its letters and digits, with the column's name for a placeholder.
            Assert.AreEqual("EX_people_full_name_town_lowerEmailx_expr", resolver.Name(null, "EX", parts).Name, side);
            Assert.AreEqual("my name", resolver.Name("my name", "EX", parts).Name, side);
        }
    }

    [TestMethod]
    public void A_name_the_database_would_shorten_is_refused_declared_or_default()
    {
        using var sides = Resolve(Provider.Npgsql);
        var parts = Enumerable.Repeat(sides.Model.Column("Address.Geo.Lat"), 4).ToList();

        StringAssert.Contains(Refused(() => sides.Model.Name(new string('n', 64), "EX", [])), "is 64 long and this database keeps 63");
        StringAssert.Contains(Refused(() => sides.Model.Name(null, "EX", parts)), "'EX_people_Address_Geo_Lat_Address_Geo_Lat_Address_Geo_Lat_Address_Geo_Lat' is 73 long");
        Assert.AreEqual(63, sides.Model.Name(new string('n', 63), "EX", []).Name.Length);
    }

    // ---- what a fragment reads ---------------------------------------------------------------------------

    /// <summary>A dependency lost in composition is an operation of EF Core's that can break the object unnoticed.</summary>
    [TestMethod]
    public void A_fragment_composed_of_others_reads_what_they_read()
    {
        using var sides = Resolve(Provider.Npgsql);
        var resolver = sides.Model;
        var name = resolver.Column("Name");
        var filter = resolver.Template("{Address.City} = {Name}");

        var composed = resolver.Sql.Fragment($"({name}) WHERE {filter} AND {resolver.Sql.Literal("x")}");
        var list = resolver.Sql.List([name, filter, resolver.Column("Id")]);

        Assert.AreEqual("(full_name) WHERE town = full_name AND 'x'", composed.ToString());
        CollectionAssert.AreEqual(new[] { sides.Column("full_name"), sides.Column("town") }, composed.Dependencies.ToList());
        CollectionAssert.AreEqual(new[] { sides.Column("full_name"), sides.Column("town"), sides.Column("Id") }, list.Dependencies.ToList());
        Assert.AreEqual(0, resolver.Sql.Literal("x").Dependencies.Count);
    }

    /// <summary>A descriptor's equality is its diff identity, and that is the SQL: not where the SQL came from.</summary>
    [TestMethod]
    public void Fragments_compare_by_their_text_alone()
    {
        using var sides = Resolve(Provider.Npgsql);
        var resolver = sides.Model;
        var column = resolver.Column("Name");
        var composed = resolver.Sql.Fragment($"{resolver.Dialect.Identifier("full_name")}");

        Assert.AreEqual(column, composed);
        Assert.AreEqual(column.GetHashCode(), composed.GetHashCode());
        Assert.AreNotEqual(column.Dependencies.Count, composed.Dependencies.Count);
        Assert.AreNotEqual(column, resolver.Column("Id"));
    }

    // ---- under EF Core's renames ---------------------------------------------------------------------------

    private sealed class Renames(ISchemaDialect dialect) : ISchemaRenames
    {
        public TableRef Forward(TableRef table)
            => table.Name.Name == "people" ? new TableRef(dialect.Identifier("crm"), dialect.Identifier("persons")) : table;

        public Identifier Forward(TableRef table, Identifier column)
            => table.Name.Name == "persons" && column.Name == "full_name" ? dialect.Identifier("display_name") : column;
    }

    /// <summary>
    /// The source side of a diff, for an object that follows EF Core's renames: its SQL and what it
    /// reads take the names the target has, and its default name does not. So the object differs
    /// from the target's in the name alone, which is a rename of the object.
    /// </summary>
    [TestMethod]
    public void Under_renames_the_sql_takes_the_new_names_and_a_default_name_the_old_ones()
    {
        using var sides = Resolve(Provider.Npgsql);
        var dialect = sides.Dialect;
        var renamed = sides.Context.GetService<SchemaResolverFactory>().Create(sides.SnapshotEntity, Declaration, new Renames(dialect));
        var persons = new TableRef(dialect.Identifier("crm"), dialect.Identifier("persons"));

        var column = renamed.Column("Name");
        var template = renamed.Template("lower({Name}) <> {Address.City}");

        Assert.AreEqual(persons, renamed.Table);
        Assert.AreEqual("display_name", column.ToString());
        Assert.AreEqual("lower(display_name) <> town", template.ToString());
        CollectionAssert.AreEqual(new Dependency[] { new Dependency.Column(persons, dialect.Identifier("display_name")) }, column.Dependencies.ToList());
        CollectionAssert.AreEqual(
            new Dependency[] { new Dependency.Column(persons, dialect.Identifier("display_name")), new Dependency.Column(persons, dialect.Identifier("town")) },
            template.Dependencies.ToList());
        CollectionAssert.AreEqual(new Dependency[] { new Dependency.WholeTable(persons) }, renamed.Template("1 = 1").Dependencies.ToList());

        Assert.AreEqual("EX_people_full_name_lowerfullnametown", renamed.Name(null, "EX", [column, template]).Name);
        Assert.AreEqual(
            sides.Snapshot.Name(null, "EX", [sides.Snapshot.Column("Name"), sides.Snapshot.Template("lower({Name}) <> {Address.City}")]),
            renamed.Name(null, "EX", [column, template]),
            "The default name is the one the model has without the renames.");
    }

    // ---- the factory -------------------------------------------------------------------------------------

    /// <summary>
    /// An epoch this version does not know is how a later version resolves. Resolving it by today's
    /// rules would give the source side of a diff another answer than the version that wrote it.
    /// </summary>
    [TestMethod]
    [DataRow(2), DataRow(7)]
    public void A_declaration_of_an_epoch_this_version_does_not_know_is_refused(int epoch)
    {
        using var sides = Resolve(Provider.Sqlite);
        var factory = sides.Context.GetService<SchemaResolverFactory>();
        var declaration = new TestGuard.Trigger("Name") { Name = "tr_name", Epoch = epoch };

        var error = Refused(() => factory.Create(sides.SnapshotEntity, declaration));

        StringAssert.Contains(error, $"Test.Guard trigger 'tr_name' on Person was written under the resolution epoch {epoch}; this version of SchemaKit resolves epoch 1");
    }

    [TestMethod]
    public void A_declaration_on_a_type_that_is_mapped_to_no_table_is_refused()
    {
        using var sides = Resolve(Provider.Sqlite);
        var factory = sides.Context.GetService<SchemaResolverFactory>();
        var report = sides.Context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Report))!;

        StringAssert.Contains(Refused(() => factory.Create(report, Declaration)), "Report declares Test.Guard trigger, but is not mapped to a table");
    }
}
