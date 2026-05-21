using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUnaccentAndDropIcd10 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Habilitar `unaccent` para que las columnas tsv (GENERATED) puedan
            // strippear acentos al construir el tsvector. La extension `pg_trgm`
            // ya fue habilitada en 20260428045454_AddFormsAndIcd10.
            // En cluster Postgres recién creado, `unaccent` requiere un rol con
            // permiso CREATE EXTENSION (típicamente superuser); en dev y staging
            // standard de IMed esto se cumple — ver runbook de Épica O.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");

            migrationBuilder.DropTable(
                name: "icd10_catalog");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "icd10_catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    chapter = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    display_es = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_icd10_catalog", x => x.code);
                });

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

            // No dropeamos `unaccent` en Down — futuras migraciones (ICD-11, LOINC)
            // dependen de la extension. Si alguien quiere revertir hasta antes de
            // esta migración, igual puede dejar la extension instalada (idempotente).
        }
    }
}
