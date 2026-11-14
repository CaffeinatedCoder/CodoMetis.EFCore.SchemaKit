using CodoMetis.EFCore.SchemaKit.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Shop;

public sealed class Booking
{
    public int Id { get; set; }
    public int RoomId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
    public DateOnly? CancelledOn { get; set; }
}

/// <summary>
/// One constructor, taking the options: what <c>AddDbContextPool</c> requires, and what
/// <c>dotnet ef</c> reaches through the host Program.cs builds.
/// </summary>
public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Booking>(booking =>
        {
            booking.ToTable("bookings");

            // No two bookings of a room overlap, among those not cancelled. SMOKE_MODEL=changed drops
            // the condition: a change only SchemaKit's differ can see.
            var changed = Environment.GetEnvironmentVariable("SMOKE_MODEL") == "changed";
            booking.DeclarePostgres(pg =>
            [
                changed
                    ? pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), name: "ex_bookings_room_period")
                    : pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Period), where: x => x.CancelledOn == null, name: "ex_bookings_room_period"),
            ]);
        });
}
