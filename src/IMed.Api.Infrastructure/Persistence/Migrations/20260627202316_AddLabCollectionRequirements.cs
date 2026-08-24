using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabCollectionRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "capacity_ml",
                table: "tube_type",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "dead_volume_ml",
                table: "tube_type",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "analyzer_min_volume_ml",
                table: "lab_test",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "min_volume_ml",
                table: "lab_test",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "preferred_volume_ml",
                table: "lab_test",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "stability_minutes",
                table: "lab_test",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "specimen_rejection_reason",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requires_new_sample = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specimen_rejection_reason", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_specimen_rejection_reason_org_code",
                table: "specimen_rejection_reason",
                columns: new[] { "organization_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "specimen_rejection_reason");

            migrationBuilder.DropColumn(
                name: "capacity_ml",
                table: "tube_type");

            migrationBuilder.DropColumn(
                name: "dead_volume_ml",
                table: "tube_type");

            migrationBuilder.DropColumn(
                name: "analyzer_min_volume_ml",
                table: "lab_test");

            migrationBuilder.DropColumn(
                name: "min_volume_ml",
                table: "lab_test");

            migrationBuilder.DropColumn(
                name: "preferred_volume_ml",
                table: "lab_test");

            migrationBuilder.DropColumn(
                name: "stability_minutes",
                table: "lab_test");
        }
    }
}
