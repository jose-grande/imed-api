using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagramDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "diagram_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    specialty_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    svg_content = table.Column<string>(type: "text", nullable: false),
                    zones_json = table.Column<string>(type: "jsonb", nullable: false),
                    annotation_style_json = table.Column<string>(type: "jsonb", nullable: false),
                    linked_form_codes_json = table.Column<string>(type: "jsonb", nullable: false),
                    zone_body_site_map_json = table.Column<string>(type: "jsonb", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagram_definitions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_diagram_definitions_org_specialty_status",
                table: "diagram_definitions",
                columns: new[] { "organization_id", "specialty_code", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_diagram_definitions_org_code_version",
                table: "diagram_definitions",
                columns: new[] { "organization_id", "code", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagram_definitions");
        }
    }
}
