using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerLocationCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_lab_order_counter",
                table: "lab_order_counter");

            migrationBuilder.DropPrimaryKey(
                name: "PK_lab_accession_counter",
                table: "lab_accession_counter");

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "lab_order_counter",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "lab_accession_counter",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_lab_order_counter",
                table: "lab_order_counter",
                columns: new[] { "organization_id", "location_id", "day" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_lab_accession_counter",
                table: "lab_accession_counter",
                columns: new[] { "organization_id", "location_id", "day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_lab_order_counter",
                table: "lab_order_counter");

            migrationBuilder.DropPrimaryKey(
                name: "PK_lab_accession_counter",
                table: "lab_accession_counter");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "lab_order_counter");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "lab_accession_counter");

            migrationBuilder.AddPrimaryKey(
                name: "PK_lab_order_counter",
                table: "lab_order_counter",
                columns: new[] { "organization_id", "day" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_lab_accession_counter",
                table: "lab_accession_counter",
                columns: new[] { "organization_id", "day" });
        }
    }
}
