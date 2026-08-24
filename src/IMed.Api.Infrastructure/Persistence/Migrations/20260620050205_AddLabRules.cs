using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_rule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    target_loinc = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_rule", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lab_rule_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lab_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    logic_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_rule_version", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rule_execution",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observation_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lab_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_version = table.Column<int>(type: "integer", nullable: false),
                    fired = table.Column<bool>(type: "boolean", nullable: false),
                    verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rule_execution", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lab_rule_lookup",
                table: "lab_rule",
                columns: new[] { "organization_id", "scope", "target_loinc", "active" });

            migrationBuilder.CreateIndex(
                name: "ux_lab_rule_org_code",
                table: "lab_rule",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_lab_rule_version_rule_version",
                table: "lab_rule_version",
                columns: new[] { "lab_rule_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rule_execution_observation",
                table: "rule_execution",
                column: "observation_fhir_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_rule");

            migrationBuilder.DropTable(
                name: "lab_rule_version");

            migrationBuilder.DropTable(
                name: "rule_execution");
        }
    }
}
