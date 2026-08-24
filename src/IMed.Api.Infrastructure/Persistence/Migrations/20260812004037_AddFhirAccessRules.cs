using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFhirAccessRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fhir_access_rule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    verb = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    permission_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fhir_access_rule", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fhir_access_rule_resource_verb",
                table: "fhir_access_rule",
                columns: new[] { "resource_type", "verb" });

            migrationBuilder.CreateIndex(
                name: "ux_fhir_access_rule",
                table: "fhir_access_rule",
                columns: new[] { "organization_id", "resource_type", "verb", "permission_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fhir_access_rule");
        }
    }
}
