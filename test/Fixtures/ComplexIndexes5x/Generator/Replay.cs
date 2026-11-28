using EFCore.ComplexIndexes;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NpgsqlTypes;

namespace Legacy5x.Replay;

// The operations only 5.x's SQL generator rendered (CodoMetis.EFCore.SchemaKit#28): an index with an
// expression or a null order is scaffolded as a CreateIndex with the annotation
// CustomIndex:IndexParts, and written from it when the migration is applied. Each index here
// carries that annotation with an option of Npgsql's the generator wrote beside the parts: the
// access method, a collation and an operator class per part, included columns, NULLS NOT
// DISTINCT, storage parameters of each kind of value, CONCURRENTLY, a condition.
//
// Two migrations, so that a Down holds such an operation too: the second takes two of the
// indexes away, changes one and adds one. LEGACY5X_REPLAY_STEP says which model is scaffolded.

public sealed class Money
{
    public string Currency { get; set; } = "";
    public long Cents { get; set; }
}

public sealed class Entry
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public int Rank { get; set; }
    public int? Score { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
    public Money Amount { get; set; } = new();
}

public sealed class ReplayContext(DbContextOptions<ReplayContext> options) : DbContext(options)
{
    private static readonly bool Changed = Environment.GetEnvironmentVariable("LEGACY5X_REPLAY_STEP") == "2";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entry>(entry =>
        {
            entry.ToTable("entries", "ledger");
            entry.ComplexProperty(x => x.Amount);

            // A collation and an operator class on an expression. Gone in the second migration,
            // so its Down creates the index again. Its expression is no other index's: 5.x kept
            // one index per list of parts, and a second one with the same parts replaced the first.
            if (!Changed)
            {
                entry.HasExpressionIndex(idx => idx
                    .Expression("lower(\"Kind\")")
                    .UseCollation("C")
                    .HasOperators("text_pattern_ops")
                    .HasName("ix_entry_kind_lower"));
            }

            // Everything at once: two parts, an order, a collation and a class for the second part
            // alone, included columns, NULLS NOT DISTINCT, two storage parameters, a condition.
            // The condition changes in the second migration.
            entry.HasExpressionIndex(idx => idx
                .Expression("\"Kind\"")
                .Expression("upper(\"Title\")").Descending().NullsLast()
                .UseCollation("", "C")
                .HasOperators("", "text_pattern_ops")
                .IsUnique()
                .AreNullsDistinct(false)
                .IncludeProperties("Rank", "Amount.Currency")
                .HasStorageParameter("fillfactor", 70)
                .HasStorageParameter("deduplicate_items", false)
                .HasFilter(Changed ? "\"Rank\" > 1" : "\"Rank\" > 0")
                .HasName("ux_entry_kind_title"));

            // The other access methods, each with a storage parameter of its own.
            entry.HasExpressionIndex(idx => idx
                .Expression("to_tsvector('simple', \"Title\")")
                .UseGin()
                .HasStorageParameter("fastupdate", false)
                .HasStorageParameter("gin_pending_list_limit", 128)
                .HasName("ix_entry_title_search"));
            entry.HasExpressionIndex(idx => idx
                .Expression("\"Period\"")
                .UseGist()
                .HasStorageParameter("buffering", "auto")
                .HasStorageParameter("fillfactor", 90)
                .HasName("ix_entry_period"));
            entry.HasExpressionIndex(idx => idx
                .Expression("\"Rank\" + 0")
                .UseBrin()
                .HasStorageParameter("pages_per_range", 16)
                .HasStorageParameter("autosummarize", true)
                .HasName("ix_entry_rank_brin"));
            entry.HasExpressionIndex(idx => idx.Expression("lower(\"Code\")").UseHash().HasName("ix_entry_code_hash"));
            entry.HasExpressionIndex(idx => idx.Expression("lower(\"Title\")").UseSpGist().HasName("ix_entry_title_sp"));

            // Built outside the migration's transaction. Gone in the second migration.
            if (!Changed)
                entry.HasExpressionIndex(idx => idx.Expression("upper(\"Code\")").IsCreatedConcurrently().HasName("ix_entry_code_upper"));

            // Columns alone, with a null order: no expression, and still only 5.x's generator's.
            entry.HasComplexCompositeIndex(
                x => new { x.Kind, Score = DbOrder.NullsFirst(x.Score), x.Amount.Currency },
                idx => idx.IncludeProperties("Title").HasStorageParameter("fillfactor", 80));

            // A lambda with a lambda's condition, under the default name.
            entry.HasExpressionIndex(x => x.Title.ToLower(), x => x.Note != null);

            // New in the second migration.
            if (Changed)
                entry.HasExpressionIndex(idx => idx.Expression("lower(\"Note\")").HasName("ix_entry_note_lower"));
        });
    }
}

public sealed class ReplayContextFactory : IDesignTimeDbContextFactory<ReplayContext>
{
    public ReplayContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ReplayContext>()
            .UseNpgsql("Host=localhost;Database=legacy5x")
            .UseNpgsqlComplexIndexes()
            .Options);
}
