using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Legacy5x.Migrations.Tpc
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.CreateSequence(
                name: "PermitSequence");

            migrationBuilder.CreateSequence(
                name: "RentalSequence");

            migrationBuilder.CreateTable(
                name: "boat_rentals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"RentalSequence\"')"),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_boat_rentals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "car_rentals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"RentalSequence\"')"),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_car_rentals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fishing_permits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"PermitSequence\"')"),
                    HolderId = table.Column<int>(type: "integer", nullable: false),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fishing_permits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "parking_permits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"PermitSequence\"')"),
                    HolderId = table.Column<int>(type: "integer", nullable: false),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parking_permits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rentals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('\"RentalSequence\"')"),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    During = table.Column<NpgsqlRange<DateOnly>>(type: "daterange", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rentals", x => x.Id);
                });

            migrationBuilder.Sql("ALTER TABLE \"fishing_permits\" DROP CONSTRAINT IF EXISTS \"ex_permit_fishing_permits\";\nALTER TABLE \"fishing_permits\" ADD CONSTRAINT \"ex_permit_fishing_permits\" EXCLUDE USING gist (\"HolderId\" WITH =, \"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"parking_permits\" DROP CONSTRAINT IF EXISTS \"ex_permit_parking_permits\";\nALTER TABLE \"parking_permits\" ADD CONSTRAINT \"ex_permit_parking_permits\" EXCLUDE USING gist (\"HolderId\" WITH =, \"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"rentals\" DROP CONSTRAINT IF EXISTS \"ex_rental\";\nALTER TABLE \"rentals\" ADD CONSTRAINT \"ex_rental\" EXCLUDE USING gist (\"UnitId\" WITH =, \"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"boat_rentals\" DROP CONSTRAINT IF EXISTS \"ex_rental_boat_rentals\";\nALTER TABLE \"boat_rentals\" ADD CONSTRAINT \"ex_rental_boat_rentals\" EXCLUDE USING gist (\"UnitId\" WITH =, \"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"car_rentals\" DROP CONSTRAINT IF EXISTS \"ex_rental_car_rentals\";\nALTER TABLE \"car_rentals\" ADD CONSTRAINT \"ex_rental_car_rentals\" EXCLUDE USING gist (\"UnitId\" WITH =, \"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"rentals\" DROP CONSTRAINT IF EXISTS \"EX_rentals_During\";\nALTER TABLE \"rentals\" ADD CONSTRAINT \"EX_rentals_During\" EXCLUDE USING gist (\"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"boat_rentals\" DROP CONSTRAINT IF EXISTS \"EX_boat_rentals_During\";\nALTER TABLE \"boat_rentals\" ADD CONSTRAINT \"EX_boat_rentals_During\" EXCLUDE USING gist (\"During\" WITH &&);");

            migrationBuilder.Sql("ALTER TABLE \"car_rentals\" DROP CONSTRAINT IF EXISTS \"EX_car_rentals_During\";\nALTER TABLE \"car_rentals\" ADD CONSTRAINT \"EX_car_rentals_During\" EXCLUDE USING gist (\"During\" WITH &&);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "boat_rentals");

            migrationBuilder.DropTable(
                name: "car_rentals");

            migrationBuilder.DropTable(
                name: "fishing_permits");

            migrationBuilder.DropTable(
                name: "parking_permits");

            migrationBuilder.DropTable(
                name: "rentals");

            migrationBuilder.DropSequence(
                name: "PermitSequence");

            migrationBuilder.DropSequence(
                name: "RentalSequence");
        }
    }
}
