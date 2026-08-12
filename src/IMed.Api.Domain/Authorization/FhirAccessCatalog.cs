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
            "Observation",
            "Provenance",
            "Specimen",
            "ServiceRequest",
            "AuditEvent",
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
        ..Reads("DiagnosticReport", PermissionCatalog.LabResultReadAny, PermissionCatalog.LabResultReadOrdered),
        ..Reads("ServiceRequest", PermissionCatalog.LabRead),
        ..Reads("Specimen", PermissionCatalog.LabRead),
        ..Reads("Provenance", PermissionCatalog.LabRead),

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
        ..Crud("Location", PermissionCatalog.OrgLocationsManage, PermissionCatalog.OrgLocationsManage),
        ..Crud("PractitionerRole", PermissionCatalog.OrgTeamManage, PermissionCatalog.OrgTeamManage),
    ];

    /// <summary>Lectura y búsqueda para cada permiso indicado.</summary>
    private static IEnumerable<(string, FhirVerb, string)> Reads(string resource, params string[] permissions) =>
        permissions.SelectMany(p => new[]
        {
            (resource, FhirVerb.Read, p),
            (resource, FhirVerb.Search, p),
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
