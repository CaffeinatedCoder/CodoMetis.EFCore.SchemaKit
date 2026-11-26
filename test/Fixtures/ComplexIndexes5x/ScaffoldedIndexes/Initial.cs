using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Legacy5x.Migrations.Indexes
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "VehicleSequence");

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Nick = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    GranteeId = table.Column<int>(type: "integer", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: true),
                    DeletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Tags = table.Column<string[]>(type: "text[]", nullable: false),
                    Period = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false),
                    Contact_City = table.Column<string>(type: "text", nullable: false),
                    Contact_Phone = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    prefs = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "boats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"VehicleSequence\"')"),
                    Plate = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_boats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"VehicleSequence\"')"),
                    Plate = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"VehicleSequence\"')"),
                    Plate = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vehicles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Contact_City",
                table: "accounts",
                column: "Contact_City");

            migrationBuilder.CreateIndex(
                name: "ux_account_phone",
                table: "accounts",
                column: "Contact_Phone",
                unique: true,
                filter: "\"DeletedOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_prefsTheme",
                table: "accounts",
                columns: new[] { "\"prefs\" ->> 'Theme'", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022prefs\\u0022 -\\u003E\\u003E \\u0027Theme\\u0027\"}]");

            migrationBuilder.CreateIndex(
                name: "ux_account_email_active",
                table: "accounts",
                column: "Email",
                unique: true,
                filter: "\"DeletedOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_account_email_all",
                table: "accounts",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_GranteeId_Kind",
                table: "accounts",
                columns: new[] { "GranteeId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Kind_Rank_Score",
                table: "accounts",
                columns: new[] { "Kind", "Rank", "Score", "__requires_UseNpgsqlComplexIndexes__" },
                descending: new[] { false, true, false, false })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":false,\"v\":\"Kind\"},{\"e\":false,\"v\":\"Rank\",\"d\":true},{\"e\":false,\"v\":\"Score\",\"n\":\"First\"}]");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Title_Nick",
                table: "accounts",
                columns: new[] { "Title", "Nick" })
                .Annotation("Npgsql:IndexInclude", new[] { "Rank", "Contact_City" })
                .Annotation("Npgsql:StorageParameter:deduplicate_items", false)
                .Annotation("Npgsql:StorageParameter:fillfactor", 70)
                .Annotation("Relational:Collation", new[] { "C", "" });

            migrationBuilder.CreateIndex(
                name: "ux_account_nick",
                table: "accounts",
                column: "Nick",
                unique: true)
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" })
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_account_rank",
                table: "accounts",
                column: "Rank")
                .Annotation("Npgsql:CreatedConcurrently", true);

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Tags",
                table: "accounts",
                column: "Tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Period",
                table: "accounts",
                column: "Period")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_CreatedAt",
                table: "accounts",
                column: "CreatedAt")
                .Annotation("Npgsql:IndexMethod", "brin")
                .Annotation("Npgsql:StorageParameter:pages_per_range", 16);

            migrationBuilder.CreateIndex(
                name: "ix_account_title_sp",
                table: "accounts",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "spgist");

            migrationBuilder.CreateIndex(
                name: "ix_account_kind_hash",
                table: "accounts",
                column: "Kind")
                .Annotation("Npgsql:IndexMethod", "hash");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_lowerEmail",
                table: "accounts",
                columns: new[] { "lower(\"Email\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Email\\u0022)\"}]");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_lowerTitle",
                table: "accounts",
                columns: new[] { "lower(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" },
                unique: true)
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Title\\u0022)\"}]");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_coalesceNickKindTitle",
                table: "accounts",
                columns: new[] { "((coalesce(\"Nick\", \"Kind\") || ' ') || \"Title\")", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"((coalesce(\\u0022Nick\\u0022, \\u0022Kind\\u0022) || \\u0027 \\u0027) || \\u0022Title\\u0022)\"}]");

            migrationBuilder.CreateIndex(
                name: "ux_account_kind_title",
                table: "accounts",
                columns: new[] { "\"Kind\"", "upper(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" },
                unique: true,
                descending: new[] { false, true, false },
                filter: "\"Rank\" > 0")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022Kind\\u0022\"},{\"e\":true,\"v\":\"upper(\\u0022Title\\u0022)\",\"d\":true,\"n\":\"Last\"}]");

            migrationBuilder.CreateIndex(
                name: "ix_account_kind_live",
                table: "accounts",
                column: "Kind",
                filter: "\"DeletedOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_GranteeId_Rank",
                table: "accounts",
                columns: new[] { "GranteeId", "Rank" },
                filter: "(\"DeletedOn\" IS NOT NULL AND \"Kind\" = 'federated')");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_upperEmail",
                table: "accounts",
                columns: new[] { "upper(\"Email\")", "__requires_UseNpgsqlComplexIndexes__" },
                filter: "(\"Nick\" <> 'blocked' OR \"Nick\" IS NULL)")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"upper(\\u0022Email\\u0022)\"}]");

            migrationBuilder.CreateIndex(
                name: "ux_account_level",
                table: "accounts",
                columns: new[] { "CAST(\"prefs\" ->> 'Level' AS integer)", "__requires_UseNpgsqlComplexIndexes__" },
                unique: true)
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"CAST(\\u0022prefs\\u0022 -\\u003E\\u003E \\u0027Level\\u0027 AS integer)\"}]");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_prefsAddressZip",
                table: "accounts",
                columns: new[] { "\"prefs\" #>> '{Address,Zip}'", "__requires_UseNpgsqlComplexIndexes__" })
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"\\u0022prefs\\u0022 #\\u003E\\u003E \\u0027{Address,Zip}\\u0027\"}]");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_Kind_prefsActive",
                table: "accounts",
                columns: new[] { "Kind", "CAST(\"prefs\" ->> 'Active' AS boolean)", "__requires_UseNpgsqlComplexIndexes__" },
                filter: "(CAST(\"prefs\" ->> 'Level' AS integer)) > 3")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":false,\"v\":\"Kind\"},{\"e\":true,\"v\":\"CAST(\\u0022prefs\\u0022 -\\u003E\\u003E \\u0027Active\\u0027 AS boolean)\"}]");

            migrationBuilder.CreateIndex(
                name: "IX_accounts_payload",
                table: "accounts",
                column: "payload")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_vehicle_plate",
                table: "vehicles",
                column: "Plate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_vehicle_plate_boats",
                table: "boats",
                column: "Plate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_vehicle_plate_cars",
                table: "cars",
                column: "Plate",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vehicles_OwnerId",
                table: "vehicles",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_boats_OwnerId",
                table: "boats",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_cars_OwnerId",
                table: "cars",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "boats");

            migrationBuilder.DropTable(
                name: "cars");

            migrationBuilder.DropTable(
                name: "vehicles");

            migrationBuilder.DropSequence(
                name: "VehicleSequence");
        }
    }
}
