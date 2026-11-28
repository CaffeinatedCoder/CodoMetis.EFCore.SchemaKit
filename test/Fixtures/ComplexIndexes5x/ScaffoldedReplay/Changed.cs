using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy5x.Migrations.Replay
{
    /// <inheritdoc />
    public partial class Changed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entry_kind_lower",
                schema: "ledger",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "ux_entry_kind_title",
                schema: "ledger",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "ix_entry_code_upper",
                schema: "ledger",
                table: "entries");

            migrationBuilder.CreateIndex(
                name: "ux_entry_kind_title",
                schema: "ledger",
                table: "entries",
                columns: new[] { "\"Kind\"", "upper(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" },
                unique: true,
                descending: new[] { false, true, false },
                filter: "\"Rank\" > 1")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022Kind\\u0022\"},{\"e\":true,\"v\":\"upper(\\u0022Title\\u0022)\",\"d\":true,\"n\":\"Last\"}]")
                .Annotation("Npgsql:IndexInclude", new[] { "Rank", "Amount_Currency" })
                .Annotation("Npgsql:IndexOperators", new[] { "", "text_pattern_ops" })
                .Annotation("Npgsql:NullsDistinct", false)
                .Annotation("Npgsql:StorageParameter:deduplicate_items", false)
                .Annotation("Npgsql:StorageParameter:fillfactor", 70)
                .Annotation("Relational:Collation", new[] { "", "C" });

            migrationBuilder.CreateIndex(
                name: "ix_entry_note_lower",
                schema: "ledger",
                table: "entries",
                columns: new[] { "lower(\"Note\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Note\\u0022)\"}]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_entry_kind_title",
                schema: "ledger",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "ix_entry_note_lower",
                schema: "ledger",
                table: "entries");

            migrationBuilder.CreateIndex(
                name: "ix_entry_kind_lower",
                schema: "ledger",
                table: "entries",
                columns: new[] { "lower(\"Kind\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Kind\\u0022)\"}]")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" })
                .Annotation("Relational:Collation", new[] { "C" });

            migrationBuilder.CreateIndex(
                name: "ux_entry_kind_title",
                schema: "ledger",
                table: "entries",
                columns: new[] { "\"Kind\"", "upper(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" },
                unique: true,
                descending: new[] { false, true, false },
                filter: "\"Rank\" > 0")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022Kind\\u0022\"},{\"e\":true,\"v\":\"upper(\\u0022Title\\u0022)\",\"d\":true,\"n\":\"Last\"}]")
                .Annotation("Npgsql:IndexInclude", new[] { "Rank", "Amount_Currency" })
                .Annotation("Npgsql:IndexOperators", new[] { "", "text_pattern_ops" })
                .Annotation("Npgsql:NullsDistinct", false)
                .Annotation("Npgsql:StorageParameter:deduplicate_items", false)
                .Annotation("Npgsql:StorageParameter:fillfactor", 70)
                .Annotation("Relational:Collation", new[] { "", "C" });

            migrationBuilder.CreateIndex(
                name: "ix_entry_code_upper",
                schema: "ledger",
                table: "entries",
                columns: new[] { "upper(\"Code\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"upper(\\u0022Code\\u0022)\"}]")
                .Annotation("Npgsql:CreatedConcurrently", true);
        }
    }
}
