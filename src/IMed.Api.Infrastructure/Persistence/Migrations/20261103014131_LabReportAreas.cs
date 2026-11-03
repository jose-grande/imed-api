using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabReportAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subsection",
                table: "lab_test",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            // Observación de staging: el informe se divide por áreas y algunas se subdividen
            // (Hematología en series, Uroanálisis y Parasitología en examen físico/químico/
            // microscópico). Las pruebas que sembramos reciben su subárea, en el catálogo global y
            // en las copias de cada organización (mismo código). Misma tabla que
            // LabCatalogSeeder.Subsections. Solo si el área sigue siendo la sembrada y la prueba no
            // tiene subárea: lo que el laboratorio cambió no se pisa. Las pruebas que cargó el
            // cliente quedan sin subárea y se imprimen directo bajo su área hasta que las clasifique.
            migrationBuilder.Sql("""
                UPDATE lab_test t
                   SET subsection = m.subsection, updated_at = now()
                  FROM (VALUES
                      ('RBC', 'Hematology', 'HEM_RED'), ('HGB', 'Hematology', 'HEM_RED'),
                      ('HCT', 'Hematology', 'HEM_RED'), ('MCV', 'Hematology', 'HEM_RED'),
                      ('MCH', 'Hematology', 'HEM_RED'), ('MCHC', 'Hematology', 'HEM_RED'),
                      ('WBC', 'Hematology', 'HEM_WHITE'), ('NEU', 'Hematology', 'HEM_WHITE'),
                      ('LYM', 'Hematology', 'HEM_WHITE'),
                      ('PLT', 'Hematology', 'HEM_PLATELET'),
                      ('UA_COLOR', 'Urinalysis', 'UA_PHYSICAL'),
                      ('UA_PH', 'Urinalysis', 'UA_CHEMICAL'), ('UA_SG', 'Urinalysis', 'UA_CHEMICAL'),
                      ('UA_GLU', 'Urinalysis', 'UA_CHEMICAL'), ('UA_PROT', 'Urinalysis', 'UA_CHEMICAL')
                  ) AS m(code, department, subsection)
                 WHERE t.code = m.code AND t.department = m.department AND t.subsection IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subsection",
                table: "lab_test");
        }
    }
}
