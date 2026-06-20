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
