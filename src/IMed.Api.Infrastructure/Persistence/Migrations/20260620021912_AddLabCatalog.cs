using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "derived_calculation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    output_test_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    formula_json = table.Column<string>(type: "jsonb", nullable: false),
                    inputs_json = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_derived_calculation", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lab_panel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_panel", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lab_panel_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lab_panel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_panel_version", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lab_panel_version_item",
                columns: table => new
                {
                    lab_panel_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lab_test_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_panel_version_item", x => new { x.lab_panel_version_id, x.lab_test_id });
                });

            migrationBuilder.CreateTable(
                name: "lab_test",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    loinc_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    datatype = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    department = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fasting_hours = table.Column<int>(type: "integer", nullable: true),
                    collection_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    patient_instruction = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_test", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lab_test_offering",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lab_test_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offered = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_test_offering", x => new { x.organization_id, x.lab_test_id });
                });

            migrationBuilder.CreateTable(
                name: "reference_range",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lab_test_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sex = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    age_min_years = table.Column<int>(type: "integer", nullable: true),
                    age_max_years = table.Column<int>(type: "integer", nullable: true),
                    pregnancy = table.Column<bool>(type: "boolean", nullable: true),
                    method = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    high = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    critical_low = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    critical_high = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reference_range", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tat_target",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    target_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tat_target", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_derived_calculation_output",
                table: "derived_calculation",
                column: "output_test_code");

            migrationBuilder.CreateIndex(
                name: "ix_lab_panel_name_trgm",
                table: "lab_panel",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_lab_panel_org_code",
                table: "lab_panel",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_lab_panel_version_panel_version",
                table: "lab_panel_version",
                columns: new[] { "lab_panel_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lab_panel_version_item_version",
                table: "lab_panel_version_item",
                column: "lab_panel_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_lab_test_name_trgm",
                table: "lab_test",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_lab_test_org_active",
                table: "lab_test",
                columns: new[] { "organization_id", "active" });

            migrationBuilder.CreateIndex(
                name: "ux_lab_test_org_code",
                table: "lab_test",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reference_range_test",
                table: "reference_range",
                column: "lab_test_id");

            migrationBuilder.CreateIndex(
                name: "ix_tat_target_scope_code_priority",
                table: "tat_target",
                columns: new[] { "scope", "target_code", "priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "derived_calculation");

            migrationBuilder.DropTable(
                name: "lab_panel");

            migrationBuilder.DropTable(
                name: "lab_panel_version");

            migrationBuilder.DropTable(
                name: "lab_panel_version_item");

            migrationBuilder.DropTable(
                name: "lab_test");

            migrationBuilder.DropTable(
                name: "lab_test_offering");

            migrationBuilder.DropTable(
                name: "reference_range");

            migrationBuilder.DropTable(
                name: "tat_target");
        }
    }
}
