
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

            // Estrategia upsert-por-code: insertar sólo los presets que aún no existen.
            // Los que ya existen no se tocan (puede haber ediciones del admin).
            // Esto permite agregar nuevos presets sin borrar la DB.
            var presets = BuildPresets();

            if (!hasGlobalPresets)
            {
                // Primera ejecución: insertar todos de una vez (ruta rápida).
                foreach (var preset in presets)
                {
                    preset.Publish(SystemSeederId, timeProvider);
                }
                db.FormDefinitions.AddRange(presets);
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("FormDefinitions seed inicial: {Count} presets globales insertados.", presets.Length);
                return;
            }

            // Ejecuciones posteriores: sólo insertar (code, version) que falten. Antes esto
            // comparaba solo por Code, lo que impedía sembrar una nueva Version del mismo
            // Code (ej. "odontologia" 2.0.0 conviviendo con 1.0.0 ya sembrada) — el
            // constraint único real de la tabla es (organization_id, code, version), así
            // que el chequeo de "qué falta" debe respetar esa misma clave compuesta.
            var existingKeys = (await db.FormDefinitions
                .Where(x => x.OrganizationId == null)
                .Select(x => new { x.Code, x.Version })
                .ToListAsync(cancellationToken))
                .Select(x => (x.Code, x.Version))
                .ToHashSet();

            var missing = presets.Where(p => !existingKeys.Contains((p.Code, p.Version))).ToArray();
            if (missing.Length == 0)
            {
                logger.LogDebug("FormDefinitions seed: todos los presets globales ya existen; skip.");
                return;
            }

            foreach (var preset in missing)
            {
                preset.Publish(SystemSeederId, timeProvider);
            }
            db.FormDefinitions.AddRange(missing);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("FormDefinitions seed: {Count} preset(s) nuevo(s) insertado(s): {Codes}.",
                missing.Length, string.Join(", ", missing.Select(p => $"{p.Code}@{p.Version}")));
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

            FormDefinition.CreateDraft(
                code: "oftalmologia",
                title: "Consulta oftalmológica",
                description: "Formulario para consulta de oftalmología con asistencia de voz: anamnesis, agudeza visual, examen ocular (PIO, biomicroscopía, fondo de ojo) y plan.",
                schemaJson: OftalmologiaFormSchema,
                fhirMappingJson: OftalmologiaFormFhirMapping,
                createdBy: SystemSeederId,
                // SNOMED 394586005 = Oftalmología.
                specialtyCode: "394586005",
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),

            FormDefinition.CreateDraft(
                code: "odontologia",
                title: "Consulta odontológica",
                description: "Formulario para consulta de odontología: anamnesis, hallazgos dentales (con pieza FDI) y procedimientos realizados, y plan.",
                schemaJson: OdontologiaFormSchema,
                fhirMappingJson: OdontologiaFormFhirMapping,
                createdBy: SystemSeederId,
                // SNOMED 722163006 = Odontología general.
                specialtyCode: "722163006",
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),

            // v2: mismo Code+specialty, layout de "Anam. Odontológica" pasa de Tabs a
            // Accordion (nivel 3 del diseño real, ver imagen de referencia con Filiación/
            // Historia clínica/... > Anam. Odontología/Nota evolución/... > Sección/
            // Enfermedad actual/Antecedentes/...). v1 queda intacta — no se toca ni se
            // borra, sigue resolviéndose para encuentros históricos ya cerrados con ella.
            // Al tener mayor Version y mismo Status Published, ResolveBySpecialtyAsync
            // la prefiere automáticamente para encuentros nuevos (no hace falta "activar"
            // nada a mano). Mismos linkIds que v1 → mismo FhirMappingJson, sin duplicar.
            FormDefinition.CreateDraft(
                code: "odontologia",
                title: "Consulta odontológica",
                description: "v2: Anam. Odontológica con acordeón (Motivo/Enfermedad actual/Antecedentes/Hallazgos/Plan) en vez de sub-tabs.",
                schemaJson: OdontologiaFormSchemaV2,
                fhirMappingJson: OdontologiaFormFhirMapping,
                createdBy: SystemSeederId,
                specialtyCode: "722163006",
                organizationId: null,
                version: "2.0.0",
                timeProvider: timeProvider),

            // ─── Mini-formularios vinculados al odontograma (diagram_definitions) ───
            // Se abren al hacer clic en una zona del odontograma con un "modo formulario"
            // activo (ver widget de diagrama). SIN specialtyCode a propósito: no deben
            // participar en la resolución por especialidad (ResolveBySpecialtyAsync),
            // sólo se resuelven por code exacto (GetByCode) desde `linked_form_codes_json`.
            FormDefinition.CreateDraft(
                code: "odonto-caries",
                title: "Caries",
                description: "Registro de hallazgo de caries dental — vinculado a una zona del odontograma.",
                schemaJson: OdontoCariesFormSchema,
                fhirMappingJson: OdontoCariesFormFhirMapping,
                createdBy: SystemSeederId,
                specialtyCode: null,
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),

            FormDefinition.CreateDraft(
                code: "odonto-endodoncia",
                title: "Endodoncia",
                description: "Registro de tratamiento de conducto — vinculado a una zona del odontograma.",
                schemaJson: OdontoEndodonciaFormSchema,
                fhirMappingJson: OdontoEndodonciaFormFhirMapping,
                createdBy: SystemSeederId,
                specialtyCode: null,
                organizationId: null,
                version: "1.0.0",
                timeProvider: timeProvider),

            FormDefinition.CreateDraft(
                code: "odonto-extraccion",
                title: "Extracción",
                description: "Registro de extracción dental — vinculado a una zona del odontograma.",
                schemaJson: OdontoExtraccionFormSchema,
                fhirMappingJson: OdontoExtraccionFormFhirMapping,
                createdBy: SystemSeederId,
                specialtyCode: null,
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

    /// <summary>
    /// Variante de <see cref="Tabs"/> con <c>tabStyle: "pills"</c> — del tipo declarado
    /// (default/pills/underline/outlined/filled) solo "pills" tiene un render distinto hoy,
    /// el resto cae al mismo estilo subrayado (ver <c>TabsRenderComponent.getTabClasses</c>).
    /// Util para diferenciar visualmente Tabs anidados (ej. nivel 1 en pills, nivel 2 default).
    /// </summary>
    private static object TabsPills(string name, params object[] tabs) => new
    {
        id = $"widget-{name}",
        type = "tabs",
        name,
        tabStyle = "pills",
        tabs,
    };

    /// <summary>Tab dentro del Tabs widget. <c>id</c> estable, <c>label</c> visible, <c>children</c> = widgets.</summary>
    private static object Tab(string id, string label, params object[] children) => new
    {
        id = $"tab-{id}",
        label,
        children,
    };

    /// <summary>Widget accordion — secciones colapsables, cada una con su propio contenido. Mismo patron que Tabs/Tab.</summary>
    private static object Accordion(string name, params object[] sections) => new
    {
        id = $"widget-{name}",
        type = "accordion",
        name,
        sections,
    };

    /// <summary>Sección dentro de un Accordion. <c>expanded: true</c> en la primera para que no arranque todo cerrado.</summary>
    private static object Section(string id, string title, bool expanded, params object[] children) => new
    {
        id = $"section-{id}",
        title,
        expanded,
        children,
    };

    /// <summary>
    /// Fieldset: seccion con borde y titulo (<c>legend</c>), siempre visible — no es
    /// tab ni acordeon (no requiere click para ver el contenido). Se usa para agrupar
    /// secciones dentro de un tab largo (ej. las sub-secciones de "Anamnesis") sin
    /// anidar Tabs dentro de Tabs.
    /// </summary>
    private static object Fieldset(string name, string legend, params object[] children) => new
    {
        id = $"widget-{name}",
        type = "fieldset",
        name,
        legend,
        children,
    };

    /// <summary>Input text (single line). <c>name</c> = linkId del FHIR mapping + key del FormControl.</summary>
    private static object Text(string linkId, string label, bool? required = null, int? maxLength = null, bool hideLabel = false, int? columnSpan = null, object? styles = null, object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "text",
        name = linkId,
        label,
        hideLabel,
        required,
        maxLength,
        gridPosition = GridPos(columnSpan),
        styles,
        metadata,
    };

    /// <summary>
    /// Container: agrupa widgets con layout de grilla (<c>columns &gt; 1</c>) o apilado
    /// vertical. Es el mecanismo REAL para columnas dentro de un Tab (el motor SI lee
    /// <c>layout</c>/<c>columns</c> de Container en runtime — ver
    /// <c>container-render.component.ts</c>). Ademas, DENTRO de un Container el motor
    /// SI respeta el <c>gridPosition.columnSpan</c> de cada hijo (cada widget se envuelve
    /// en un <c>&lt;div [ngStyle]="getWidgetGridStyles(widget)"&gt;</c> que aplica
    /// <c>grid-column: span N</c>) — asi se logran anchos asimetricos (ej. 6/2/4 de 12)
    /// en una misma fila, usando <c>columnSpan</c> en cada widget hijo.
    /// </summary>
    private static object Container(string name, int columns, params object[] children) => new
    {
        id = $"widget-{name}",
        type = "container",
        name,
        layout = columns > 1 ? "grid" : "vertical",
        columns,
        gap = 4,
        children,
    };

    /// <summary>Construye <c>gridPosition</c> con <c>columnSpan</c> (o <c>null</c> si no se especifica, para no ensuciar el JSON).</summary>
    private static object? GridPos(int? columnSpan) => columnSpan is null ? null : new { columnSpan };

    private static object Label(string name, string text, int? columnSpan = null, object? styles = null) => new
    {
        id = $"widget-{name}",
        type = "label",
        name,
        text,
        gridPosition = GridPos(columnSpan),
        styles,
    };

    private static object Radio(
        string linkId,
        string label,
        (string value, string label)[] options,
        bool inline = false,
        bool hideLabel = false,
        string? defaultValue = null,
        int? columnSpan = null,
        object? styles = null) => new
    {
        id = $"widget-{linkId}",
        type = "radio",
        name = linkId,
        gridPosition = GridPos(columnSpan),
        label,
        hideLabel,
        inline,
        defaultValue,
        styles,
        options = options.Select(o => new { value = o.value, label = o.label }).ToArray(),
    };

    /// <summary>Regla de visibilidad condicional. <c>condition</c> es una expresion JS
    /// evaluada contra los valores actuales del form (por <c>name</c> de cada widget) —
    /// ver <c>RulesEngineService.evaluateRules</c>. Ej: <c>"hasAllergiesFlag === 'si'"</c>.</summary>
    private static object VisibilityRule(string id, string condition) => new
    {
        id,
        type = "visibility",
        condition,
        value = true,
        enabled = true,
    };

    /// <summary>
    /// Fila "pregunta + Sí/No" para checklists tipo "¿Tiene o ha tenido?" — pensada para
    /// ir dentro de un <c>Container(columns: 2)</c> que las acomoda en 2 columnas.
    /// </summary>
    private static object YesNoRow(string linkId, string questionLabel) => Container($"{linkId}Row", 2,
        Label($"{linkId}Label", questionLabel),
        Radio(linkId, "", [("no", "No"), ("si", "Sí")], inline: true, hideLabel: true)
    );

    /// <summary>
    /// Fila "pregunta + Sí/No + detalle" (texto libre u otro widget) — una por línea,
    /// grilla propia de 12 columnas: label 6, radio Sí/No 2 (sin label, se duplicaria
    /// con el de la fila), detalle 4. Label con borde inferior gris y los 3 hijos
    /// alineados al fondo de la fila (alignSelf: end) para que no queden despegados
    /// del control cuando el label ocupa mas de una linea. El widget de detalle debe
    /// venir ya armado con <c>columnSpan: 4</c> y <c>styles: new { alignSelf = "end" }</c>
    /// (ver <see cref="Text"/>/<see cref="Typeahead"/>) para respetar la misma grilla.
    /// </summary>
    private static object YesNoDetailRow(string linkId, string questionLabel, object detailWidget) => Container($"{linkId}Row", 12,
        Label($"{linkId}Label", questionLabel, columnSpan: 6,
            styles: new
            {
                borderColor = new { bottom = "#e2e8f0" },
                borderWidth = new { bottom = 1 },
                padding = new { bottom = 8 },
                alignSelf = "end",
            }),
        Radio(linkId, "", [("no", "No"), ("si", "Sí")], inline: true, hideLabel: true,
            columnSpan: 2, styles: new { alignSelf = "end" }),
        detailWidget
    );

    private static object Textarea(string linkId, string label, int? rows = null, bool? required = null, object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "textarea",
        name = linkId,
        label,
        rows,
        required,
        metadata,
    };

    /// <summary>Editor WYSIWYG (rich_text). El valor persistido es HTML, no texto plano.</summary>
    private static object RichText(string linkId, string label, bool? required = null, string toolbar = "standard", object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "rich_text",
        name = linkId,
        label,
        required,
        toolbar,
        minHeight = "120px",
        maxHeight = "320px",
        metadata,
    };

    private static object Number(string linkId, string label, double? minimum = null, double? maximum = null, double? step = null, object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "number",
        name = linkId,
        label,
        minimum,
        maximum,
        step,
        metadata,
    };

    private static object Date(string linkId, string label) => new
    {
        id = $"widget-{linkId}",
        type = "date",
        name = linkId,
        label,
    };

    private static object Toggle(string linkId, string label, bool? required = null, object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "toggle",
        name = linkId,
        label,
        required,
        metadata,
    };

    /// <summary>
    /// Multiselect con dataSource estático y chips visibles (checklist fijo, sin catálogo).
    /// El valor guardado es un array de <c>value</c>s planos.
    /// </summary>
    private static object Multiselect(
        string linkId,
        string label,
        (string value, string label)[] options,
        bool? required = null,
        bool chipDisplay = true,
        object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "multiselect",
        name = linkId,
        label,
        required,
        chipDisplay,
        metadata,
        dataSource = new
        {
            type = "static",
            data = options.Select(o => new { value = o.value, label = o.label }).ToArray(),
        },
    };

    /// <summary>
    /// Typeahead contra un endpoint de catálogo (ICD-11, ATC, etc.). El valor
    /// guardado es un array de objetos crudos del API (<c>{code, display, ...}</c>),
    /// que es lo que leen los extractors <c>multi-coding-to-*</c>.
    /// </summary>
    private static object Typeahead(
        string linkId,
        string label,
        string dataSourceUrl,
        string displayPath = "display",
        string codePath = "code",
        string? secondaryPath = null,
        int minChars = 2,
        int debounceMs = 300,
        int? maxSelections = null,
        bool? required = null,
        bool hideLabel = false,
        object[]? rules = null,
        object? metadata = null,
        int? columnSpan = null,
        object? styles = null) => new
    {
        id = $"widget-{linkId}",
        type = "typeahead",
        name = linkId,
        label,
        hideLabel,
        required,
        minChars,
        debounceMs,
        displayPath,
        codePath,
        secondaryPath,
        maxSelections,
        rules,
        metadata,
        gridPosition = GridPos(columnSpan),
        styles,
        dataSource = new
        {
            type = "api",
            url = dataSourceUrl,
        },
    };

    /// <summary>
    /// Select con dataSource estático. <paramref name="multiple"/> permite selección
    /// múltiple (el control guarda un array de <c>value</c>s planos, sin `display` —
    /// limitación actual del widget `select` de craft-system; los extractors que leen
    /// este campo (ej. <c>multi-coding-to-conditions</c>) aceptan codes planos).
    /// </summary>
    private static object Select(
        string linkId,
        string label,
        (string value, string label)[] options,
        bool multiple = false,
        bool? required = null,
        string? defaultValue = null,
        object? metadata = null) => new
    {
        id = $"widget-{linkId}",
        type = "select",
        name = linkId,
        label,
        multiple,
        required,
        defaultValue,
        metadata,
        dataSource = new
        {
            type = "static",
            data = options.Select(o => new { value = o.value, label = o.label }).ToArray(),
        },
    };

    // ─── Helpers para metadata de campos (voice LLM hints) ──────────────

    /// <summary>
    /// Metadatos de voz para un campo: hint para el LLM, sinónimos hablados,
    /// ejemplos de valores y unidad de medida. Estos valores son consumidos por
    /// IMed.Scribe para guiar la extracción de campos desde transcripciones.
    /// Las propiedades nulas son omitidas en el JSON gracias a
    /// <c>JsonIgnoreCondition.WhenWritingNull</c>.
    /// La habilitación de la feature de voz en el frontend se controla por
    /// <c>subscription_tier</c> del JWT — no por el schema.
    /// </summary>
    private static object? Meta(
        string? promptHint = null,
        string[]? synonyms = null,
        string[]? examples = null,
        string? unit = null)
    {
        if (promptHint is null && synonyms is null && examples is null && unit is null)
        {
            return null;
        }
        return new { promptHint, synonyms, examples, unit };
    }

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

    // ─── Oftalmología ─────────────────────────────────────────────────────

    /// <summary>
    /// Schema de consulta oftalmológica con voz habilitada. Incluye:
    ///   • Tab 1 Anamnesis:      motivo, síntomas, antecedentes oculares/sistémicos, medicamentos.
    ///   • Tab 2 Agudeza visual: AV OD/OI sin y con corrección, refracción.
    ///   • Tab 3 Examen ocular:  PIO OD/OI, biomicroscopía, fondo de ojo.
    ///   • Tab 4 Plan:           diagnóstico y plan terapéutico.
    ///
    /// Los campos incluyen metadata (promptHint, synonyms, examples) para guiar
    /// la extracción de voz en IMed.Scribe. La barra de voz se habilita por
    /// subscription_tier del JWT — no por el schema. SNOMED 394586005 = Oftalmología.
    /// </summary>
    private static readonly string OftalmologiaFormSchema = JsonSerializer.Serialize(
        FormSchema("oftalmologia", "Consulta oftalmológica",
            Tabs("layoutTabs",
                Tab("anamnesis", "Anamnesis",
                    Text("chiefComplaint", "Motivo de consulta", required: true, maxLength: 200,
                        metadata: Meta(
                            promptHint: "¿Cuál es el motivo de consulta oftalmológico?",
                            synonyms: ["motivo", "consulta", "molestia", "síntoma principal", "queja", "lo que lo trae"],
                            examples: ["Ojo rojo bilateral", "Baja visión progresiva del ojo derecho", "Control de glaucoma", "Revisión de cataratas"])),
                    Textarea("ocularSymptoms", "Síntomas oculares actuales", rows: 4,
                        metadata: Meta(
                            promptHint: "¿Cuáles son los síntomas oculares actuales?",
                            synonyms: ["síntomas", "molestias oculares", "ojo rojo", "visión borrosa", "dolor ocular", "lagrimeo", "fotofobia", "halos", "flashes", "fotopsias"],
                            examples: ["Ojo rojo bilateral hace 3 días con secreción mucopurulenta", "Baja visión progresiva del OD desde hace 6 meses", "Fotopsias y miodesopsias en OI desde ayer"])),
                    Textarea("ocularHistory", "Antecedentes oculares", rows: 3,
                        metadata: Meta(
                            promptHint: "¿Qué antecedentes oculares tiene el paciente?",
                            synonyms: ["antecedentes oculares", "cirugías oculares", "trauma ocular", "enfermedades oculares previas", "operaciones de los ojos"],
                            examples: ["Cirugía de cataratas OAO en 2018", "LASIK OD hace 5 años. Sin otros antecedentes.", "Glaucoma de ángulo abierto diagnosticado en 2020"])),
                    Text("systemicHistory", "Antecedentes sistémicos relevantes",
                        metadata: Meta(
                            promptHint: "¿Qué antecedentes sistémicos relevantes para oftalmología tiene el paciente?",
                            synonyms: ["antecedentes sistémicos", "diabetes", "hipertensión", "enfermedades sistémicas", "lupus", "artritis"],
                            examples: ["DM2 hace 15 años, con retinopatía diabética leve", "HTA controlada con enalapril. Sin diabetes.", "LES en tratamiento con hidroxicloroquina"])),
                    Textarea("currentMedications", "Medicamentos actuales", rows: 3,
                        metadata: Meta(
                            promptHint: "¿Qué medicamentos toma actualmente el paciente, incluyendo gotas oculares?",
                            synonyms: ["medicamentos", "gotas", "colirios", "tratamiento ocular", "fármacos", "está tomando", "usa"],
                            examples: ["Timolol 0.5% 1 gota OD c/12 h. Latanoprost 1 gota OAO noche.", "Artificial tears PRN. Sin otros medicamentos."]))
                ),
                Tab("visual-acuity", "Agudeza visual",
                    Text("vaOdSc", "AV OD sin corrección",
                        metadata: Meta(
                            promptHint: "¿Cuál es la agudeza visual del ojo derecho sin corrección?",
                            synonyms: ["AV OD", "agudeza visual ojo derecho", "visión OD", "AVSC OD", "sin lentes OD", "ojo derecho sin corrección"],
                            examples: ["20/20", "20/40", "20/200", "6/6", "0.5", "cuenta dedos a 2 metros"])),
                    Text("vaOiSc", "AV OI sin corrección",
                        metadata: Meta(
                            promptHint: "¿Cuál es la agudeza visual del ojo izquierdo sin corrección?",
                            synonyms: ["AV OI", "agudeza visual ojo izquierdo", "visión OI", "AVSC OI", "sin lentes OI", "ojo izquierdo sin corrección"],
                            examples: ["20/20", "20/30", "20/400", "percepción de luz", "no percibe luz"])),
                    Text("vaOdCc", "AV OD con corrección",
                        metadata: Meta(
                            promptHint: "¿Cuál es la agudeza visual del ojo derecho con mejor corrección?",
                            synonyms: ["AVCC OD", "AV OD con corrección", "mejor agudeza OD", "con lentes OD", "con corrección OD"],
                            examples: ["20/20", "20/25", "6/6", "0.8"])),
                    Text("vaOiCc", "AV OI con corrección",
                        metadata: Meta(
                            promptHint: "¿Cuál es la agudeza visual del ojo izquierdo con mejor corrección?",
                            synonyms: ["AVCC OI", "AV OI con corrección", "mejor agudeza OI", "con lentes OI", "con corrección OI"],
                            examples: ["20/20", "20/30", "6/9"])),
                    Text("refractionOd", "Refracción OD (Esf / Cil × Eje)",
                        metadata: Meta(
                            promptHint: "¿Cuál es la refracción del ojo derecho?",
                            synonyms: ["refracción OD", "graduación OD", "Rx OD", "esfera cilindro eje OD", "lentes OD", "dioptrías OD"],
                            examples: ["-2.00 -0.75 x 180", "+1.50 -0.50 x 90", "-4.00 esférico", "plano"])),
                    Text("refractionOi", "Refracción OI (Esf / Cil × Eje)",
                        metadata: Meta(
                            promptHint: "¿Cuál es la refracción del ojo izquierdo?",
                            synonyms: ["refracción OI", "graduación OI", "Rx OI", "esfera cilindro eje OI", "lentes OI", "dioptrías OI"],
                            examples: ["-1.75 -0.50 x 170", "+2.00 plano", "-3.50 -1.00 x 15"]))
                ),
                Tab("ocular-exam", "Examen ocular",
                    Number("pioOd", "PIO OD (mmHg)", minimum: 5, maximum: 80,
                        metadata: Meta(
                            promptHint: "¿Cuál es la presión intraocular del ojo derecho?",
                            synonyms: ["PIO OD", "presión ocular OD", "tensión ocular OD", "tonometría OD", "presión intraocular OD"],
                            unit: "mmHg",
                            examples: ["16", "22", "18 mmHg", "PIO OD de 20"])),
                    Number("pioOi", "PIO OI (mmHg)", minimum: 5, maximum: 80,
                        metadata: Meta(
                            promptHint: "¿Cuál es la presión intraocular del ojo izquierdo?",
                            synonyms: ["PIO OI", "presión ocular OI", "tensión ocular OI", "tonometría OI", "presión intraocular OI"],
                            unit: "mmHg",
                            examples: ["14", "24", "16 mmHg", "PIO OI de 22"])),
                    Textarea("biomicroscopy", "Biomicroscopía (lámpara de hendidura)", rows: 5,
                        metadata: Meta(
                            promptHint: "¿Qué se encontró en la biomicroscopía con lámpara de hendidura?",
                            synonyms: ["biomicroscopía", "lámpara de hendidura", "polo anterior", "córnea", "cristalino", "cámara anterior", "iris", "conjuntiva"],
                            examples: ["Córnea clara OAO. CA amplia, sin células ni flare. Iris en plano. Cristalino con esclerosis nuclear 2/4 OD.", "Úlcera corneal paracentral OD de 2 mm, bordes mal definidos, sin hipopion."])),
                    Textarea("fundoscopy", "Fondo de ojo (oftalmoscopía)", rows: 5,
                        metadata: Meta(
                            promptHint: "¿Qué se encontró en el fondo de ojo?",
                            synonyms: ["fondo de ojo", "oftalmoscopía", "polo posterior", "retina", "nervio óptico", "papila", "mácula", "vasos", "excavación"],
                            examples: ["Papila rosada, C/D 0.3, mácula sana, vasos de calibre normal, retina plana OAO.", "Microaneurismas y exudados duros en polo posterior OD. Compatible con RD no proliferativa moderada."]))
                ),
                Tab("plan", "Diagnósticos y plan",
                    Textarea("diagnosis", "Diagnóstico", rows: 4, required: true,
                        metadata: Meta(
                            promptHint: "¿Cuál es el diagnóstico oftalmológico?",
                            synonyms: ["diagnóstico", "impresión diagnóstica", "se diagnostica", "se concluye", "conclusión", "dg"],
                            examples: ["Glaucoma primario de ángulo abierto OAO, moderado.", "Catarata nuclear OD grado 3. OI sin alteraciones.", "Retinopatía diabética no proliferativa moderada OAO."])),
                    Textarea("plan", "Plan terapéutico", rows: 5, required: true,
                        metadata: Meta(
                            promptHint: "¿Cuál es el plan terapéutico oftalmológico?",
                            synonyms: ["plan", "tratamiento", "indicaciones", "manejo", "conducta", "se indica", "prescribir", "control"],
                            examples: ["Iniciar Latanoprost 1 gota OAO noche. Control en 4 semanas con campo visual y OCT.", "Referir a cirugía de cataratas OD. Continuar seguimiento OI c/6 meses."]))
                )
            )
        ),
        JsonOpts);

    // Oftalmología: common (chiefComplaint → Encounter.reason, diagnostics → Condition, plan → Encounter.note)
    // + PIO OD/OI como Observation con LOINC (79893-4 / 79894-2 = IOP right/left eye por tonometría).
    // Agudeza visual y refracción quedan sólo en QR — son texto libre, no cantidades estructuradas LOINC.
    private static readonly string OftalmologiaFormFhirMapping = SerializeMapping(
        [
            ..CommonMappingItems(),
            ObservationItem("pioOd", "79893-4", "Intraocular pressure of right eye", "mmHg", "mm[Hg]"),
            ObservationItem("pioOi", "79894-2", "Intraocular pressure of left eye",  "mmHg", "mm[Hg]"),
        ]);

    // ─── Odontología ──────────────────────────────────────────────────────

    /// <summary>System URL FDI (numeración dental ISO 3950) para `bodySite` de Procedure.</summary>
    private const string FdiSystem = "http://terminology.hl7.org/CodeSystem/FDI";

    private static readonly int[] FdiQuadrants = [1, 2, 3, 4];

    /// <summary>Las 32 piezas dentales en notación FDI (cuadrantes 1-4, del 8 al 1 por cuadrante).</summary>
    private static (string value, string label)[] FdiToothOptions() =>
        FdiQuadrants
            .SelectMany(quadrant => Enumerable.Range(1, 8).Reverse().Select(tooth => (quadrant, tooth)))
            .Select(t => ($"{t.quadrant}{t.tooth}", $"Pieza {t.quadrant}{t.tooth}"))
            .ToArray();

    /// <summary>
    /// Schema de consulta odontológica: anamnesis, hallazgos dentales (SNOMED, multi-selección
    /// vía <c>multi-coding-to-conditions</c>), procedimiento realizado con pieza FDI (vía
    /// <c>coding-to-procedure-with-bodysite</c>), y plan. El odontograma interactivo vive
    /// aparte (ver <c>diagram_definitions</c>) — este form cubre la nota clínica estructurada
    /// de la consulta, no el chart visual pieza-por-pieza.
    /// </summary>
    private static readonly string OdontologiaFormSchema = JsonSerializer.Serialize(
        FormSchema("odontologia", "Consulta odontológica",
            // Nivel 1: secciones de la Historia Clínica. Solo "Anam. Odontológica" esta
            // construida — el resto son placeholders (se llenan por pasos). El Tabs de
            // "Anam. Odontológica" (nivel 2, mismo que antes vivia como raiz del form)
            // ahora es el unico hijo de su Tab — Tabs anidado dentro de un Tab si esta
            // soportado (el widget Tabs es un widget mas, puede ser child de otro Tab).
            Tabs("historiaClinicaTabs",
                Tab("odontologiaAnam", "Anam. Odontológica",
                    OdontologiaAnamTabs()
                ),
                Tab("notaEvolutiva", "Nota Evolutiva"),
                Tab("odontopediatrica", "Anam. Odontopediatra"),
                Tab("endodoncia", "Endodoncia"),
                Tab("signosVitales", "Signos Vitales"),
                Tab("consentimientos", "Consentimientos")
            )
        ),
        JsonOpts);

    /// <summary>
    /// Tabs nivel 2 dentro de "Anam. Odontológica": Motivo, Enfermedad actual,
    /// Antecedentes, Hallazgos generales, Plan. Extraido a su propio metodo para que
    /// <see cref="OdontologiaFormSchema"/> quede legible con el nivel 1 (Historia
    /// Clínica) arriba.
    /// </summary>
    private static object OdontologiaAnamTabs() =>
            TabsPills("layoutTabs",
                // NOTA: rediseño en curso "por pasos" — de Fieldsets apilados en un solo
                // tab "Anamnesis" a tabs de primer nivel (sin anidar dentro de este
                // segundo nivel), replicando el formulario en papel que ya usan en
                // clínica. Este paso cubre Motivo, Enfermedad actual y Antecedentes.
                // Medicación actual/Hábitos/Gineco-obstétrico/Antecedentes odontológicos
                // se rehacen en un paso posterior con el mismo enfoque (fuera por ahora,
                // no perdidos).
                Tab("motivo", "Motivo de consulta",
                    RichText("chiefComplaint", "Motivo de consulta", required: true,
                        metadata: Meta(
                            promptHint: "¿Cuál es el motivo de consulta odontológico?",
                            synonyms: ["motivo", "consulta", "molestia", "dolor"],
                            examples: ["Dolor en pieza 16", "Control de rutina", "Sangrado de encías"]))
                ),
                Tab("enfermedadActual", "Enfermedad actual",
                    RichText("presentIllness", "Evolución del problema actual"),
                    Text("illnessDuration", "Tiempo de la enfermedad",
                        metadata: Meta(
                            promptHint: "¿Cuánto tiempo tiene con este problema?",
                            examples: ["2 días", "1 semana", "3 meses"]))
                ),
                Tab("antecedentes", "Antecedentes",
                    Textarea("familyHistory", "Antecedentes familiares", rows: 2),
                    Textarea("personalHistorySummary", "Antecedentes personales", rows: 2),
                    Label("hasHadLabel", "¿Tiene o ha tenido?"),
                    Container("hasHadGrid", 2,
                        YesNoRow("hasHighBloodPressure", "Presión alta"),
                        YesNoRow("hasHiv", "VIH"),
                        YesNoRow("hasLowBloodPressure", "Presión baja"),
                        YesNoRow("hasDiabetes", "Diabetes"),
                        YesNoRow("hasHepatitis", "Hepatitis"),
                        YesNoRow("hasAsthma", "Asma"),
                        YesNoRow("hasGastritis", "Gastritis"),
                        YesNoRow("smokes", "¿Fuma?"),
                        YesNoRow("hasUlcers", "Úlceras")
                    ),
                    Textarea("personalHistoryComment", "Comentario adicional", rows: 2),
                    // Grilla 12 col (6/2/4) con borde inferior en el label y los 3
                    // hijos alineados al fondo — mismo patron confirmado en la fila
                    // piloto "Enfermedades sanguíneas", ahora aplicado a todas las
                    // filas de detalle de este tab via YesNoDetailRow.
                    YesNoDetailRow("hasBloodDisorders", "Enfermedades sanguíneas",
                        Text("bloodDisordersDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasHeartProblems", "Problemas cardíacos",
                        Text("heartProblemsDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasOtherDisease", "¿Padece de alguna otra enfermedad?",
                        Text("otherDiseaseDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("brushesTeeth", "¿Cuántas veces al día se cepilla los dientes?",
                        Text("brushingFrequencyDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasGumBleeding", "¿Le sangra sus encías?",
                        Text("gumBleedingDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasAbnormalBleedingAfterExtraction", "¿Ha tenido hemorragias anormales después de una extracción?",
                        Text("abnormalBleedingDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasBruxism", "¿Hace rechinar o aprieta los dientes?",
                        Text("bruxismDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasOtherMouthComplaints", "Otras molestias en la boca",
                        Text("otherMouthComplaintsDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    // Alergias: el "detalle" es el typeahead ATC (con chips), visible solo
                    // si el radio de esta misma fila responde "Sí" — reemplaza el tab
                    // "Alergias" separado que se habia armado antes (un solo lugar,
                    // integrado al flujo del checklist, en vez de duplicarlo).
                    YesNoDetailRow("hasAllergiesFlag", "Alergias",
                        Typeahead("drugAllergies", "", "/api/medications/search?q={searchTerm}&limit=20",
                            displayPath: "nameGeneric", codePath: "atcCode", secondaryPath: "nameCommercial",
                            maxSelections: 10, hideLabel: true,
                            rules: [VisibilityRule("rule-drugAllergies-visible", "hasAllergiesFlag === 'si'")],
                            columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("hasMajorSurgery", "¿Ha tenido alguna operación grande en los últimos años?",
                        Text("majorSurgeryDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                    YesNoDetailRow("takesPermanentMedication", "¿Toma alguna medicación de manera permanente?",
                        Text("permanentMedicationDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" }))
                ),
                Tab("findings", "Hallazgos generales",
                    // Hallazgos NO asociados a una pieza especifica (ej. maloclusion,
                    // lesiones de tejidos blandos, gingivitis/enfermedad periodontal
                    // generalizada). Los hallazgos POR PIEZA (caries, fractura, etc.)
                    // y los procedimientos realizados ya se capturan en el odontograma
                    // (con su bodySite FDI correcto resuelto automaticamente por zona) —
                    // duplicarlos aqui con un select generico era redundante.
                    Typeahead("generalFindings", "Hallazgos generales (no asociados a una pieza)", "/api/diagnoses/search?q={searchTerm}&limit=20",
                        displayPath: "display", codePath: "code", maxSelections: 10)
                ),
                Tab("plan", "Plan",
                    Textarea("plan", "Plan de tratamiento", rows: 4, required: true)
                )
            );

    /// <summary>
    /// v2 de <see cref="OdontologiaFormSchema"/>: mismo nivel 1 (Historia Clínica), pero
    /// dentro de "Anam. Odontológica" el nivel 2 es un Accordion en vez de Tabs (replica
    /// el diseño real: Sección/Enfermedad actual/Antecedentes colapsables, no en pestañas).
    /// Contenido de cada sección DUPLICADO a propósito desde <see cref="OdontologiaAnamTabs"/>
    /// (no compartido) — v1 debe quedar congelada tal cual está, sin riesgo de que un
    /// cambio futuro acá la afecte retroactivamente si algún día se re-siembra desde cero.
    /// Mismos linkIds que v1 en todos los campos → el FHIR mapping no cambia.
    /// </summary>
    private static readonly string OdontologiaFormSchemaV2 = JsonSerializer.Serialize(
        FormSchema("odontologia", "Consulta odontológica",
            Tabs("historiaClinicaTabs",
                Tab("odontologiaAnam", "Anam. Odontológica",
                    OdontologiaAnamAccordion()
                ),
                Tab("notaEvolutiva", "Nota Evolutiva"),
                Tab("odontopediatrica", "Anam. Odontopediatra"),
                Tab("endodoncia", "Endodoncia"),
                Tab("signosVitales", "Signos Vitales"),
                Tab("consentimientos", "Consentimientos")
            )
        ),
        JsonOpts);

    private static object OdontologiaAnamAccordion() =>
        Accordion("layoutAccordion",
            Section("motivo", "Motivo de consulta", true,
                RichText("chiefComplaint", "Motivo de consulta", required: true,
                    metadata: Meta(
                        promptHint: "¿Cuál es el motivo de consulta odontológico?",
                        synonyms: ["motivo", "consulta", "molestia", "dolor"],
                        examples: ["Dolor en pieza 16", "Control de rutina", "Sangrado de encías"]))
            ),
            Section("enfermedadActual", "Enfermedad actual", false,
                RichText("presentIllness", "Evolución del problema actual"),
                Text("illnessDuration", "Tiempo de la enfermedad",
                    metadata: Meta(
                        promptHint: "¿Cuánto tiempo tiene con este problema?",
                        examples: ["2 días", "1 semana", "3 meses"]))
            ),
            Section("antecedentes", "Antecedentes", false,
                Textarea("familyHistory", "Antecedentes familiares", rows: 2),
                Textarea("personalHistorySummary", "Antecedentes personales", rows: 2),
                Label("hasHadLabel", "¿Tiene o ha tenido?"),
                Container("hasHadGrid", 2,
                    YesNoRow("hasHighBloodPressure", "Presión alta"),
                    YesNoRow("hasHiv", "VIH"),
                    YesNoRow("hasLowBloodPressure", "Presión baja"),
                    YesNoRow("hasDiabetes", "Diabetes"),
                    YesNoRow("hasHepatitis", "Hepatitis"),
                    YesNoRow("hasAsthma", "Asma"),
                    YesNoRow("hasGastritis", "Gastritis"),
                    YesNoRow("smokes", "¿Fuma?"),
                    YesNoRow("hasUlcers", "Úlceras")
                ),
                Textarea("personalHistoryComment", "Comentario adicional", rows: 2),
                YesNoDetailRow("hasBloodDisorders", "Enfermedades sanguíneas",
                    Text("bloodDisordersDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasHeartProblems", "Problemas cardíacos",
                    Text("heartProblemsDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasOtherDisease", "¿Padece de alguna otra enfermedad?",
                    Text("otherDiseaseDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("brushesTeeth", "¿Cuántas veces al día se cepilla los dientes?",
                    Text("brushingFrequencyDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasGumBleeding", "¿Le sangra sus encías?",
                    Text("gumBleedingDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasAbnormalBleedingAfterExtraction", "¿Ha tenido hemorragias anormales después de una extracción?",
                    Text("abnormalBleedingDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasBruxism", "¿Hace rechinar o aprieta los dientes?",
                    Text("bruxismDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasOtherMouthComplaints", "Otras molestias en la boca",
                    Text("otherMouthComplaintsDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasAllergiesFlag", "Alergias",
                    Typeahead("drugAllergies", "", "/api/medications/search?q={searchTerm}&limit=20",
                        displayPath: "nameGeneric", codePath: "atcCode", secondaryPath: "nameCommercial",
                        maxSelections: 10, hideLabel: true,
                        rules: [VisibilityRule("rule-drugAllergies-visible", "hasAllergiesFlag === 'si'")],
                        columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("hasMajorSurgery", "¿Ha tenido alguna operación grande en los últimos años?",
                    Text("majorSurgeryDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" })),
                YesNoDetailRow("takesPermanentMedication", "¿Toma alguna medicación de manera permanente?",
                    Text("permanentMedicationDetail", "", hideLabel: true, columnSpan: 4, styles: new { alignSelf = "end" }))
            ),
            Section("findings", "Hallazgos generales", false,
                Typeahead("generalFindings", "Hallazgos generales (no asociados a una pieza)", "/api/diagnoses/search?q={searchTerm}&limit=20",
                    displayPath: "display", codePath: "code", maxSelections: 10)
            ),
            Section("plan", "Plan", false,
                Textarea("plan", "Plan de tratamiento", rows: 4, required: true)
            )
        );

    // Odontología: common (chiefComplaint/plan a Encounter, diagnostics del typeahead general)
    // + generalFindings → Condition[] (ICD-11, multi-coding-to-conditions) — hallazgos NO
    //   asociados a una pieza especifica. Los hallazgos/procedimientos POR PIEZA se
    //   capturan en el odontograma (bodySite FDI resuelto por zona), no aqui.
    private static readonly string OdontologiaFormFhirMapping = SerializeMapping(
        [
            ..CommonMappingItems(),
            new
            {
                linkId = "generalFindings",
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
                linkId = "drugAllergies",
                mapsTo = "AllergyIntolerance",
                extractor = "multi-coding-to-allergies",
                codingSystem = CodeSystems.Atc,
                fixed_ = new
                {
                    clinicalStatus = "active",
                    verificationStatus = "confirmed",
                    category = "medication",
                },
            },
        ]);

    // ─── Mini-formularios del odontograma (linked forms, por zona) ─────────
    //
    // A diferencia del form clínico "odontologia" (una nota estructurada de la
    // consulta completa), estos se abren al hacer clic en UNA zona del
    // odontograma con el modo de formulario correspondiente activo — por eso
    // el código clínico (SNOMED) no es un picker: ya está implícito en cuál
    // formulario se abrió, así que va como `select` de una sola opción con
    // `defaultValue` (el runtime de craft-system lo precarga en el FormGroup
    // sin que el usuario tenga que tocarlo). El sitio anatómico (pieza FDI) NO
    // es un campo de este form — se resuelve desde la zona del diagrama que
    // disparó la apertura (`diagram_definitions.zone_body_site_map_json`);
    // hasta que esa integración exista en el frontend, el Procedure se emite
    // sin `bodySite` (comportamiento ya soportado por el extractor).

    private static readonly string OdontoCariesFormSchema = JsonSerializer.Serialize(
        FormSchema("odonto_caries", "Caries",
            Select("dentalFindingCode", "Hallazgo", required: true, defaultValue: "80967001", options:
            [
                ("80967001", "Caries dental"),
            ]),
            Select("severity", "Severidad", required: true, options:
            [
                ("leve", "Leve"),
                ("moderada", "Moderada"),
                ("severa", "Severa"),
            ]),
            Date("detectedAt", "Fecha de detección"),
            Textarea("notes", "Notas", rows: 3)
        ),
        JsonOpts);

    private static readonly string OdontoCariesFormFhirMapping = SerializeMapping(
        new
        {
            linkId = "dentalFindingCode",
            mapsTo = "Condition",
            extractor = "multi-coding-to-conditions",
            codingSystem = "http://snomed.info/sct",
            fixed_ = new
            {
                clinicalStatus = "active",
                verificationStatus = "confirmed",
                category = "encounter-diagnosis",
            },
        });

    private static readonly string OdontoEndodonciaFormSchema = JsonSerializer.Serialize(
        FormSchema("odonto_endodoncia", "Endodoncia",
            Select("dentalProcedureCode", "Procedimiento", required: true, defaultValue: "359862007", options:
            [
                ("359862007", "Endodoncia (tratamiento de conducto)"),
            ]),
            Number("canalCount", "Número de conductos"),
            Select("obturationMaterial", "Material de obturación", options:
            [
                ("gutapercha", "Gutapercha"),
                ("mta", "MTA"),
                ("biodentine", "Biodentine"),
            ]),
            Date("sessionDate", "Fecha de sesión"),
            Textarea("notes", "Notas", rows: 3)
        ),
        JsonOpts);

    private static readonly string OdontoEndodonciaFormFhirMapping = SerializeMapping(
        new
        {
            linkId = "dentalProcedureCode",
            mapsTo = "Procedure",
            extractor = "coding-to-procedure-with-bodysite",
            codingSystem = "http://snomed.info/sct",
            fixed_ = new { status = "completed" },
        });

    private static readonly string OdontoExtraccionFormSchema = JsonSerializer.Serialize(
        FormSchema("odonto_extraccion", "Extracción",
            Select("dentalProcedureCode", "Procedimiento", required: true, defaultValue: "25373001", options:
            [
                ("25373001", "Extracción dental"),
            ]),
            Select("reason", "Motivo", options:
            [
                ("caries_extensa", "Caries extensa"),
                ("fractura", "Fractura"),
                ("impactacion", "Impactación"),
                ("ortodoncia", "Indicación ortodóncica"),
            ]),
            Date("extractionDate", "Fecha de extracción"),
            Textarea("notes", "Notas", rows: 3)
        ),
        JsonOpts);

    private static readonly string OdontoExtraccionFormFhirMapping = SerializeMapping(
        new
        {
            linkId = "dentalProcedureCode",
            mapsTo = "Procedure",
            extractor = "coding-to-procedure-with-bodysite",
            codingSystem = "http://snomed.info/sct",
            fixed_ = new { status = "completed" },
        });
}