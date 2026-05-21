using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFormsAndIcd10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Garantiza pg_trgm — necesaria para el índice GIN sobre
            // icd10_catalog.display_es. Idempotente.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateTable(
                name: "form_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    specialty_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    schema_json = table.Column<string>(type: "jsonb", nullable: false),
                    fhir_mapping_json = table.Column<string>(type: "jsonb", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "icd10_catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    display_es = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    chapter = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_icd10_catalog", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_form_definitions_org_specialty_status",
                table: "form_definitions",
                columns: new[] { "organization_id", "specialty_code", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_form_definitions_org_code_version",
                table: "form_definitions",
                columns: new[] { "organization_id", "code", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_icd10_active",
                table: "icd10_catalog",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_icd10_display_trgm",
                table: "icd10_catalog",
                column: "display_es")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "form_definitions");

            migrationBuilder.DropTable(
                name: "icd10_catalog");
        }
    }
}
