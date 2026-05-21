using System.Text.Json;
using System.Text.Json.Serialization;
using IMed.Api.Domain.Forms;
using IMed.Api.Domain.Terminology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seeder de <see cref="FormDefinition"/> con presets globales (organization_id = null)
/// que cualquier organización puede usar como fallback. Solo siembra si NO hay
/// definiciones globales todavía.
///
/// <para>El <c>schema_json</c> sigue el formato nativo de <c>@constellation/craft-system</c>
/// (FormSchema con <c>fields[]</c>, widgets con <c>id/type/name/label</c>).
/// El <c>name</c> de cada widget cumple el doble rol de FormControl key + linkId
/// del FHIR mapping (ver <c>form-definitions-schema.md</c> §3).</para>
///
/// <para>El typeahead de diagnósticos queda fuera del schema porque la página
/// del encuentro lo renderiza con el componente reutilizable
/// <c>&lt;imed-typeahead&gt;</c> (Hito 1 + Épica N L2). El FHIR mapping sigue
/// exponiendo <c>linkId: "diagnostics"</c> y EncounterCloseFlow lo puebla desde
/// la signal de la página, no desde el FormGroup.</para>
///
/// <para><b>TODO Épica M:</b> el typeahead debería ser un widget registrado en
/// <c>@constellation/craft-system</c> (igual que <c>text</c>, <c>textarea</c>,
/// etc.) en vez de un componente Angular orquestado por la página. La capa L3
/// del widget se construye en Épica N5 (master plan); su uso dentro del
/// schema del encuentro queda para Épica M cuando se rediseñen los presets.
/// Detalles en <c>imed-infra/docs/api/form-definitions-schema.md</c>.</para>
/// </summary>
public sealed class FormDefinitionsSeeder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<FormDefinitionsSeeder> logger) : IHostedService
{
    /// <summary>UUID fijo del "system seeder" — actor en created_by/published_by para los presets.</summary>
    private static readonly Guid SystemSeederId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>Omitir nulls; schema-craft acepta campos ausentes igual que null pero el JSON queda más limpio.</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Tolerar tabla aún no existente (caso de tests donde MigrateAsync corre tarde).
            bool hasGlobalPresets;
            try
            {
                hasGlobalPresets = await db.FormDefinitions
                    .AnyAsync(x => x.OrganizationId == null, cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tabla form_definitions aún no existe; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                hasGlobalPresets = await db.FormDefinitions
                    .AnyAsync(x => x.OrganizationId == null, cancellationToken);
            }

            if (hasGlobalPresets)
            {
                logger.LogDebug("Presets globales de FormDefinition ya existen; skip seed.");
                return;
            }

            var presets = BuildPresets();
            foreach (var preset in presets)
            {
                preset.Publish(SystemSeederId, timeProvider);
            }

            db.FormDefinitions.AddRange(presets);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("FormDefinitions seed cargado con {Count} presets globales.", presets.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "FormDefinitionsSeeder falló; presets globales pueden no estar disponibles.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private FormDefinition[] BuildPresets()
    {
        return
        [
            FormDefinition.CreateDraft(
                code: "general",
                title: "Consulta general",
                description: "Formulario clínico básico para cualquier especialidad: anamnesis, examen físico, plan, diagnósticos CIE-10.",
                schemaJson: GeneralFormSchema,
                fhirMappingJson: GeneralFormFhirMapping,
                createdBy: SystemSeederId,
                specialtyCode: null,
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),

            FormDefinition.CreateDraft(
                code: "pediatria",
                title: "Consulta pediátrica",
                description: "Formulario específico para pediatría: incluye antropometría, hitos del desarrollo, vacunación.",
                schemaJson: PediatriaFormSchema,
                fhirMappingJson: PediatriaFormFhirMapping,
                createdBy: SystemSeederId,
                // SNOMED 394537008 = Pediatría general.
                specialtyCode: "394537008",
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),

            FormDefinition.CreateDraft(
                code: "prenatal",
                title: "Control prenatal",
                description: "Formulario para control de embarazo: anamnesis obstétrica, semanas de gestación, antropometría materna, signos vitales, tamizaje y plan.",
                schemaJson: PrenatalFormSchema,
                fhirMappingJson: PrenatalFormFhirMapping,
                createdBy: SystemSeederId,
                // SNOMED 394585009 = Obstetricia y ginecología.
                specialtyCode: "394585009",
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),
        ];
    }

    // ─── Helpers para construir el schema-craft JSON ─────────────────────

    /// <summary>
    /// Form root: <c>{ id, name, title, version, fields, settings }</c>. El bloque
    /// <c>settings</c> es leído por <c>JsonMapperService</c> del motor y mergeado
    /// con los defaults — por eso lo declaramos acá, no en el page consumer:
    /// el schema es autoritativo, los presets clínicos no quieren los botones
    /// genéricos del motor (Submit/Reset) porque <c>EncounterActivePage</c>
    /// expone Pausar/Guardar borrador/Cerrar consulta a nivel de página.
    /// </summary>
    private static object FormSchema(string name, string title, params object[] fields) => new
    {
        id = $"form-{name}",
        name,
        title,
        version = "1.0",
        fields,
        settings = new
        {
            isForm = true,
            showSubmitButton = false,
            showResetButton = false,
            showCancelButton = false,
        },
    };

    /// <summary>Tabs container widget. <c>name</c> es solo identificador del control, no se ve.</summary>
    private static object Tabs(string name, params object[] tabs) => new
    {
        id = $"widget-{name}",
        type = "tabs",
        name,
        tabs,
    };

    /// <summary>Tab dentro del Tabs widget. <c>id</c> estable, <c>label</c> visible, <c>children</c> = widgets.</summary>
    private static object Tab(string id, string label, params object[] children) => new
    {
        id = $"tab-{id}",
        label,
        children,
    };

    /// <summary>Input text (single line). <c>name</c> = linkId del FHIR mapping + key del FormControl.</summary>
    private static object Text(string linkId, string label, bool? required = null, int? maxLength = null) => new
    {
        id = $"widget-{linkId}",
        type = "text",
        name = linkId,
        label,
        required,
        maxLength,
    };

    private static object Textarea(string linkId, string label, int? rows = null) => new
    {
        id = $"widget-{linkId}",
        type = "textarea",
        name = linkId,
        label,
        rows,
    };

    private static object Number(string linkId, string label, double? minimum = null, double? maximum = null, double? step = null) => new
    {
        id = $"widget-{linkId}",
        type = "number",
        name = linkId,
        label,
        minimum,
        maximum,
        step,
    };

    private static object Date(string linkId, string label) => new
    {
        id = $"widget-{linkId}",
        type = "date",
        name = linkId,
        label,
    };

    // ─── Helpers para construir items del fhir_mapping_json ──────────────

    /// <summary>
    /// Item de mapping a Observation (signo vital / antropometría) con LOINC + UCUM.
    /// El extractor <c>value-quantity-to-observation</c> lee <c>formValue[linkId]</c>,
    /// genera un Observation R5 con <c>valueQuantity</c>, <c>code</c>, <c>category</c>,
    /// <c>subject</c>, <c>encounter</c>, <c>effectiveDateTime</c>, <c>performer</c>,
    /// <c>status: final</c>. Skip si el value está vacío.
    /// </summary>
    private static object ObservationItem(string linkId, string loincCode, string loincDisplay, string unit, string ucumCode) => new
    {
        linkId,
        mapsTo = "Observation",
        extractor = "value-quantity-to-observation",
        code = new
        {
            system = "http://loinc.org",
            code = loincCode,
            display = loincDisplay,
        },
        unit,
        ucumCode,
        category = "vital-signs",
        status = "final",
    };

    // Items comunes que aplican a los tres presets. `chiefComplaint` y `plan`
    // proyectan a campos del Encounter (no generan recursos aparte). `diagnostics`
    // proyecta a N Condition (CIE-10). El truco `fixed_` → `fixed` por el keyword
    // reservado se aplica al serializar abajo.
    private static object[] CommonMappingItems() => new object[]
    {
        new
        {
            linkId = "chiefComplaint",
            mapsTo = "Encounter.reason",
            extractor = "string-to-codeable-concept-text",
        },
        new
        {
            linkId = "diagnostics",
            mapsTo = "Condition",
            extractor = "multi-coding-to-conditions",
            codingSystem = CodeSystems.Icd11,
            fixed_ = new
            {
                clinicalStatus = "active",
                verificationStatus = "confirmed",
                category = "encounter-diagnosis",
            },
        },
        new
        {
            linkId = "plan",
            mapsTo = "Encounter.note",
            extractor = "text-to-encounter-note",
        },
    };

    /// <summary>
    /// Serializa un mapping con <c>version</c> y <c>items[]</c>. Aplica el truco
    /// <c>fixed_</c> → <c>fixed</c> sobre el JSON resultante (System.Text.Json no
    /// permite property names que sean keywords de C#).
    /// </summary>
    private static string SerializeMapping(params object[] items) =>
        JsonSerializer
            .Serialize(new { version = "1.0", items }, JsonOpts)
            .Replace("fixed_", "fixed", StringComparison.Ordinal);

    // ─── Schemas ──────────────────────────────────────────────────────────

    private static readonly string GeneralFormSchema = JsonSerializer.Serialize(
        FormSchema("general", "Consulta general",
            Tabs("layoutTabs",
                Tab("anamnesis", "Anamnesis",
                    Text("chiefComplaint", "Motivo de consulta", required: true, maxLength: 200),
                    Textarea("history", "Historia de la enfermedad actual", rows: 5)
                ),
                Tab("physical", "Examen físico",
                    Textarea("physicalExam", "Hallazgos del examen físico", rows: 5)
                ),
                Tab("plan", "Diagnósticos y plan",
                    Textarea("plan", "Plan terapéutico", rows: 4)
                )
            )
        ),
        JsonOpts);

    // General: solo proyecciones a Encounter + Condition (sin signos vitales — texto libre).
    private static readonly string GeneralFormFhirMapping = SerializeMapping(CommonMappingItems());

    private static readonly string PediatriaFormSchema = JsonSerializer.Serialize(
        FormSchema("pediatria", "Consulta pediátrica",
            Tabs("layoutTabs",
                Tab("anamnesis", "Anamnesis",
                    Text("chiefComplaint", "Motivo de consulta", required: true, maxLength: 200),
                    Textarea("history", "Historia de la enfermedad actual", rows: 5),
                    Text("feeding", "Alimentación / lactancia"),
                    Text("vaccinations", "Vacunación al día (sí/no/parcial)")
                ),
                Tab("anthropometry", "Antropometría",
                    Number("weightKg", "Peso (kg)", step: 0.1),
                    Number("heightCm", "Talla (cm)", step: 0.1),
                    Number("headCircumferenceCm", "Perímetro cefálico (cm)", step: 0.1)
                ),
                Tab("physical", "Examen físico",
                    Textarea("physicalExam", "Hallazgos del examen físico", rows: 5)
                ),
                Tab("plan", "Diagnósticos y plan",
                    Textarea("plan", "Plan terapéutico", rows: 4)
                )
            )
        ),
        JsonOpts);

    // Pediatría: common + antropometría como Observation (LOINC + UCUM). Las medidas
    // viven en QR (trazabilidad de "qué se preguntó") Y en Observation (dato clínico
    // estructurado, indexable, base para curvas de crecimiento).
    private static readonly string PediatriaFormFhirMapping = SerializeMapping(
        [
            ..CommonMappingItems(),
            ObservationItem("weightKg", "29463-7", "Body weight", "kg", "kg"),
            ObservationItem("heightCm", "8302-2", "Body height", "cm", "cm"),
            ObservationItem("headCircumferenceCm", "9843-4", "Head Occipital-frontal circumference", "cm", "cm"),
        ]);

    private static readonly string PrenatalFormSchema = JsonSerializer.Serialize(
        FormSchema("prenatal", "Control prenatal",
            Tabs("layoutTabs",
                Tab("anamnesis", "Anamnesis obstétrica",
                    Text("chiefComplaint", "Motivo de consulta", required: true, maxLength: 200),
                    Date("lmpDate", "Fecha de última menstruación (FUM)"),
                    Number("gestationalWeeks", "Semanas de gestación", minimum: 0, maximum: 45),
                    Number("gravida", "Gestaciones (G)", minimum: 0, maximum: 20),
                    Number("para", "Partos (P)", minimum: 0, maximum: 20),
                    Number("abortions", "Abortos (A)", minimum: 0, maximum: 20),
                    Textarea("obstetricHistory", "Antecedentes obstétricos relevantes", rows: 4)
                ),
                Tab("vitals", "Antropometría y signos vitales",
                    Number("weightKg", "Peso (kg)", step: 0.1),
                    Number("heightCm", "Talla (cm)", step: 0.1),
                    Number("systolicBp", "Presión sistólica (mmHg)", minimum: 60, maximum: 250),
                    Number("diastolicBp", "Presión diastólica (mmHg)", minimum: 40, maximum: 150),
                    Number("fetalHeartRate", "Frecuencia cardíaca fetal (lpm)", minimum: 60, maximum: 220),
                    Number("fundalHeightCm", "Altura uterina (cm)", step: 0.1)
                ),
                Tab("physical", "Examen físico",
                    Textarea("physicalExam", "Hallazgos del examen físico", rows: 5),
                    Text("fetalMovements", "Movimientos fetales (sí / no / no aplica)")
                ),
                Tab("plan", "Diagnósticos y plan",
                    Textarea("plan", "Plan (controles, suplementación, exámenes, signos de alarma)", rows: 5)
                )
            )
        ),
        JsonOpts);

    // Prenatal: common + antropometría materna + signos vitales + FCF + altura uterina.
    // Anamnesis estructurada (lmpDate, gestationalWeeks, gravida/para/abortions) queda
    // sólo en QR — son antecedentes, no observaciones puntuales del momento de la consulta.
    private static readonly string PrenatalFormFhirMapping = SerializeMapping(
        [
            ..CommonMappingItems(),
            ObservationItem("weightKg", "29463-7", "Body weight", "kg", "kg"),
            ObservationItem("heightCm", "8302-2", "Body height", "cm", "cm"),
            ObservationItem("systolicBp", "8480-6", "Systolic blood pressure", "mmHg", "mm[Hg]"),
            ObservationItem("diastolicBp", "8462-4", "Diastolic blood pressure", "mmHg", "mm[Hg]"),
            ObservationItem("fetalHeartRate", "11951-7", "Fetal Heart rate", "lpm", "/min"),
            ObservationItem("fundalHeightCm", "11881-0", "Uterine Fundal height", "cm", "cm"),
        ]);
}
