using CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests;

/// <summary>
/// A project that comes from <c>EFCore.ComplexIndexes</c> 5.x with its exclusion constraints over
/// columns of ValueRanges (#4): its snapshot against the same model declared here, with the
/// range through <c>WithUncheckedOperator</c>.
/// </summary>
/// <remarks>
/// The snapshot is written in this file, in the form the published 5.6.0 scaffolded for a
/// constraint with a name and a filter (<c>test/Fixtures/ComplexIndexes5x/Scaffolded</c>, the
/// shifts' first constraint). No fixture was scaffolded by 5.x beside ValueRanges 10.x, which
/// needs both on <c>net10.0</c>. What makes the form the right one: a snapshot holds a property
/// of ValueRanges as Npgsql's range (<c>A_snapshot_holds_the_property_as_Npgsqls_range_and_the_column_type_as_text</c>
/// in <see cref="ValueRangesMigrationTests" />), so to a snapshot such a column is the fixture's own.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ComingFrom5xTests
{
    /// <summary>What 5.x wrote into a snapshot for <see cref="Bookings.Declared" />'s constraint.</summary>
    private sealed class BookingsOf5x : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
            modelBuilder
                .HasAnnotation("CustomIndex:RenderingVersion", 3)
                .HasAnnotation("ProductVersion", "10.0.15")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness.Booking", b =>
            {
                var id = b.Property<int>("Id")
                    .ValueGeneratedOnAdd()
                    .HasColumnType("integer");

                NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(id);

                b.Property<DateOnly?>("CancelledOn")
                    .HasColumnType("date");

                b.Property<NpgsqlRange<DateOnly>>("During")
                    .HasColumnType("daterange");

                b.Property<int>("RoomId")
                    .HasColumnType("integer");

                b.HasKey("Id");

                b.ToTable("bookings");

                b.HasAnnotation(
                    "CustomExclusion:Constraints",
                    "[{\"parts\":[{\"path\":\"RoomId\",\"op\":\"=\"},{\"path\":\"During\",\"op\":\"\\u0026\\u0026\"}],\"filter\":\"{CancelledOn} IS NULL\",\"name\":\"ex_booking_room_during\"}]");
            });
        }
    }

    private static IRelationalModel Of5x(DbContext context)
        => context.GetService<IModelRuntimeInitializer>().Initialize(new BookingsOf5x().Model, designTime: true, validationLogger: null).GetRelationalModel();

    /// <summary>
    /// The first <c>migrations add</c> after the move has no operations, and neither has its
    /// Down: the constraint 5.x created is the one the model declares.
    /// </summary>
    [TestMethod]
    public void A_5x_snapshot_against_the_same_constraint_over_a_DateRange_is_no_operations()
    {
        using var context = Contexts.Of<Bookings.Declared>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var from5x = Of5x(context);

        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(from5x, context.Relational()).Shape());
        CollectionAssert.AreEqual(Array.Empty<string>(), differ.GetDifferences(context.Relational(), from5x).Shape());
        Assert.IsFalse(differ.HasDifferences(from5x, context.Relational()));
    }

    /// <summary>
    /// The control: the snapshot's constraint is read. Against a model that declares nothing it
    /// is dropped by the name 5.x gave it, and from a model that declares nothing it is the one
    /// statement that adds it.
    /// </summary>
    [TestMethod]
    public void The_constraint_in_the_5x_snapshot_is_read()
    {
        using var context = Contexts.Of<Bookings.Bare>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var from5x = Of5x(context);

        CollectionAssert.AreEqual(
            new[] { "ALTER TABLE bookings DROP CONSTRAINT ex_booking_room_during;" },
            differ.GetDifferences(from5x, context.Relational()).Shape());
    }
}
