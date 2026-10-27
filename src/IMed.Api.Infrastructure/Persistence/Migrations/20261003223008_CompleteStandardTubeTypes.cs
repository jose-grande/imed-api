using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteStandardTubeTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Completa el conjunto estandar de tubos de venopuncion.
            //
            // Viene de la primera ronda de pruebas en staging: «nos hizo falta el tubo tapa
            // roja, no supimos donde crearlo», y lo dijeron todos los tecnicos. Tenian razon
            // en las dos mitades — el tubo no estaba, y no hay donde crearlo: `tube_type` es
            // un catalogo GLOBAL que solo se siembra desde el codigo (solo existe
            // GET /tube-types, sin POST ni PUT).
            //
            // Decision: el catalogo sigue siendo global y se completa la lista. Es el criterio
            // que ya regia —`CIT` estaba sembrado sin que existiera ninguna prueba de
            // coagulacion—, o sea que la lista es el conjunto estandar y no un derivado del
            // menu de pruebas.
            //
            // Va en migracion porque el seeder se saltea en cuanto existe catalogo global:
            // agregarlos solo alla no alcanza a ninguna base ya sembrada.
            //
            // Los volumenes son los habituales del mercado; cada laboratorio los ajusta a su
            // proveedor desde el catalogo.
            migrationBuilder.Sql("""
                INSERT INTO tube_type (
                    id, organization_id, code, name, color, additive,
                    volume_min_ml, capacity_ml, dead_volume_ml, active, created_at, updated_at)
                SELECT gen_random_uuid(), NULL, v.code, v.name, v.color, v.additive,
                       v.vol, v.cap, v.dead, TRUE, now(), now()
                  FROM (VALUES
                           ('FLUOR_GREY', 'Tubo fluoruro/oxalato (tapa gris)', 'gris', 'Fluoruro de sodio + oxalato de potasio', 2, 4, 0.3),
                           ('ESR_BLACK', 'Tubo para VSG (tapa negra)', 'negro', 'Citrato de sodio 3.8%', 1.28, 1.28, 0.2),
                           ('PST_LTGREEN', 'Tubo con gel separador de plasma (tapa verde claro)', 'verde claro', 'Heparina de litio + gel (PST)', 3, 4.5, 0.5),
                           ('EDTA_PINK', 'Tubo EDTA para banco de sangre (tapa rosada)', 'rosado', 'EDTA K2', 6, 6, 0.5),
                           ('TRACE_BLUE', 'Tubo para elementos traza (tapa azul real)', 'azul real', 'Libre de metales (sin aditivo o EDTA)', 6, 6, 0.5)
                       ) AS v(code, name, color, additive, vol, cap, dead)
                 WHERE NOT EXISTS (
                     SELECT 1 FROM tube_type t
                      WHERE t.code = v.code AND t.organization_id IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo los que ninguna prueba tenga asignados: quitarle el tubo a una prueba no
            // se nota hasta la toma de muestra.
            migrationBuilder.Sql("""
                DELETE FROM tube_type t
                 WHERE t.organization_id IS NULL
                   AND t.code IN ('FLUOR_GREY', 'ESR_BLACK', 'PST_LTGREEN', 'EDTA_PINK', 'TRACE_BLUE')
                   AND NOT EXISTS (SELECT 1 FROM lab_test l WHERE l.tube_type_id = t.id);
                """);
        }
    }
}
