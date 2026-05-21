using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAllergyCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "allergy_catalog",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    display_es = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allergy_catalog", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allergy_active",
                table: "allergy_catalog",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_allergy_category",
                table: "allergy_catalog",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_allergy_display_trgm",
                table: "allergy_catalog",
                column: "display_es")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allergy_catalog");
        }
    }
}
