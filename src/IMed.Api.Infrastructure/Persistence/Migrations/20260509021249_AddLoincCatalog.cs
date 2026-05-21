using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoincCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loinc_catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display_es = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: false),
                    display_en = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    properties = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "imed_search_normalize_es(coalesce(display_es,''), coalesce(display_en,''))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loinc_catalog", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_loinc_active",
                table: "loinc_catalog",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_loinc_code_trgm",
                table: "loinc_catalog",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_loinc_search_vector",
                table: "loinc_catalog",
                column: "tsv")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loinc_catalog");
        }
    }
}
