using IMed.Api.Domain.Catalogs.Lab;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seed mock HN del catálogo de laboratorio (Épica LIS-1, L1-F2). Globales
/// (<c>organization_id = NULL</c>) = plantillas que una org puede sobrescribir
/// (L1-D1). Estructura import-ready: cuando el cliente entregue paneles/rangos/
/// críticos reales, reemplazar este seeder por un importer del CSV correspondiente.
///
/// <para><b>Datos de DEMOSTRACIÓN</b> — rangos y críticos son valores de
/// referencia estándar, NO validados clínicamente para la población local. La UI
/// muestra un banner "datos de demostración" hasta que se importen los reales.</para>
/// </summary>
public sealed class LabCatalogSeeder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<LabCatalogSeeder> logger) : IHostedService
{
    // (code, name, datatype, loinc, unit, department, fastingHours, collectionType)
    private static readonly (string Code, string Name, LabTestDatatype Type, string? Loinc, string? Unit, LabDepartment Dept, int? Fasting, string? Collection)[] Tests =
    [
        // Hematología (CBC)
        ("WBC", "Leucocitos (WBC)", LabTestDatatype.Numeric, "6690-2", "10^3/uL", LabDepartment.Hematology, null, "blood"),
        ("RBC", "Eritrocitos (RBC)", LabTestDatatype.Numeric, "789-8", "10^6/uL", LabDepartment.Hematology, null, "blood"),
        ("HGB", "Hemoglobina", LabTestDatatype.Numeric, "718-7", "g/dL", LabDepartment.Hematology, null, "blood"),
        ("HCT", "Hematocrito", LabTestDatatype.Numeric, "4544-3", "%", LabDepartment.Hematology, null, "blood"),
        ("MCV", "Volumen corpuscular medio (VCM)", LabTestDatatype.Numeric, "787-2", "fL", LabDepartment.Hematology, null, "blood"),
        ("MCH", "Hemoglobina corpuscular media (HCM)", LabTestDatatype.Numeric, "785-6", "pg", LabDepartment.Hematology, null, "blood"),
        ("MCHC", "Concentración HCM (CHCM)", LabTestDatatype.Numeric, "786-4", "g/dL", LabDepartment.Hematology, null, "blood"),
        ("PLT", "Plaquetas", LabTestDatatype.Numeric, "777-3", "10^3/uL", LabDepartment.Hematology, null, "blood"),
        ("NEU", "Neutrófilos %", LabTestDatatype.Numeric, "770-8", "%", LabDepartment.Hematology, null, "blood"),
        ("LYM", "Linfocitos %", LabTestDatatype.Numeric, "736-9", "%", LabDepartment.Hematology, null, "blood"),

        // Química clínica
        ("GLU", "Glucosa", LabTestDatatype.Numeric, "2345-7", "mg/dL", LabDepartment.Chemistry, null, "blood"),
        ("GLU_FAST", "Glucosa en ayunas", LabTestDatatype.Numeric, "1558-6", "mg/dL", LabDepartment.Chemistry, 8, "blood"),
        ("BUN", "Nitrógeno ureico (BUN)", LabTestDatatype.Numeric, "3094-0", "mg/dL", LabDepartment.Chemistry, null, "blood"),
        ("CREAT", "Creatinina", LabTestDatatype.Numeric, "2160-0", "mg/dL", LabDepartment.Chemistry, null, "blood"),
        ("NA", "Sodio", LabTestDatatype.Numeric, "2951-2", "mmol/L", LabDepartment.Chemistry, null, "blood"),
        ("K", "Potasio", LabTestDatatype.Numeric, "2823-3", "mmol/L", LabDepartment.Chemistry, null, "blood"),
        ("CL", "Cloro", LabTestDatatype.Numeric, "2075-0", "mmol/L", LabDepartment.Chemistry, null, "blood"),
        ("CO2", "Bicarbonato (CO2)", LabTestDatatype.Numeric, "1963-8", "mmol/L", LabDepartment.Chemistry, null, "blood"),
        ("CA", "Calcio", LabTestDatatype.Numeric, "17861-6", "mg/dL", LabDepartment.Chemistry, null, "blood"),
        ("ALB", "Albúmina", LabTestDatatype.Numeric, "1751-7", "g/dL", LabDepartment.Chemistry, null, "blood"),
        ("TP", "Proteína total", LabTestDatatype.Numeric, "2885-2", "g/dL", LabDepartment.Chemistry, null, "blood"),
        ("ALT", "Alanina aminotransferasa (ALT)", LabTestDatatype.Numeric, "1742-6", "U/L", LabDepartment.Chemistry, null, "blood"),
        ("AST", "Aspartato aminotransferasa (AST)", LabTestDatatype.Numeric, "1920-8", "U/L", LabDepartment.Chemistry, null, "blood"),
        ("TBIL", "Bilirrubina total", LabTestDatatype.Numeric, "1975-2", "mg/dL", LabDepartment.Chemistry, null, "blood"),
        ("ALP", "Fosfatasa alcalina", LabTestDatatype.Numeric, "6768-6", "U/L", LabDepartment.Chemistry, null, "blood"),

        // Perfil lipídico (LDL calculado)
        ("CHOL", "Colesterol total", LabTestDatatype.Numeric, "2093-3", "mg/dL", LabDepartment.Chemistry, 12, "blood"),
        ("HDL", "Colesterol HDL", LabTestDatatype.Numeric, "2085-9", "mg/dL", LabDepartment.Chemistry, 12, "blood"),
        ("LDL_CALC", "Colesterol LDL (calculado)", LabTestDatatype.Numeric, "13457-7", "mg/dL", LabDepartment.Chemistry, 12, "blood"),
        ("TRIG", "Triglicéridos", LabTestDatatype.Numeric, "2571-8", "mg/dL", LabDepartment.Chemistry, 12, "blood"),

        // Perfil tiroideo
        ("TSH", "Hormona estimulante de tiroides (TSH)", LabTestDatatype.Numeric, "3016-3", "mIU/L", LabDepartment.Immunology, null, "blood"),
        ("FT4", "Tiroxina libre (T4 libre)", LabTestDatatype.Numeric, "3024-7", "ng/dL", LabDepartment.Immunology, null, "blood"),
        ("T3", "Triyodotironina (T3)", LabTestDatatype.Numeric, "3053-6", "ng/dL", LabDepartment.Immunology, null, "blood"),

        // Examen general de orina
        ("UA_COLOR", "Color (orina)", LabTestDatatype.Text, "5778-6", null, LabDepartment.Urinalysis, null, "urine-spot"),
        ("UA_PH", "pH (orina)", LabTestDatatype.Numeric, "5803-2", null, LabDepartment.Urinalysis, null, "urine-spot"),
        ("UA_SG", "Densidad (orina)", LabTestDatatype.Numeric, "5811-5", null, LabDepartment.Urinalysis, null, "urine-spot"),
        ("UA_GLU", "Glucosa (orina)", LabTestDatatype.Coded, "5792-7", null, LabDepartment.Urinalysis, null, "urine-spot"),
        ("UA_PROT", "Proteína (orina)", LabTestDatatype.Coded, "5804-0", null, LabDepartment.Urinalysis, null, "urine-spot"),
    ];

    // (panelCode, name, description, testCodes)
    private static readonly (string Code, string Name, string Description, string[] TestCodes)[] Panels =
    [
        ("CBC", "Hemograma completo (Biometría hemática)", "Evaluación de glóbulos rojos, blancos y plaquetas.",
            ["WBC", "RBC", "HGB", "HCT", "MCV", "MCH", "MCHC", "PLT", "NEU", "LYM"]),
        ("BMP", "Panel metabólico básico", "Glucosa, electrolitos y función renal.",
            ["GLU", "BUN", "CREAT", "NA", "K", "CL", "CO2", "CA"]),
        ("CMP", "Panel metabólico completo", "BMP + función hepática y proteínas.",
            ["GLU", "BUN", "CREAT", "NA", "K", "CL", "CO2", "CA", "ALB", "TP", "ALT", "AST", "TBIL", "ALP"]),
        ("LIPID", "Perfil lipídico", "Colesterol total, HDL, LDL (calculado) y triglicéridos.",
            ["CHOL", "HDL", "LDL_CALC", "TRIG"]),
        ("THYROID", "Perfil tiroideo", "TSH, T4 libre y T3.",
            ["TSH", "FT4", "T3"]),
        ("GLU_FAST", "Glucosa en ayunas", "Glucosa plasmática en ayuno de 8 horas.",
            ["GLU_FAST"]),
        ("UA", "Examen general de orina", "Tira reactiva + características físicas.",
            ["UA_COLOR", "UA_PH", "UA_SG", "UA_GLU", "UA_PROT"]),
    ];

    // (testCode, sex, ageMin, ageMax, low, high, critLow, critHigh, unit)
    private static readonly (string Test, ReferenceSex Sex, int? AgeMin, int? AgeMax, decimal? Low, decimal? High, decimal? CritLow, decimal? CritHigh, string? Unit)[] Ranges =
    [
        ("GLU", ReferenceSex.Any, 18, null, 70m, 99m, 40m, 500m, "mg/dL"),
        ("GLU_FAST", ReferenceSex.Any, 18, null, 70m, 99m, 40m, 500m, "mg/dL"),
        ("HGB", ReferenceSex.Male, 18, null, 13.5m, 17.5m, 6m, 20m, "g/dL"),
        ("HGB", ReferenceSex.Female, 18, null, 12.0m, 15.5m, 6m, 20m, "g/dL"),
        ("HCT", ReferenceSex.Male, 18, null, 41m, 53m, null, null, "%"),
        ("HCT", ReferenceSex.Female, 18, null, 36m, 46m, null, null, "%"),
        ("K", ReferenceSex.Any, 18, null, 3.5m, 5.2m, 2.5m, 6.5m, "mmol/L"),
        ("NA", ReferenceSex.Any, 18, null, 135m, 145m, 120m, 160m, "mmol/L"),
        ("CREAT", ReferenceSex.Any, 18, null, 0.6m, 1.3m, null, 7m, "mg/dL"),
        ("PLT", ReferenceSex.Any, 18, null, 150m, 450m, 20m, 1000m, "10^3/uL"),
        ("WBC", ReferenceSex.Any, 18, null, 4.5m, 11.0m, 1m, 50m, "10^3/uL"),
        ("CA", ReferenceSex.Any, 18, null, 8.5m, 10.5m, 6m, 13m, "mg/dL"),
        ("BUN", ReferenceSex.Any, 18, null, 7m, 20m, null, null, "mg/dL"),
        ("ALT", ReferenceSex.Any, 18, null, 7m, 56m, null, null, "U/L"),
        ("AST", ReferenceSex.Any, 18, null, 10m, 40m, null, null, "U/L"),
        ("TSH", ReferenceSex.Any, 18, null, 0.4m, 4.0m, null, null, "mIU/L"),
        ("CHOL", ReferenceSex.Any, 18, null, null, 200m, null, null, "mg/dL"),
        ("TRIG", ReferenceSex.Any, 18, null, null, 150m, null, null, "mg/dL"),
        ("HDL", ReferenceSex.Any, 18, null, 40m, null, null, null, "mg/dL"),
    ];

    // (outputTestCode, name, formulaJson, inputsJson)
    private static readonly (string Output, string Name, string Formula, string Inputs)[] Derived =
    [
        ("LDL_CALC", "LDL (Friedewald)",
            "{\"-\":[{\"var\":\"CHOL\"},{\"var\":\"HDL\"},{\"/\":[{\"var\":\"TRIG\"},5]}]}",
            "[\"CHOL\",\"HDL\",\"TRIG\"]"),
        ("ANION_GAP", "Anion Gap",
            "{\"-\":[{\"+\":[{\"var\":\"NA\"},{\"var\":\"K\"}]},{\"+\":[{\"var\":\"CL\"},{\"var\":\"CO2\"}]}]}",
            "[\"NA\",\"K\",\"CL\",\"CO2\"]"),
        ("EGFR", "eGFR (CKD-EPI 2021)",
            "{\"formula\":\"ckd-epi-2021\"}",
            "[\"CREAT\",\"_age\",\"_sex\"]"),
    ];

    // (scope, targetCode, priority, minutes)
    private static readonly (TatScope Scope, string Code, LabRequestPriority Priority, int Minutes)[] TatTargets =
    [
        (TatScope.Panel, "CBC", LabRequestPriority.Routine, 60),
        (TatScope.Panel, "BMP", LabRequestPriority.Routine, 60),
        (TatScope.Panel, "CMP", LabRequestPriority.Routine, 90),
        (TatScope.Panel, "LIPID", LabRequestPriority.Routine, 120),
        (TatScope.Panel, "THYROID", LabRequestPriority.Routine, 240),
        (TatScope.Test, "GLU_FAST", LabRequestPriority.Routine, 60),
        (TatScope.Test, "GLU_FAST", LabRequestPriority.Urgent, 30),
        (TatScope.Test, "K", LabRequestPriority.Stat, 30),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            bool hasGlobals;
            try
            {
                hasGlobals = await db.LabTests.AnyAsync(t => t.OrganizationId == null, cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tablas del catálogo de lab aún no existen; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                hasGlobals = await db.LabTests.AnyAsync(t => t.OrganizationId == null, cancellationToken);
            }

            if (hasGlobals)
            {
                logger.LogDebug("Catálogo de laboratorio global ya sembrado; skip seed.");
                return;
            }

            var effectiveDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

            // 1) Pruebas — mapa code → entity para resolver Ids en paneles/rangos.
            var testsByCode = new Dictionary<string, LabTestDefinition>(StringComparer.Ordinal);
            foreach (var t in Tests)
            {
                var test = LabTestDefinition.Create(
                    t.Code, t.Name, t.Type, t.Loinc, t.Unit, t.Dept, t.Fasting, t.Collection,
                    timeProvider: timeProvider);
                testsByCode[t.Code] = test;
            }
            db.LabTests.AddRange(testsByCode.Values);

            // 2) Paneles + versión v1 activa + items.
            foreach (var p in Panels)
            {
                var panel = LabPanel.Create(p.Code, p.Name, p.Description, timeProvider: timeProvider);
                db.LabPanels.Add(panel);

                var version = LabPanelVersion.Create(panel.Id, 1, effectiveDate, PanelVersionStatus.Active, timeProvider);
                db.LabPanelVersions.Add(version);

                var order = 0;
                foreach (var code in p.TestCodes)
                {
                    if (!testsByCode.TryGetValue(code, out var test))
                    {
                        logger.LogWarning("Panel {Panel} referencia prueba inexistente {Code}; se omite.", p.Code, code);
                        continue;
                    }
                    db.LabPanelVersionItems.Add(LabPanelVersionItem.Create(version.Id, test.Id, order++));
                }
            }

            // 3) Rangos de referencia (con críticos embebidos).
            foreach (var r in Ranges)
            {
                if (!testsByCode.TryGetValue(r.Test, out var test))
                {
                    continue;
                }
                db.ReferenceRanges.Add(ReferenceRange.Create(
                    test.Id, r.Sex, r.AgeMin, r.AgeMax, pregnancy: null, method: null,
                    low: r.Low, high: r.High, criticalLow: r.CritLow, criticalHigh: r.CritHigh,
                    unit: r.Unit, timeProvider: timeProvider));
            }

            // 4) Fórmulas calculadas.
            foreach (var d in Derived)
            {
                db.DerivedCalculations.Add(DerivedCalculation.Create(
                    d.Output, d.Name, d.Formula, d.Inputs, timeProvider: timeProvider));
            }

            // 5) TAT targets.
            foreach (var tt in TatTargets)
            {
                db.TatTargets.Add(TatTarget.Create(tt.Scope, tt.Code, tt.Priority, tt.Minutes, timeProvider: timeProvider));
            }

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Lab catalog seed cargado: {Tests} pruebas, {Panels} paneles, {Ranges} rangos, {Derived} fórmulas, {Tat} TAT targets.",
                testsByCode.Count, Panels.Length, Ranges.Length, Derived.Length, TatTargets.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LabCatalogSeeder falló; el catálogo de laboratorio puede estar incompleto.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
