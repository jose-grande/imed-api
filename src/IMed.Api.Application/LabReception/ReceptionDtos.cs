namespace IMed.Api.Application.LabReception;

/// <summary>Vista de recepción de una orden: paciente, examen, enrutamiento y precondiciones (LIS-3).</summary>
public sealed record ReceptionOrderView(
    string ServiceRequestId,
    string PatientFhirId,
    string? LoincCode,
    string? CodeText,
    string Routing,
    int? FastingHours,
    bool AlreadyReceived,
    string? IntakeStatus);

public sealed record CreateExternalOrderCommand(
    Guid OrganizationId,
    string PatientFhirId,
    string RequesterName,
    string? RequesterIdentifier,
    string? LoincCode,
    string CodeText,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note);

public sealed record CreatedExternalOrder(string ServiceRequestId);

public sealed record ReceiveCommand(
    Guid OrganizationId,
    Guid UserId,
    string ServiceRequestId,
    bool? FastingSatisfied,
    bool IsWalkIn,
    DateTimeOffset? ScheduledFor,
    string? TypeText,
    string? CollectedDateTime,
    string? Note);

public sealed record ReceiveResult(
    Guid? IntakeId,
    string Status,
    string Routing,
    string? AccessionNumber,
    string? SpecimenFhirId,
    string? BlockReason);

// -------------------- Recepción por requisición (R1-F2, LIS-RD-02/03/04) --------------------

/// <summary>Vista de toda la visita en recepción: órdenes agrupadas por tipo de muestra a recolectar.</summary>
public sealed record RequisitionView(
    string EncounterId,
    string PatientFhirId,
    IReadOnlyList<RequisitionGroup> Groups);

/// <summary>Un tipo de muestra a recolectar (1 specimen) y las órdenes que cubre (N:M).</summary>
public sealed record RequisitionGroup(
    string SpecimenTypeCode,
    string SpecimenTypeName,
    string CollectionMode,
    string? TubeCode,
    string? TubeName,
    string? TubeColor,
    IReadOnlyList<RequisitionOrder> Orders);

public sealed record RequisitionOrder(
    string ServiceRequestId,
    string? LoincCode,
    string? CodeText,
    string Routing,
    int? FastingHours,
    string? IntakeStatus);

public sealed record ReceiveRequisitionCommand(
    Guid OrganizationId,
    Guid UserId,
    string Requisition,
    IReadOnlyList<ReceiveGroup> Groups);

/// <summary>Acción sobre un grupo de tipo de muestra: recibir (1 specimen para todas) o rechazar.</summary>
public sealed record ReceiveGroup(
    string SpecimenTypeCode,
    string? SpecimenTypeName,
    string? TubeCode,
    IReadOnlyList<string> ServiceRequestIds,
    string? CollectedDateTime,
    bool? FastingSatisfied,
    bool Reject,
    string? RejectReason,
    // Detalles de toma on-site (R1-F7): método (venopunción/hisopado) y sitio.
    string? CollectionMethod = null,
    string? CollectionBodySite = null);

public sealed record ReceiveGroupResult(
    string SpecimenTypeCode,
    string Status,
    string? AccessionNumber,
    string? SpecimenFhirId,
    string? Message);

// -------------------- Etiqueta / barcode (R1-F3, LIS-RD-18) --------------------

/// <summary>Datos de la etiqueta de la muestra: accession (= valor del código de barras), paciente, tipo y fecha.</summary>
public sealed record LabelDto(
    string Accession,
    string BarcodeValue,
    string? PatientName,
    string PatientFhirId,
    string? SpecimenType,
    string? ReceivedTime,
    string? IdentifierLabel,
    string? IdentifierValue);

// -------------------- Cola de órdenes pendientes (R1-F5, LIS-RD-10) --------------------

/// <summary>Orden de laboratorio activa aún no recibida (cola de recepción).</summary>
public sealed record PendingOrder(
    string ServiceRequestId,
    string PatientFhirId,
    string? EncounterFhirId,
    string? LoincCode,
    string? CodeText,
    string? Priority,
    DateTimeOffset? AuthoredOn,
    // Fase 2: N.º de orden (requisición) para agrupar/navegar en la cola de toma.
    string? Requisition = null);

// -------------------- Cola de toma on-site (R1-F7, LIS-RD-24) --------------------

/// <summary>Orden on-site (sangre/hisopado/OGTT) pendiente de toma en el laboratorio.</summary>
public sealed record CollectionQueueItem(
    string ServiceRequestId,
    string PatientFhirId,
    string? EncounterFhirId,
    string? LoincCode,
    string? CodeText,
    string SpecimenTypeCode,
    string SpecimenTypeName,
    string? DefaultMethod,
    string? DefaultBodySite,
    int? FastingHours,
    string? Priority);
