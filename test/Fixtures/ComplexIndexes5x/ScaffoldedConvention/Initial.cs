using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Legacy5x.Migrations.Convention
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.CreateTable(
                name: "memos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Text = table.Column<string>(type: "text", nullable: false),
                    AuthorId = table.Column<int>(type: "integer", nullable: false),
                    Period = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    QueueId = table.Column<int>(type: "integer", nullable: false),
                    Period = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false),
                    ClosedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DeletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Label_Code = table.Column<string>(type: "text", nullable: false),
                    Label_Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_memo_text",
                table: "memos",
                column: "Text",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ticket_label_code_open",
                table: "tickets",
                column: "Label_Code",
                unique: true,
                filter: "(\"ClosedOn\" IS NULL) AND (\"DeletedOn\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_label_name",
                table: "tickets",
                column: "Label_Name",
                unique: true,
                filter: "\"DeletedOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_code",
                table: "tickets",
                column: "Code",
                unique: true,
                filter: "\"DeletedOn\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_queue_code_open",
                table: "tickets",
                columns: new[] { "QueueId", "Code" },
                unique: true,
                filter: "(\"ClosedOn\" IS NULL) AND (\"DeletedOn\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_owner_title_open",
                table: "tickets",
                columns: new[] { "OwnerId", "Title" },
                unique: true,
                filter: "((\"ClosedOn\" IS NULL AND \"QueueId\" > 0)) AND (\"DeletedOn\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_title_lower_open",
                table: "tickets",
                columns: new[] { "lower(\"Title\")", "__requires_UseNpgsqlComplexIndexes__" },
                unique: true,
                filter: "(\"ClosedOn\" IS NULL) AND (\"DeletedOn\" IS NULL)")
                .Annotation("CustomIndex:IndexParts", "[{\"e\":true,\"v\":\"lower(\\u0022Title\\u0022)\"}]");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_owner",
                table: "tickets",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_queue_open",
                table: "tickets",
                column: "QueueId",
                filter: "\"ClosedOn\" IS NULL");

            migrationBuilder.Sql("ALTER TABLE \"memos\" DROP CONSTRAINT IF EXISTS \"ex_memo_author\";\nALTER TABLE \"memos\" ADD CONSTRAINT \"ex_memo_author\" EXCLUDE USING gist (\"AuthorId\" WITH =, \"Period\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"tickets\" DROP CONSTRAINT IF EXISTS \"ex_ticket_owner\";\nALTER TABLE \"tickets\" ADD CONSTRAINT \"ex_ticket_owner\" EXCLUDE USING gist (\"OwnerId\" WITH =, \"Period\" WITH &&) WHERE (\"DeletedOn\" IS NULL);");

            migrationBuilder.Sql("ALTER TABLE \"tickets\" DROP CONSTRAINT IF EXISTS \"ex_ticket_queue_open\";\nALTER TABLE \"tickets\" ADD CONSTRAINT \"ex_ticket_queue_open\" EXCLUDE USING gist (\"QueueId\" WITH =, \"Period\" WITH &&) WHERE ((\"ClosedOn\" IS NULL) AND (\"DeletedOn\" IS NULL));");

            migrationBuilder.Sql("ALTER TABLE \"tickets\" DROP CONSTRAINT IF EXISTS \"ex_ticket_owner_queue_open\";\nALTER TABLE \"tickets\" ADD CONSTRAINT \"ex_ticket_owner_queue_open\" EXCLUDE USING gist (\"OwnerId\" WITH =, \"QueueId\" WITH =, \"Period\" WITH &&) WHERE (((\"ClosedOn\" IS NULL AND \"QueueId\" > 0)) AND (\"DeletedOn\" IS NULL));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memos");

            migrationBuilder.DropTable(
                name: "tickets");
        }
    }
}
