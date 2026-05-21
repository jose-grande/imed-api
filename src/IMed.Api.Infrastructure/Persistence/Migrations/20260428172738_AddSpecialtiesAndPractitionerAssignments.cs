using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecialtiesAndPractitionerAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "specialty_catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    display_es = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    system = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specialty_catalog", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "user_practitioner_assignments",
                columns: table => new
                {
                    keycloak_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    practitioner_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    practitioner_role_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_practitioner_assignments", x => new { x.keycloak_user_id, x.organization_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_specialty_active",
                table: "specialty_catalog",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_specialty_display_trgm",
                table: "specialty_catalog",
                column: "display_es")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_user_practitioner_assignments_fhir_id",
                table: "user_practitioner_assignments",
                column: "practitioner_fhir_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "specialty_catalog");

            migrationBuilder.DropTable(
                name: "user_practitioner_assignments");
        }
    }
}
