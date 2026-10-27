using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRedTopTubeType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tubo de suero sin gel (tapa roja). Reportado por los tecnicos en la primera
            // ronda de pruebas en staging: «nos hizo falta el tubo tapa roja, no supimos
            // donde crearlo» — y tenian razon en las dos mitades. No estaba en el catalogo,
            // y no hay forma de crearlo desde la aplicacion: `tube_type` es un catalogo
            // global que solo se siembra desde el codigo (solo existe GET /tube-types).
            //
            // Va en una migracion y no solo en el seeder porque el seeder se saltea en
            // cuanto existe catalogo global, asi que agregarlo alla no alcanza a ninguna
            // base ya sembrada — la misma leccion de AddFastingHoursRange.
            //
            // Es global (organization_id NULL) porque los tubos son referencia compartida:
            // el aprovisionamiento por organizacion conserva sus ids en vez de clonarlos.
            migrationBuilder.Sql("""
                INSERT INTO tube_type (
                    id, organization_id, code, name, color, additive,
                    volume_min_ml, capacity_ml, dead_volume_ml, active, created_at, updated_at)
                SELECT gen_random_uuid(), NULL, 'SERUM_RED',
                       'Tubo de suero sin gel (tapa roja)', 'rojo',
                       'Activador de coagulo (sin gel)',
                       4, 6, 0.5, TRUE, now(), now()
                 WHERE NOT EXISTS (
                     SELECT 1 FROM tube_type
                      WHERE code = 'SERUM_RED' AND organization_id IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo si ninguna prueba lo tiene asignado: borrarlo con pruebas apuntando a el
            // las dejaria sin tubo, y eso no se nota hasta la toma de muestra.
            migrationBuilder.Sql("""
                DELETE FROM tube_type t
                 WHERE t.code = 'SERUM_RED'
                   AND t.organization_id IS NULL
                   AND NOT EXISTS (SELECT 1 FROM lab_test l WHERE l.tube_type_id = t.id);
                """);
        }
    }
}
