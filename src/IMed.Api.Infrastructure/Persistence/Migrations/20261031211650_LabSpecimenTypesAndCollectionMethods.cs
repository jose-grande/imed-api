using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabSpecimenTypesAndCollectionMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Observación de staging: en el formulario de la prueba "tipo de recolección" era texto
            // libre y en "tipo de muestra" no estaban Suero ni Plasma, y "Sangre" debía decir
            // "Sangre total". Tipo de muestra = qué material se analiza; tipo de recolección = cómo
            // se obtiene (LabCollectionMethods).
            //
            // Todo es condicional a que el catálogo global ya esté sembrado. En una base nueva las
            // migraciones corren antes que LabCatalogSeeder, y el sembrador se saltea los tipos de
            // muestra en cuanto existe alguno global: insertar acá en una base vacía le cortaría
            // la siembra (la misma trampa que tuvieron los tubos). Ahí los pone el sembrador.

            // 1. "Sangre" → "Sangre total". Solo el nombre: el código BLOOD no cambia.
            migrationBuilder.Sql("""
                UPDATE specimen_type
                   SET name = 'Sangre total', updated_at = now()
                 WHERE organization_id IS NULL AND code = 'BLOOD' AND name = 'Sangre';
                """);

            // 2. Tipos de muestra nuevos (globales, como los demás).
            migrationBuilder.Sql("""
                INSERT INTO specimen_type (
                    id, organization_id, code, name, snomed_code, collection_mode, default_method,
                    default_body_site, container_kind, stability_minutes, discipline, active, created_at, updated_at)
                SELECT gen_random_uuid(), NULL, v.code, v.name, NULL, v.mode, v.method,
                       NULL, v.container, NULL, v.discipline, TRUE, now(), now()
                  FROM (VALUES
                      ('SERUM',     'Suero',             'OnSite',           'Venopunción',                      'Tube',             'Other'),
                      ('PLASMA',    'Plasma',            'OnSite',           'Venopunción',                      'Tube',             'Other'),
                      ('URINE_24H', 'Orina de 24 horas', 'PatientCollected', 'Recolección de orina de 24 horas', 'Cup',              'Urinalysis'),
                      ('SPUTUM',    'Esputo',            'PatientCollected', 'Expectoración',                    'SterileContainer', 'Microbiology'),
                      ('SECRETION', 'Secreciones',       'OnSite',           'Hisopado',                         'Swab',             'Microbiology')
                  ) AS v(code, name, mode, method, container, discipline)
                 WHERE EXISTS (SELECT 1 FROM specimen_type WHERE organization_id IS NULL)
                   AND NOT EXISTS (
                       SELECT 1 FROM specimen_type s WHERE s.organization_id IS NULL AND s.code = v.code);
                """);

            // 3. Tipo de recolección: de los valores internos de la siembra a la lista. Un texto
            //    libre que no está en la lista se deja: el formulario lo muestra como valor anterior
            //    y el servidor lo acepta mientras no se cambie.
            migrationBuilder.Sql("""
                UPDATE lab_test SET collection_type = 'Venopunción', updated_at = now()
                 WHERE collection_type = 'blood';
                UPDATE lab_test SET collection_type = 'Micción espontánea', updated_at = now()
                 WHERE collection_type IN ('urine', 'urine-spot');
                UPDATE lab_test SET collection_type = 'Recolección de orina de 24 horas', updated_at = now()
                 WHERE collection_type = 'urine-24h';
                """);

            // 4. Las pruebas en tubo con o sin gel (SST, tapa roja) analizan suero, no sangre total.
            //    Se pasan todas juntas porque comparten tubo: el plan de toma arma un tubo por
            //    tipo de muestra + tubo, y dejar unas en "Sangre total" y otras en "Suero" pediría
            //    dos tubos iguales. Las de EDTA (hemograma) siguen en sangre total.
            migrationBuilder.Sql("""
                UPDATE lab_test t
                   SET specimen_type_id = (SELECT id FROM specimen_type
                                            WHERE organization_id IS NULL AND code = 'SERUM'),
                       updated_at = now()
                  FROM specimen_type s, tube_type tt
                 WHERE s.id = t.specimen_type_id AND s.code = 'BLOOD'
                   AND tt.id = t.tube_type_id AND tt.code IN ('SST', 'SERUM_RED')
                   AND EXISTS (SELECT 1 FROM specimen_type WHERE organization_id IS NULL AND code = 'SERUM');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE lab_test t
                   SET specimen_type_id = (SELECT id FROM specimen_type
                                            WHERE organization_id IS NULL AND code = 'BLOOD'),
                       updated_at = now()
                  FROM specimen_type s
                 WHERE s.id = t.specimen_type_id AND s.code = 'SERUM' AND s.organization_id IS NULL;

                UPDATE lab_test SET collection_type = 'blood' WHERE collection_type = 'Venopunción';
                UPDATE lab_test SET collection_type = 'urine-spot' WHERE collection_type = 'Micción espontánea';
                UPDATE lab_test SET collection_type = 'urine-24h' WHERE collection_type = 'Recolección de orina de 24 horas';

                UPDATE specimen_type SET name = 'Sangre', updated_at = now()
                 WHERE organization_id IS NULL AND code = 'BLOOD' AND name = 'Sangre total';
                """);

            // Solo los que nada usa: una prueba o un contenedor apuntando a ellos quedaría colgado.
            migrationBuilder.Sql("""
                DELETE FROM specimen_type s
                 WHERE s.organization_id IS NULL
                   AND s.code IN ('SERUM', 'PLASMA', 'URINE_24H', 'SPUTUM', 'SECRETION')
                   AND NOT EXISTS (SELECT 1 FROM lab_test l WHERE l.specimen_type_id = s.id);
                """);
        }
    }
}
