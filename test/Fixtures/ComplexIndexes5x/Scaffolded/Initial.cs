using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Legacy5x.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.CreateTable(
                name: "Booking",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoomId = table.Column<int>(type: "integer", nullable: false),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Booking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lease",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Term_Span = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lease", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    worker_id = table.Column<int>(type: "integer", nullable: false),
                    site_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    during = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false),
                    cancelled_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Slot",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Slot", x => x.Id);
                });

            migrationBuilder.Sql("ALTER TABLE \"Booking\" DROP CONSTRAINT IF EXISTS \"EX_Booking_RoomId_During\";\nALTER TABLE \"Booking\" ADD CONSTRAINT \"EX_Booking_RoomId_During\" EXCLUDE USING gist (\"RoomId\" WITH =, \"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"Lease\" DROP CONSTRAINT IF EXISTS \"EX_Lease_lowerCode_Term_Span\";\nALTER TABLE \"Lease\" ADD CONSTRAINT \"EX_Lease_lowerCode_Term_Span\" EXCLUDE USING gist ((lower(\"Code\")) WITH =, \"Term_Span\" WITH &&) DEFERRABLE;");

            migrationBuilder.Sql("ALTER TABLE \"ops\".\"shifts\" DROP CONSTRAINT IF EXISTS \"ex_shift_active\";\nALTER TABLE \"ops\".\"shifts\" ADD CONSTRAINT \"ex_shift_active\" EXCLUDE USING gist (\"worker_id\" WITH =, \"during\" WITH &&) WHERE (\"cancelled_on\" IS NULL) DEFERRABLE INITIALLY DEFERRED;");

            migrationBuilder.Sql("ALTER TABLE \"ops\".\"shifts\" DROP CONSTRAINT IF EXISTS \"ex_shift_site\";\nALTER TABLE \"ops\".\"shifts\" ADD CONSTRAINT \"ex_shift_site\" EXCLUDE USING gist (\"worker_id\" WITH =, \"site_id\" WITH =, \"during\" WITH &&) WHERE (\"kind\" <> 'draft');");

            migrationBuilder.Sql("ALTER TABLE \"Slot\" DROP CONSTRAINT IF EXISTS \"EX_Slot_During\";\nALTER TABLE \"Slot\" ADD CONSTRAINT \"EX_Slot_During\" EXCLUDE USING spgist (\"During\" WITH &&);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Booking");

            migrationBuilder.DropTable(
                name: "Lease");

            migrationBuilder.DropTable(
                name: "shifts",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "Slot");
        }
    }
}
