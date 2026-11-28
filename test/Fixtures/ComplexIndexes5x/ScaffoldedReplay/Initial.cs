using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Legacy5x.Migrations.Replay
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ledger");

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "ledger",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: true),
                    Period = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false),
                    Amount_Cents = table.Column<long>(type: "bigint", nullable: false),
                    Amount_Currency = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entries", x => x.Id);
                });

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
                name: "ix_entry_title_search",
                schema: "ledger",
                table: "entries",
                columns: new[] { "to_tsvector('simple', \"Title\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"to_tsvector(\\u0027simple\\u0027, \\u0022Title\\u0022)\"}]")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:StorageParameter:fastupdate", false)
                .Annotation("Npgsql:StorageParameter:gin_pending_list_limit", 128);

            migrationBuilder.CreateIndex(
                name: "ix_entry_period",
                schema: "ledger",
                table: "entries",
                columns: new[] { "\"Period\"", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022Period\\u0022\"}]")
                .Annotation("Npgsql:IndexMethod", "gist")
                .Annotation("Npgsql:StorageParameter:buffering", "auto")
                .Annotation("Npgsql:StorageParameter:fillfactor", 90);

            migrationBuilder.CreateIndex(
                name: "ix_entry_rank_brin",
                schema: "ledger",
                table: "entries",
                columns: new[] { "\"Rank\" + 0", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022Rank\\u0022 \\u002B 0\"}]")
                .Annotation("Npgsql:IndexMethod", "brin")
                .Annotation("Npgsql:StorageParameter:autosummarize", true)
                .Annotation("Npgsql:StorageParameter:pages_per_range", 16);

            migrationBuilder.CreateIndex(
                name: "ix_entry_code_hash",
                schema: "ledger",
                table: "entries",
                columns: new[] { "lower(\"Code\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Code\\u0022)\"}]")
                .Annotation("Npgsql:IndexMethod", "hash");

            migrationBuilder.CreateIndex(
                name: "ix_entry_title_sp",
                schema: "ledger",
                table: "entries",
                columns: new[] { "lower(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Title\\u0022)\"}]")
                .Annotation("Npgsql:IndexMethod", "spgist");

            migrationBuilder.CreateIndex(
                name: "ix_entry_code_upper",
                schema: "ledger",
                table: "entries",
                columns: new[] { "upper(\"Code\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"upper(\\u0022Code\\u0022)\"}]")
                .Annotation("Npgsql:CreatedConcurrently", true);

            migrationBuilder.CreateIndex(
                name: "IX_entries_Kind_Score_Amount_Currency",
                schema: "ledger",
                table: "entries",
                columns: new[] { "Kind", "Score", "Amount_Currency", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":false,\"v\":\"Kind\"},{\"e\":false,\"v\":\"Score\",\"n\":\"First\"},{\"e\":false,\"v\":\"Amount_Currency\"}]")
                .Annotation("Npgsql:IndexInclude", new[] { "Title" })
                .Annotation("Npgsql:StorageParameter:fillfactor", 80);

            migrationBuilder.CreateIndex(
                name: "IX_entries_lowerTitle",
                schema: "ledger",
                table: "entries",
                columns: new[] { "lower(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" },
                filter: "\"Note\" IS NOT NULL")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Title\\u0022)\"}]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries",
                schema: "ledger");
        }
    }
}
