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

    // LOINC de panel cuando existe (dual coding, LIS-RD-22). Los demás quedan solo con código local.
    private static readonly Dictionary<string, string> PanelLoinc = new(StringComparer.Ordinal)
    {
        ["CBC"] = "58410-2",
        ["BMP"] = "51990-0",
        ["CMP"] = "24323-8",
        ["LIPID"] = "57698-3",
    };

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

    // (code, name, collectionMode, method?, bodySite?, container, stabilityMin?, discipline) — LIS-RD-26
    private static readonly (string Code, string Name, CollectionMode Mode, string? Method, string? BodySite, ContainerKind Container, int? Stability, LabDepartment Discipline)[] SpecimenTypes =
    [
        ("BLOOD", "Sangre", CollectionMode.OnSite, "Venopunción", null, ContainerKind.Tube, null, LabDepartment.Other),
        ("URINE", "Orina", CollectionMode.PatientCollected, null, null, ContainerKind.Cup, 120, LabDepartment.Urinalysis),
        ("SWAB", "Hisopado", CollectionMode.OnSite, "Hisopado", null, ContainerKind.Swab, null, LabDepartment.Microbiology),
        ("SEMEN", "Semen (espermograma)", CollectionMode.OnSite, "Masturbación", null, ContainerKind.Cup, 60, LabDepartment.Other),
        ("SALIVA", "Saliva", CollectionMode.PatientCollected, null, null, ContainerKind.Cup, null, LabDepartment.Other),
        ("STOOL", "Heces", CollectionMode.PatientCollected, null, null, ContainerKind.SterileContainer, null, LabDepartment.Microbiology),
        ("CSF", "Líquido cefalorraquídeo (LCR)", CollectionMode.OnSite, "Punción lumbar", null, ContainerKind.SterileContainer, 60, LabDepartment.Other),
        ("TISSUE", "Tejido (biopsia)", CollectionMode.OnSite, "Biopsia", null, ContainerKind.SterileContainer, null, LabDepartment.Other),
    ];

    // (code, name, color, additive, volumeMinMl, capacityMl, deadVolumeMl) — LIS-RD-16 + R6-F1a
    private static readonly (string Code, string Name, string Color, string Additive, decimal? Vol, decimal? Cap, decimal? Dead)[] TubeTypes =
    [
        ("EDTA", "Tubo EDTA (tapa lila)", "lila", "EDTA K2/K3", 3m, 4m, 0.5m),
        ("SST", "Tubo con gel separador (tapa dorada)", "dorado", "Activador de coágulo + gel (SST)", 5m, 5m, 0.5m),
        ("CIT", "Tubo citrato (tapa celeste)", "celeste", "Citrato de sodio 3.2%", 2.7m, 2.7m, 0.3m),
        ("HEP", "Tubo heparina (tapa verde)", "verde", "Heparina de litio", 4m, 5m, 0.5m),
    ];

    // (code, name, requiresNewSample, sortOrder) — R6-F1a, lista completa decisión UX §8.7
    private static readonly (string Code, string Name, bool RequiresNew, int Sort)[] RejectionReasons =
    [
        ("WRONG_CONTAINER", "Tubo/contenedor incorrecto", true, 1),
        ("LABEL_ILLEGIBLE", "Etiqueta ilegible", true, 2),
        ("LABEL_MISMATCH", "Etiqueta no coincide con el paciente/orden", true, 3),
        ("INSUFFICIENT_VOLUME", "Volumen/cantidad insuficiente", true, 4),
        ("HEMOLYSIS", "Hemólisis", true, 5),
        ("CLOTTED", "Muestra coagulada", true, 6),
        ("BROKEN_CONTAINER", "Recipiente roto o derramado", true, 7),
        ("EXPIRED", "Muestra vencida (fuera de estabilidad)", true, 8),
        ("TEMPERATURE_TRANSPORT", "Temperatura/traslado fuera de rango", true, 9),
    ];

    /// <summary>
    /// Motivos por los que un validador devuelve un informe al técnico. A diferencia de los de
    /// arriba —que son preanalíticos, la muestra no sirve— estos son analíticos: la muestra
    /// sirvió y el resultado no convence. Solo algunos obligan a una muestra nueva; la mayoría
    /// se resuelve repitiendo la corrida o corrigiendo la transcripción.
    /// </summary>
    private static readonly (string Code, string Name, string Description, bool RequiresNew, int Sort)[]
        ValidationRejectionReasons =
    [
        ("RERUN_REQUIRED", "Repetir la corrida",
            "El valor no es plausible o el equipo dio un comportamiento anómalo; repetir sobre la misma muestra.", false, 1),
        ("TRANSCRIPTION_SUSPECTED", "Posible error de transcripción",
            "El valor parece mal digitado (orden de magnitud, coma decimal).", false, 2),
        ("DELTA_UNEXPLAINED", "Variación contra el previo sin explicación",
            "El delta contra el resultado anterior no se explica por la clínica del paciente.", false, 3),
        ("INCONSISTENT_PANEL", "Inconsistente con el resto del panel",
            "El valor no es coherente con los demás analitos del mismo informe.", false, 4),
        ("QC_FAILED", "Control de calidad no conforme",
            "La corrida se hizo con un QC fuera de rango.", false, 5),
        ("INSTRUMENT_ERROR", "Falla del analizador",
            "El equipo reportó error o estaba fuera de calibración.", false, 6),
        ("NEW_SAMPLE_REQUIRED", "Requiere muestra nueva",
            "La muestra ya no sirve para repetir: hay que volver a tomar.", true, 7),
        ("MISSING_ANALYTE", "Faltan analitos del panel",
            "El informe está incompleto respecto de lo solicitado.", false, 8),
    ];

    // (code, name, widthMm, heightMm, marginMm, isDefault, sortOrder) — R6-F4b.
    // Tamaños estándar de la industria de etiquetas térmicas: el laboratorio los edita
    // o agrega los suyos según el rollo que compre (Zebra, Brother, TSC…).
    private static readonly (string Code, string Name, decimal W, decimal H, decimal Margin, bool Default, int Sort)[] LabelFormats =
    [
        ("TUBE-51X25", "Tubo estándar — 51 × 25 mm (2\" × 1\")", 51m, 25m, 1.5m, true, 1),
        ("TUBE-76X25", "Tubo wrap / largo — 76 × 25 mm (3\" × 1\")", 76m, 25m, 1.5m, false, 2),
        ("MICRO-38X19", "Microtainer pediátrico — 38 × 19 mm", 38m, 19m, 1m, false, 3),
        ("CRYO-33X13", "Cryovial — 33 × 13 mm", 33m, 13m, 0.8m, false, 4),
        ("CUP-76X51", "Frasco orina/heces — 76 × 51 mm (3\" × 2\")", 76m, 51m, 2.5m, false, 5),
    ];

    // Asignación por defecto formato ← tubo / tipo de muestra (R6-F4b). Los tubos de
    // sangre usan la etiqueta estándar; los recipientes sin tubo (orina, heces, semen,
    // saliva) usan la de frasco, que es la que físicamente le cabe al envase.
    private static readonly (string TubeCode, string FormatCode)[] TubeLabelAssignments =
    [
        ("EDTA", "TUBE-51X25"),
        ("SST", "TUBE-51X25"),
        ("CIT", "TUBE-51X25"),
        ("HEP", "TUBE-51X25"),
    ];

    private static readonly (string SpecimenCode, string FormatCode)[] SpecimenLabelAssignments =
    [
        ("URINE", "CUP-76X51"),
        ("STOOL", "CUP-76X51"),
        ("SEMEN", "CUP-76X51"),
        ("SALIVA", "CUP-76X51"),
        ("SWAB", "CRYO-33X13"),
        ("CSF", "MICRO-38X19"),
        ("TISSUE", "CUP-76X51"),
    ];

    /// <summary>Mapea una prueba a su tipo de muestra + tubo por departamento (LIS-RD-16/26).</summary>
    private static (string SpecimenCode, string? TubeCode) MapSpecimen(LabTestDefinition test)
    {
        var isUrine = test.CollectionType?.StartsWith("urine", StringComparison.OrdinalIgnoreCase) == true;
        return test.Department switch
        {
            LabDepartment.Hematology => ("BLOOD", "EDTA"),
            LabDepartment.Chemistry => ("BLOOD", "SST"),
            LabDepartment.Immunology => ("BLOOD", "SST"),
            LabDepartment.Urinalysis => ("URINE", null),
            _ => isUrine ? ("URINE", null) : ("BLOOD", "SST"),
        };
    }

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
                logger.LogDebug("Catálogo de laboratorio global ya sembrado; verificando catálogo de muestras.");
                await SeedSpecimenCatalogAsync(db, cancellationToken);
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
                var panel = LabPanel.Create(
                    p.Code, p.Name, p.Description, loincCode: PanelLoinc.GetValueOrDefault(p.Code), timeProvider: timeProvider);
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

            // 6) Tipos de muestra + tubos + mapeo (idempotente; ver SeedSpecimenCatalogAsync).
            await SeedSpecimenCatalogAsync(db, cancellationToken);

            logger.LogInformation(
                "Lab catalog seed cargado: {Tests} pruebas, {Panels} paneles, {Ranges} rangos, {Derived} fórmulas, {Tat} TAT targets, {Specimens} tipos de muestra, {Tubes} tubos.",
                testsByCode.Count, Panels.Length, Ranges.Length, Derived.Length, TatTargets.Length, SpecimenTypes.Length, TubeTypes.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LabCatalogSeeder falló; el catálogo de laboratorio puede estar incompleto.");
        }
    }

    /// <summary>
    /// Siembra los tipos de muestra + tubos globales (LIS-RD-26/16) y mapea las
    /// pruebas globales que aún no tienen tipo de muestra. Idempotente: sirve
    /// tanto para DB fresca como para backfill de catálogos ya sembrados.
    /// </summary>
    private async Task SeedSpecimenCatalogAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.SpecimenTypes.AnyAsync(s => s.OrganizationId == null, cancellationToken))
        {
            foreach (var s in SpecimenTypes)
            {
                db.SpecimenTypes.Add(SpecimenType.Create(
                    s.Code, s.Name, s.Mode, snomedCode: null, defaultMethod: s.Method,
                    defaultBodySite: s.BodySite, containerKind: s.Container, stabilityMinutes: s.Stability,
                    discipline: s.Discipline, timeProvider: timeProvider));
            }
            foreach (var t in TubeTypes)
            {
                db.TubeTypes.Add(TubeType.Create(
                    t.Code, t.Name, t.Color, t.Additive, t.Vol, t.Cap, t.Dead, timeProvider: timeProvider));
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Motivos de rechazo de muestra (R6-F1a). Idempotente.
        if (!await db.SpecimenRejectionReasons.AnyAsync(r => r.OrganizationId == null, cancellationToken))
        {
            foreach (var r in RejectionReasons)
            {
                db.SpecimenRejectionReasons.Add(SpecimenRejectionReason.Create(
                    r.Code, r.Name, requiresNewSample: r.RequiresNew, sortOrder: r.Sort, timeProvider: timeProvider));
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Motivos de devolución desde Validación. Idempotente. Son analíticos, no preanalíticos:
        // la muestra sirvió, lo que no convence es el resultado.
        if (!await db.ValidationRejectionReasons.AnyAsync(r => r.OrganizationId == null, cancellationToken))
        {
            foreach (var r in ValidationRejectionReasons)
            {
                db.ValidationRejectionReasons.Add(ValidationRejectionReason.Create(
                    r.Code, r.Name, r.Description, requiresNewSample: r.RequiresNew,
                    sortOrder: r.Sort, timeProvider: timeProvider));
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Formatos de etiqueta + sus asignaciones (R6-F4b). Idempotente.
        if (!await db.LabLabelFormats.AnyAsync(f => f.OrganizationId == null, cancellationToken))
        {
            var formatIdByCode = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var f in LabelFormats)
            {
                var format = LabLabelFormat.Create(
                    f.Code, f.Name, f.W, f.H, f.Margin, isDefault: f.Default, sortOrder: f.Sort, timeProvider: timeProvider);
                db.LabLabelFormats.Add(format);
                formatIdByCode[f.Code] = format.Id;
            }
            foreach (var (tubeCode, formatCode) in TubeLabelAssignments)
            {
                if (formatIdByCode.TryGetValue(formatCode, out var formatId))
                {
                    db.LabLabelFormatAssignments.Add(
                        LabLabelFormatAssignment.ForTube(tubeCode, formatId, timeProvider: timeProvider));
                }
            }
            foreach (var (specimenCode, formatCode) in SpecimenLabelAssignments)
            {
                if (formatIdByCode.TryGetValue(formatCode, out var formatId))
                {
                    db.LabLabelFormatAssignments.Add(
                        LabLabelFormatAssignment.ForSpecimen(specimenCode, formatId, timeProvider: timeProvider));
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Backfill de capacidad/volumen muerto en tubos globales ya sembrados (R6-F1a).
        // El bloque de seed de arriba solo corre en BD fresca; en DBs ya sembradas
        // (R1-F1) los tubos existen sin estas columnas, así que las rellenamos por código.
        var tubesNeedingCapacity = await db.TubeTypes
            .Where(t => t.OrganizationId == null && t.CapacityMl == null)
            .ToListAsync(cancellationToken);
        if (tubesNeedingCapacity.Count > 0)
        {
            var tubeSpecByCode = TubeTypes.ToDictionary(x => x.Code, StringComparer.Ordinal);
            foreach (var tube in tubesNeedingCapacity)
            {
                if (tubeSpecByCode.TryGetValue(tube.Code, out var spec))
                {
                    tube.Edit(tube.Name, tube.Color, tube.Additive, tube.VolumeMinMl, spec.Cap, spec.Dead, timeProvider);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Backfill de requerimientos de colección (R6-F1a): pruebas globales sin volumen.
        // Por ahora defaults conservadores y uniformes; se refinan por prueba cuando
        // llegue el catálogo real de analizadores (LIS-12). El motor de plan (R6-F2)
        // consume estos valores para derivar contenedores y consolidar por aditivo+capacidad.
        var testsNeedingVolume = await db.LabTests
            .Where(t => t.OrganizationId == null && t.MinVolumeMl == null)
            .ToListAsync(cancellationToken);
        if (testsNeedingVolume.Count > 0)
        {
            foreach (var test in testsNeedingVolume)
            {
                test.SetCollectionRequirements(
                    minVolumeMl: 0.5m, preferredVolumeMl: 1.0m, analyzerMinVolumeMl: 0.2m,
                    stabilityMinutes: null, timeProvider: timeProvider);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        // Backfill: mapear pruebas globales sin tipo de muestra.
        var specimenByCode = (await db.SpecimenTypes.Where(s => s.OrganizationId == null).ToListAsync(cancellationToken))
            .ToDictionary(s => s.Code, StringComparer.Ordinal);
        var tubeByCode = (await db.TubeTypes.Where(t => t.OrganizationId == null).ToListAsync(cancellationToken))
            .ToDictionary(t => t.Code, StringComparer.Ordinal);
        var unmapped = await db.LabTests
            .Where(t => t.OrganizationId == null && t.SpecimenTypeId == null)
            .ToListAsync(cancellationToken);
        if (unmapped.Count == 0)
        {
            return;
        }
        foreach (var test in unmapped)
        {
            var (specimenCode, tubeCode) = MapSpecimen(test);
            Guid? specimenId = specimenByCode.TryGetValue(specimenCode, out var sp) ? sp.Id : null;
            Guid? tubeId = tubeCode is not null && tubeByCode.TryGetValue(tubeCode, out var tb) ? tb.Id : null;
            test.SetSpecimenMapping(specimenId, tubeId, collectionMode: null, timeProvider: timeProvider);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
