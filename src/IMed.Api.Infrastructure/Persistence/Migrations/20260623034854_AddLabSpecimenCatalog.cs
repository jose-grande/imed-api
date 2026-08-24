using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabSpecimenCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "collection_mode",
                table: "lab_test",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "specimen_type_id",
                table: "lab_test",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tube_type_id",
                table: "lab_test",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "specimen_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    snomed_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    collection_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    default_method = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    default_body_site = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    container_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stability_minutes = table.Column<int>(type: "integer", nullable: true),
                    discipline = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specimen_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tube_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    color = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    additive = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    volume_min_ml = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tube_type", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_specimen_type_org_code",
                table: "specimen_type",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tube_type_org_code",
                table: "tube_type",
                columns: new[] { "organization_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "specimen_type");

            migrationBuilder.DropTable(
                name: "tube_type");

            migrationBuilder.DropColumn(
                name: "collection_mode",
                table: "lab_test");

            migrationBuilder.DropColumn(
                name: "specimen_type_id",
                table: "lab_test");

            migrationBuilder.DropColumn(
                name: "tube_type_id",
                table: "lab_test");
        }
    }
}
