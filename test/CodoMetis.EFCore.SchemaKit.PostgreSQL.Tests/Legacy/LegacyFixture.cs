using System.Runtime.Loader;
using CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Harness;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

// The consumer's own namespace: its types are the same before and after the move, and the
// scaffolded files name its context.
namespace Legacy5x
{
    public sealed class Booking
    {
        public int Id { get; set; }
        public int RoomId { get; set; }
        public NpgsqlRange<DateOnly> During { get; set; }
    }

    public sealed class Shift
    {
        public int Id { get; set; }
        public int WorkerId { get; set; }
        public int SiteId { get; set; }
        public string Kind { get; set; } = "";
        public NpgsqlRange<DateOnly> During { get; set; }
        public DateOnly? CancelledOn { get; set; }
    }

    public sealed class Term
    {
        public NpgsqlRange<DateOnly> Span { get; set; }
    }

    public sealed class Lease
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public Term Term { get; set; } = new();
    }

    public sealed class Slot
    {
        public int Id { get; set; }
        public NpgsqlRange<DateOnly> During { get; set; }
    }

    public abstract class Vehicle
    {
        public int Id { get; set; }
        public NpgsqlRange<DateOnly> During { get; set; }
    }

    public sealed class Car : Vehicle;

    public sealed class Truck : Vehicle;

    /// <summary>What the scaffolded files' <c>[DbContext]</c> names. Nothing is made of it.</summary>
    public abstract class LegacyContext : DbContext;

    /// <summary>The same for the snapshot of the kinds nothing reads yet.</summary>
    public abstract class OtherKindsContext : DbContext;
}

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Legacy
{
    using Legacy5x;

    /// <summary>A context of the consumer's entity types alone, with SchemaKit wired.</summary>
    public sealed class Moved<TModel>(DbContextOptions<Moved<TModel>> options) : DbContext(options)
        where TModel : ITestModel
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => TModel.Build(modelBuilder);
    }

    /// <summary>
    /// The model of <c>test/Fixtures/ComplexIndexes5x/Generator/Model.cs</c> as it is written after
    /// the move, and what is varied about it.
    /// </summary>
    public static class MovedModels
    {
        private static void Tables(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Booking>();
            modelBuilder.Entity<Shift>(shift =>
            {
                shift.ToTable("shifts", "ops");
                shift.Property(x => x.WorkerId).HasColumnName("worker_id");
                shift.Property(x => x.SiteId).HasColumnName("site_id");
                shift.Property(x => x.Kind).HasColumnName("kind");
                shift.Property(x => x.During).HasColumnName("during");
                shift.Property(x => x.CancelledOn).HasColumnName("cancelled_on");
            });
            modelBuilder.Entity<Lease>().ComplexProperty(x => x.Term);
            modelBuilder.Entity<Slot>();
        }

        private static void Bookings(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Booking>().DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.During))]);

        private static void Leases(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Lease>().DeclarePostgres(pg =>
                [pg.Exclusion(pg.Equal(x => x.Code.ToLower()).Overlaps(x => x.Term.Span), deferral: Deferral.InitiallyImmediate)]);

        private static void Slots(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Slot>().DeclarePostgres(_ =>
                [new PgExclusion.SpGist(new ExclusionElement(new KeyTarget.Column("During"), ExclusionOperator.Overlaps))]);

        private static PgExclusion Active(PgSchema<Shift> pg) => pg.Exclusion(
            pg.Equal(x => x.WorkerId).Overlaps(x => x.During),
            where: x => x.CancelledOn == null,
            deferral: Deferral.InitiallyDeferred,
            name: "ex_shift_active");

        private static PgExclusion Site(PgSchema<Shift> pg, string name = "ex_shift_site") => pg.Exclusion(
            pg.Equal(x => x.WorkerId).Equal(x => x.SiteId).Overlaps(x => x.During),
            where: x => x.Kind != "draft",
            name: name);

        /// <summary>Every constraint of the 5.x model, declared with SchemaKit.</summary>
        public sealed class Declared : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
            {
                Tables(modelBuilder);
                Bookings(modelBuilder);
                modelBuilder.Entity<Shift>().DeclarePostgres(pg => [Active(pg), Site(pg)]);
                Leases(modelBuilder);
                Slots(modelBuilder);
            }
        }

        /// <summary>The tables alone: what a consumer has who swapped the packages and declared nothing yet.</summary>
        public sealed class NothingDeclared : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder) => Tables(modelBuilder);
        }

        /// <summary><see cref="Declared" /> with one constraint under another name.</summary>
        public sealed class OneRenamed : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
            {
                Tables(modelBuilder);
                Bookings(modelBuilder);
                modelBuilder.Entity<Shift>().DeclarePostgres(pg => [Active(pg), Site(pg, "ex_shift_per_site")]);
                Leases(modelBuilder);
                Slots(modelBuilder);
            }
        }

        /// <summary><see cref="Declared" /> with one constraint's condition widened: drafts count too.</summary>
        public sealed class OneWithoutItsCondition : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
            {
                Tables(modelBuilder);
                Bookings(modelBuilder);
                modelBuilder.Entity<Shift>().DeclarePostgres(pg =>
                    [Active(pg), pg.Exclusion(pg.Equal(x => x.WorkerId).Equal(x => x.SiteId).Overlaps(x => x.During), name: "ex_shift_site")]);
                Leases(modelBuilder);
                Slots(modelBuilder);
            }
        }

        /// <summary>One table and one constraint: the range alone, under GiST.</summary>
        public sealed class SlotUnderGist : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
                => modelBuilder.Entity<Slot>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During))]);
        }

        /// <summary><see cref="SlotUnderGist" />, deferrable and checked at once.</summary>
        public sealed class SlotUnderGistDeferrable : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
                => modelBuilder.Entity<Slot>().DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.During), deferral: Deferral.InitiallyImmediate)]);
        }

        /// <summary>One table and no constraint.</summary>
        public sealed class SlotAlone : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder) => modelBuilder.Entity<Slot>();
        }

        /// <summary><see cref="Declared" />, and the 5.x annotation still on one entity type.</summary>
        public sealed class DeclaredAndTheOldAnnotationKept : ITestModel
        {
            public static void Build(ModelBuilder modelBuilder)
            {
                Declared.Build(modelBuilder);
                modelBuilder.Entity<Slot>().HasAnnotation(
                    "CustomExclusion:Constraints", """[{"parts":[{"path":"During","op":"&&"}],"method":"spgist"}]""");
            }
        }
    }

    /// <summary>
    /// The files <c>EFCore.ComplexIndexes.PostgreSQL</c> 5.6.0 scaffolded for the model, compiled as
    /// they are checked in (<c>test/Fixtures/ComplexIndexes5x</c>): the snapshot a consumer's next
    /// diff starts from, and the migration their database was built by.
    /// </summary>
    internal static class LegacyFixture
    {
        private static readonly string Fixtures = Path.Combine(FindRoot(), "test", "Fixtures", "ComplexIndexes5x");

        /// <summary>The model with exclusion constraints, which the package reads.</summary>
        public static string Folder { get; } = Path.Combine(Fixtures, "Scaffolded");

        /// <summary>The model with what 5.x stores besides: indexes and a temporal constraint.</summary>
        public static string OtherKindsFolder { get; } = Path.Combine(Fixtures, "ScaffoldedOtherKinds");

        private static readonly Lazy<(ModelSnapshot Snapshot, Migration Migration)> Compiled = new(() => Compile(Folder));

        private static readonly Lazy<(ModelSnapshot Snapshot, Migration Migration)> CompiledOtherKinds = new(() => Compile(OtherKindsFolder));

        /// <summary>The snapshot of the kinds nothing reads yet, as 5.x wrote it.</summary>
        public static IModel OtherKinds => CompiledOtherKinds.Value.Snapshot.Model;

        public static Moved<TModel> Context<TModel>(string? connection = null)
            where TModel : ITestModel
            => new(new DbContextOptionsBuilder<Moved<TModel>>()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .UseNpgsql(connection ?? Contexts.Offline, o => o.UseSchemaKit())
                .Options);

        /// <summary>The 5.x snapshot as <c>dotnet ef</c> prepares it for a diff.</summary>
        public static IRelationalModel Snapshot(DbContext context) => Prepare(context, Compiled.Value.Snapshot.Model);

        /// <summary>A model as the source of a diff.</summary>
        public static IRelationalModel Prepare(DbContext context, IModel model)
            => context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: true, validationLogger: null).GetRelationalModel();

        /// <summary>What the 5.x migration does to a database, statement by statement as 5.x scaffolded it.</summary>
        public static IReadOnlyList<MigrationOperation> Up => Compiled.Value.Migration.UpOperations;

        /// <summary>What <c>migrations add</c> scaffolds for the consumer's first migration after the move.</summary>
        public static IReadOnlyList<MigrationOperation> DiffTo<TModel>()
            where TModel : ITestModel
        {
            using var context = Context<TModel>();
            return context.GetService<IMigrationsModelDiffer>().GetDifferences(Snapshot(context), context.Relational());
        }

        /// <summary>
        /// The diff from a model written here to <typeparamref name="TModel" />: for what a 5.x
        /// project could have stored and the fixture does not hold.
        /// </summary>
        public static IReadOnlyList<MigrationOperation> DiffFrom<TModel>(Action<ModelBuilder> source)
            where TModel : ITestModel
        {
            using var context = Context<TModel>();
            var builder = new ModelBuilder(context.GetService<IConventionSetBuilder>().CreateConventionSet());
            source(builder);
            return context.GetService<IMigrationsModelDiffer>().GetDifferences(Prepare(context, builder.FinalizeModel()), context.Relational());
        }

        private static (ModelSnapshot, Migration) Compile(string folder)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.Location.Length > 0).Select(a => a.Location))
                .Append(typeof(LegacyFixture).Assembly.Location)
                .Distinct()
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

            // The three files and nothing else: the context they name is this assembly's.
            var compilation = CSharpCompilation.Create(
                $"Legacy5x_{Guid.NewGuid():N}",
                Directory.GetFiles(folder, "*.cs").Order(StringComparer.Ordinal).Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)),
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

            stream.Position = 0;
            var assembly = new AssemblyLoadContext("legacy5x", isCollectible: false).LoadFromStream(stream);
            var snapshot = (ModelSnapshot)Activator.CreateInstance(assembly.GetTypes().Single(t => typeof(ModelSnapshot).IsAssignableFrom(t)))!;
            var migration = (Migration)Activator.CreateInstance(assembly.GetTypes().Single(t => typeof(Migration).IsAssignableFrom(t)))!;
            migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            return (snapshot, migration);
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "global.json")) && Directory.Exists(Path.Combine(directory.FullName, "src")))
                    return directory.FullName;

            throw new InvalidOperationException($"No repository root (global.json beside src/) above {AppContext.BaseDirectory}.");
        }
    }
}
