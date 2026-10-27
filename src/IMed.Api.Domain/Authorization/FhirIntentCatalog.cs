namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Las formas de consulta que la aplicación tiene permitido hacer (ADR-007 §4, SEC-4).
///
/// <para><b>Por qué vive en código y no en una tabla.</b> Un intent declara qué combinación de
/// parámetros es segura para un recurso. Poner eso en una tabla es poner en una tabla la potestad
/// de abrir agujeros: alguien agrega una fila con <c>_include</c> libre y la búsqueda acotada pasa
/// a devolver lo que nadie autorizó. Qué rol tiene qué permiso sigue siendo dato —
/// <c>role_permission</c> y <c>fhir_access_rule</c>—; qué forma de consulta existe, no.</para>
///
/// <para><b>De dónde salió esta lista.</b> De dos fuentes, y ninguna es una suposición: las 13
/// formas de búsqueda que la bitácora de <c>phi_access</c> registró en tres días de uso real, y un
/// barrido de los servicios FHIR del cliente para las pantallas que en esas pasadas nunca se
/// abrieron. Lo que ninguna de las dos vio no está acá — y por eso SEC-4 arranca en sombra: una
/// forma sin intent aparece en el log con la consulta exacta al lado, y la lista se completa
/// midiendo en vez de adivinando.</para>
///
/// <para><b>El orden importa.</b> Se toma el primer intent que encaja, así que lo específico va
/// antes que lo general: <c>agenda.mine</c> tiene que evaluarse antes que <c>agenda.all</c>, o una
/// consulta de la propia agenda terminaría exigiendo lectura amplia.</para>
/// </summary>
public static class FhirIntentCatalog
{
    // --- Parámetros que identifican a una persona ---------------------------
    // Traen un dato que el que busca ya tiene que conocer. Es la línea entre encontrar a alguien y
    // descargar el padrón: buscar por nombre obliga a saber a quién buscás, enumerar no.
    private static readonly IReadOnlySet<string> IdentifyingPatient =
        P("name", "family", "given", "identifier", "telecom", "phone", "email", "birthdate");

    public static IReadOnlyList<FhirIntent> All =>
    [
        // ================= Paciente ==========================================
        new("patient.find", "Patient", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            RequiredAnyOf: IdentifyingPatient,
            Allowed: P("active"),
            Projection: PatientIdentificationElements),

        // La enumeración del padrón: sin filtro que nombre a nadie. Es una consulta legítima para
        // quien administra la organización y una exportación del censo para quien solo agenda.
        new("patient.census", "Patient", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            Allowed: P("active")),

        new("patient.open", "Patient", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("patient.register", "Patient", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("patient.amend", "Patient", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // ================= Contactos del paciente ============================
        new("patient.contacts", "RelatedPerson", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("patient"),
            Allowed: P("active")),
        new("patient.contact.open", "RelatedPerson", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("patient.contact.add", "RelatedPerson", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("patient.contact.amend", "RelatedPerson", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // ================= Agenda ============================================
        // Filtrada por tu propio Practitioner. Es lo único que se puede afirmar sobre pertenencia
        // sin consultar HAPI: quién sos lo dice el token.
        new("agenda.mine", "Appointment", FhirVerb.Search, PermissionCatalog.RecordReadOwn,
            RequiredAnyOf: P("actor"),
            Allowed: P("date", "status"),
            SelfPractitionerParam: "actor"),

        new("patient.appointments", "Appointment", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("patient"),
            Allowed: P("date", "status")),

        // Cae acá la agenda de otro profesional y la cola de recepción (`status=arrived`).
        new("agenda.all", "Appointment", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            Allowed: P("actor", "date", "status")),

        new("appointment.open", "Appointment", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("appointment.schedule", "Appointment", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("appointment.amend", "Appointment", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // ================= Encuentro =========================================
        new("encounter.byAppointment", "Encounter", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("appointment"),
            Allowed: P("status")),
        new("patient.encounters", "Encounter", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("status", "date")),
        new("encounter.open", "Encounter", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("encounter.start", "Encounter", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("encounter.amend", "Encounter", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // ================= Contenido clínico del expediente ==================
        new("patient.conditions", "Condition", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient", "encounter"),
            Allowed: P("clinical-status", "category")),
        new("condition.open", "Condition", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("condition.record", "Condition", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("condition.amend", "Condition", FhirVerb.Update, PermissionCatalog.RecordWrite),

        new("patient.allergies", "AllergyIntolerance", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("patient", "subject"),
            Allowed: P("clinical-status", "category")),
        new("allergy.open", "AllergyIntolerance", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("allergy.record", "AllergyIntolerance", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("allergy.amend", "AllergyIntolerance", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // Antecedentes por sección (Épica P §2.2): una lista por paciente y código LOINC de sección.
        new("patient.historyLists", "List", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("code", "status")),
        new("historyList.open", "List", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("historyList.record", "List", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("historyList.amend", "List", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // Medicación habitual (Épica P-2). `status` acota recorded/draft/entered-in-error.
        new("patient.medicationStatements", "MedicationStatement", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("status", "encounter")),
        new("medicationStatement.open", "MedicationStatement", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("medicationStatement.record", "MedicationStatement", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("medicationStatement.amend", "MedicationStatement", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // Resto de los antecedentes (Épica P-3): siempre de un paciente.
        new("patient.procedures", "Procedure", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("status")),
        new("procedure.open", "Procedure", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("procedure.record", "Procedure", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("procedure.amend", "Procedure", FhirVerb.Update, PermissionCatalog.RecordWrite),

        new("patient.familyHistory", "FamilyMemberHistory", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("patient"),
            Allowed: P("status")),
        new("familyHistory.open", "FamilyMemberHistory", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("familyHistory.record", "FamilyMemberHistory", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("familyHistory.amend", "FamilyMemberHistory", FhirVerb.Update, PermissionCatalog.RecordWrite),

        new("patient.immunizations", "Immunization", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("patient"),
            Allowed: P("status")),
        new("immunization.open", "Immunization", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("immunization.record", "Immunization", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("immunization.amend", "Immunization", FhirVerb.Update, PermissionCatalog.RecordWrite),

        new("patient.devices", "DeviceUsage", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("patient"),
            Allowed: P("status")),
        new("device.open", "DeviceUsage", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("device.record", "DeviceUsage", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("device.amend", "DeviceUsage", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // Hábitos, gineco-obstétricos y perinatales: Observations de categoría `social-history`.
        // La categoría se acota por valor, como en signos vitales: sin eso, esta forma leería
        // resultados de laboratorio.
        new("patient.historyObservations", "Observation", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("date", "status", "code"),
            RequiredValues: Values(("category", P("social-history")))),

        // Constancia del cierre: qué antecedentes cambió esta consulta.
        new("history.provenance", "Provenance", FhirVerb.Create, PermissionCatalog.RecordWrite),
        // La consulta cerrada muestra qué antecedentes cambió (Épica P §3.6): las constancias de
        // un encuentro. Exige el encuentro: sin él sería la cadena de custodia de toda la clínica.
        new("encounter.provenance", "Provenance", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("encounter")),

        // Signos vitales. `category` está acotada por valor a propósito: exigir que el parámetro
        // venga sin mirar qué dice dejaría que `category=laboratory` leyera resultados por acá.
        new("patient.vitals", "Observation", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("date", "status"),
            RequiredValues: Values(("category", P("vital-signs")))),
        new("encounter.observations", "Observation", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("encounter"),
            Allowed: P("status", "category")),
        new("vitals.capture", "Observation", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("vitals.amend", "Observation", FhirVerb.Update, PermissionCatalog.RecordWrite),

        new("encounter.prescriptions", "MedicationRequest", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("encounter"),
            Allowed: P("status")),
        new("patient.prescriptions", "MedicationRequest", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("status")),
        new("prescription.open", "MedicationRequest", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("prescription.write", "MedicationRequest", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("prescription.amend", "MedicationRequest", FhirVerb.Update, PermissionCatalog.RecordWrite),

        new("encounter.forms", "QuestionnaireResponse", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("encounter"),
            Allowed: P("status")),
        new("patient.forms", "QuestionnaireResponse", FhirVerb.Search, PermissionCatalog.RecordReadAny,
            RequiredAnyOf: P("subject", "patient"),
            Allowed: P("status")),
        new("form.answers.open", "QuestionnaireResponse", FhirVerb.Read, PermissionCatalog.RecordReadAny),
        new("form.answer", "QuestionnaireResponse", FhirVerb.Create, PermissionCatalog.RecordWrite),
        new("form.answer.amend", "QuestionnaireResponse", FhirVerb.Update, PermissionCatalog.RecordWrite),

        // El cierre de consulta. Las entradas se evalúan una por una con estas mismas reglas.
        new("encounter.close", "Bundle", FhirVerb.Create, PermissionCatalog.RecordWrite),

        // ================= Directorio y catálogo: no son PHI =================
        new("directory.practitioners", "Practitioner", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("active", "name", "identifier", "family", "given")),
        new("practitioner.open", "Practitioner", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics),

        new("team.roles", "PractitionerRole", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("practitioner", "organization", "location", "active", "role", "specialty", "identifier", "_id")),
        new("team.role.open", "PractitionerRole", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics),
        new("team.role.add", "PractitionerRole", FhirVerb.Create, PermissionCatalog.OrgTeamManage),
        new("team.role.amend", "PractitionerRole", FhirVerb.Update, PermissionCatalog.OrgTeamManage),

        new("locations.list", "Location", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("name", "status", "type", "organization", "partof", "identifier", "address", "_id")),
        new("location.open", "Location", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics),
        new("location.add", "Location", FhirVerb.Create, PermissionCatalog.OrgLocationsManage),
        new("location.amend", "Location", FhirVerb.Update, PermissionCatalog.OrgLocationsManage),

        new("organizations.list", "Organization", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("name", "identifier", "active", "_id")),
        new("organization.open", "Organization", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics),

        // Diseñador de formularios: leer el catálogo es de cualquiera del equipo, publicar no.
        new("forms.catalog", "Questionnaire", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("status", "url", "name", "title", "context", "identifier", "_id")),
        new("form.definition.open", "Questionnaire", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics),
        new("form.publish", "Questionnaire", FhirVerb.Create, PermissionCatalog.LabManage),
        new("form.definition.amend", "Questionnaire", FhirVerb.Update, PermissionCatalog.LabManage),

        // ================= Terminología ======================================
        // Llegan como Operation y el evaluador ya las reescribió a Read en la puerta 1.
        new("terminology.expand", "ValueSet", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("url", "filter", "count", "offset", "displayLanguage", "includeDesignations")),
        new("terminology.valueset.open", "ValueSet", FhirVerb.Search, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("url", "name", "status", "_id")),
        new("terminology.lookup", "CodeSystem", FhirVerb.Read, PermissionCatalog.RecordSearchDemographics,
            Allowed: P("url", "system", "code", "version", "displayLanguage", "property")),
    ];

    /// <summary>
    /// Campos de <c>Patient</c> que ve quien solo puede buscar por identificación.
    ///
    /// <para>Es poco lo que recorta, y conviene ser honesto sobre eso: <c>Patient</c> en FHIR es
    /// casi todo identificación. Lo que de verdad separa a recepción del expediente es no tener
    /// método sobre <c>Encounter</c>, <c>Condition</c> ni <c>Observation</c>. Esto recorta los
    /// bordes —contactos familiares, médico de cabecera, enlaces a otros registros— y deja fuera lo
    /// que se agregue mañana al recurso sin que nadie revise esta lista.</para>
    /// </summary>
    public const string PatientIdentificationElements =
        "id,meta,identifier,active,name,telecom,gender,birthDate,address,managingOrganization";

    private static HashSet<string> P(params string[] names) =>
        new(names, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IReadOnlySet<string>> Values(
        params (string Parameter, IReadOnlySet<string> Accepted)[] pairs) =>
        pairs.ToDictionary(x => x.Parameter, x => x.Accepted, StringComparer.OrdinalIgnoreCase);
}
