using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabUnitsUcum : Migration
    {
        // Observación de staging: guardar el resultado de una TSH daba 500. La unidad de la prueba
        // y del rango era texto libre ("mIU/L", "10^3/uL", "mEq/L"…), HAPI valida las unidades
        // contra UCUM y rechazaba la Observation. Desde ahora la unidad es un código de la lista
        // fija LabUnits (UCUM: "m[IU]/L", "10*3/uL") y la pantalla muestra su etiqueta.
        //
        // Esta migración pasa los datos existentes (incluida la carga real del cliente) al código
        // UCUM. La comparación ignora espacios y mayúsculas y unifica µ/μ → u y × → x, así que
        // "mg/dl", "x10³/µL" o "UI/L" también se reconocen. Lo que no se reconoce SE DEJA COMO
        // ESTÁ: el catálogo lo marca como "unidad no válida" y no deja guardar la prueba hasta
        // elegir una de la lista, y la captura de resultados lo rechaza con un mensaje claro.
        // La consulta que lista lo que quedó sin reconocer está en ESTADO.md (rev 103): correrla
        // después de migrar y pasarle la lista al laboratorio.
        //
        // Las Observations ya guardadas en HAPI no se tocan: si se guardaron, su código ya era UCUM.

        private const string Map = """
            (VALUES
                    ('%', '%'),
                    ('/[hpf]', '/[HPF]'),
                    ('/[lpf]', '/[LPF]'),
                    ('/campo', '/[HPF]'),
                    ('/campo(altopoder)', '/[HPF]'),
                    ('/campo(bajopoder)', '/[LPF]'),
                    ('/cap', '/[HPF]'),
                    ('/cbp', '/[LPF]'),
                    ('/hpf', '/[HPF]'),
                    ('/lpf', '/[LPF]'),
                    ('/mm3', '/uL'),
                    ('/mm³', '/uL'),
                    ('/ul', '/uL'),
                    ('10*12/l', '10*12/L'),
                    ('10*3/ul', '10*3/uL'),
                    ('10*6/ul', '10*6/uL'),
                    ('10*9/l', '10*9/L'),
                    ('10^12/l', '10*12/L'),
                    ('10^3/mm3', '10*3/uL'),
                    ('10^3/ul', '10*3/uL'),
                    ('10^6/mm3', '10*6/uL'),
                    ('10^6/ul', '10*6/uL'),
                    ('10^9/l', '10*9/L'),
                    ('10e12/l', '10*12/L'),
                    ('10e3/ul', '10*3/uL'),
                    ('10e6/ul', '10*6/uL'),
                    ('10e9/l', '10*9/L'),
                    ('10³/mm3', '10*3/uL'),
                    ('10³/ul', '10*3/uL'),
                    ('10¹²/l', '10*12/L'),
                    ('10⁶/ul', '10*6/uL'),
                    ('10⁹/l', '10*9/L'),
                    ('[iu]/l', '[IU]/L'),
                    ('[iu]/ml', '[IU]/mL'),
                    ('[ph]', '[pH]'),
                    ('cel/mm3', '{cells}/uL'),
                    ('cel/ul', '{cells}/uL'),
                    ('cells/mm3', '{cells}/uL'),
                    ('cells/ul', '{cells}/uL'),
                    ('celulas/mm3', '{cells}/uL'),
                    ('celulas/ul', '{cells}/uL'),
                    ('copias/ml', '{copies}/mL'),
                    ('copies/ml', '{copies}/mL'),
                    ('células/mm3', '{cells}/uL'),
                    ('células/ul', '{cells}/uL'),
                    ('dil', '{titer}'),
                    ('dilución', '{titer}'),
                    ('fl', 'fL'),
                    ('g%', 'g/dL'),
                    ('g/(24.h)', 'g/(24.h)'),
                    ('g/24h', 'g/(24.h)'),
                    ('g/24horas', 'g/(24.h)'),
                    ('g/24hr', 'g/(24.h)'),
                    ('g/24hrs', 'g/(24.h)'),
                    ('g/dl', 'g/dL'),
                    ('g/l', 'g/L'),
                    ('gr/dl', 'g/dL'),
                    ('index', '{index}'),
                    ('indice', '{index}'),
                    ('inr', '{INR}'),
                    ('iu/l', '[IU]/L'),
                    ('iu/ml', '[IU]/mL'),
                    ('k/ul', '10*3/uL'),
                    ('k[iu]/l', 'k[IU]/L'),
                    ('kiu/l', 'k[IU]/L'),
                    ('kpa', 'kPa'),
                    ('ku/l', 'kU/L'),
                    ('kui/l', 'k[IU]/L'),
                    ('log10copias/ml', '{log_copies}/mL'),
                    ('log10copies/ml', '{log_copies}/mL'),
                    ('logcopias/ml', '{log_copies}/mL'),
                    ('logcopies/ml', '{log_copies}/mL'),
                    ('m/ul', '10*6/uL'),
                    ('m[iu]/l', 'm[IU]/L'),
                    ('m[iu]/ml', 'm[IU]/mL'),
                    ('mcg/dl', 'ug/dL'),
                    ('mcg/l', 'ug/L'),
                    ('mcg/ml', 'ug/mL'),
                    ('mcmol/l', 'umol/L'),
                    ('meq/l', 'meq/L'),
                    ('mg/(24.h)', 'mg/(24.h)'),
                    ('mg/24h', 'mg/(24.h)'),
                    ('mg/24horas', 'mg/(24.h)'),
                    ('mg/24hr', 'mg/(24.h)'),
                    ('mg/24hrs', 'mg/(24.h)'),
                    ('mg/dia', 'mg/(24.h)'),
                    ('mg%', 'mg/dL'),
                    ('mg/dl', 'mg/dL'),
                    ('mg/día', 'mg/(24.h)'),
                    ('mg/g', 'mg/g'),
                    ('mg/l', 'mg/L'),
                    ('min', 'min'),
                    ('mins', 'min'),
                    ('minutos', 'min'),
                    ('miu/l', 'm[IU]/L'),
                    ('miu/ml', 'm[IU]/mL'),
                    ('ml/(24.h)', 'mL/(24.h)'),
                    ('ml/24h', 'mL/(24.h)'),
                    ('ml/24horas', 'mL/(24.h)'),
                    ('ml/24hr', 'mL/(24.h)'),
                    ('ml/min/1,73m2', 'mL/min/{1.73_m2}'),
                    ('ml/min/1,73m²', 'mL/min/{1.73_m2}'),
                    ('ml/min/1.73', 'mL/min/{1.73_m2}'),
                    ('ml/min/1.73m2', 'mL/min/{1.73_m2}'),
                    ('ml/min/1.73m²', 'mL/min/{1.73_m2}'),
                    ('ml/min/{1.73_m2}', 'mL/min/{1.73_m2}'),
                    ('mm/1h', 'mm/h'),
                    ('mm/1hr', 'mm/h'),
                    ('mm/h', 'mm/h'),
                    ('mm/hora', 'mm/h'),
                    ('mm/hr', 'mm/h'),
                    ('mm[hg]', 'mm[Hg]'),
                    ('mmhg', 'mm[Hg]'),
                    ('mmol/l', 'mmol/L'),
                    ('mol/l', 'mol/L'),
                    ('mosm/kg', 'mosm/kg'),
                    ('mosm/kgh2o', 'mosm/kg'),
                    ('mui/l', 'm[IU]/L'),
                    ('mui/ml', 'm[IU]/mL'),
                    ('ng/dl', 'ng/dL'),
                    ('ng/l', 'ng/L'),
                    ('ng/ml', 'ng/mL'),
                    ('nmol/l', 'nmol/L'),
                    ('pg', 'pg'),
                    ('pg/ml', 'pg/mL'),
                    ('ph', '[pH]'),
                    ('pmol/l', 'pmol/L'),
                    ('porcampo', '/[HPF]'),
                    ('ratio', '{ratio}'),
                    ('razon', '{ratio}'),
                    ('razón', '{ratio}'),
                    ('s', 's'),
                    ('sec', 's'),
                    ('seg', 's'),
                    ('segs', 's'),
                    ('segundos', 's'),
                    ('titer', '{titer}'),
                    ('titulo', '{titer}'),
                    ('título', '{titer}'),
                    ('u/l', 'U/L'),
                    ('u/ml', 'U/mL'),
                    ('u[iu]/ml', 'u[IU]/mL'),
                    ('ug/dl', 'ug/dL'),
                    ('ug/l', 'ug/L'),
                    ('ug/ml', 'ug/mL'),
                    ('ui/l', '[IU]/L'),
                    ('ui/ml', '[IU]/mL'),
                    ('uiu/ml', 'u[IU]/mL'),
                    ('umol/l', 'umol/L'),
                    ('uui/ml', 'u[IU]/mL'),
                    ('x/campo', '/[HPF]'),
                    ('x10*3/ul', '10*3/uL'),
                    ('x10*6/ul', '10*6/uL'),
                    ('x10^12/l', '10*12/L'),
                    ('x10^3/mm3', '10*3/uL'),
                    ('x10^3/ul', '10*3/uL'),
                    ('x10^6/mm3', '10*6/uL'),
                    ('x10^6/ul', '10*6/uL'),
                    ('x10^9/l', '10*9/L'),
                    ('x10e3/ul', '10*3/uL'),
                    ('x10e6/ul', '10*6/uL'),
                    ('x10³/mm3', '10*3/uL'),
                    ('x10³/ul', '10*3/uL'),
                    ('x10¹²/l', '10*12/L'),
                    ('x10⁶/mm3', '10*6/uL'),
                    ('x10⁶/ul', '10*6/uL'),
                    ('x10⁹/l', '10*9/L'),
                    ('xcampo', '/[HPF]'),
                    ('xmm3', '/uL'),
                    ('{cells}/ul', '{cells}/uL'),
                    ('{copies}/ml', '{copies}/mL'),
                    ('{index}', '{index}'),
                    ('{inr}', '{INR}'),
                    ('{log_copies}/ml', '{log_copies}/mL'),
                    ('{ratio}', '{ratio}'),
                    ('{titer}', '{titer}'),
                    ('índice', '{index}')
            ) AS m(k, code)
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE lab_test t
                   SET unit = m.code, updated_at = now()
                  FROM {Map}
                 WHERE t.unit IS NOT NULL
                   AND lower(translate(regexp_replace(t.unit, '\s', '', 'g'), 'µμ×', 'uux')) = m.k
                   AND t.unit IS DISTINCT FROM m.code;
                """);

            migrationBuilder.Sql($"""
                UPDATE reference_range t
                   SET unit = m.code, updated_at = now()
                  FROM {Map}
                 WHERE t.unit IS NOT NULL
                   AND lower(translate(regexp_replace(t.unit, '\s', '', 'g'), 'µμ×', 'uux')) = m.k
                   AND t.unit IS DISTINCT FROM m.code;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Vuelve a la etiqueta (la forma de texto libre más parecida a la original). No es una
            // inversa exacta: "mg/dl" y "mg/dL" quedaron en el mismo código.
            const string Labels = """
                (VALUES
                    ('ug/dL', 'µg/dL'),
                    ('ug/L', 'µg/L'),
                    ('ug/mL', 'µg/mL'),
                    ('umol/L', 'µmol/L'),
                    ('meq/L', 'mEq/L'),
                    ('[IU]/L', 'IU/L'),
                    ('[IU]/mL', 'IU/mL'),
                    ('k[IU]/L', 'kIU/L'),
                    ('m[IU]/L', 'mIU/L'),
                    ('m[IU]/mL', 'mIU/mL'),
                    ('u[IU]/mL', 'µIU/mL'),
                    ('10*3/uL', '×10³/µL'),
                    ('10*6/uL', '×10⁶/µL'),
                    ('10*9/L', '×10⁹/L'),
                    ('10*12/L', '×10¹²/L'),
                    ('/uL', '/µL'),
                    ('{cells}/uL', 'células/µL'),
                    ('{ratio}', 'ratio'),
                    ('{index}', 'índice'),
                    ('{INR}', 'INR'),
                    ('mm[Hg]', 'mmHg'),
                    ('[pH]', 'pH'),
                    ('mL/min/{1.73_m2}', 'mL/min/1.73m²'),
                    ('mosm/kg', 'mOsm/kg'),
                    ('mg/(24.h)', 'mg/24h'),
                    ('g/(24.h)', 'g/24h'),
                    ('mL/(24.h)', 'mL/24h'),
                    ('/[HPF]', '/campo (alto poder)'),
                    ('/[LPF]', '/campo (bajo poder)'),
                    ('{copies}/mL', 'copias/mL'),
                    ('{log_copies}/mL', 'log10 copias/mL'),
                    ('{titer}', 'título')
                ) AS l(code, label)
                """;
            migrationBuilder.Sql($"""
                UPDATE lab_test t SET unit = l.label, updated_at = now()
                  FROM {Labels} WHERE t.unit = l.code;
                UPDATE reference_range t SET unit = l.label, updated_at = now()
                  FROM {Labels} WHERE t.unit = l.code;
                """);
        }
    }
}
