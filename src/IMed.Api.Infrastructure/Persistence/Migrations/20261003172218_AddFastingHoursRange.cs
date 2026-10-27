using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFastingHoursRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "fasting_hours",
                table: "lab_test",
                newName: "fasting_hours_min");

            migrationBuilder.AddColumn<int>(
                name: "fasting_hours_max",
                table: "lab_test",
                type: "integer",
                nullable: true);

            // Correccion del perfil lipidico, confirmada por un tecnico de laboratorio: pide
            // 9 a 12 h, no 12. Con un solo valor habia que elegir, y en 12 se rechazaba a quien
            // llegaba con 10 h de ayuno perfectamente validas.
            //
            // Va en la migracion porque el seeder NO puede corregirlo: se saltea en cuanto
            // existe catalogo global, asi que un cambio en los valores sembrados solo alcanza a
            // bases nuevas. Sin esto, toda organizacion ya aprovisionada se queda con «al menos
            // 12 h» — es decir, con el defecto que esta migracion viene a arreglar.
            //
            // Acotado a las filas que AUN tienen el valor anterior (12 sin maximo): si un
            // laboratorio ya ajusto su catalogo, su decision no se toca.
            migrationBuilder.Sql("""
                UPDATE lab_test
                   SET fasting_hours_min = 9,
                       fasting_hours_max = 12
                 WHERE code IN ('CHOL', 'HDL', 'LDL_CALC', 'TRIG')
                   AND fasting_hours_min = 12
                   AND fasting_hours_max IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Devolver el perfil lipidico a un solo valor: 12, que es lo que la columna
            // contenia antes de esta migracion.
            migrationBuilder.Sql("""
                UPDATE lab_test
                   SET fasting_hours_min = 12
                 WHERE code IN ('CHOL', 'HDL', 'LDL_CALC', 'TRIG')
                   AND fasting_hours_min = 9
                   AND fasting_hours_max = 12;
                """);

            migrationBuilder.DropColumn(
                name: "fasting_hours_max",
                table: "lab_test");

            migrationBuilder.RenameColumn(
                name: "fasting_hours_min",
                table: "lab_test",
                newName: "fasting_hours");
        }
    }
}
