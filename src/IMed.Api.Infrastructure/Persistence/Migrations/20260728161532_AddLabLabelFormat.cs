using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabLabelFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_label_format",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    width_mm = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    height_mm = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    margin_mm = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    offset_xmm = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    offset_ymm = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_label_format", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lab_label_format_assignment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tube_type_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    specimen_type_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    lab_label_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_label_format_assignment", x => x.id);
                    table.ForeignKey(
                        name: "FK_lab_label_format_assignment_lab_label_format_lab_label_form~",
                        column: x => x.lab_label_format_id,
                        principalTable: "lab_label_format",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_lab_label_format_org_code",
                table: "lab_label_format",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lab_label_format_assignment_lab_label_format_id",
                table: "lab_label_format_assignment",
                column: "lab_label_format_id");

            migrationBuilder.CreateIndex(
                name: "ux_lab_label_assignment_org_specimen",
                table: "lab_label_format_assignment",
                columns: new[] { "organization_id", "specimen_type_code" },
                unique: true,
                filter: "specimen_type_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_lab_label_assignment_org_tube",
                table: "lab_label_format_assignment",
                columns: new[] { "organization_id", "tube_type_code" },
                unique: true,
                filter: "tube_type_code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_label_format_assignment");

            migrationBuilder.DropTable(
                name: "lab_label_format");
        }
    }
}
