using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIcd11Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Wrapper IMMUTABLE para Postgres GENERATED columns con FTS+unaccent.
            //
            // Postgres rechaza GENERATED ALWAYS AS expressions que no sean IMMUTABLE.
            // Las funciones core que necesitamos (`to_tsvector(text,text)` y
            // `unaccent(text)`) son STABLE — su comportamiento depende del
            // search_path y de la dictionary `unaccent` instalada. Estrictamente
            // no son IMMUTABLE.
            //
            // Workaround estándar (documentado en docs Postgres FTS): un wrapper
            // marcado IMMUTABLE en SQL. Esto es seguro en la práctica porque:
            //   - La dictionary `unaccent` viene del package contrib y no muta.
            //   - El text search config 'spanish' es parte de la instalación.
            //   - No tenemos `ALTER TEXT SEARCH CONFIGURATION` en la pila.
            // Si en el futuro algo de eso cambia, hay que reindexar manualmente.
            //
            // Esta función es shared infra: la usan icd11_catalog y la usarán
            // loinc_catalog (Épica N3) y futuros catalogs con display ES+EN.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION imed_search_normalize_es(text, text)
                RETURNS tsvector
                LANGUAGE sql
                IMMUTABLE
                AS $$
                    SELECT to_tsvector('spanish'::regconfig, unaccent('unaccent'::regdictionary, $1 || ' ' || $2))
                $$;
                """);

            migrationBuilder.CreateTable(
                name: "icd11_catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display_es = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    display_en = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "imed_search_normalize_es(coalesce(display_es,''), coalesce(display_en,''))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_icd11_catalog", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_icd11_active",
                table: "icd11_catalog",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_icd11_code_trgm",
                table: "icd11_catalog",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_icd11_search_vector",
                table: "icd11_catalog",
                column: "tsv")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "icd11_catalog");

            // El wrapper se dropea solo si NADIE más lo está usando. Por las dudas,
            // CASCADE — si LOINC o algún catalog futuro lo agarró, se queda.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS imed_search_normalize_es(text, text);");
        }
    }
}
