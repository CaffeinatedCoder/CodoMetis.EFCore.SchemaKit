using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Legacy5x.Migrations.OtherKinds
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.CreateTable(
                name: "Customer",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Address_City = table.Column<string>(type: "text", nullable: false),
                    Address_Zip = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Supplier",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Address_City = table.Column<string>(type: "text", nullable: false),
                    Address_Zip = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Supplier", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tariff",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Valid = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tariff", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customer_Address_City",
                table: "Customer",
                column: "Address_City");

            migrationBuilder.CreateIndex(
                name: "IX_Supplier_Address_Zip",
                table: "Supplier",
                column: "Address_Zip",
                unique: true);

            migrationBuilder.Sql("ALTER TABLE \"Tariff\" ADD CONSTRAINT \"AK_Tariff_ProductId_Valid\" UNIQUE (\"ProductId\", \"Valid\" WITHOUT OVERLAPS);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customer");

            migrationBuilder.DropTable(
                name: "Supplier");

            migrationBuilder.DropTable(
                name: "Tariff");
        }
    }
}
