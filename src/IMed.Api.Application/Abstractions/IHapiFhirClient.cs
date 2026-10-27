using System.Text.Json.Nodes;
namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto hacia HAPI FHIR Server. Minimal en Hito 1 con extensiones para Epica B
/// (partition management).
/// </summary>
public interface IHapiFhirClient
{
    /// <summary>
    /// Crea una particion nombrada en HAPI via la operacion
    /// <c>$partition-management-create-partition</c>. Idempotente: si la
    /// particion ya existe, retorna sin error.
    /// </summary>
    /// <returns>El nombre de la particion creada (igual al input).</returns>
    Task<string> CreatePartitionAsync(string partitionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un recurso FHIR <c>Organization</c>. Si <paramref name="request"/>
    /// trae <c>PartitionName</c>, se envia el header <c>X-Tenant-Partition</c>
    /// para que el interceptor escriba en esa particion. Si no, escribe en
    /// la particion default.
    /// </summary>
    /// <returns>El id server-assigned por HAPI.</returns>
    Task<string> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea o reemplaza (PUT, id determinístico) el <c>Organization</c> de una razón social,
    /// <c>partOf</c> la organización tenant (FAC-9). Idempotente.
    /// </summary>
    Task UpsertLegalEntityOrganizationAsync(UpsertLegalEntityOrganizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea o reemplaza (PUT, id determinístico) el <c>Location</c> de una sede, gestionado por su
    /// razón social (FAC-9). Idempotente.
    /// </summary>
    Task UpsertLocationAsync(UpsertLocationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un Practitioner FHIR en la partición indicada. Devuelve el id server-assigned.
    /// </summary>
    Task<string> CreatePractitionerAsync(UpsertPractitionerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reemplaza (PUT) un Practitioner existente. El id es server-assigned por
    /// HAPI; el caller debe pasar el mismo id retornado al crear.
    /// </summary>
    Task UpdatePractitionerAsync(string practitionerFhirId, UpsertPractitionerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lee un Practitioner. Devuelve null si no existe (404 → null en vez de throw).
    /// </summary>
    Task<PractitionerSnapshot?> GetPractitionerAsync(string practitionerFhirId, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>Crea un PractitionerRole FHIR en la partición indicada.</summary>
    Task<string> CreatePractitionerRoleAsync(UpsertPractitionerRoleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reemplaza (PUT) un PractitionerRole existente.</summary>
    Task UpdatePractitionerRoleAsync(string practitionerRoleFhirId, UpsertPractitionerRoleRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lee un PractitionerRole. Devuelve null si no existe (404 → null en vez de throw).
    /// </summary>
    Task<PractitionerRoleSnapshot?> GetPractitionerRoleAsync(string practitionerRoleFhirId, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista PractitionerRoles activos de una organización. Búsqueda FHIR:
    /// <c>GET /PractitionerRole?organization=Organization/{id}&amp;active=true</c>.
    /// Devuelve un array de snapshots con la info que <c>GET /api/team</c> necesita
    /// (id, practitioner ref, specialty). El caller hace los lookups paralelos
    /// de Practitioner+UserPractitionerAssignment+KC user.
    /// </summary>
    Task<IReadOnlyList<PractitionerRoleListItem>> SearchPractitionerRolesByOrganizationAsync(
        string organizationFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca Patients dentro de la partition cuyo <c>identifier</c> matchea
    /// el par <c>system|value</c>. Devuelve los matches (puede haber más de
    /// uno si la data tiene drift, aunque normalmente 0 o 1).
    /// </summary>
    Task<IReadOnlyList<PatientMatch>> SearchPatientsByIdentifierAsync(
        string identifierSystem,
        string identifierValue,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lee un Patient por id. Devuelve null si no existe. Usado por el
    /// renderer de PDF de receta (K5) para resolver el nombre + identifier
    /// del paciente en el header.
    /// </summary>
    Task<PatientSnapshot?> GetPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un <c>RelatedPerson</c> FHIR R5 en la partition indicada.
    /// Vincula al Patient via <c>RelatedPerson.patient</c>. Devuelve el id
    /// server-assigned.
    /// </summary>
    Task<string> CreateRelatedPersonAsync(
        UpsertRelatedPersonRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reemplaza (PUT) un RelatedPerson existente.</summary>
    Task UpdateRelatedPersonAsync(
        string relatedPersonFhirId,
        UpsertRelatedPersonRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista RelatedPersons activos asociados a un Patient. Búsqueda:
    /// <c>GET /RelatedPerson?patient=Patient/{id}&amp;active=true</c>.
    /// </summary>
    Task<IReadOnlyList<RelatedPersonSnapshot>> SearchRelatedPersonsByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un <c>AllergyIntolerance</c> FHIR R5 en la partition indicada
    /// (Épica J1). Vincula al Patient via <c>AllergyIntolerance.patient</c>.
    /// Devuelve el id server-assigned.
    /// </summary>
    Task<string> CreateAllergyIntoleranceAsync(
        UpsertAllergyIntoleranceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista AllergyIntolerances asociados a un Patient. Filtra por
    /// <c>clinicalStatus=active</c> por default (los inactivos quedan visibles
    /// solo para historia clínica). Búsqueda:
    /// <c>GET /AllergyIntolerance?patient=Patient/{id}&amp;clinical-status=active</c>.
    /// </summary>
    Task<IReadOnlyList<AllergyIntoleranceSnapshot>> SearchAllergyIntolerancesByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    // ──────────────── MedicationRequest (Épica K) ────────────────

    /// <summary>
    /// Crea un <c>MedicationRequest</c> FHIR R5 en la partition indicada
    /// (Épica K). Status inicial típico: <c>draft</c> mientras el médico edita
    /// la receta; <c>active</c> al cerrar el encounter. Devuelve el id
    /// server-assigned.
    /// </summary>
    Task<string> CreateMedicationRequestAsync(
        UpsertMedicationRequestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reemplaza (PUT) un MedicationRequest existente.</summary>
    Task UpdateMedicationRequestAsync(
        string medicationRequestFhirId,
        UpsertMedicationRequestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lee un MedicationRequest por id. Devuelve null si no existe.
    /// Usado por el renderer PDF (K5) para obtener todos los datos de la receta.
    /// </summary>
    Task<MedicationRequestSnapshot?> GetMedicationRequestAsync(
        string medicationRequestFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista MedicationRequests asociados a un Encounter. El médico los ve
    /// dentro del <c>EncounterActivePage</c>.
    /// </summary>
    Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsByEncounterAsync(
        string encounterFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista MedicationRequests <c>active</c>/<c>completed</c> de un Patient
    /// (historia). La tab "Recetas" del patient-detail los lista.
    /// </summary>
    Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista los MedicationRequests que comparten un <c>groupIdentifier</c> — es
    /// decir, todos los medicamentos de una misma "receta" (multi-medicamento).
    /// Usado por el renderer PDF de receta agrupada.
    /// </summary>
    Task<IReadOnlyList<MedicationRequestSnapshot>> SearchMedicationRequestsByGroupAsync(
        string groupIdentifierValue,
        string partitionName,
        CancellationToken cancellationToken = default);

    // ──────────────── ServiceRequest / Lab Orders (Épica L) ────────────────

    /// <summary>
    /// Crea un <c>ServiceRequest</c> FHIR R5 con <c>category=laboratory</c>
    /// (Épica L). Status típico inicial: <c>draft</c> mientras el médico edita,
    /// <c>active</c> al cerrar el encounter.
    /// </summary>
    Task<string> CreateLabOrderAsync(
        UpsertLabOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reemplaza (PUT) un ServiceRequest existente.</summary>
    Task UpdateLabOrderAsync(
        string serviceRequestFhirId,
        UpsertLabOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lee un ServiceRequest por id. Devuelve null si no existe. Usado por el
    /// renderer PDF (L5) para obtener todos los datos de la orden.
    /// </summary>
    Task<LabOrderSnapshot?> GetLabOrderAsync(
        string serviceRequestFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>Lista órdenes de laboratorio asociadas a un Encounter.</summary>
    Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersByEncounterAsync(
        string encounterFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista las órdenes de laboratorio que comparten un <c>requisition</c> — es
    /// decir, todos los exámenes/paneles de una misma "orden de laboratorio".
    /// Usado por el renderer PDF de orden agrupada.
    /// </summary>
    Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersByRequisitionAsync(
        string requisitionValue,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista órdenes de laboratorio <c>active</c>/<c>completed</c> de un Patient
    /// (historia). La tab "Laboratorio" del patient-detail consume esto.
    /// </summary>
    Task<IReadOnlyList<LabOrderSnapshot>> SearchLabOrdersByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    // ---- LIS-3: muestras, cadena de custodia, solicitante externo ----

    /// <summary>Crea un <c>Specimen</c> FHIR R5 (recepción de muestra, LIS-3). Devuelve el id.</summary>
    Task<string> CreateSpecimenAsync(
        UpsertSpecimenRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Órdenes de laboratorio activas de la partición (cola de recepción pendiente, R1-F5).</summary>
    /// <summary>
    /// Órdenes de laboratorio en <c>draft</c> de una organización.
    ///
    /// <para>La usa la reconciliación: una orden en borrador cuyo encuentro ya se cerró es una
    /// orden que el médico pidió y que el laboratorio nunca va a ver.</para>
    /// </summary>
    Task<IReadOnlyList<LabOrderSnapshot>> SearchDraftLabOrdersAsync(
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Estado de un <c>Encounter</c>, o null si no existe.
    ///
    /// <para>Se devuelve el string crudo y no un enum: lo único que se decide con esto es si la
    /// consulta ya terminó, y encerrar el vocabulario de FHIR en un enum propio obliga a tocar
    /// código cada vez que aparece un estado nuevo que no nos importa.</para>
    /// </summary>
    Task<string?> GetEncounterStatusAsync(
        string encounterFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LabOrderSnapshot>> SearchActiveLabOrdersAsync(
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>Lee un Specimen por id. Null si no existe.</summary>
    Task<SpecimenSnapshot?> GetSpecimenAsync(
        string specimenFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>Muestras de un paciente (historia/trazabilidad).</summary>
    Task<IReadOnlyList<SpecimenSnapshot>> SearchSpecimensByPatientAsync(
        string patientFhirId,
        string partitionName,
        CancellationToken cancellationToken = default);

    /// <summary>Crea un <c>Provenance</c> FHIR R5 (cadena de custodia / firma). Devuelve el id.</summary>
    Task<string> CreateProvenanceAsync(
        CreateProvenanceRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un <c>Practitioner</c> "ligero" para un médico solicitante externo
    /// (LIS-DEC-10) — sin usuario KC. Devuelve el id.
    /// </summary>
    Task<string> CreateExternalPractitionerAsync(
        ExternalPractitionerRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Busca un Practitioner externo por nombre (dedupe). Null si no existe.</summary>
    Task<string?> FindExternalPractitionerByNameAsync(
        string fullName,
        string partitionName,
        CancellationToken cancellationToken = default);

    // ---- LIS-4: resultados ----

    /// <summary>Crea un <c>Observation</c> FHIR R5 (resultado de laboratorio). Devuelve el id.</summary>
    Task<string> CreateObservationAsync(
        UpsertObservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reemplaza un <c>Observation</c> existente (corrección de resultado, LIS-4b). HAPI versiona
    /// el recurso, así que el valor anterior queda accesible en <c>_history</c> y la corrección
    /// es auditable sin necesidad de una tabla propia.
    /// </summary>
    Task UpdateObservationAsync(
        string observationFhirId,
        UpsertObservationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Crea un <c>DiagnosticReport</c> FHIR R5 (informe que agrupa Observations). Devuelve el id.</summary>
    Task<string> CreateDiagnosticReportAsync(
        UpsertDiagnosticReportRequest request,
        CancellationToken cancellationToken = default);

    Task<DiagnosticReportSnapshot?> GetDiagnosticReportAsync(
        string diagnosticReportFhirId, string partitionName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiagnosticReportSnapshot>> SearchDiagnosticReportsByPatientAsync(
        string patientFhirId, string partitionName, CancellationToken cancellationToken = default);

    Task<ObservationSnapshot?> GetObservationAsync(
        string observationFhirId, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Valor numérico más reciente de un paciente para un LOINC (para delta check, LIS-5).
    /// Null si no hay histórico numérico.
    /// </summary>
    Task<decimal?> GetLatestNumericObservationAsync(
        string patientFhirId, string loincCode, string partitionName, CancellationToken cancellationToken = default);

    // ---- LIS-6: validación / liberación ----

    /// <summary>
    /// Escribe el <c>conclusion</c> de un <c>DiagnosticReport</c> vía JSON Patch. Lo usa el
    /// comentario del validador al liberar: va en el informe para que viaje con el resultado
    /// e salga impreso, en vez de quedar en una tabla interna que nadie consulta.
    /// </summary>
    Task PatchDiagnosticReportConclusionAsync(
        string reportFhirId, string conclusion, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>Actualiza el <c>status</c> de un recurso vía JSON Patch (LIS-6).</summary>
    Task PatchResourceStatusAsync(
        string resourceType, string resourceFhirId, string status, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>Informes en estado <c>preliminary</c> de la partición (pendientes de validación).</summary>
    Task<IReadOnlyList<DiagnosticReportSnapshot>> SearchPreliminaryReportsAsync(
        string partitionName, CancellationToken cancellationToken = default);

    /// <summary>Histórico numérico de un paciente para un LOINC (tendencia, LIS-6). Más reciente primero.</summary>
    Task<IReadOnlyList<ObservationHistoryPoint>> GetObservationHistoryAsync(
        string patientFhirId, string loincCode, string partitionName, int count, CancellationToken cancellationToken = default);

    // ---- Acceso genérico (Épica P) ----------------------------------------------------------
    // Los métodos tipados de arriba cubren un recurso cada uno. Estos tres existen para los flujos
    // que tienen que reescribir un recurso preservando todo lo que no tocan —un PUT en FHIR
    // reemplaza el recurso entero, y un snapshot tipado perdería los campos que no modela— y para
    // armar transacciones de varios recursos.

    /// <summary>Lee un recurso tal como lo guarda HAPI. <c>null</c> si no existe.</summary>
    Task<JsonObject?> ReadResourceAsync(
        string resourceType, string id, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca recursos con una query FHIR ya armada (sin el <c>?</c>), siempre sin caché de HAPI:
    /// quien llama acá va a decidir qué escribir según lo que encuentre.
    /// </summary>
    Task<IReadOnlyList<JsonObject>> SearchResourcesAsync(
        string resourceType, string query, string partitionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ejecuta un Bundle <c>transaction</c>: o se escribe todo o nada. Devuelve el resultado de
    /// cada entrada en el mismo orden. Un conflicto de versión (<c>ifMatch</c>) se reporta como
    /// <see cref="FhirVersionConflictException"/> para distinguirlo de un fallo.
    /// </summary>
    Task<IReadOnlyList<FhirTransactionEntryResult>> ExecuteTransactionAsync(
        JsonObject bundle, string partitionName, CancellationToken cancellationToken = default);
}

/// <summary>Resultado de una entrada de transacción: estado HTTP y ubicación del recurso escrito.</summary>
public sealed record FhirTransactionEntryResult(string Status, string? Location)
{
    /// <summary>Id lógico extraído de <c>Location</c> (<c>Encounter/123/_history/1</c> → <c>123</c>).</summary>
    public string? ResourceId => Part(0);

    /// <summary>Tipo de recurso extraído de <c>Location</c>.</summary>
    public string? ResourceType => Part(1);

    private string? Part(int offsetFromId)
    {
        if (string.IsNullOrWhiteSpace(Location)) { return null; }
        var parts = Location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var history = Array.IndexOf(parts, "_history");
        var idIndex = history > 0 ? history - 1 : parts.Length - 1;
        var index = idIndex - offsetFromId;
        return index >= 0 && idIndex >= 1 ? parts[index] : null;
    }
}

/// <summary>
/// HAPI rechazó una escritura porque el recurso cambió desde que se leyó (HTTP 409 o 412). No es un
/// fallo del servidor sino una carrera entre dos personas: el llamador decide si reaplica o pregunta.
/// </summary>
public sealed class FhirVersionConflictException(string message) : Exception(message);

/// <summary>
/// Datos para crear/reemplazar un MedicationRequest FHIR R5 (Épica K).
///
/// <para><c>AtcCode</c> + <c>MedicationDisplay</c> cuando el medicamento vino
/// del catálogo (<c>/api/medications/search</c>). <c>MedicationText</c> cubre
/// el caso de texto libre cuando el médico tipea un fármaco fuera del slice.
/// Ambos pueden coexistir en el mismo CodeableConcept.</para>
///
/// <para><c>DosageText</c> es el texto humano armado por el form ("500 mg vía
/// oral cada 8 horas por 7 días"). <c>Route</c> es la vía de administración
/// (oral / im / iv / topica / sc / inhalada).</para>
/// </summary>
public sealed record UpsertMedicationRequestRequest(
    string PartitionName,
    string PatientFhirId,
    string? EncounterFhirId,
    string? RequesterPractitionerId,
    string Status,
    string Intent,
    string? AtcCode,
    string? MedicationDisplay,
    string? MedicationText,
    string DosageText,
    string? Route,
    decimal? DispenseQuantity,
    string? DispenseUnit,
    string? Note);

public sealed record MedicationRequestSnapshot(
    string Id,
    string PatientFhirId,
    string? EncounterFhirId,
    string Status,
    string Intent,
    string? MedicationCode,
    string? MedicationDisplay,
    string? MedicationText,
    string? DosageText,
    string? Route,
    decimal? DispenseQuantity,
    string? DispenseUnit,
    string? Note,
    string? RequesterPractitionerId,
    DateTimeOffset? AuthoredOn,
    // groupIdentifier.value — agrupa los medicamentos de una misma receta.
    string? GroupIdentifierValue = null,
    // dosageInstruction[0].patientInstruction — indicaciones POR medicamento
    // (las indicaciones generales de la receta van en Note).
    string? PatientInstruction = null);

/// <summary>
/// Datos para crear/reemplazar un ServiceRequest FHIR R5 con category=laboratory
/// (Épica L).
/// <list type="bullet">
///   <item><c>LoincCode</c> + <c>CodeDisplay</c>: si el examen vino del catálogo LOINC.</item>
///   <item><c>CodeText</c>: nombre humano siempre presente (fallback cuando no hay coding).</item>
///   <item><c>Priority</c>: routine / urgent / asap / stat (ValueSet request-priority).</item>
///   <item><c>OccurrenceDateTime</c>: cuándo realizar el examen, ISO 8601. Opcional.</item>
///   <item><c>ReasonText</c>: motivo clínico free text. Opcional.</item>
///   <item><c>Note</c>: indicaciones para el laboratorio. Opcional.</item>
/// </list>
/// </summary>
public sealed record UpsertLabOrderRequest(
    string PartitionName,
    string PatientFhirId,
    string? EncounterFhirId,
    string? RequesterPractitionerId,
    string Status,
    string Intent,
    string? LoincCode,
    string? CodeDisplay,
    string CodeText,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note,
    // R3/LIS-RD-02: requisición de la visita; dual coding del orderable (panel local + LOINC).
    string? Requisition = null,
    string? LocalCode = null,
    string? LocalSystem = null,
    /// <summary>
    /// Cuándo se emitió la orden. Null al crear —se sella en ese momento— y al actualizar significa
    /// "conservá el que ya tiene": el cliente que promueve <c>draft → active</c> no lo conoce, y
    /// perderlo convertía la fecha de emisión en la fecha del último cambio.
    /// </summary>
    DateTimeOffset? AuthoredOn = null);

public sealed record LabOrderSnapshot(
    string Id,
    string PatientFhirId,
    string? EncounterFhirId,
    string Status,
    string Intent,
    string? LoincCode,
    string? CodeDisplay,
    string? CodeText,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note,
    string? RequesterPractitionerId,
    DateTimeOffset? AuthoredOn,
    // requisition.value — agrupa las órdenes de una misma "orden de laboratorio".
    string? Requisition = null,
    /// <summary>
    /// Código del panel propio de la clínica, cuando la orden trae uno.
    ///
    /// <para>Va aparte de <see cref="LoincCode"/> a propósito. Las órdenes llevan coding dual
    /// —LOINC si el examen lo tiene, más el código local del panel— y leer ambos como si fueran
    /// LOINC hace que el siguiente guardado escriba <c>CBC</c> bajo <c>http://loinc.org</c>. Un
    /// código local publicado como si fuera un estándar internacional es un dato falso en el
    /// expediente, y de los que rompen la interoperabilidad justo cuando se la necesita.</para>
    /// </summary>
    string? LocalCode = null,
    /// <summary>
    /// Sistema del código local. Va junto al código porque quien reescriba la orden —la
    /// reconciliación, sin cliente que se lo dicte— tiene que poder devolverla tal cual estaba.
    /// </summary>
    string? LocalSystem = null)
{
    /// <summary>
    /// Código con el que se busca esta orden en el catálogo de la clínica.
    ///
    /// <para>El catálogo indexa por código local (<c>Code</c> del panel o de la prueba) **y** por
    /// LOINC, así que la búsqueda necesita uno de los dos. Hoy ningún panel de la clínica tiene
    /// LOINC —los 42 lo tienen nulo—, de modo que mirar solo <see cref="LoincCode"/> deja fuera del
    /// catálogo a todos los paneles y el laboratorio termina refiriendo a externo lo que sí hace
    /// en casa.</para>
    ///
    /// <para>Existe como propiedad y no como <c>LoincCode ?? LocalCode</c> repetido en cada sitio
    /// porque son once, y basta olvidarlo en uno para que esa pantalla vuelva a mentir. Se prefiere
    /// LOINC cuando está: es el código con el que el catálogo comparte significado con el mundo.</para>
    /// </summary>
    public string? CatalogCode => LoincCode ?? LocalCode;
}

/// <summary>
/// Datos para crear un <c>Specimen</c> FHIR R5 (recepción, LIS-3).
/// <c>AccessionNumber</c> va en <c>Specimen.accessionIdentifier</c>;
/// <c>FastingHours</c> (si aplica) en <c>collection.fastingStatusDuration</c>.
///
/// <para>⚠️ Acá va el <b>mínimo requerido</b> por el catálogo, no cuánto ayunó el paciente.
/// FHIR define <c>fastingStatusDuration</c> como la duración del ayuno <b>real</b>, así que hoy
/// el recurso afirma algo que no sabemos: nadie registra las horas efectivas, solo un sí/no en
/// recepción. Se conserva para no perder el dato que ya se venía escribiendo; se corrige cuando
/// la recepción capture las horas reales.</para>
/// </summary>
public sealed record UpsertSpecimenRequest(
    string PartitionName,
    string PatientFhirId,
    string ServiceRequestFhirId,
    string Status,
    string? TypeText,
    string AccessionNumber,
    string ReceivedTime,
    string? CollectedDateTime,
    int? FastingHours,
    string? Note,
    // Órdenes adicionales que cubre la misma muestra (N:M, LIS-RD-03). Emite Specimen.request[].
    IReadOnlyList<string>? AdditionalServiceRequestFhirIds = null,
    // Detalles de colección on-site (R1-F7, LIS-RD-27): Specimen.collection.collector/method/bodySite.
    string? CollectorPractitionerId = null,
    string? CollectionMethod = null,
    string? CollectionBodySite = null,
    // Muestra padre (R5: alícuota, LIS-RD-20). Emite Specimen.parent.
    string? ParentSpecimenFhirId = null);

public sealed record SpecimenSnapshot(
    string Id,
    string PatientFhirId,
    string Status,
    string? TypeText,
    string? AccessionNumber,
    string? ReceivedTime,
    string? CollectedDateTime,
    string? ServiceRequestFhirId,
    string? Note);

/// <summary>
/// Datos para crear un <c>Provenance</c> FHIR R5 (cadena de custodia / firma).
/// <c>TargetReference</c> ej. "Specimen/123"; <c>AgentWhoReference</c> ej.
/// "Practitioner/..." u "Organization/...". <c>Recorded</c> ISO 8601.
/// </summary>
public sealed record CreateProvenanceRequest(
    string PartitionName,
    string TargetReference,
    string ActivityCode,
    string ActivityDisplay,
    string AgentWhoReference,
    string Recorded);

/// <summary>Datos del médico solicitante externo (Practitioner ligero, LIS-DEC-10).</summary>
public sealed record ExternalPractitionerRequest(
    string PartitionName,
    string FullName,
    string? Identifier);

/// <summary>
/// Datos para crear un <c>Observation</c> FHIR R5 (resultado, LIS-4). Valor
/// numérico (<c>ValueNumeric</c>+<c>ValueUnit</c>) o textual (<c>ValueString</c>).
/// <c>InterpretationCode</c> = flag N/H/L/HH/LL (calculado server-side).
/// </summary>
public sealed record UpsertObservationRequest(
    string PartitionName,
    string PatientFhirId,
    string Status,
    string? LoincCode,
    string CodeText,
    decimal? ValueNumeric,
    string? ValueUnit,
    string? ValueString,
    string? InterpretationCode,
    decimal? ReferenceLow,
    decimal? ReferenceHigh,
    string? SpecimenFhirId,
    string EffectiveDateTime,
    string? PerformerReference,
    // R2 (conformidad R5): trazabilidad y agrupación de panel.
    string? ServiceRequestFhirId = null,        // Observation.basedOn
    string? AppliesToSex = null,                 // referenceRange.appliesTo (población)
    int? AppliesToAgeMin = null,                 // referenceRange.age.low (años)
    int? AppliesToAgeMax = null,                 // referenceRange.age.high (años)
    IReadOnlyList<string>? HasMemberObservationIds = null, // Observation panel → componentes
    string? TriggeredByObservationId = null,     // reflejo (R5 triggeredBy)
    string? TriggeredByType = null);

public sealed record DiagnosticReportSnapshot(
    string Id,
    string PatientFhirId,
    string Status,
    string? LoincCode,
    string? CodeText,
    string? Issued,
    string? Conclusion,
    string? PerformerDisplay,
    string? SpecimenFhirId,
    string? ServiceRequestFhirId,
    IReadOnlyList<string> ResultObservationIds);

public sealed record ObservationHistoryPoint(decimal Value, string? Date, string? FlagCode);

public sealed record ObservationSnapshot(
    string Id,
    string PatientFhirId,
    string Status,
    string? LoincCode,
    string? CodeText,
    decimal? ValueNumeric,
    string? ValueUnit,
    string? ValueString,
    string? FlagCode,
    decimal? ReferenceLow,
    decimal? ReferenceHigh,
    string? EffectiveDateTime,
    // Vínculos que hay que preservar al reescribir la Observation (corrección, LIS-4b):
    // un PUT reemplaza el recurso entero, y perderlos desconectaría el resultado de su
    // orden, su muestra y quien lo ejecutó.
    string? ServiceRequestFhirId = null,
    string? SpecimenFhirId = null,
    string? PerformerReference = null);

/// <summary>Datos para crear un <c>DiagnosticReport</c> FHIR R5 (informe, LIS-4).</summary>
public sealed record UpsertDiagnosticReportRequest(
    string PartitionName,
    string PatientFhirId,
    string ServiceRequestFhirId,
    string Status,
    string? LoincCode,
    string CodeText,
    IReadOnlyList<string> ResultObservationIds,
    string? SpecimenFhirId,
    string? PerformerReference,
    string? PerformerDisplay,
    string? Conclusion,
    string EffectiveDateTime);

/// <summary>
/// Datos para crear un AllergyIntolerance FHIR R5. <c>Code</c> es SNOMED CT
/// si vino del catálogo; <c>CodeText</c> cubre el caso de texto libre cuando
/// el auxiliar tipea un alergeno fuera del catálogo. <c>Severity</c>:
/// mild | moderate | severe (FHIR value-set required).
/// </summary>
public sealed record UpsertAllergyIntoleranceRequest(
    string PartitionName,
    string PatientFhirId,
    Guid RecorderUserId,
    string? Code,
    string? CodeText,
    string? Category,
    string? Severity,
    string? Note);

/// <summary>Snapshot de lectura — solo lo que la UI lista.</summary>
public sealed record AllergyIntoleranceSnapshot(
    string Id,
    string PatientFhirId,
    string? Code,
    string? CodeDisplay,
    string? Category,
    string? Severity,
    string? Note,
    DateTimeOffset? RecordedDate,
    bool Active);

/// <summary>
/// Datos para crear/reemplazar un RelatedPerson FHIR R5. Vincula con un
/// Patient (<c>PatientFhirId</c>) y declara una <c>Relationship</c> (parent /
/// guardian / spouse / etc., del CodeSystem
/// <c>http://terminology.hl7.org/CodeSystem/v3-RoleCode</c>).
///
/// <para><c>Identifier</c> es opcional pero recomendado para menores sin
/// identifier propio (el responsable sí tiene DNI).</para>
/// </summary>
public sealed record UpsertRelatedPersonRequest(
    string PartitionName,
    string PatientFhirId,
    string Relationship,
    string? Family,
    IReadOnlyList<string>? Given,
    string? IdentifierSystem,
    string? IdentifierValue,
    string? IdentifierTypeCode,
    string? IdentifierTypeSystem,
    string? Email,
    string? Phone,
    string? Gender,
    string? BirthDate,
    bool Active = true);

/// <summary>Snapshot de lectura de un RelatedPerson. Solo lo que la UI necesita listar.</summary>
public sealed record RelatedPersonSnapshot(
    string Id,
    string PatientFhirId,
    string? Relationship,
    string? DisplayName,
    string? IdentifierSystem,
    string? IdentifierValue,
    string? Phone,
    string? Email,
    bool Active);

/// <summary>
/// Match mínimo de Patient devuelto por el search de dedupe. Solo lo
/// imprescindible para mostrar al admin en el modal: <c>id</c> para navegar,
/// <c>displayName</c> + <c>birthDate</c> para que reconozca al paciente.
/// </summary>
public sealed record PatientMatch(
    string Id,
    string? DisplayName,
    string? BirthDate);

/// <summary>
/// Snapshot de lectura completo de un Patient (Épica K5). Incluye el primer
/// identifier oficial (label desde <c>identifier.type.text</c> o coding
/// display) para imprimirlo en el header de la receta PDF.
/// </summary>
public sealed record PatientSnapshot(
    string Id,
    string? DisplayName,
    string? BirthDate,
    string? Gender,
    string? IdentifierLabel,
    string? IdentifierValue);

/// <summary>Datos minimos para crear un Organization FHIR R5.</summary>
public sealed record CreateOrganizationRequest(
    string Name,
    string? NationalTaxId,
    string? PartitionName = null);

/// <summary>
/// Datos para crear/reemplazar un Practitioner FHIR R5. Incluye name parts
/// (familia + given) y opcionales: licencia profesional (identifier),
/// telecom (email/phone), gender.
/// </summary>
public sealed record UpsertPractitionerRequest(
    string PartitionName,
    string Family,
    IReadOnlyList<string> Given,
    string? Prefix,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Gender,
    bool Active = true);

/// <summary>Razón social como <c>Organization</c> FHIR (FAC-9).</summary>
public sealed record UpsertLegalEntityOrganizationRequest(
    string PartitionName,
    string FhirId,
    string TenantOrganizationFhirId,
    string LegalName,
    string? TradeName,
    string? TaxId,
    string? FiscalAddress,
    string? Phone,
    string? Email,
    bool Active);

/// <summary>Sede como <c>Location</c> FHIR (FAC-9).</summary>
public sealed record UpsertLocationRequest(
    string PartitionName,
    string FhirId,
    string ManagingOrganizationFhirId,
    string Code,
    string Name,
    string? FiscalEstablishmentCode,
    string? Address,
    string? Phone,
    bool Active);

/// <summary>
/// Datos para crear/reemplazar un PractitionerRole FHIR R5. Vincula un
/// Practitioner con la Organization y opcionalmente declara una especialidad
/// (SNOMED CT).
/// </summary>
/// <param name="LocationFhirIds">
/// Sedes donde atiende (FAC-9), como ids de <c>Location</c>. <c>null</c> = no tocarlas: un
/// <c>PUT</c> conserva las que el recurso ya tenía. Las escribe solo la sincronización de sedes del
/// profesional, para que editar el perfil no las borre.
/// </param>
public sealed record UpsertPractitionerRoleRequest(
    string PartitionName,
    string PractitionerFhirId,
    string OrganizationFhirId,
    string? SpecialtyCode,
    string? SpecialtyDisplay,
    string SpecialtySystem = "http://snomed.info/sct",
    bool Active = true,
    IReadOnlyList<string>? LocationFhirIds = null);

/// <summary>Snapshot de lectura de un Practitioner FHIR. Solo expone lo que el caso de uso necesita.</summary>
public sealed record PractitionerSnapshot(
    string Id,
    string? Family,
    IReadOnlyList<string> Given,
    string? Prefix,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Gender,
    bool Active);

/// <summary>
/// Snapshot de lectura de un PractitionerRole FHIR. Hito 1 solo expone la
/// primera specialty (el modelo permite N pero la UI maneja una).
/// </summary>
public sealed record PractitionerRoleSnapshot(
    string Id,
    string? SpecialtyCode,
    string? SpecialtyDisplay,
    string? SpecialtySystem,
    bool Active,
    string? PractitionerFhirId = null,
    string? OrganizationFhirId = null,
    IReadOnlyList<string>? LocationFhirIds = null);

/// <summary>
/// Item del listado de PractitionerRoles por organización (Épica F2). Más
/// detallado que el snapshot porque incluye la referencia al Practitioner
/// (necesaria para el lookup paralelo).
/// </summary>
public sealed record PractitionerRoleListItem(
    string Id,
    string PractitionerFhirId,
    string? SpecialtyCode,
    string? SpecialtyDisplay,
    string? SpecialtySystem,
    bool Active);
