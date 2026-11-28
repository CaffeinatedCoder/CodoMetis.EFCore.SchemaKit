using System.Reflection;
using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using CodoMetis.EFCore.SchemaKit.ValueRanges.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.ValueRanges.Tests;

/// <summary>
/// What SchemaKit's public surface gives a package that adds a typed <c>Overlaps</c> for another
/// library's ranges, and what it does not (#4). Evidence for ValueRanges' satellite
/// (CaffeinatedCoder/CodoMetis.ValueRanges#540), measured from a project that has no more access
/// than that package will have.
/// </summary>
[TestClass]
public sealed class SatelliteSurfaceTests
{
    private static readonly PgSchema<Booking> Bookings = PgSchema.For<Booking>();

    private static readonly PgSchema<Closure> Closures = PgSchema.For<Closure>();

    /// <summary>
    /// After another element, a typed <c>Overlaps</c> is an extension method over the unchecked
    /// operator, and the declaration is the one a consumer writes by hand today: the same value,
    /// so the same key in a snapshot and no operation when a model moves from one form to the other.
    /// </summary>
    [TestMethod]
    public void A_typed_Overlaps_after_another_element_is_the_declaration_written_with_the_unchecked_operator()
    {
        Assert.AreEqual(
            Bookings.Exclusion(Bookings.Equal(x => x.RoomId).WithUncheckedOperator(x => x.During, "&&"), name: "ex"),
            Bookings.Exclusion(Bookings.Equal(x => x.RoomId).Overlaps(x => x.During), name: "ex"));

        Assert.AreEqual(
            Closures.Exclusion(Closures.Equal(x => x.RoomId).WithUncheckedOperator(x => x.Days, "&&"), name: "ex"),
            Closures.Exclusion(Closures.Equal(x => x.RoomId).Overlaps(x => x.Days), name: "ex"));

        // And it is the element SchemaKit's own typed form makes for a range of Npgsql's.
        var twin = PgSchema.For<NpgsqlBooking>();
        CollectionAssert.AreEqual(
            twin.Equal(x => x.RoomId).Overlaps(x => x.During).Elements.ToList(),
            Bookings.Equal(x => x.RoomId).Overlaps(x => x.During).Elements.ToList());
    }

    /// <summary>
    /// The elements cannot begin with it. The factory starts them with <c>Equal</c>, or with an
    /// <c>Overlaps</c> that takes Npgsql's ranges alone; the unchecked operator is on what those
    /// return. And nothing outside SchemaKit makes the type the factory's <c>Exclusion</c> takes.
    /// So a constraint over a range of ValueRanges alone, or with that range first, is written
    /// as records today (<see cref="Harness.Bookings.DuringAlone" />).
    /// </summary>
    /// <remarks>
    /// When this fails, the surface was widened: say so in the README's paragraph on
    /// ValueRanges, and write <see cref="Harness.Bookings.DuringAlone" /> and its siblings with the new form.
    /// </remarks>
    [TestMethod]
    public void Nothing_public_starts_the_elements_with_an_operator_of_ones_own()
    {
        var factory = typeof(PgSchema<>).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        // The reading is of something: the factory's typed forms are there.
        Assert.AreEqual(4, factory.Count(method => method.Name == "Overlaps"));
        Assert.IsTrue(factory.Where(method => method.Name == "Overlaps").All(method => method.GetParameters().Single().ParameterType.ToString().Contains("NpgsqlRange", StringComparison.Ordinal)));

        Assert.AreEqual(0, factory.Count(method => method.Name == "WithUncheckedOperator"));
        Assert.AreEqual(0, typeof(ExclusionElements<>).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length);
        Assert.AreEqual(0, typeof(ExclusionDraft<>).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length);
    }
}
