using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicationCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "medication_catalog",
                columns: table => new
                {
                    atc_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name_generic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_commercial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    presentation = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_catalog", x => x.atc_code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_medication_active",
                table: "medication_catalog",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_medication_category",
                table: "medication_catalog",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_medication_name_generic_trgm",
                table: "medication_catalog",
                column: "name_generic")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "medication_catalog");
        }
    }
}
