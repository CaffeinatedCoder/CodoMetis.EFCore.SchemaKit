using Microsoft.EntityFrameworkCore;

// The consumer's own namespace, as in LegacyFixture.cs: the scaffolded migrations name its context.
namespace Legacy5x.Replay
{
    /// <summary>
    /// The context of <c>test/Fixtures/ComplexIndexes5x/Generator/Replay.cs</c>. The migrations
    /// name it in an attribute and nothing reads its model: what is replayed is the operations.
    /// </summary>
    public sealed class ReplayContext(DbContextOptions<ReplayContext> options) : DbContext(options);
}
