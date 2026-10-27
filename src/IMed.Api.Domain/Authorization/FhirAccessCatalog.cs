namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Lista blanca base de acceso FHIR y los límites que no son configurables (ADR-007).
///
/// <para><b>El reparto entre dato y código es deliberado.</b> Qué rol puede leer qué recurso es
/// política: va a la base y una clínica puede cambiarla. Que nadie escriba un
/// <c>DiagnosticReport</c> desde el lado público es un <b>invariante</b>: si viviera en una tabla,
/// alguien podría habilitarlo por error y saltarse el circuito de validación entero. Los
/// invariantes viven acá y no se pueden configurar.</para>
/// </summary>
public static class FhirAccessCatalog
{
    /// <summary>
    /// Recursos con circuito de negocio: <b>nunca</b> se escriben por el lado público.
    ///
    /// <para>Entran solo por <c>/api/lab/**</c>, que valida estado, rol y transiciones. Sin esta
    /// prohibición, un token de cualquier usuario podría hacer <c>PUT DiagnosticReport</c> con
    /// <c>status=final</c> y saltarse la validación y la liberación.</para>
    /// </summary>
    public static IReadOnlySet<string> WriteProtectedResources { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "DiagnosticReport",
            "Provenance",
            "Specimen",
            "ServiceRequest",
            "AuditEvent",
            "Location",
        };

    /// <summary>Nombre del recurso cuya escritura se decide por categoría.</summary>
    public const string ObservationResource = "Observation";

    /// <summary>
    /// Categorías de <c>Observation</c> que <b>nunca</b> se escriben desde el lado público.
    ///
    /// <para><b>Por qué la protección de Observation es por categoría y no por recurso.</b> El
    /// recurso cubre dos cosas que no se parecen en nada: el resultado de laboratorio, que sale de
    /// un circuito con orden, muestra, validación y liberación —y ahí un <c>POST</c> desde el
    /// navegador se saltaría todo eso—, y el dato clínico estructurado que el personal captura a
    /// mano, que no tiene circuito ninguno. Prohibir el recurso entero prohibía los signos vitales
    /// de preclínica y hacía fallar el Bundle de cierre de consulta completo.</para>
    ///
    /// <para><b>Sí, la categoría la manda el cliente</b> y por lo tanto se puede mentir. Lo que eso
    /// consigue es meter una Observation inventada en el expediente —el mismo poder que ya da
    /// <c>Condition</c> o <c>AllergyIntolerance</c>, que son escribibles—, no entrar al circuito de
    /// laboratorio: sin <c>ServiceRequest</c>, sin <c>Specimen</c>, sin correlativo y sin
    /// <c>DiagnosticReport</c> que la publique, no llega a ser un resultado. Lo que esta lista
    /// protege es el circuito, y ese sigue cerrado.</para>
    /// </summary>
    public static IReadOnlySet<string> WriteProtectedObservationCategories { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "laboratory" };

    /// <summary>Nombre del recurso cuya escritura se decide por sus destinos.</summary>
    public const string ProvenanceResource = "Provenance";

    /// <summary>
    /// Tipos de recurso que una <c>Provenance</c> escrita desde el lado público puede firmar
    /// (Épica P: el cierre de consulta deja constancia de qué antecedentes cambió).
    ///
    /// <para><b>Por qué una lista y no el recurso abierto.</b> El laboratorio usa <c>Provenance</c>
    /// como cadena de custodia: quién recibió la muestra, quién validó el informe. Abrir la escritura
    /// sin mirar a qué apunta permitiría fabricar una firma de validación sobre un resultado. Los
    /// antecedentes no tienen circuito que proteger; los resultados sí.</para>
    ///
    /// <para>Se amplía por fase: cada sección de antecedentes nueva agrega su recurso acá en la
    /// misma fase que lo empieza a escribir.</para>
    /// </summary>
    public static IReadOnlySet<string> PublicProvenanceTargets { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "AllergyIntolerance", "List",
            // P-2: problemas activos y medicación habitual.
            "Condition", "MedicationStatement",
            // P-3: quirúrgicos, familiares, vacunas y dispositivos. Los hábitos y los datos
            // gineco-obstétricos son Observations y NO entran: desde una referencia no se puede
            // saber la categoría, y Observation incluye resultados de laboratorio. Esas viajan en
            // la misma transacción con su encuentro y su autor.
            "Procedure", "FamilyMemberHistory", "Immunization", "DeviceUsage",
        };

    /// <summary>
    /// Operaciones <c>$…</c> permitidas. Todo lo demás se rechaza: <c>$expunge</c> borra sin
    /// dejar rastro y <c>$reindex</c> puede tumbar el servidor bajo carga.
    /// </summary>
    public static IReadOnlySet<string> AllowedOperations { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "$expand", "$lookup", "$validate-code" };

    /// <summary>
    /// Parámetros de búsqueda que ensanchan el alcance más allá del recurso pedido y por lo tanto
    /// se declaran explícitamente. <c>_include</c> y <c>_revinclude</c> son el camino clásico por
    /// el que una búsqueda acotada devuelve datos que nadie autorizó.
    /// </summary>
    public static IReadOnlySet<string> ScopeWideningParameters { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_include", "_revinclude", "_has", "_query" };

    /// <summary>Tope de página. Una búsqueda sin límite es una exportación del expediente completo.</summary>
    public const int MaxCount = 200;

    /// <summary>Combinaciones <c>_include</c>/<c>_revinclude</c> aceptadas, por recurso base.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedIncludes { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Encounter"] = new HashSet<string>(StringComparer.Ordinal) { "Encounter:patient", "Encounter:practitioner" },
            ["Appointment"] = new HashSet<string>(StringComparer.Ordinal) { "Appointment:patient", "Appointment:practitioner" },
            ["QuestionnaireResponse"] = new HashSet<string>(StringComparer.Ordinal) { "QuestionnaireResponse:encounter" },
        };

    /// <summary>
    /// Reglas base: (recurso, verbo, permiso que lo habilita). Se siembran como globales y cada
    /// organización puede agregar las suyas.
    /// </summary>
    public static IReadOnlyList<(string Resource, FhirVerb Verb, string Permission)> BaseRules =>
    [
        // --- Expediente del paciente -----------------------------------------
        ..Crud("Patient", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Reads("Patient", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.RecordReadOwn),
        ..Crud("RelatedPerson", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),

        ..Crud("Encounter", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Reads("Encounter", PermissionCatalog.RecordReadOwn),
        ..Crud("Condition", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("AllergyIntolerance", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("MedicationRequest", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("QuestionnaireResponse", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("Appointment", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Reads("Appointment", PermissionCatalog.RecordSearchDemographics),

        // --- Resultados: lectura sí, escritura jamás por acá ------------------
        ..Reads("Observation", PermissionCatalog.LabResultReadAny, PermissionCatalog.LabResultReadOrdered),
        // Signos vitales y antropometría: los captura el personal a mano —preclínica y cierre de
        // consulta— y no hay circuito de negocio que atravesar. La categoría `laboratory` sigue
        // cerrada por invariante, así que esta regla no abre resultados.
        ..Writes("Observation", PermissionCatalog.RecordWrite),
        ..Reads("DiagnosticReport", PermissionCatalog.LabResultReadAny, PermissionCatalog.LabResultReadOrdered),
        ..Reads("ServiceRequest", PermissionCatalog.LabRead),
        ..Reads("Specimen", PermissionCatalog.LabRead),
        ..Reads("Provenance", PermissionCatalog.LabRead),
        ..Reads("Provenance", PermissionCatalog.RecordReadAny),
        // Solo alta y solo sobre antecedentes: el evaluador la deja pasar por encima de
        // WriteProtectedResources únicamente en ese caso (ver PublicProvenanceTargets). Una
        // constancia no se corrige: si estuvo mal, se escribe otra.
        ("Provenance", FhirVerb.Create, PermissionCatalog.RecordWrite),

        // Listas de antecedentes por sección (Épica P §2.2): quién revisó la sección y cuándo, y
        // si está vacía a propósito ("ninguno conocido").
        ..Crud("List", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        // Medicación habitual (Épica P-2): lo que el paciente toma, no lo que se le receta.
        ..Crud("MedicationStatement", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        // Resto de los antecedentes (Épica P-3).
        ..Crud("Procedure", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("FamilyMemberHistory", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("Immunization", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),
        ..Crud("DeviceUsage", PermissionCatalog.RecordReadAny, PermissionCatalog.RecordWrite),

        // --- Directorio y catálogo: no son PHI --------------------------------
        ..Reads("Practitioner", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("PractitionerRole", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("Organization", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("Location", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("Questionnaire", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("ValueSet", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("CodeSystem", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("HealthcareService", PermissionCatalog.RecordSearchDemographics, PermissionCatalog.LabRead),
        ..Reads("Schedule", PermissionCatalog.RecordSearchDemographics),
        ..Reads("Slot", PermissionCatalog.RecordSearchDemographics),

        // El diseñador de formularios administra sus propias definiciones.
        ..Crud("Questionnaire", PermissionCatalog.LabManage, PermissionCatalog.LabManage),
        // La sede se escribe solo por /api/locations (FAC-9): el Location es la proyección de
        // organization_location, con id determinístico y su razón social. Uno escrito a mano desde
        // el cliente sería una sede que el backend no conoce.
        ..Reads("Location", PermissionCatalog.OrgLocationsManage),
        ..Crud("PractitionerRole", PermissionCatalog.OrgTeamManage, PermissionCatalog.OrgTeamManage),
    ];

    /// <summary>Lectura y búsqueda para cada permiso indicado.</summary>
    private static IEnumerable<(string, FhirVerb, string)> Reads(string resource, params string[] permissions) =>
        permissions.SelectMany(p => new[]
        {
            (resource, FhirVerb.Read, p),
            (resource, FhirVerb.Search, p),
        });

    /// <summary>Alta y modificación para cada permiso indicado, sin tocar la lectura.</summary>
    private static IEnumerable<(string, FhirVerb, string)> Writes(string resource, params string[] permissions) =>
        permissions.SelectMany(p => new[]
        {
            (resource, FhirVerb.Create, p),
            (resource, FhirVerb.Update, p),
        });

    /// <summary>Lectura con un permiso y escritura con otro. El borrado nunca se abre por acá.</summary>
    private static IEnumerable<(string, FhirVerb, string)> Crud(
        string resource, string readPermission, string writePermission) =>
        [
            (resource, FhirVerb.Read, readPermission),
            (resource, FhirVerb.Search, readPermission),
            (resource, FhirVerb.Create, writePermission),
            (resource, FhirVerb.Update, writePermission),
        ];
}
